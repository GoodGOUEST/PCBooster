// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/ProcessMatcher.cs
// ============================================================
namespace PCBooster.Infrastructure.Common
{
/// <summary>进程类型匹配器</summary>
public static class ProcessMatcher
{
private static readonly HashSet<string> _system = new(StringComparer.OrdinalIgnoreCase)
{
    "system", "svchost", "csrss", "wininit", "winlogon", "services",
    "lsass", "smss", "dwm", "explorer", "runtimebroker", "taskhostw",
    "conhost", "sihost", "fontdrvhost", "spoolsv", "audiodg",
    // 输入法宿主/剪贴板等 shell 组件（实测被当作后台进程做过 Job 限速）
    "textinputhost", "ctfmon", "inputpersonalization", "lockapp", "startmenuexperiencehost",
    "shellexperiencehost",
    // 安全/系统维护链（原列表缺失）——Defender 扫描与 Windows 更新
    // 一直是“隐形”内存大户与 CPU 大户，之前会被 AutoJob 限速、被工作集修剪，
    // 结果是杀毒扫描被拖慢，而我们要的正好相反（让路，别互相拉扯）。
    "msmpeng", "nissrv", "mpcmdrun", "mssense", "securityhealthservice",
    "tiworker", "trustedinstaller", "wuauclt", "usoclient", "smartscreen",
    "searchindexer", "searchprotocolhost", "searchfilterhost"
};
    // 关键词仅作兜底（原含 steam/epic/game/play，会把启动器与 GameBar 误判成游戏）
    private static readonly string[] _gameHints =
    {
        "valorant", "fortnite", "cs2", "csgo", "dota", "leagueclient",
        "overwatch", "minecraft", "eldenring", "cyberpunk", "witcher",
        "gta5", "apex", "pubg", "genshin", "honkai", "wuthering"
    };
    // 反作弊/保护进程——任何写操作（优先级/亲和性/Job/工作集）都必须回避
    private static readonly string[] _protectedHints =
    {
        "easyanticheat", "beservice", "battleye", "vgc", "vgk",
        "anticheatexpert", "tenprotect", "ace-game", "sguard", "tp3helper", "anticheat",
        // 补齐常见反作弊/保护组件
        "mhyprot", "nprotect", "gameguard", "xigncode", "faceit", "esea",
        "punkbuster", "equ8"
    };
    private static readonly string[] _browserHints =
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        // 国内常用浏览器（原列表全部匹配不到）
        "360se", "360chrome", "qqbrowser", "sogouexplorer", "ucbrowser", "liebao", "iexplore",
        // 实测日志里夸克（quark）被当成后台进程施加 Job 限速——补入名单
        "quark", "2345explorer", "maxthon"
    };
    private static readonly string[] _devHints =
    {
        "devenv", "code", "rider", "idea", "eclipse", "clion",
        "pycharm", "androidstudio", "unity", "unreal"
    };
    private static readonly string[] _officeHints =
    {
        "winword", "excel", "powerpnt", "wps", "et", "wpp",
        "notion", "obsidian", "onenote"
    };
    private static readonly string[] _designHints =
    {
        "photoshop", "illustrator", "figma", "sketch", "blender",
        "3dsmax", "maya", "cinema4d"
    };
    private static readonly string[] _videoHints =
    {
        "premiere", "aftereffects", "davinci", "resolve",
        "vegas", "capcut", "handbrake"
    };
    private static readonly string[] _meetingHints =
    {
        "teams", "zoom", "tencentmeeting", "wemeet", "voov",
        "dingtalk", "feishu", "skype", "webex"
    };
    // 显卡/显示链路进程 —— 绝不做任何写操作（Job 限速 / 亲和性 / 工作集 / preload）。
    // 实测日志：AutoJob 反复对 NVDisplay.Container、AMDRSServ、RadeonSoftware、AMDRSSrcExt
    // 尝试 Job CPU 限制，后台核心罩还把 40+ 个进程（含它们）压到后台核。
    // 这类进程一旦被节流或绑核，DXGI 呈现会超时 → 画面闪一下/黑屏（同时 nvlddmkm 153）。
    private static readonly HashSet<string> _displayChain = new(StringComparer.OrdinalIgnoreCase)
    {
        // NVIDIA
        "nvdisplay.container", "nvcontainer", "nvsphelper64", "nvtopps", "nvbroadcast",
        "nvidia web helper", "nvidia share", "nvtelemetry",
        // AMD
        "amdrsserv", "radeonsoftware", "amdrssrcext", "amdfendrsr", "amdow",
        "atiesrxx", "atieclxx", "amdloggingserver", "amdwddmg", "amdkmdap",
        // Intel
        "igfxem", "igfxext", "igfxtray", "igfxhk",
        // 补充: NVIDIA 其余常驻组件（本机实际运行 nvcontainer/NVDisplay.Container，
        // 下面这些在其它驱动版本/装完完整套件后会出现）
        "nvbackend", "nvprofileupdater64", "nvprofileupdater", "nvsmartmax",
        "nvcplui", "nvtelemetrycontainer", "nvsvc", "nvidia overlay", "nvidia app"
    };
    // 存储/缓存链路——PrimoCache（Romex）等磁盘缓存驱动的服务进程。
    // 本机实测进程名是 primoccsvc（服务 PrimoCacheSvc，驱动 rxfcv.sys）。
    // 这类进程本来就是靠刷写/预读缓存吃饭的，任何 Job CPU 限速、工作集 Trim、
    // 后台核绑定都会直接拖慢整机磁盘响应。
    private static readonly string[] _storageChainHints =
    {
        "primocache", "primoccsvc", "fancyccv", "rxfcv", "romex",
        "primoramdisk",
        // FlyDisk（开源 HDD 块级读缓存，单进程形态）——L1 是它自己的
        // 非托管内存池（4KB 块 / 64MB slab），被 Trim 就等于把缓存页赶回备用列表。
        "flydisk"
    };
    public static bool IsSystemProcess(string name)
    {
        var n = StringHelper.NormalizeProcessName(name);
        return _system.Contains(n) || IsDisplayChain(name) || IsStorageChain(name);
    }
    /// <summary>显卡/显示链路进程（独立判据，供需要显式说明的调用点使用）</summary>
    public static bool IsDisplayChain(string name)
        => _displayChain.Contains(StringHelper.NormalizeProcessName(name));
    /// <summary>存储/缓存链路（PrimoCache 等）——与显示链路同等对待，一律不写</summary>
    public static bool IsStorageChain(string name)
        => !string.IsNullOrEmpty(name) && ContainsAny(name, _storageChainHints);
    public static bool IsGame(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // 目录优先（Steam 清单识别，覆盖率高），关键词兜底
        if (GameCatalog.Contains(name)) return true;
        return ContainsAny(name, _gameHints);
    }
    /// <summary>反作弊/保护进程（禁止任何写操作）</summary>
    public static bool IsProtected(string name) => ContainsAny(name, _protectedHints);
    public static bool IsBrowser(string name) => ContainsAny(name, _browserHints);
    public static bool IsDevTool(string name) => ContainsAny(name, _devHints);
    public static bool IsOffice(string name) => ContainsAny(name, _officeHints);
    public static bool IsDesignTool(string name) => ContainsAny(name, _designHints);
    public static bool IsVideoEditor(string name) => ContainsAny(name, _videoHints);
    public static bool IsMeetingTool(string name) => ContainsAny(name, _meetingHints);

