// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// tests/PCBooster.Tests/ProcessMatcherTests.cs
// 进程分类判据：这组判据是四条写路径的闸门，必须有回归测试
// ============================================================
using PCBooster.Infrastructure.Common;

namespace PCBooster.Tests
{
    public class ProcessMatcherTests
    {
        // ── 系统 / 显示链路 / 存储链路：必须被豁免 ──
        [Theory]
        [InlineData("MsMpEng.exe", true)]
        [InlineData("TiWorker.exe", true)]
        [InlineData("TrustedInstaller.exe", true)]
        [InlineData("NVDisplay.Container.exe", true)]
        [InlineData("nvcontainer.exe", true)]
        [InlineData("AMDRSServ.exe", true)]
        [InlineData("primoccsvc.exe", true)]
        [InlineData("FlyDisk.exe", true)]
        [InlineData("TextInputHost.exe", true)]
        [InlineData("explorer.exe", true)]
        [InlineData("notepad.exe", false)]
        [InlineData("chrome.exe", false)]
        [InlineData("", false)]
        public void IsSystemProcess_分类正确(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsSystemProcess(name));

        [Theory]
        [InlineData("PRIMOCCSVC.EXE", true)]     // 大小写不敏感
        [InlineData("PRIMOCACHE.EXE", true)]
        [InlineData("rxfcv.exe", true)]
        [InlineData("chrome.exe", false)]
        public void IsStorageChain_识别缓存链路(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsStorageChain(name));

        [Theory]
        [InlineData("NVDisplay.Container.exe", true)]
        [InlineData("nvcontainer.exe", true)]
        [InlineData("RadeonSoftware.exe", true)]
        [InlineData("notepad.exe", false)]
        public void IsDisplayChain_识别显卡链路(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsDisplayChain(name));

        // ── 编译工具链：短名必须精确匹配，不能把 clion 当 cl ──
        [Theory]
        [InlineData("MSBuild.exe", true)]
        [InlineData("cl.exe", true)]
        [InlineData("link.exe", true)]
        [InlineData("ninja.exe", true)]
        [InlineData("dotnet.exe", true)]
        [InlineData("rustc.exe", true)]
        [InlineData("clion64.exe", false)]
        [InlineData("clickup.exe", false)]
        [InlineData("notepad.exe", false)]
        [InlineData("", false)]
        public void IsBuildTool_短名不误判(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsBuildTool(name));

        // ── 反作弊：任何写操作都不能碰 ──
        [Theory]
        [InlineData("EasyAntiCheat.exe", true)]
        [InlineData("vgc.exe", true)]
        [InlineData("BEService.exe", true)]
        [InlineData("notepad.exe", false)]
        public void IsProtected_识别反作弊(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsProtected(name));

        // ── 其余分类 ──
        [Theory]
        [InlineData("cs2.exe", true)]
        [InlineData("notepad.exe", false)]
        public void IsGame_识别游戏(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsGame(name));

        [Theory]
        [InlineData("quark.exe", true)]
        [InlineData("chrome.exe", true)]
        [InlineData("msedgewebview2.exe", true)]
        [InlineData("notepad.exe", false)]
        public void IsBrowserFamily_识别国内浏览器与WebView2(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsBrowserFamily(name));

        [Theory]
        [InlineData("WINWORD.EXE", true)]
        [InlineData("wps.exe", true)]
        [InlineData("notepad.exe", false)]
        public void IsOffice_识别办公(string name, bool expected)
            => Assert.Equal(expected, ProcessMatcher.IsOffice(name));
    }
}
