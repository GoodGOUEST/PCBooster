// ============================================================
// PC Booster v3.6.8
// UI/AppServices.cs
// UI 与宿主（Program）之间的单向桥
// 说明: 页面不直接引用 Program / MainForm，避免 UI↔Program 循环依赖；
//       Program 在启动时注入“提权重启”实现，页面只发请求，主窗体负责执行与收尾。
// ============================================================
using System;

namespace PCBooster.UI
{
    /// <summary>宿主服务注入点（仅 UI 层内部使用）</summary>
    internal static class AppServices
    {
        /// <summary>由 Program 注入：以管理员身份重启，返回 false 表示用户取消了 UAC</summary>
        public static Func<bool> RestartElevated { get; set; }

        /// <summary>页面请求“提权重启”（由主窗体订阅后执行）</summary>
        public static event Action RestartRequested;

        public static void RequestRestart() => RestartRequested?.Invoke();
    }
}
