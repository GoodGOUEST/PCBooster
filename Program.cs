// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Program.cs
// 入口（单实例 + 64位断言 + 三道异常防线 + 初始化顺序）
// ============================================================
using System.Threading;
using System.Windows.Forms;
using PCBooster.Core;
using PCBooster.Execution.Power;
using PCBooster.Execution.Rules;
using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Profiling;
using PCBooster.Intelligence;
using PCBooster.UI;
namespace PCBooster
{
    internal static class Program
    {
        private static Mutex _mutex;
        /// <summary>Q2修复: 权限探测（转发至 AppEnvironment，下层不再反向依赖 Program）</summary>
        public static bool HasAdminPrivileges => AppEnvironment.IsElevated;

        /// <summary>以管理员身份重启（设置页“以管理员重启”按钮调用）。
        /// 先释放单实例互斥锁，否则新进程会以为“已在运行”而直接退出。</summary>
        public static bool RestartElevated()
        {
            try
            {
                // 单文件发布下 Assembly.Location 为空，用 ProcessPath（回退 ExecutablePath）
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) exe = System.Windows.Forms.Application.ExecutablePath;
                if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return false;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
                };
                try { _mutex?.ReleaseMutex(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                try { _mutex?.Dispose(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                _mutex = null;
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"提权重启失败: {ex.Message}");
                return false;
            }
        }
        [STAThread]
        static void Main()
        {
            // UI 重写: 必须在任何窗口创建前声明 DPI 模式（配合 app.manifest），
            // 否则高 DPI 下整窗被系统位图拉伸，会出现模糊与重影。
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            // ① 单实例
            _mutex = new Mutex(true, Constants.MUTEX_NAME, out bool createdNew);
            if (!createdNew)
            {
                // 第二次双击原来只弹“已在运行”，看不到窗口；
                // 现在直接唤醒已运行实例（托盘隐藏时也能拉回来）。
                if (!SignalExistingInstance())
                    MessageBox.Show("PC Booster 已在运行中（窗口可能在托盘里）。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // ② 64位断言（v3.9.8）
            if (!Environment.Is64BitProcess)
            {
                MessageBox.Show("PC Booster 仅支持 64 位操作系统/进程。", "不支持",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try
            {
                // ③ 三道异常防线
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                    AppLogger.Error($"💥 UI线程异常: {e.Exception}");
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try
                    {
                        // 崩溃日志写 %AppData%（exe 可能位于 Program Files，不可写）
                        System.IO.Directory.CreateDirectory(AppPaths.LogDir);
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(AppPaths.LogDir, "crash.log"),
                            $"[{DateTime.Now}] {e.ExceptionObject}\n");
                    }
                    catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                };
                // CS0103修复: 全限定 TaskScheduler
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    AppLogger.Error($"💥 异步未观察异常: {e.Exception}");
                    e.SetObserved();
                };
                // ④ 日志最先
                AppLogger.Initialize();
                AppLogger.Info($"🚀 PC Booster v{Constants.VERSION} 启动中...");
                // 打通基建层故障通道（Execution 报故障，Core 记健康）
                ModuleHealthMonitor.EnableInfrastructureReporting();
                // ⑤ 配置
                AppConfig.Load();
                // 开机自启自愈（换目录 / 重装后旧任务指向旧 exe，用户以为会自启）
                StartupHelper.EnsureLaunchAtStartup();
                // ⑥ Q2修复: 权限探测（状态归 Infrastructure.AppEnvironment 持有）
                if (!AppEnvironment.Detect())
                    AppLogger.Warning("⚠️ 非管理员运行: Job/内存锁定已全局降级");
                // ⑦ P4修复: 初始化顺序（规则引擎先于智能引擎）
                ProcessRuleEngine.Initialize();
                IntelligenceEngine.Initialize();
                // ⑧ 执行器与基础设施
                PowerSaver.Initialize();
                FrequencyManager.Initialize();
                WorkingSetLock.Initialize();
                CacheAwareScheduler.Initialize();
                // ⑨ 缓存预热（异步）
                _ = CacheManager.Warmup();
                // 后台扫描 Steam 游戏库（识别游戏 exe，24h 缓存）
                GameCatalog.StartScan();
                // ⑩ 调度器（内部单点安装前台钩子，L1）
                SchedulerCoordinator.Initialize();
                // ⑪ UI
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 崩溃兜底 —— 程序挂了也要把电源方案 / Job / 亲和性还原，
                // 而不是让“高性能方案 + 限速”留在用户机器上。
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    AppLogger.Error("界面线程未处理异常: " + e.Exception);
                    MessageBox.Show("界面出现未预期的错误，已记入日志（日志页可查看）。",
                        "PC Booster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try
                    {
                        AppLogger.Error("未处理异常，先还原所有优化: " + e.ExceptionObject);
                        Core.BoosterActions.RestoreAll();
                    }
                    catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                };
                // UI 不直接引用 Program：提权重启由宿主注入
                UI.AppServices.RestartElevated = RestartElevated;
                var mainForm = new MainForm();
                StartActivationListener(mainForm.RestoreFromAnotherInstance);
                Application.Run(mainForm);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"启动异常: {ex}");
                MessageBox.Show($"启动失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Shutdown();
            }
        }
        private static void Shutdown()
        {
            AppLogger.Info("🛑 PC Booster 关闭中...");
            try
            {
                SchedulerCoordinator.Shutdown();
                IntelligenceEngine.Shutdown();
                CacheAwareScheduler.Shutdown();
                // 退出全量还原（原来退出后优先级/亲和性/电源方案仍处于被改状态）
                try
                {
                    Execution.Jobs.JobObjectApi.ReleaseAll();
                    Execution.Jobs.AutoJobManager.ReleaseAllLimits();
                    Infrastructure.Profiling.InstructionSetOptimizer.ResetState();
                    int restored = ProcessSafeHelper.RestoreAll();
                    FrequencyManager.RestoreAutoFrequency();
                    Infrastructure.Native.NativeApi.RestorePowerSettings();
                    if (restored > 0) AppLogger.Info($"♻️ 已还原 {restored} 个进程的优先级/亲和性");
                }
                catch (Exception ex) { AppLogger.Error($"还原失败: {ex.Message}"); }
                _activateEvent?.Dispose();
                AppConfig.ForceSave();
                EventBus.Clear();
                AppLogger.Shutdown();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"关闭异常: {ex.Message}");
            }
            finally
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
        }
        // ── 单实例唤醒（第二个实例 → 第一个实例）──────────────
        private const string ACTIVATE_EVENT = "PCBooster_Activate_Show";
        private static System.Threading.EventWaitHandle _activateEvent;

        /// <summary>第二个实例：通知已运行实例把窗口拉到前台（失败返回 false）</summary>
        private static bool SignalExistingInstance()
        {
            try
            {
                using var evt = System.Threading.EventWaitHandle.OpenExisting(ACTIVATE_EVENT);
                evt.Set();
                return true;
            }
            catch { return false; }
        }

        /// <summary>第一个实例：后台线程等待唤醒信号（托盘隐藏时也能拉回窗口）</summary>
        private static void StartActivationListener(Action onActivate)
        {
            try
            {
                _activateEvent = new System.Threading.EventWaitHandle(
                    false, System.Threading.EventResetMode.AutoReset, ACTIVATE_EVENT);
                var thread = new System.Threading.Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            if (!_activateEvent.WaitOne(2000)) continue;
                            onActivate?.Invoke();
                        }
                        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                    }
                })
                {
                    IsBackground = true,
                    Name = "PCBooster.Activate"
                };
                thread.Start();
            }
            catch (Exception ex) { AppLogger.Debug($"唤醒监听启动失败: {ex.Message}"); }
        }
    }
}
