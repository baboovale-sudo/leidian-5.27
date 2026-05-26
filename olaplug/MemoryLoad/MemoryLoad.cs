// =============================================================================
// 内存加载模块（单文件拷贝版）
// -----------------------------------------------------------------------------
// 将本文件与 olaplug/MemoryLoad/Resources/loader_x64.enc.bin 一并拷入目标工程，
// 在 .csproj 中加入：
//   <EmbeddedResource Include="路径\loader_x64.enc.bin" />
// 无需再写 LogicalName：清单名形如「程序集默认命名空间.相对路径」，本模块按程序集名前缀 +
// 文件名片段自动匹配（见 MemoryLoadHelper.ResolveEncryptedLoaderStream）。
//
// 协议与算法对齐 MyLoadDllPortable.cpp：共享内存结构、XOR 解密、CreateThread 执行 shellcode。
// =============================================================================

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OLAPlug.MemoryLoad;

#region 可配置：XOR 密钥（与 tools/encrypt_loader_x64.py 输出一致）

/// <summary>
/// 与加密脚本共用的密钥材料；更换 loader 或密码后，运行脚本并将输出的 byte 数组粘贴到
/// <see cref="LoaderXorKey"/> 中。
/// </summary>
public static class MemoryLoadKeys
{
    /// <summary>UTF-8 密码对应的密钥字节流，长度任意；与密文逐字节 XOR（循环密钥）。</summary>
    public static ReadOnlySpan<byte> LoaderXorKey => new byte[]
    {
        0xe6, 0xac, 0xa7, 0xe6, 0x8b, 0x89, 0xe6, 0x8f, 0x92, 0xe4, 0xbb, 0xb6
    };
}

#endregion

#region 对外 API

/// <summary>
/// 在进程内分配内存、解密内置 loader shellcode、通过命名共享内存与 loader 通信，
/// 将「DLL 文件映像字节」映射为可调用模块（不落盘）。仅支持 64 位宿主（与 x64 shellcode 一致）。
/// </summary>
public static class MemoryLoadHelper
{
    /// <summary>串行化内存加载，避免多线程同时映射/执行 shellcode 导致状态错乱。</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// 嵌入资源清单名匹配片段（大小写不敏感）。默认匹配任意以程序集名为前缀且包含此片段的资源，
    /// 例如 <c>OLAMemoryLoad.olaplug.MemoryLoad.Resources.loader_x64.enc.bin</c>。
    /// 若你重命名文件，请同步修改此常量或改为更明确的子串。
    /// </summary>
    public const string EncryptedLoaderResourceHint = "loader_x64.enc";

    [ThreadStatic]
    private static uint t_lastStatus;

    /// <summary>最近一次加载流程结束时，共享内存中写入的状态码（与 shellcode 约定一致）。</summary>
    public static uint LastStatus => t_lastStatus;

    /// <summary>将 <see cref="LastStatus"/> 转为简短英文说明，便于日志。</summary>
    public static string LastStatusText => MemoryLoadProtocol.StatusToText(t_lastStatus);

    /// <summary>
    /// 将完整 DLL PE 映像载入当前进程，返回模块基址（失败为 <see cref="IntPtr.Zero"/>）。
    /// </summary>
    /// <param name="dllImage">DLL 文件的原始字节（与从磁盘读入一致）。</param>
    public static IntPtr LoadModuleFromMemory(ReadOnlySpan<byte> dllImage)
    {
        lock (Gate)
            return LoadModuleFromMemoryCore(dllImage);
    }

    /// <summary><see cref="LoadModuleFromMemory(ReadOnlySpan{byte})"/> 的数组重载。</summary>
    public static IntPtr LoadModuleFromMemory(byte[] dllImage)
    {
        ArgumentNullException.ThrowIfNull(dllImage);
        return LoadModuleFromMemory(dllImage.AsSpan());
    }

