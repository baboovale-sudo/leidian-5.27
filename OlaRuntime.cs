using OLAPlug;
using OLAPlug.MemoryLoad;

namespace OLA
{
    /// <summary>
    /// OLA 插件统一入口：将嵌入资源中的 OLAPlug_x64.dll 以内存方式加载，
    /// 业务代码只通过 Create() 创建 OLAPlugServer，避免依赖运行目录下的 OLA.dll。
    /// </summary>
    public static class OlaRuntime
    {
        private const string EmbeddedDllName = "OLAPlug_x64.dll";

        private static readonly Lazy<nint> ModuleHandle = new Lazy<nint>(() =>
        {
            byte[] dllBytes = MemoryLoadHelper.LoadEmbeddedResourceAsBytes(EmbeddedDllName);
            nint handle = MemoryLoadHelper.LoadModuleFromMemory(dllBytes);

            if (handle == nint.Zero)
            {
                throw new InvalidOperationException($"OLAPlug 内存加载失败: {MemoryLoadHelper.LastStatusText}");
            }

            return handle;
        });

        public static OLAPlugServer Create()
        {
            // ownsHandle=false：该句柄由官方内存加载器返回，不应由 FreeLibrary 释放。
            return new OLAPlugServer(ModuleHandle.Value, "", false);
        }
    }
}
