// ============================================================
// PC Booster v3.6.8
// Core/ForegroundWatcher.cs
// 前台窗口事件钩子（取代轮询，反应 ≤50ms）
// 3.6.7: 200ms 节流具名化（FOREGROUND_THROTTLE_MS）
// ============================================================
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
namespace PCBooster.Core
{
/// <summary>前台变化事件监听</summary>
public static class ForegroundWatcher
{
private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventHook, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    private static WinEventDelegate _delegate;
    private static IntPtr _hook = IntPtr.Zero;
    private static Action<int, string> _callback;
    private static DateTime _lastFire = DateTime.MinValue;
    public static bool IsInstalled => _hook != IntPtr.Zero;
    public static void Install(Action<int, string> callback)
    {
        _callback = callback;
        _delegate = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        AppLogger.Info(_hook != IntPtr.Zero ? "🪝 前台事件钩子已安装" : "⚠️ 钩子安装失败，回退轮询");
    }
    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
    private static void OnWinEvent(IntPtr h, uint evt, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        var now = DateTime.Now;
        if ((now - _lastFire).TotalMilliseconds < Constants.FOREGROUND_THROTTLE_MS) return; // ★ 具名
        _lastFire = now;
        Task.Run(() =>
        {
            try
            {
                int pid = NativeApi.GetForegroundPid();
                if (pid <= 0) return;
                string name = ProcessSafeHelper.GetProcessName(pid);
                _callback?.Invoke(pid, name);
            }
            catch (Exception ex) { AppLogger.Debug($"前台事件异常: {ex.Message}"); }
        });
    }
}
}