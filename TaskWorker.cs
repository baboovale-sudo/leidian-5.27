using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OLAPlug;

namespace OLA
{
    public class TaskWorker
    {
        public int RowIndex { get; set; }
        public string EmulatorName { get; set; }
        public string EmulatorClass { get; set; }
        public string EmulatorBasePath { get; set; }
        public string PackageName { get; set; } = "com.syyx.nuoya.idle";
        public List<string> TaskList { get; set; } = new List<string>();

        public WorkerState RunState { get; private set; } = WorkerState.Idle;
        public DateTime LastStartTime { get; private set; }
        public DateTime LastActionTime { get; set; } = DateTime.Now;

        public OLAPlugServer Ola => _ola!;
        public long CurrentBindHwnd { get; private set; } = 0;

        private OLAPlugServer? _ola = null;
        private CancellationTokenSource? _logicTokenSource;
        private CancellationToken _currentToken;
        private string _lastStatusMsg = "";
        private string _lastExceptionMsg = "";
        private bool _keepUnfinishedStatus = false;
        private readonly Random _rnd = new Random();

        private const int DefaultClickDelay = 1000;
        private const int DefaultClickOffset = 5;
        private const double DefaultImageSimilarity = 0.85;
        private const string DefaultDeltaColor = "101010";
        private const string DefaultDictName = "无尽黑暗.txt";
        private const double DefaultTextSimilarity = 0.8;

        public Action<string>? LogCallback;
        public Action<int, string, string>? StatusCallback;
        public Action<int, string>? ExceptionCallback;

        public TaskWorker(int row, string name, string className, string path, string packageName = "")
        {
            RowIndex = row;
            EmulatorName = name;
            EmulatorClass = className;
            EmulatorBasePath = path;

            if (!string.IsNullOrEmpty(packageName))
            {
                PackageName = packageName;
            }
        }

        #region 生命周期控制

        public void Start()
        {
            if (RunState == WorkerState.Running)
            {
                return;
            }

            _keepUnfinishedStatus = false;
            RunState = WorkerState.Running;
            LastStartTime = DateTime.Now;
            LastActionTime = DateTime.Now;

            UpdateException("等待60秒监控介入...");

            _logicTokenSource?.Dispose();
            _logicTokenSource = new CancellationTokenSource();

            var token = _logicTokenSource.Token;
            Task.Run(async () => await RunLogicThread(token), token);
        }

        public void Stop()
        {
            RunState = WorkerState.Stopped;
            _logicTokenSource?.Cancel();
            UpdateStatus("已停止", "0");
            UpdateException("");
        }

        public void Pause()
        {
            if (RunState == WorkerState.Running)
            {
                RunState = WorkerState.Paused;
                UpdateStatus("已暂停", "");
            }
        }

        public void Resume()
        {
            if (RunState == WorkerState.Paused)
            {
                RunState = WorkerState.Resuming;
            }
        }

        public bool IsAlive()
        {
            try
            {
                return _ola != null && FindWindowWithPlugin() != 0;
            }
            catch (Exception ex)
            {
                WriteLog($"存活检测异常: {ex.Message}");
                return false;
            }
        }

        public void MarkAsMonitored()
        {
            if (_lastExceptionMsg.Contains("等待") || _lastExceptionMsg.Contains("监控"))
            {
                UpdateException("监控中");
            }
        }

