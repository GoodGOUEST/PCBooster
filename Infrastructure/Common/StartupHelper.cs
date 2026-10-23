// ============================================================
// PC Booster v3.6.8 修订
// Infrastructure/Common/StartupHelper.cs
// 开机自启管理（任务计划提权方案 + 注册表降级方案）
// 机制: schtasks ONLOGON + HIGHEST 实现静默提权自启
// ============================================================

using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PCBooster.Infrastructure.Common
{
    /// <summary>开机自启管理器</summary>
    public static class StartupHelper
    {
        private const string TASK_NAME = "PCBooster_AutoStart";
        private const string RUN_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string REG_VALUE = "PCBooster";

        /// <summary>
        /// 设置开机自启
        /// 优先使用任务计划（提权），失败则降级为注册表 Run 键（标准权限）
        /// </summary>
        public static void SetLaunchAtStartup(bool enable)
        {
            string exePath = ResolveExePath();
            if (string.IsNullOrEmpty(exePath)) return;

            try
            {
                // ★ 方案 A: 任务计划程序 (schtasks)
                // /sc ONLOGON: 用户登录时触发
                // /rl HIGHEST: 以最高权限(管理员)运行，实现静默提权
                // /f: 强制覆盖已存在的任务
                string args = enable
                    ? $"/create /tn \"{TASK_NAME}\" /tr \"\\\"{exePath}\\\"\" /sc ONLOGON /rl HIGHEST /f"
                    : $"/delete /tn \"{TASK_NAME}\" /f";

                var psi = new ProcessStartInfo("schtasks.exe", args)
                {
                    UseShellExecute = true,
                    Verb = "runas", // 触发 UAC 弹窗（仅在此次操作时）
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                using var p = Process.Start(psi);
                p?.WaitForExit();
                int code = p?.ExitCode ?? -1;
                if (enable && code != 0)
                {
                    // ★ 3.6.8: 原实现不看退出码，任务创建失败也打印“已开启”
                    AppLogger.Warning($"⚠️ 任务计划创建失败(code={code})，降级为注册表自启");
                    FallbackRunKey(true);
                    return;
                }
                // 任务计划成功（或关闭操作完成），清理可能存在的旧版注册表键
                FallbackRunKey(false);
                AppLogger.Info($"✅ 开机自启(任务计划): {(enable ? "已开启" : "已关闭")}");
            }
            catch (Exception ex)
            {
                // 用户拒绝 UAC 或无权限，降级到方案 B
                AppLogger.Warning($"⚠️ 任务计划提权失败({ex.Message})，降级为注册表自启");
                FallbackRunKey(enable);
            }
        }

        /// <summary>★ 本轮新增: 自启是否指向“当前这个 exe”（任务计划或 Run 键任一匹配）</summary>
        public static bool IsLaunchAtStartup()
        {
            string exe = ResolveExePath();
            if (string.IsNullOrEmpty(exe)) return false;
            return TaskMatches(exe) || RegistryMatches(exe);
        }

        /// <summary>★ 本轮新增: 自启自愈。换了目录 / 重装 / 任务被删时，
        /// 配置仍是“开启”，用户以为会自启，实际不会。这里只补 HKCU Run 键（不弹 UAC）。</summary>
        public static void EnsureLaunchAtStartup()
        {
            if (!AppConfig.LaunchAtStartup) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (IsLaunchAtStartup()) return;
                    FallbackRunKey(true);
                    AppLogger.Info("🔧 开机自启已自愈（原任务/键失效或指向旧路径）");
                }
                catch { }
            });
        }

        private static bool RegistryMatches(string exe)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false);
                string value = key?.GetValue(REG_VALUE) as string;
                if (string.IsNullOrEmpty(value)) return false;
                return value.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TaskMatches(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TASK_NAME}\" /xml ONE")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                if (p.ExitCode != 0 || string.IsNullOrEmpty(xml)) return false;
                return xml.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>降级方案：注册表 HKCU Run 键（非提权，依赖 Program 降级逻辑）</summary>
        private static void FallbackRunKey(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = ResolveExePath();
                    key.SetValue(REG_VALUE, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(REG_VALUE, false);
                }
                AppLogger.Info($"✅ 开机自启(注册表): {(enable ? "已开启" : "已关闭")}");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"❌ 注册表自启操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析自身 exe 路径。
        /// ★ 单文件发布: Assembly.Location 恒为空字符串（IL3000），
        ///   必须用 Environment.ProcessPath / AppContext.BaseDirectory。
        /// </summary>
        private static string ResolveExePath()
        {
            var p = AppPaths.ExePath;
            if (!string.IsNullOrEmpty(p)) return p;
            try { return Path.Combine(AppContext.BaseDirectory, "PCBooster.exe"); }
            catch { return ""; }
        }
    }
}
