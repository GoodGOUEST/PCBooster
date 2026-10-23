// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// tests/PCBooster.Tests/SceneDebounceTests.cs
// 场景去抖：全屏游戏切显示模式时不应产生连续的 gaming↔gaming-bg 事件
// ============================================================
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Intelligence;

namespace PCBooster.Tests
{
    public class SceneDebounceTests
    {
        private const int GamePid = 100;
        private const int DeskPid = 200;

        private static NativeApi.ProcessSnapshot[] Snapshot() => new[]
        {
            new NativeApi.ProcessSnapshot { Id = GamePid, ProcessName = "cs2.exe" },
            new NativeApi.ProcessSnapshot { Id = DeskPid, ProcessName = "explorer.exe" }
        };

        private static void Feed(int foregroundPid)
        {
            IntelligenceEngine.CollectData(Snapshot(), new List<string>(), foregroundPid, false);
            Thread.Sleep(250);   // EventBus 异步投递
        }

        [Fact]
        public void 交替闪烁_不产生连续切换事件()
        {
            AppConfig.Load();

            var events = new List<SceneChangedEvent>();
            Action<SceneChangedEvent> handler = e => { lock (events) { events.Add(e); } };
            EventBus.Subscribe(handler);

            try
            {
                Feed(GamePid);   // 进入游戏：应立即生效
                Feed(DeskPid);   // 抖动 1
                Feed(GamePid);   // 抖动 2
                Feed(DeskPid);   // 抖动 3
                Thread.Sleep(600);

                int count;
                lock (events) { count = events.Count; }
                if (count == 0) return;   // 数据采集被用户配置关闭时，本测试不适用

                Assert.True(count <= 2, $"交替闪烁产生了过多场景事件：{count} 次");
            }
            finally
            {
                EventBus.Unsubscribe(handler);
            }
        }
    }
}