    private static IntPtr LoadModuleFromMemoryCore(ReadOnlySpan<byte> dllImage)
    {
        if (Marshal.SizeOf<IntPtr>() != 8)
            throw new PlatformNotSupportedException("MemoryLoadHelper 仅支持 64 位进程（与 x64 loader shellcode 一致）。");

        t_lastStatus = (uint)MemoryLoadProtocol.StatusPending;

        if (dllImage.IsEmpty)
        {
            t_lastStatus = (uint)MemoryLoadProtocol.StatusInvalidPayload;
            return IntPtr.Zero;
        }

        using var encStream = ResolveEncryptedLoaderStream();
        var encrypted = new byte[encStream.Length];
        if (encStream.Read(encrypted, 0, encrypted.Length) != encrypted.Length)
        {
            t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
            return IntPtr.Zero;
        }

        var shellcode = MemoryLoadShellcode.Decode(encrypted, MemoryLoadKeys.LoaderXorKey);
        if (shellcode.Length == 0)
        {
            t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
            return IntPtr.Zero;
        }

        var transferSize = (UIntPtr)(uint)dllImage.Length;
        var transferBuffer = Native.VirtualAlloc(IntPtr.Zero, transferSize,
            Native.MemCommit | Native.MemReserve, Native.PageReadWrite);
        if (transferBuffer == IntPtr.Zero)
        {
            t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
            return IntPtr.Zero;
        }

        try
        {
            CopyDllImageToUnmanaged(dllImage, transferBuffer, dllImage.Length);

            var hMap = Native.CreateFileMappingW(Native.InvalidHandleValue, IntPtr.Zero, Native.PageReadWrite,
                0, (uint)Marshal.SizeOf<MemoryLoadProtocol.SharedMemData>(), MemoryLoadProtocol.BuildSharedMemName());
            if (hMap == IntPtr.Zero)
            {
                t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
                return IntPtr.Zero;
            }

            try
            {
                var mapSize = (UIntPtr)(uint)Marshal.SizeOf<MemoryLoadProtocol.SharedMemData>();
                var sharedView = Native.MapViewOfFile(hMap, Native.FileMapAllAccess, 0, 0, mapSize);
                if (sharedView == IntPtr.Zero)
                {
                    t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
                    return IntPtr.Zero;
                }

                try
                {
                    var structSize = Marshal.SizeOf<MemoryLoadProtocol.SharedMemData>();
                    ZeroMemory(sharedView, structSize);

                    Marshal.WriteInt64(sharedView, MemoryLoadProtocol.OfsDllDataAddress, transferBuffer.ToInt64());
                    Marshal.WriteInt64(sharedView, MemoryLoadProtocol.OfsDllDataSize, dllImage.Length);
                    Marshal.WriteInt64(sharedView, MemoryLoadProtocol.OfsStatus, MemoryLoadProtocol.StatusPending);
                    Marshal.WriteInt64(sharedView, MemoryLoadProtocol.OfsModuleHandle, 0);

                    if (!ExecuteShellcodeAndWait(sharedView, shellcode))
                    {
                        t_lastStatus = (uint)Marshal.ReadInt64(sharedView, MemoryLoadProtocol.OfsStatus);
                        return IntPtr.Zero;
                    }

                    var okStatus = Marshal.ReadInt64(sharedView, MemoryLoadProtocol.OfsStatus);
                    var hModule = new IntPtr(Marshal.ReadInt64(sharedView, MemoryLoadProtocol.OfsModuleHandle));
                    t_lastStatus = (uint)okStatus;

                    if (okStatus != MemoryLoadProtocol.StatusSuccess || hModule == IntPtr.Zero)
                        return IntPtr.Zero;

                    return hModule;
                }
                finally
                {
                    Native.UnmapViewOfFile(sharedView);
                }
            }
            finally
            {
                Native.CloseHandle(hMap);
            }
        }
        finally
        {
            Native.VirtualFree(transferBuffer, UIntPtr.Zero, Native.MemRelease);
        }
    }