        public void PerformRestart()
        {
            if (RunState == WorkerState.Stopped)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    UpdateStatus("掉线重连", "0");
                    UpdateException("检测掉线，正在重启...");
                    _logicTokenSource?.Cancel();
                    RunState = WorkerState.Idle;

                    CloseEmulator();
                    await Task.Delay(3000);

                    WriteLog("执行重启...");
                    Start();
                }
                catch (Exception ex)
                {
                    LogError($"重启异常: {ex.Message}");
                }
            });
        }

        public void MarkCurrentTaskUnfinished()
        {
            _keepUnfinishedStatus = true;
            UpdateStatus("未完成", CurrentBindHwnd.ToString());
        }

        #endregion

        #region 逻辑线程核心

        private async Task RunLogicThread(CancellationToken token)
        {
            try
            {
                _ola = OlaRuntime.Create();

                if (_ola.OLAObject == 0)
                {
                    LogError("插件接口创建失败");
                    return;
                }

                string imageBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Output");
                _ola.SetPath(imageBasePath);

                long parentHwnd = FindWindowWithPlugin();

                if (parentHwnd == 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    UpdateStatus("启动中...", "0");

                    if (!LaunchEmulator())
                    {
                        LogError("启动失败");
                        return;
                    }

                    UpdateStatus("等待画面10s", "0");

                    try
                    {
                        await Task.Delay(10000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    UpdateException("等待60秒监控介入...");

                    int retry = 0;
                    while (parentHwnd == 0 && retry < 30)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        parentHwnd = FindWindowWithPlugin();

                        if (parentHwnd != 0)
                        {
                            break;
                        }

                        await Task.Delay(1000, token);
                        retry++;
                    }
                }

                if (parentHwnd == 0)
                {
                    LogError("启动超时");
                    return;
                }

                UpdateStatus("等待画面", parentHwnd.ToString());

                long childHwnd = 0;
                while (RunState != WorkerState.Stopped && childHwnd == 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    childHwnd = _ola!.GetWindow(parentHwnd, 1);

                    if (childHwnd != 0)
                    {
                        break;
                    }

                    await Task.Delay(1000, token);
                }

                int ret = _ola!.BindWindowEx(
                    childHwnd,
                    Form1.OLAConfig.Bind_Display,
                    Form1.OLAConfig.Bind_Mouse,
                    Form1.OLAConfig.Bind_Keypad,
                    "",
                    Form1.OLAConfig.Bind_Mode);

                if (ret == 1)
                {
                    UpdateStatus("运行中", childHwnd.ToString());
                    WriteLog($"成功绑定窗口: 0x{childHwnd:X}");

                    try
                    {
                        await DoGameLogic(token, childHwnd);
                    }
                    catch (OperationCanceledException)
                    {
                        WriteLog("任务已取消");
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            LogError($"逻辑异常: {ex.Message}");
                        }
                    }

                    RunState = WorkerState.Stopped;
                }
                else
                {
                    LogError($"绑定失败: {ret}");
                }
            }
            catch (OperationCanceledException)
            {
                WriteLog("逻辑线程已取消");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogError($"异常: {ex.Message}");
                }
            }
            finally
            {
                Cleanup();
            }
        }

        private async Task DoGameLogic(CancellationToken token, long currentHwnd)
        {
            _currentToken = token;
            CurrentBindHwnd = currentHwnd;

            if (TaskList == null || TaskList.Count == 0)
            {
                WriteLog("未分配任务");
                await Task.Delay(2000, token);
                return;
            }

            var gameTask = new GameTask(this);
            bool allCompleted = true;

            foreach (var taskName in TaskList)
            {
                await CheckPauseStateAsync();

                if (RunState == WorkerState.Stopped)
                {
                    allCompleted = false;
                    break;
                }

                WriteLog($"======= 开始执行: {taskName} =======");
                UpdateStatus($"执行中: {taskName}", currentHwnd.ToString());

                try
                {
                    await gameTask.Execute(taskName);
                }
                catch (OperationCanceledException)
                {
                    allCompleted = false;
                    WriteLog("任务取消，线程结束");
                    throw;
                }
                catch (Exception ex)
                {
                    allCompleted = false;
                    _keepUnfinishedStatus = true;
                    WriteLog($"{taskName} 未完成");
                    WriteLog($"任务[{taskName}]异常: {ex.Message}");
                    WriteLog("线程结束");
                    UpdateStatus("未完成", currentHwnd.ToString());
                    break;
                }

                if (RunState == WorkerState.Stopped)
                {
                    allCompleted = false;
                    WriteLog($"{taskName} 未完成");
                    WriteLog("线程结束");
                    UpdateStatus("未完成", currentHwnd.ToString());
                    break;
                }

                if (!gameTask.LastTaskCompleted)
                {
                    allCompleted = false;
                    _keepUnfinishedStatus = true;
                    WriteLog($"{taskName} 未完成");
                    WriteLog("线程结束");
                    UpdateStatus("未完成", currentHwnd.ToString());
                    break;
                }

                WriteLog($"{taskName} 已完成");
                UpdateStatus("已完成", currentHwnd.ToString());
                await Task.Delay(1000, token);
            }

            if (RunState != WorkerState.Stopped && allCompleted)
            {
                UpdateStatus("任务已全部完成", currentHwnd.ToString());
                WriteLog("所有任务已完成");
            }
        }

        #endregion

        #region OL_SDK 封装方法

        /// <summary>
        /// 在指定区域内匹配图片，找到后点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>x1</term><description>匹配区域左上角 X 坐标。</description></item>
        /// <item><term>y1</term><description>匹配区域左上角 Y 坐标。</description></item>
        /// <item><term>x2</term><description>匹配区域右下角 X 坐标。</description></item>
        /// <item><term>y2</term><description>匹配区域右下角 Y 坐标。</description></item>
        /// <item><term>imgName</term><description>要匹配的图片文件名。图片文件需要放在 Output 目录下。</description></item>
        /// <item><term>targetX</term><description>匹配成功后要点击的指定 X 坐标。</description></item>
        /// <item><term>targetY</term><description>匹配成功后要点击的指定 Y 坐标。</description></item>
        /// <item><term>delay</term><description>点击完成后的等待时间，默认 1000 毫秒，即 1 秒。</description></item>
        /// <item><term>offset</term><description>点击随机偏移范围，默认 5。实际点击会在目标坐标 ±offset 范围内随机浮动。</description></item>
        /// <item><term>sim</term><description>图片相似度，默认 0.85。数值越高匹配越严格。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// if (await _worker.OL_MatchWindowsFromPath(0, 0, 960, 540, "确认按钮.bmp", 810, 478))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>单独修改等待时间：</para>
        /// <code>
        /// if (await _worker.OL_MatchWindowsFromPath(0, 0, 960, 540, "确认按钮.bmp", 810, 478, 3000))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>找到图片后会自动调用 OL_LeftClick 点击 targetX,targetY。</description></item>
        /// <item><description>默认 delay = 1000，即点击后等待 1 秒。</description></item>
        /// <item><description>默认 offset = 5，即点击位置会有小范围随机偏移。</description></item>
        /// <item><description>找到图片并完成点击返回 true，未找到图片返回 false。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合判断某个图片是否存在，并在存在后点击固定坐标。</description></item>
        /// <item><description>适合按钮位置固定，但需要用图片确认当前页面状态的场景。</description></item>
        /// <item><description>如果需要点击图片实际出现的位置，建议使用 OL_FindWindowsFromPathCenter。</description></item>
        /// <item><description>如果只是固定颜色判断，OL_CmpColor 通常更快。</description></item>
        /// <item><description>如果误识别较多，提高 sim；如果经常找不到，适当降低 sim。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_MatchWindowsFromPath(
            int x1,
            int y1,
            int x2,
            int y2,
            string imgName,
            int targetX,
            int targetY,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            double sim = DefaultImageSimilarity)
        {
            if (string.IsNullOrWhiteSpace(imgName))
            {
                return false;
            }

            var res = _ola!.MatchWindowsFromPath(x1, y1, x2, y2, imgName, sim, 0, 0, 1.0);

            if (res != null && res.MatchState)
            {
                WriteLog($"匹配图片: {imgName} -> 找到({res.X},{res.Y}) -> 点击指定位置({targetX},{targetY})");
                await OL_LeftClick(targetX, targetY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内查找图片，找到后点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>x1</term><description>查找区域左上角 X 坐标。</description></item>
        /// <item><term>y1</term><description>查找区域左上角 Y 坐标。</description></item>
        /// <item><term>x2</term><description>查找区域右下角 X 坐标。</description></item>
        /// <item><term>y2</term><description>查找区域右下角 Y 坐标。</description></item>
        /// <item><term>imgName</term><description>要查找的图片文件名或图片路径。支持多图，例如："确定.bmp|关闭.bmp|继续.bmp"。</description></item>
        /// <item><term>targetX</term><description>找到图片后要点击的指定 X 坐标。</description></item>
        /// <item><term>targetY</term><description>找到图片后要点击的指定 Y 坐标。</description></item>
        /// <item><term>delay</term><description>点击完成后的等待时间，默认 1000 毫秒，即 1 秒。</description></item>
        /// <item><term>offset</term><description>点击随机偏移范围，默认 5。实际点击会在目标坐标 ±offset 范围内随机浮动。</description></item>
        /// <item><term>deltaColor</term><description>颜色差值，默认 "101010"。值越大，颜色容错越宽。</description></item>
        /// <item><term>sim</term><description>图片相似度，默认 0.85。数值越高匹配越严格。</description></item>
        /// <item><term>dir</term><description>查找方向，默认 0，表示从左到右、从上到下查找。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// if (await _worker.OL_FindWindowsFromPath(0, 0, 960, 540, "确定按钮.bmp", 810, 478))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>搜索整个绑定窗口：</para>
        /// <code>
        /// if (await _worker.OL_FindWindowsFromPath(0, 0, 0, 0, "关闭.bmp", 920, 35))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>多图查找：</para>
        /// <code>
        /// if (await _worker.OL_FindWindowsFromPath(0, 0, 960, 540, "确定.bmp|继续.bmp|关闭.bmp", 810, 478))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>完整命名参数写法：</para>
        /// <code>
        /// if (await _worker.OL_FindWindowsFromPath(
        ///     x1: 0,
        ///     y1: 0,
        ///     x2: 960,
        ///     y2: 540,
        ///     imgName: "确定按钮.bmp",
        ///     targetX: 810,
        ///     targetY: 478,
        ///     delay: 500,
        ///     offset: 5,
        ///     deltaColor: "101010",
        ///     sim: 0.85,
        ///     dir: 0))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>四种绑定窗口找图接口使用场景：</para>
        /// <list type="table">
        /// <item>
        /// <term>FindWindowsFromPath</term>
        /// <description>通过图片文件路径在绑定窗口中查找图片，只返回第一个匹配结果。适合普通按钮、图标、弹窗、任务入口等“找到一个就处理”的场景。</description>
        /// </item>
        /// <item>
        /// <term>FindWindowsFromPathAll</term>
        /// <description>通过图片文件路径在绑定窗口中查找图片，返回所有匹配结果。适合一个画面中可能同时出现多个相同按钮、多个相同图标、多个可领取项的场景。</description>
        /// </item>
        /// <item>
        /// <term>FindWindowsFromPtr</term>
        /// <description>通过已经加载到内存的 OLAImage 图片指针在绑定窗口中查找图片，只返回第一个匹配结果。适合同一张模板图会被高频重复查找的场景。</description>
        /// </item>
        /// <item>
        /// <term>FindWindowsFromPtrAll</term>
        /// <description>通过已经加载到内存的 OLAImage 图片指针在绑定窗口中查找图片，返回所有匹配结果。适合高频循环批量查找多个相同目标的场景。</description>
        /// </item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>按钮、图标、弹窗位置不固定时，优先使用这个方法。</description></item>
        /// <item><description>只需要找第一个目标，优先使用 FindWindowsFromPath。</description></item>
        /// <item><description>需要找出所有目标，使用 FindWindowsFromPathAll。</description></item>
        /// <item><description>图片模板会反复使用，优先考虑 FindWindowsFromPtr。</description></item>
        /// <item><description>图片模板会反复使用，并且要查找全部匹配位置，使用 FindWindowsFromPtrAll。</description></item>
        /// <item><description>普通任务脚本先用 Path 版本，后期性能不够再改 Ptr 版本。</description></item>
        /// <item><description>如果目标坐标固定，只是判断页面状态，优先用 OL_CmpColor。</description></item>
        /// <item><description>如果误识别较多，提高 sim，例如 0.9；如果经常找不到，降低到 0.8 或适当增大 deltaColor。</description></item>
        /// <item><description>不要一开始就全窗口查找，能限制区域就限制区域，速度和稳定性更好。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPath(
            int x1,
            int y1,
            int x2,
            int y2,
            string imgName,
            int targetX,
            int targetY,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity,
            int dir = 0)
        {
            if (string.IsNullOrWhiteSpace(imgName))
            {
                return false;
            }

            var res = _ola!.FindWindowsFromPath(x1, y1, x2, y2, imgName, deltaColor, sim, dir);

            if (res != null && res.MatchState)
            {
                WriteLog($"找图(Path): {imgName} -> 找到({res.X},{res.Y}) -> 点击指定位置({targetX},{targetY})");
                await OL_LeftClick(targetX, targetY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内查找图片，找到后点击图片中心点。
        /// </summary>
        /// <remarks>
        /// <para>默认写法：</para>
        /// <code>
        /// if (await _worker.OL_FindWindowsFromPathCenter(0, 0, 960, 540, "确定按钮.bmp"))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>图片位置不固定，但点击位置就是图片本身时，使用这个重载。</description></item>
        /// <item><description>例如查找“确定按钮”“关闭按钮”“领取按钮”，找到哪里点哪里。</description></item>
        /// <item><description>如果找到图片后要点击另一个固定位置，使用带 targetX、targetY 的重载。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPathCenter(
            int x1,
            int y1,
            int x2,
            int y2,
            string imgName,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity,
            int dir = 0)
        {
            if (string.IsNullOrWhiteSpace(imgName))
            {
                return false;
            }

            var res = _ola!.FindWindowsFromPath(x1, y1, x2, y2, imgName, deltaColor, sim, dir);

            if (res != null && res.MatchState)
            {
                int clickX = res.X + res.Width / 2;
                int clickY = res.Y + res.Height / 2;

                WriteLog($"找图(Path): {imgName} -> 找到({res.X},{res.Y}) -> 点击图片中心({clickX},{clickY})");
                await OL_LeftClick(clickX, clickY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内查找所有匹配图片，只要存在匹配结果就点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合一个画面里可能同时出现多个相同目标，但只需要确认存在后点击固定位置的场景。</description></item>
        /// <item><description>例如存在多个可领取项时，先确认至少有一个，再点击固定的批量领取按钮。</description></item>
        /// <item><description>如果要逐个处理所有匹配位置，建议直接调用 Ola.FindWindowsFromPathAll 获取结果列表后循环处理。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>存在匹配结果并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPathAll(
            int x1,
            int y1,
            int x2,
            int y2,
            string imgName,
            int targetX,
            int targetY,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity)
        {
            if (string.IsNullOrWhiteSpace(imgName))
            {
                return false;
            }

            var results = _ola!.FindWindowsFromPathAll(x1, y1, x2, y2, imgName, deltaColor, sim);
            var res = results?.FirstOrDefault(r => r != null && r.MatchState);

            if (res != null)
            {
                WriteLog($"找图全部(PathAll): {imgName} -> 找到({res.X},{res.Y}) -> 点击指定位置({targetX},{targetY})");
                await OL_LeftClick(targetX, targetY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内查找所有匹配图片，找到后点击第一个匹配结果的中心点。
        /// </summary>
        /// <remarks>
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合画面里可能有多个相同按钮，但只处理第一个匹配目标的场景。</description></item>
        /// <item><description>如果需要从上到下逐个点击多个目标，建议直接调用 Ola.FindWindowsFromPathAll 后自行排序和循环。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPathAllCenter(
            int x1,
            int y1,
            int x2,
            int y2,
            string imgName,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity)
        {
            if (string.IsNullOrWhiteSpace(imgName))
            {
                return false;
            }

            var results = _ola!.FindWindowsFromPathAll(x1, y1, x2, y2, imgName, deltaColor, sim);
            var res = results?.FirstOrDefault(r => r != null && r.MatchState);

            if (res != null)
            {
                int clickX = res.X + res.Width / 2;
                int clickY = res.Y + res.Height / 2;

                WriteLog($"找图全部(PathAll): {imgName} -> 找到({res.X},{res.Y}) -> 点击图片中心({clickX},{clickY})");
                await OL_LeftClick(clickX, clickY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内使用内存图片指针查找图片，找到后点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合同一张模板图在循环中反复查找的场景。</description></item>
        /// <item><description>需要先通过 LoadImage 等接口加载图片，拿到 OLAImage 指针后传入 imagePtr。</description></item>
        /// <item><description>如果只是偶尔查找一次，使用 OL_FindWindowsFromPath 更简单。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPtr(
            int x1,
            int y1,
            int x2,
            int y2,
            long imagePtr,
            int targetX,
            int targetY,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity,
            int dir = 0)
        {
            if (imagePtr == 0)
            {
                return false;
            }

            var res = _ola!.FindWindowsFromPtr(x1, y1, x2, y2, imagePtr, deltaColor, sim, dir);

            if (res != null && res.MatchState)
            {
                WriteLog($"找图(Ptr): {imagePtr} -> 找到({res.X},{res.Y}) -> 点击指定位置({targetX},{targetY})");
                await OL_LeftClick(targetX, targetY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内使用内存图片指针查找图片，找到后点击图片中心点。
        /// </summary>
        /// <remarks>
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合同一张模板图高频查找，并且点击位置就是图片本身的场景。</description></item>
        /// <item><description>例如循环检测固定模板按钮，找到后直接点击按钮中心。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPtrCenter(
            int x1,
            int y1,
            int x2,
            int y2,
            long imagePtr,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity,
            int dir = 0)
        {
            if (imagePtr == 0)
            {
                return false;
            }

            var res = _ola!.FindWindowsFromPtr(x1, y1, x2, y2, imagePtr, deltaColor, sim, dir);

            if (res != null && res.MatchState)
            {
                int clickX = res.X + res.Width / 2;
                int clickY = res.Y + res.Height / 2;

                WriteLog($"找图(Ptr): {imagePtr} -> 找到({res.X},{res.Y}) -> 点击图片中心({clickX},{clickY})");
                await OL_LeftClick(clickX, clickY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内使用内存图片指针查找所有匹配结果，只要存在匹配结果就点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合高频循环中批量检测多个相同目标，但只需要确认存在后点击固定位置的场景。</description></item>
        /// <item><description>如果要处理每一个匹配结果，建议直接调用 Ola.FindWindowsFromPtrAll 获取列表后循环处理。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>存在匹配结果并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPtrAll(
            int x1,
            int y1,
            int x2,
            int y2,
            long imagePtr,
            int targetX,
            int targetY,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity)
        {
            if (imagePtr == 0)
            {
                return false;
            }

            var results = _ola!.FindWindowsFromPtrAll(x1, y1, x2, y2, imagePtr, deltaColor, sim);
            var res = results?.FirstOrDefault(r => r != null && r.MatchState);

            if (res != null)
            {
                WriteLog($"找图全部(PtrAll): {imagePtr} -> 找到({res.X},{res.Y}) -> 点击指定位置({targetX},{targetY})");
                await OL_LeftClick(targetX, targetY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在绑定窗口指定区域内使用内存图片指针查找所有匹配结果，找到后点击第一个匹配结果的中心点。
        /// </summary>
        /// <remarks>
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合高频批量查找多个相同目标，但当前逻辑只处理第一个目标的场景。</description></item>
        /// <item><description>如果需要逐个处理所有目标，建议直接调用 Ola.FindWindowsFromPtrAll 获取列表后自行排序和循环。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到图片并完成点击返回 true；未找到图片返回 false。</returns>
        public async Task<bool> OL_FindWindowsFromPtrAllCenter(
            int x1,
            int y1,
            int x2,
            int y2,
            long imagePtr,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset,
            string deltaColor = DefaultDeltaColor,
            double sim = DefaultImageSimilarity)
        {
            if (imagePtr == 0)
            {
                return false;
            }

            var results = _ola!.FindWindowsFromPtrAll(x1, y1, x2, y2, imagePtr, deltaColor, sim);
            var res = results?.FirstOrDefault(r => r != null && r.MatchState);

            if (res != null)
            {
                int clickX = res.X + res.Width / 2;
                int clickY = res.Y + res.Height / 2;

                WriteLog($"找图全部(PtrAll): {imagePtr} -> 找到({res.X},{res.Y}) -> 点击图片中心({clickX},{clickY})");
                await OL_LeftClick(clickX, clickY, offset);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判断多个坐标点颜色是否全部匹配，全部匹配后点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>pointsStr</term><description>多点颜色字符串，格式："x,y,color|x,y,color|x,y,color"。</description></item>
        /// <item><term>targetX</term><description>全部颜色匹配成功后要点击的指定 X 坐标。</description></item>
        /// <item><term>targetY</term><description>全部颜色匹配成功后要点击的指定 Y 坐标。</description></item>
        /// <item><term>delay</term><description>点击完成后的等待时间，默认 1000 毫秒，即 1 秒。</description></item>
        /// <item><term>offset</term><description>点击随机偏移范围，默认 5。实际点击会在目标坐标 ±offset 范围内随机浮动。</description></item>
        /// </list>
        ///
        /// <para>pointsStr 格式：</para>
        /// <code>
        /// "x,y,color|x,y,color|x,y,color"
        /// </code>
        ///
        /// <para>pointsStr 示例：</para>
        /// <code>
        /// "76,21,32435c|794,12,efd13d|934,18,919187"
        /// </code>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// if (await _worker.OL_CmpColor("76,21,32435c|794,12,efd13d", 810, 478))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>所有颜色点全部匹配后，才会点击 targetX,targetY。</description></item>
        /// <item><description>任意一个颜色点不匹配，直接返回 false，不会点击。</description></item>
        /// <item><description>默认 delay = 1000，即点击后等待 1 秒。</description></item>
        /// <item><description>传入 5000 时，只表示这一处点击后等待 5 秒。</description></item>
        /// <item><description>全部匹配并完成点击返回 true，任意点不匹配返回 false。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合固定分辨率、固定 UI 布局、固定坐标的页面状态判断。</description></item>
        /// <item><description>适合判断弹窗是否出现、按钮是否变亮、页面是否加载完成。</description></item>
        /// <item><description>多点颜色判断比单点颜色判断更稳，适合防止误点。</description></item>
        /// <item><description>如果按钮或目标位置会移动，不建议使用本方法，应改用 OL_FindWindowsFromPath。</description></item>
        /// <item><description>如果颜色受光效、透明度、动画影响较大，建议改用找图。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>所有颜色点匹配并完成点击返回 true；任意一个点不匹配返回 false。</returns>
        public async Task<bool> OL_CmpColor(
            string pointsStr,
            int targetX,
            int targetY,
            int delay = DefaultClickDelay,
            int offset = DefaultClickOffset)
        {
            if (string.IsNullOrWhiteSpace(pointsStr))
            {
                return false;
            }

            string[] points = pointsStr.Split('|');

            foreach (string p in points)
            {
                string[] item = p.Split(',');

                if (item.Length < 3)
                {
                    return false;
                }

                if (!int.TryParse(item[0], out int x) || !int.TryParse(item[1], out int y))
                {
                    return false;
                }

                string color = item[2].Trim();

                if (string.IsNullOrWhiteSpace(color))
                {
                    return false;
                }

                if (_ola!.CmpColor(x, y, color, color) == 0)
                {
                    return false;
                }
            }

            WriteLog($"找色: 多点特征 -> 匹配 -> 点击指定位置({targetX},{targetY})");
            await OL_LeftClick(targetX, targetY, offset);
            await SmartSleep(delay);
            return true;
        }

        /// <summary>
        /// 在指定区域内通过字库查找文字，找到后点击识别到的文字坐标。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>x1</term><description>查找区域左上角 X 坐标。</description></item>
        /// <item><term>y1</term><description>查找区域左上角 Y 坐标。</description></item>
        /// <item><term>x2</term><description>查找区域右下角 X 坐标。</description></item>
        /// <item><term>y2</term><description>查找区域右下角 Y 坐标。</description></item>
        /// <item><term>text</term><description>要查找的文字内容。</description></item>
        /// <item><term>color</term><description>文字颜色或偏色配置，例如："ada187-101010"。</description></item>
        /// <item><term>delay</term><description>点击完成后的等待时间，默认 1000 毫秒，即 1 秒。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// if (await _worker.OL_FindStr(0, 0, 960, 540, "确定", "ffffff"))
        /// {
        ///     continue;
        /// }
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>这个重载会点击识别到的文字位置。</description></item>
        /// <item><description>文字位置不固定时，优先使用这个方法。</description></item>
        /// <item><description>默认使用字库文件："无尽黑暗.txt"。</description></item>
        /// <item><description>找到文字并完成点击返回 true，未找到文字返回 false。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合文字位置不固定，但需要点击文字本身的场景。</description></item>
        /// <item><description>例如点击任务名称、菜单文字、NPC 名称、列表项文字。</description></item>
        /// <item><description>如果文字只是判断条件，实际点击位置固定，使用带 clickX、clickY 的重载。</description></item>
        /// <item><description>如果界面文字颜色变化明显，需要调整 color 参数或使用更宽的偏色范围。</description></item>
        /// <item><description>查找文字区域应尽量缩小，避免识别到相同文字导致误点。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到文字并完成点击返回 true；未找到文字返回 false。</returns>
        public async Task<bool> OL_FindStr(
            int x1,
            int y1,
            int x2,
            int y2,
            string text,
            string color,
            int delay = DefaultClickDelay)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(color))
            {
                return false;
            }

            int x, y;
            if (_ola!.FindStr(x1, y1, x2, y2, text, color, DefaultDictName, DefaultTextSimilarity, out x, out y) != -1)
            {
                WriteLog($"找字: {text} -> 找到({x},{y}) -> 点击文字位置");
                await OL_LeftClick(x, y);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在指定区域内通过字库查找文字，找到后点击指定坐标。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>x1</term><description>查找区域左上角 X 坐标。</description></item>
        /// <item><term>y1</term><description>查找区域左上角 Y 坐标。</description></item>
        /// <item><term>x2</term><description>查找区域右下角 X 坐标。</description></item>
        /// <item><term>y2</term><description>查找区域右下角 Y 坐标。</description></item>
        /// <item><term>text</term><description>要查找的文字内容。</description></item>
        /// <item><term>color</term><description>文字颜色或偏色配置，例如："ada187-101010"。</description></item>
        /// <item><term>clickX</term><description>找到文字后要点击的指定 X 坐标。</description></item>
        /// <item><term>clickY</term><description>找到文字后要点击的指定 Y 坐标。</description></item>
        /// <item><term>delay</term><description>点击完成后的等待时间，默认 1000 毫秒，即 1 秒。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// if (await _worker.OL_FindStr(78, 36, 91, 49, "等级达到30", "ada187-101010", 871, 79))
        /// {
        ///     break;
        /// }
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>这个重载不会点击文字本身，而是点击 clickX,clickY。</description></item>
        /// <item><description>文字只是判断依据，实际点击位置固定时，使用这个方法。</description></item>
        /// <item><description>默认 delay = 1000，即点击后等待 1 秒。</description></item>
        /// <item><description>默认使用字库文件："无尽黑暗.txt"。</description></item>
        /// <item><description>找到文字并完成点击返回 true，未找到文字返回 false。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合用文字判断页面状态，但实际点击固定按钮位置的场景。</description></item>
        /// <item><description>例如识别到“等级达到30”后点击右上角关闭按钮。</description></item>
        /// <item><description>适合文字本身不可点击，或者点击文字位置不稳定的情况。</description></item>
        /// <item><description>如果文字位置就是要点击的位置，使用不带 clickX、clickY 的重载。</description></item>
        /// <item><description>如果文字识别经常误判，应缩小识别区域或提高字库匹配相似度。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>找到文字并完成指定坐标点击返回 true；未找到文字返回 false。</returns>
        public async Task<bool> OL_FindStr(
            int x1,
            int y1,
            int x2,
            int y2,
            string text,
            string color,
            int clickX,
            int clickY,
            int delay = DefaultClickDelay)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(color))
            {
                return false;
            }

            int x, y;
            if (_ola!.FindStr(x1, y1, x2, y2, text, color, DefaultDictName, DefaultTextSimilarity, out x, out y) != -1)
            {
                WriteLog($"找字: {text} -> 找到({x},{y}) -> 点击指定位置({clickX},{clickY})");
                await OL_LeftClick(clickX, clickY);
                await SmartSleep(delay);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 在指定区域内通过字库 OCR 识别文字，并返回识别结果。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>x1</term><description>识别区域左上角 X 坐标。</description></item>
        /// <item><term>y1</term><description>识别区域左上角 Y 坐标。</description></item>
        /// <item><term>x2</term><description>识别区域右下角 X 坐标。</description></item>
        /// <item><term>y2</term><description>识别区域右下角 Y 坐标。</description></item>
        /// <item><term>color</term><description>文字颜色或偏色配置，例如："ffffff-101010"。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// string ocrText = _worker.OL_OcrFromDict(50, 200, 350, 600, "ffffff-101010");
        ///
        /// if (ocrText.Contains("支线"))
        /// {
        ///     await _worker.OL_LeftClick(100, 250, 15);
        /// }
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>这个方法只识别文字，不会自动点击。</description></item>
        /// <item><description>默认使用字库文件："无尽黑暗.txt"。</description></item>
        /// <item><description>识别成功返回文字内容，识别失败返回空字符串。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合只读取文字内容，不立即点击的场景。</description></item>
        /// <item><description>例如判断任务列表、状态提示、背包数量、页面标题。</description></item>
        /// <item><description>适合需要根据识别结果再决定下一步操作的逻辑。</description></item>
        /// <item><description>如果识别后要立即点击文字，使用 OL_FindStr。</description></item>
        /// <item><description>OCR 区域应尽量小，区域越大越容易识别到无关文字。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>识别到的文字；如果没有识别到，则返回空字符串。</returns>
        public string OL_OcrFromDict(int x1, int y1, int x2, int y2, string color)
        {
            if (string.IsNullOrWhiteSpace(color))
            {
                return "";
            }

            string text = _ola!.OcrFromDict(x1, y1, x2, y2, color, DefaultDictName, DefaultTextSimilarity);
            return text ?? "";
        }

        /// <summary>
        /// 移动鼠标到指定坐标附近，并执行一次左键点击。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>x</term><description>目标 X 坐标。</description></item>
        /// <item><term>y</term><description>目标 Y 坐标。</description></item>
        /// <item><term>range</term><description>点击随机偏移范围，默认 5。实际点击会在目标坐标 ±range 范围内随机浮动。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// await _worker.OL_LeftClick(500, 300);
        /// </code>
        ///
        /// <para>不使用随机偏移：</para>
        /// <code>
        /// await _worker.OL_LeftClick(500, 300, 0);
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>内部执行顺序：MoveTo → LeftDown → LeftUp。</description></item>
        /// <item><description>默认 range = 5，即点击位置会有小范围随机偏移。</description></item>
        /// <item><description>传入 range = 0 时，表示固定点击 x,y 坐标。</description></item>
        /// <item><description>该方法没有成功失败返回值，只负责执行点击动作。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合已经确定点击坐标后，单独执行点击动作。</description></item>
        /// <item><description>通常由 OL_CmpColor、OL_FindStr、OL_FindWindowsFromPath 等封装方法内部调用。</description></item>
        /// <item><description>手动点击固定按钮时可以直接使用。</description></item>
        /// <item><description>默认带随机偏移，适合减少固定坐标点击痕迹。</description></item>
        /// <item><description>如果需要精准点击某个像素点，range 传 0。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>异步点击任务。</returns>
        public async Task OL_LeftClick(int x, int y, int range = DefaultClickOffset)
        {
            LastActionTime = DateTime.Now;

            int safeRange = Math.Max(0, range);
            int rndX = x + _rnd.Next(-safeRange, safeRange + 1);
            int rndY = y + _rnd.Next(-safeRange, safeRange + 1);

            _ola!.MoveTo(rndX, rndY);
            await Task.Delay(_rnd.Next(30, 100), _currentToken);
            _ola.LeftDown();
            await Task.Delay(_rnd.Next(50, 200), _currentToken);
            _ola.LeftUp();
        }

        /// <summary>
        /// 智能等待指定毫秒数，等待前后会检查暂停、停止、取消状态。
        /// </summary>
        /// <remarks>
        /// <para>参数说明：</para>
        /// <list type="table">
        /// <item><term>ms</term><description>等待时间，单位毫秒。1000 表示等待 1 秒。</description></item>
        /// </list>
        ///
        /// <para>默认写法：</para>
        /// <code>
        /// if (!await _worker.SmartSleep(1000))
        /// {
        ///     return;
        /// }
        /// </code>
        ///
        /// <para>说明：</para>
        /// <list type="bullet">
        /// <item><description>等待前会检查任务是否被停止或取消。</description></item>
        /// <item><description>等待期间支持取消。</description></item>
        /// <item><description>等待后会再次检查任务是否被停止或取消。</description></item>
        /// <item><description>正常等待完成返回 true；任务被停止、取消或中断返回 false。</description></item>
        /// </list>
        ///
        /// <para>使用场景建议：</para>
        /// <list type="bullet">
        /// <item><description>适合替代普通 Task.Delay，用于脚本动作后的等待。</description></item>
        /// <item><description>支持暂停、停止、取消状态检查，比直接 Task.Delay 更适合任务脚本。</description></item>
        /// <item><description>点击、找图、找字、切换页面后建议使用 SmartSleep。</description></item>
        /// <item><description>短等待用于界面响应，长等待用于加载、传送、战斗、动画过渡。</description></item>
        /// <item><description>不要在循环里无脑长时间等待，优先用短等待配合状态判断。</description></item>
        /// </list>
        /// </remarks>
        /// <returns>正常等待完成返回 true；任务被取消、停止或中断返回 false。</returns>
        public async Task<bool> SmartSleep(int ms)
        {
            try
            {
                if (await CheckLoopStateAsync())
                {
                    return false;
                }

                await Task.Delay(ms, _currentToken);

                if (await CheckLoopStateAsync())
                {
                    return false;
                }

                return true;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }

        #endregion

        #region 内部辅助方法

        public void EnsureGameRunning()
        {
            if (!EmulatorName.Contains("雷电"))
            {
                return;
            }

            try
            {
                string indexStr = "0";

                if (EmulatorName.Contains("-"))
                {
                    indexStr = EmulatorName.Split('-')[1];
                }

                string cmdExe = Path.Combine(EmulatorBasePath, "ldconsole.exe");

                if (!File.Exists(cmdExe))
                {
                    WriteLog("未找到 ldconsole.exe");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = cmdExe,
                    Arguments = $"launchex --index {indexStr} --packagename {PackageName}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                WriteLog($"正在拉起游戏: {PackageName}");
            }
            catch (Exception ex)
            {
                WriteLog($"启动指令失败: {ex.Message}");
            }
        }

        private async Task<bool> CheckLoopStateAsync()
        {
            if (_currentToken.IsCancellationRequested)
            {
                return true;
            }

            await CheckPauseStateAsync();
            return RunState == WorkerState.Stopped;
        }

        private async Task CheckPauseStateAsync()
        {
            bool wasPaused = false;

            while (RunState == WorkerState.Paused)
            {
                wasPaused = true;
                _currentToken.ThrowIfCancellationRequested();
                await Task.Delay(500, _currentToken);
            }

            if (RunState == WorkerState.Resuming)
            {
                RunState = WorkerState.Running;
            }

            if (wasPaused)
            {
                UpdateStatus("运行中", CurrentBindHwnd.ToString());
                LastActionTime = DateTime.Now;
            }

            _currentToken.ThrowIfCancellationRequested();
        }

        private long FindWindowWithPlugin()
        {
            if (_ola is null)
            {
                return 0;
            }

            long hwnd = _ola.FindWindow(EmulatorClass, EmulatorName);

            if (hwnd == 0)
            {
                hwnd = _ola.FindWindow(EmulatorClass, EmulatorName + "(64)");
            }

            if (hwnd == 0 && EmulatorName.EndsWith("-0"))
            {
                string altName = EmulatorName.Replace("-0", "");
                hwnd = _ola.FindWindow(EmulatorClass, altName);

                if (hwnd == 0)
                {
                    hwnd = _ola.FindWindow(EmulatorClass, altName + "(64)");
                }
            }

            return hwnd;
        }

        private bool LaunchEmulator()
        {
            try
            {
                string cmdExe = "";
                string args = "";
                string indexStr = "0";

                if (EmulatorName.Contains("-"))
                {
                    indexStr = EmulatorName.Split('-')[^1];
                }

                if (EmulatorName.Contains("雷电"))
                {
                    cmdExe = Path.Combine(EmulatorBasePath, "ldconsole.exe");
                    args = $"launchex --index {indexStr} --packagename {PackageName}";
                }
                else if (EmulatorName.Contains("MuMu"))
                {
                    string shellPath = Path.Combine(Directory.GetParent(EmulatorBasePath)?.FullName ?? "", "shell");
                    cmdExe = Path.Combine(shellPath, "MuMuManager.exe");

                    if (!File.Exists(cmdExe))
                    {
                        cmdExe = Path.Combine(EmulatorBasePath, "MuMuManager.exe");
                    }

                    args = $"player launch {indexStr}";
                }

                if (!File.Exists(cmdExe))
                {
                    WriteLog($"启动文件不存在: {cmdExe}");
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = cmdExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                return true;
            }
            catch (Exception ex)
            {
                WriteLog($"启动模拟器异常: {ex.Message}");
                return false;
            }
        }

        private void CloseEmulator()
        {
            try
            {
                string cmdExe = "";
                string args = "";
                string indexStr = "0";

                if (EmulatorName.Contains("-"))
                {
                    indexStr = EmulatorName.Split('-')[^1];
                }

                if (EmulatorName.Contains("雷电"))
                {
                    cmdExe = Path.Combine(EmulatorBasePath, "ldconsole.exe");
                    args = $"quit --index {indexStr}";
                }
                else if (EmulatorName.Contains("MuMu"))
                {
                    string[] possiblePaths =
                    {
                        Path.Combine(EmulatorBasePath, "shell", "MuMuManager.exe"),
                        Path.Combine(EmulatorBasePath, "MuMuManager.exe"),
                        Path.Combine(EmulatorBasePath, "nx_main", "MuMuManager.exe")
                    };

                    foreach (var p in possiblePaths)
                    {
                        if (File.Exists(p))
                        {
                            cmdExe = p;
                            args = $"api -v {indexStr} shutdown_player";
                            break;
                        }
                    }
                }

                if (File.Exists(cmdExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = cmdExe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    WriteLog($"关闭文件不存在: {cmdExe}");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"关闭模拟器异常: {ex.Message}");
            }
        }

        private void LogError(string msg)
        {
            WriteLog(msg);
            UpdateStatus("错误", "0");
            UpdateException(msg);
        }

        private void WriteLog(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            LogCallback?.Invoke(line);
            Debug.WriteLine(line);
        }

        private void UpdateStatus(string status, string hwnd)
        {
            if (_lastStatusMsg != status)
            {
                _lastStatusMsg = status;
                StatusCallback?.Invoke(RowIndex, status, hwnd);
            }
        }

        private void UpdateException(string msg)
        {
            if (_lastExceptionMsg != msg)
            {
                _lastExceptionMsg = msg;
                ExceptionCallback?.Invoke(RowIndex, msg);
            }
        }

        private void Cleanup()
        {
            try
            {
                if (_ola != null)
                {
                    _ola.UnBindWindow();
                    _ola.ReleaseObj();
                    _ola = null;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"释放插件异常: {ex.Message}");
            }

            if (RunState == WorkerState.Stopped)
            {
                if (!_keepUnfinishedStatus)
                {
                    UpdateStatus("已停止", "0");
                }

                UpdateException("");
            }
        }

        #endregion
    }
}
