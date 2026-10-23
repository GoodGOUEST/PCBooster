// ============================================================
// PC Booster v3.6.8
// Infrastructure/Common/AppPaths.cs
// 路径中心（B模式单文件兼容：可写数据走 %AppData%）
// ============================================================

using System.IO;

namespace PCBooster.Infrastructure.Common
{
    /// <summary>路径中心（区分只读 exe 目录与可写数据目录）</summary>
    public static class AppPaths
    {
        /// <summary>用户可写数据根目录</summary>
        public static string DataRoot { get; }

        public static string ConfigFile { get; }
        public static string LogDir { get; }

        /// <summary>当前 exe 完整路径（单文件下 Assembly.Location 为空，用 ProcessPath）</summary>
        public static string ExePath { get; }

        static AppPaths()
        {
            DataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PCBooster");
            ConfigFile = Path.Combine(DataRoot, Constants.FILE_CONFIG);
            LogDir = Path.Combine(DataRoot, Constants.DIR_LOGS);
            ExePath = Environment.ProcessPath ?? "";

            try { Directory.CreateDirectory(DataRoot); Directory.CreateDirectory(LogDir); } catch { }
        }
    }
}