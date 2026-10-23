// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// tests/PCBooster.Tests/WorkloadGuardTests.cs
// 日常负载保护：编译 / 办公 / 浏览器 三类豁免窗口
// ============================================================
using PCBooster.Execution.Work;
using PCBooster.Infrastructure.Native;

namespace PCBooster.Tests
{
    public class WorkloadGuardTests
    {
        private static NativeApi.ProcessSnapshot Snap(int id, string name)
            => new NativeApi.ProcessSnapshot { Id = id, ProcessName = name };

        [Fact]
        public void 空闲时_不做任何豁免()
        {
            WorkloadGuard.Reset();
            Assert.False(WorkloadGuard.IsTrimExempt(1, "notepad.exe"));
            Assert.False(WorkloadGuard.IsJobExempt(1, "notepad.exe"));
        }

        [Fact]
        public void 编译中_工具链免限速免修剪()
        {
            WorkloadGuard.Reset();
            WorkloadGuard.Refresh(new[] { Snap(9001, "MSBuild.exe") }, 9001);

            Assert.True(WorkloadGuard.IsBuilding(DateTime.Now));
            Assert.True(WorkloadGuard.IsTrimExempt(9001, "MSBuild.exe"));
            Assert.True(WorkloadGuard.IsJobExempt(9001, "MSBuild.exe"));
            Assert.False(WorkloadGuard.IsTrimExempt(2, "notepad.exe"), "编译期间不应豁免无关进程");
        }

        [Fact]
        public void 办公前台_办公家族免限速且提优先级()
        {
            WorkloadGuard.Reset();
            WorkloadGuard.OnForeground(0, "WINWORD.EXE");

            Assert.True(WorkloadGuard.IsJobExempt(3, "EXCEL.EXE"));
            Assert.True(WorkloadGuard.ShouldBoostForeground("WINWORD.EXE"));
            Assert.False(WorkloadGuard.ShouldBoostForeground("notepad.exe"));
        }

        [Fact]
        public void 浏览器活跃期_不修剪后台标签页()
        {
            WorkloadGuard.Reset();
            WorkloadGuard.OnForeground(5, "chrome.exe");
            Assert.True(WorkloadGuard.IsTrimExempt(6, "chrome.exe"));

            WorkloadGuard.Reset();
            Assert.False(WorkloadGuard.IsTrimExempt(6, "chrome.exe"), "Reset 后应恢复为可修剪");
        }

        [Fact]
        public void Reset_清空全部状态()
        {
            WorkloadGuard.OnForeground(5, "chrome.exe");
            WorkloadGuard.Refresh(new[] { Snap(9002, "cl.exe") }, 9002);
            WorkloadGuard.Reset();

            Assert.False(WorkloadGuard.IsBuilding(DateTime.Now));
            Assert.False(WorkloadGuard.IsTrimExempt(6, "chrome.exe"));
            Assert.False(WorkloadGuard.IsTrimExempt(9002, "cl.exe"));
        }
    }
}