        // 本地影音播放器（只用于“关停泊”，不锁频）
        private static readonly string[] _playerHints =
        {
            "potplayer", "potplayermini", "mpv", "vlc", "mpc-hc", "mpc-be", "kmplayer",
            "qqplayer", "bilibili", "iqiyi", "youku", "plex", "kodi"
        };
        public static bool IsVideoPlayer(string name) => ContainsAny(name, _playerHints);
        // 编译工具链（msbuild/cl/ninja/rustc…）——编译期绝不能限速/修剪/降优先级。
        // 短名必须走“精确名”匹配：cl / link / rc / cc 这类 2~4 字母若用 ContainsAny
        // 会把 clion、clickup、rcman 之类误判成编译器。
        private static readonly HashSet<string> _buildExact = new(StringComparer.OrdinalIgnoreCase)
        {
            "cl", "clang", "clang-cl", "link", "lib", "lld", "lld-link", "ml", "ml64",
            "rc", "csc", "vbc", "fsc", "cc1", "cc1plus", "cc", "c++", "gcc", "g++",
            "javac", "make", "ninja", "cmake", "msbuild", "mvn", "gradle", "cargo",
            "rustc", "dotnet", "xcodebuild"
        };
        private static readonly string[] _buildHints =
        {
            "msbuild", "rustc", "cargo", "clang", "ninja", "cmake", "gradle"
        };
        public static bool IsBuildTool(string name)
        {
            var n = StringHelper.NormalizeProcessName(name);
            if (string.IsNullOrEmpty(n)) return false;
            return _buildExact.Contains(n) || ContainsAny(n, _buildHints);
        }
        /// <summary>浏览器全家桶（主进程 + renderer/gpu/WebView2 子进程同名）</summary>
        public static bool IsBrowserFamily(string name)
        {
            if (IsBrowser(name)) return true;
            var n = StringHelper.NormalizeProcessName(name);
            return n.Contains("webview2") || n.Contains("crashpad_handler");
        }
    private static bool ContainsAny(string name, string[] hints)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name.ToLowerInvariant();
        return hints.Any(h => n.Contains(h));
    }
}
}
