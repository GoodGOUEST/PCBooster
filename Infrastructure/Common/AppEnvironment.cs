// ============================================================
// PC Booster v3.6.8
// Infrastructure/Common/AppEnvironment.cs
// 运行环境上下文（权限探测单一事实源）
// 3.6.7 修复: 原 Program.HasAdminPrivileges 被 Execution/Infrastructure
//             反向引用，形成 Program ↔ 下层循环依赖；改为基建层持有状态，
//             Program 仅做写入与转发。
// ============================================================
namespace PCBooster.Infrastructure.Common
{
    /// <summary>进程运行环境（权限/架构等只读事实）</summary>
    public static class AppEnvironment
    {
        private static volatile bool _isElevated;

        /// <summary>★ 3.6.8: 进程启动时刻（UI“运行时间”用；原实现显示的是系统开机时长）</summary>
        public static DateTime StartedAt { get; } = DateTime.Now;

        /// <summary>当前进程是否以管理员权限运行（启动期探测一次）</summary>
        public static bool IsElevated => _isElevated;

        /// <summary>探测并记录提权状态（幂等，可重复调用）</summary>
        public static bool Detect()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                _isElevated = principal.IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isElevated = false;
            }
            return _isElevated;
        }

        /// <summary>显式设置（测试或外部注入用）</summary>
        public static void Set(bool isElevated) => _isElevated = isElevated;
    }
}