    /// <summary>
    /// 按「入口程序集优先、否则当前程序集」解析宿主程序集，并在其嵌入资源中查找加密 loader。
    /// 规则与 Program 中按程序集名前缀筛选 DLL 的思路一致：先限定 <c>GetName().Name</c> 前缀，
    /// 再按 <see cref="EncryptedLoaderResourceHint"/> 做子串匹配，避免写死完整 LogicalName。
    /// </summary>
    private static Stream ResolveEncryptedLoaderStream()
    {
        var candidates = new[] { Assembly.GetEntryAssembly(), Assembly.GetExecutingAssembly() }
            .Where(a => a != null)
            .Cast<Assembly>()
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("无法解析程序集：EntryAssembly 与 ExecutingAssembly 均为空。");

        foreach (var asm in candidates)
        {
            var stream = TryOpenLoaderStream(asm);
            if (stream != null)
                return stream;
        }

        var report = string.Join("; ", candidates.Select(a =>
            $"{a.GetName().Name}: [{string.Join(", ", a.GetManifestResourceNames())}]"));
        throw new InvalidOperationException(
            $"未找到嵌入的加密 loader（需资源名以程序集名为前缀且包含 \"{EncryptedLoaderResourceHint}\"）。已检查: {report}");
    }

    /// <summary>在单个程序集的清单中查找匹配的加密 loader 流。</summary>
    private static Stream? TryOpenLoaderStream(Assembly assembly)
    {
        var prefix = assembly.GetName().Name ?? "";
        var names = assembly.GetManifestResourceNames();

        // 优先：与 Program 中加载插件 DLL 相同思路——清单名以程序集名为前缀，避免误命中其它依赖里的同名资源。
        List<string> matches = names
            .Where(n =>
                prefix.Length > 0 &&
                n.StartsWith(prefix, StringComparison.Ordinal) &&
                n.Contains(EncryptedLoaderResourceHint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 回退：若宿主修改了 RootNamespace 等导致清单名前缀与 AssemblyName 不一致，则仅按片段匹配。
        if (matches.Count == 0)
        {
            matches = names
                .Where(n => n.Contains(EncryptedLoaderResourceHint, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matches.Count == 0)
            return null;

        // 多个命中时优先完整文件名，否则取第一个（确定性：按字母序）
        var pick = matches
            .OrderByDescending(n => n.EndsWith("loader_x64.enc.bin", StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.Ordinal)
            .First();

        return assembly.GetManifestResourceStream(pick);
    }

    /// <summary>在可执行内存中启动 shellcode 线程，阻塞直到线程结束，再根据共享内存判断成功与否。</summary>
    private static bool ExecuteShellcodeAndWait(IntPtr sharedData, byte[] shellcode)
    {
        var scSize = (UIntPtr)(uint)shellcode.Length;
        var execMem = Native.VirtualAlloc(IntPtr.Zero, scSize,
            Native.MemCommit | Native.MemReserve, Native.PageExecuteReadwrite);
        if (execMem == IntPtr.Zero)
        {
            t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
            return false;
        }

        try
        {
            Marshal.Copy(shellcode, 0, execMem, shellcode.Length);
            Native.FlushInstructionCache(Native.GetCurrentProcess(), execMem, scSize);

            var hThread = Native.CreateThread(IntPtr.Zero, new UIntPtr(16u * 1024 * 1024), execMem, IntPtr.Zero,
                Native.StackSizeParamIsAReservation, out _);
            if (hThread == IntPtr.Zero)
            {
                t_lastStatus = (uint)MemoryLoadProtocol.StatusLoadFailure;
                return false;
            }

            try
            {
                Native.WaitForSingleObject(hThread, Native.Infinite);
            }
            finally
            {
                Native.CloseHandle(hThread);
            }

            var status = Marshal.ReadInt64(sharedData, MemoryLoadProtocol.OfsStatus);
            var moduleHandle = new IntPtr(Marshal.ReadInt64(sharedData, MemoryLoadProtocol.OfsModuleHandle));
            return status == MemoryLoadProtocol.StatusSuccess && moduleHandle != IntPtr.Zero;
        }
        finally
        {
            Native.VirtualFree(execMem, UIntPtr.Zero, Native.MemRelease);
        }
    }

    private static void CopyDllImageToUnmanaged(ReadOnlySpan<byte> dllImage, IntPtr dest, int length)
    {
        var tmp = new byte[length];
        dllImage.CopyTo(tmp);
        Marshal.Copy(tmp, 0, dest, length);
    }

    private static void ZeroMemory(IntPtr ptr, int byteCount)
    {
        for (var i = 0; i < byteCount; i++)
            Marshal.WriteByte(ptr, i, 0);
    }

    #region 嵌入式文件资源读取
    public static byte[] LoadEmbeddedResourceAsBytes(string resourceName)
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("无法获取入口程序集，无法读取嵌入的 OLA 插件。");

        var prefix = $"{assembly.GetName().Name}";
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                        && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (names.Count == 0)
            throw new FileNotFoundException("未在程序集嵌入资源中找到 *.dll。");

        var pick = names.FirstOrDefault(x => x.Contains(resourceName, StringComparison.OrdinalIgnoreCase));
        if (pick == null)
            throw new FileNotFoundException($"未在程序集嵌入资源中找到 {resourceName}。");
        using var stream = assembly.GetManifestResourceStream(pick)
            ?? throw new InvalidOperationException($"无法打开嵌入资源: {pick}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    #endregion

    #region Shellcode 解密（与 MyLoadDllPortable.cpp DecodeLoaderShellcode 等价）

    private static class MemoryLoadShellcode
    {
        /// <summary>逐字节 XOR：<c>plain[i] = enc[i] ^ key[i % key.Length]</c>。</summary>
        internal static byte[] Decode(ReadOnlySpan<byte> encryptedBytes, ReadOnlySpan<byte> xorKey)
        {
            if (encryptedBytes.Length == 0 || xorKey.Length == 0)
                return Array.Empty<byte>();

            var decoded = new byte[encryptedBytes.Length];
            for (var i = 0; i < encryptedBytes.Length; i++)
                decoded[i] = (byte)(encryptedBytes[i] ^ xorKey[i % xorKey.Length]);

            return decoded;
        }
    }

    #endregion

    #region 共享内存协议（与 MyLoadDllPortable.cpp #pragma pack(1) 的 SharedMemData 一致）

    private static class MemoryLoadProtocol
    {
        /// <summary>
        /// 根据当前进程 PID 动态生成共享内存名称，格式：ola_mm_<pid>
        /// </summary>
        /// <param name="buf">用于接收共享内存名称的 StringBuilder</param>
        public static string BuildSharedMemName()
        {
            int pid = Process.GetCurrentProcess().Id;
            return $"Local\\ola_mm_{pid}";
        }

        /// <summary>shellcode 写入的状态码（int64 宽度与 C++ 侧一致）。</summary>
        internal const long StatusPending = 0;
        internal const long StatusSuccess = 1;
        internal const long StatusInvalidPayload = 2;
        internal const long StatusDeleteTempDllFailed = 3;
        internal const long StatusResolveLoaderFailed = 4;
        internal const long StatusLoadFailure = 5;
        internal const long StatusIdentityCheckFailed = 6;

        internal const int OfsDllDataAddress = 0;
        internal const int OfsDllDataSize = 8;
        internal const int OfsStatus = 16;
        internal const int OfsModuleHandle = 24;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct SharedMemData
        {
            internal long DllDataAddress;
            internal ulong DllDataSize;
            internal long Status;
            internal long ModuleHandle;
        }

        internal static string StatusToText(uint status) => status switch
        {
            (uint)StatusPending => "pending",
            (uint)StatusSuccess => "success",
            (uint)StatusInvalidPayload => "invalid-payload",
            (uint)StatusDeleteTempDllFailed => "delete-temp-dll-failed",
            (uint)StatusResolveLoaderFailed => "resolve-loader-failed",
            (uint)StatusLoadFailure => "load-failure",
            (uint)StatusIdentityCheckFailed => "identity-check-failed",
            _ => "unknown"
        };
    }

    #endregion

    #region 本机 API

    private static class Native
    {
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        internal const uint MemCommit = 0x1000;
        internal const uint MemReserve = 0x2000;
        internal const uint MemRelease = 0x8000;
        internal const uint PageReadWrite = 4;
        internal const uint PageExecuteReadwrite = 0x40;
        internal const uint FileMapAllAccess = 0xF001F;
        internal const uint StackSizeParamIsAReservation = 0x00010000;
        internal const uint Infinite = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes, uint flProtect,
            uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateThread(IntPtr lpThreadAttributes, UIntPtr dwStackSize,
            IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }

    #endregion
}

#endregion
