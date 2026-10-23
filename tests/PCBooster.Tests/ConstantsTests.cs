// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// tests/PCBooster.Tests/ConstantsTests.cs
// 关键常量的不变式：这些值直接决定系统写入频率，改错会造成抖动/卡顿
// ============================================================
using System.Text.RegularExpressions;
using PCBooster.Infrastructure.Common;

namespace PCBooster.Tests
{
    public class ConstantsTests
    {
        [Fact]
        public void 版本号为三段式()
            => Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), Constants.VERSION);

        [Fact]
        public void 心跳间隔_快慢分明且为正()
        {
            Assert.True(Constants.FAST_HEARTBEAT_MS > 0);
            Assert.True(Constants.SLOW_HEARTBEAT_MS > 0);
            Assert.True(Constants.SLOW_HEARTBEAT_MS >= Constants.FAST_HEARTBEAT_MS, "慢心跳不应快于快心跳");
        }

        [Fact]
        public void 防抖常量_在合理区间()
        {
            Assert.True(Constants.SCENE_CONFIRM_SAMPLES >= 2, "场景确认至少需要 2 次采样");
            Assert.True(Constants.FREQ_LOCK_STABLE_SECONDS >= 1, "锁频前必须留稳定窗口");
            Assert.True(Constants.FREQ_TRANSITION_MIN_INTERVAL_SECONDS >= Constants.FREQ_LOCK_STABLE_SECONDS,
                "转场最小间隔不应小于稳定窗口");
            Assert.True(Constants.STALL_WARN_GAP_SECONDS >= 5, "看门狗阈值过小会误报");
        }

        [Fact]
        public void 负载保护常量_在合理区间()
        {
            Assert.True(Constants.WORKGUARD_BUILD_TAIL_SECONDS >= 5, "编译收尾窗口过短会截断链接/落盘");
            Assert.True(Constants.WORKGUARD_BROWSER_QUIET_MINUTES >= 1);
            Assert.True(Constants.WORKGUARD_OFFICE_PROTECT_MINUTES >= 1);
            Assert.InRange(Constants.WORKGUARD_MAX_ENTRIES, 16, 4096);
        }
    }
}
