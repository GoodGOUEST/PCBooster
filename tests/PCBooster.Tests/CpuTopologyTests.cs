// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// tests/PCBooster.Tests/CpuTopologyTests.cs
// CPU 拓扑与掩码不变量：游戏掩码与后台掩码必须互斥、并集恒定、同环开关不扩大核数
// ============================================================
using PCBooster.Infrastructure.Profiling;

namespace PCBooster.Tests
{
    public class CpuTopologyTests
    {
        private static int PopCount(ulong v)
        {
            int n = 0;
            while (v != 0) { n += (int)(v & 1UL); v >>= 1; }
            return n;
        }

        [Fact]
        public void Detect_可用且拓扑自洽()
        {
            CpuTopologyEx.Detect();
            Assert.True(CpuTopologyEx.IsReady, "拓扑检测失败（Win32 API 不可用？）");
            Assert.True(CpuTopologyEx.CoreCount > 0, "物理核数应大于 0");
            Assert.True(CpuTopologyEx.LogicalCount >= CpuTopologyEx.CoreCount, "逻辑核数不应少于物理核数");
            Assert.InRange(CpuTopologyEx.L3DomainCount, 1, 64);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void 游戏与后台掩码_互斥且并集恒定(int cores)
        {
            CpuTopologyEx.Detect();
            Assert.True(CpuTopologyEx.IsReady);

            ulong game = CpuTopologyEx.GetGameMask(cores);
            ulong bg = CpuTopologyEx.GetBackgroundMask(cores);

            Assert.NotEqual(0UL, game);
            Assert.Equal(0UL, game & bg);                                   // 互斥
            ulong full = CpuTopologyEx.GetGameMask(1) | CpuTopologyEx.GetBackgroundMask(1);
            Assert.Equal(full, game | bg);                                   // 并集不随核数变化
            Assert.True(PopCount(game) >= cores, "至少应覆盖请求的物理核数（含 SMT 线程可能更多）");
        }

        [Fact]
        public void 同环开关_不会扩大游戏核数()
        {
            CpuTopologyEx.Detect();
            if (!CpuTopologyEx.IsReady || CpuTopologyEx.CoreCount < 8) return;   // 少核机器上无意义

            ulong normal = CpuTopologyEx.GetGameMask(12, false);
            ulong sameRing = CpuTopologyEx.GetGameMask(12, true);

            Assert.NotEqual(0UL, sameRing);
            Assert.True(PopCount(sameRing) <= PopCount(normal), "同环模式不应给出更多核");
        }
    }
}
