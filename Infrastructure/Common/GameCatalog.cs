// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/GameCatalog.cs
// 游戏识别目录（新增；增补：用户手动指定目录）
// 缘起: 原 ProcessMatcher.IsGame 只有 12 个关键词硬匹配，
// eldenring.exe / gta5.exe / apex_legends.exe 等全部识别不到，
// 导致整套“游戏场景”策略不触发。
// 方案: 三路来源合并为 exe名 → 游戏名 索引
// 1) Steam 库解析（libraryfolders.vdf + appmanifest_*.acf），缓存 24h
// 2) 用户手动指定安装目录（Epic / GOG / 独立游戏 / 绿色版 / 自编译）
// 3) 排除列表（误识别可移除，重新扫描不会复活）
// 手动项与排除项独立落盘 usergames.json，不随 games.json 缓存过期丢失。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PCBooster.Infrastructure.Common
{
    /// <summary>游戏目录：exe 名 → 游戏名（线程安全，读多写少）</summary>
    public static class GameCatalog
    {
        private const string CacheFile = "games.json";
        private const string UserFile = "usergames.json";
        private const int MaxExePerGame = 6;
        private const int MaxManualScan = 40;       // 单目录最多展示的候选 exe
        private const int ManualScanDepth = 3;      // 手动目录向下枚举层数
        private static readonly Dictionary<string, string> _exeToGame = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _userDirs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _userExes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();
        /// <summary>存档 JSON 选项（缩进 + 中文不转义，便于用户查看/手改）</summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        private static volatile bool _scanning;
        private static DateTime _lastScan = DateTime.MinValue;

        static GameCatalog()
        {
            // 手动项先于缓存/扫描载入——同名时用户配置优先级最高
            LoadUserData();
        }

        /// <summary>已识别 exe 数量</summary>
        public static int Count { get { lock (_lock) return _exeToGame.Count; } }

        /// <summary>手动添加的 exe 数量</summary>
        public static int UserCount { get { lock (_lock) return _userExes.Count; } }

        /// <summary>是否正在扫描（UI 进度提示用）</summary>
        public static bool IsScanning => _scanning;

        /// <summary>是否已完成一次扫描（手动添加也算就绪）</summary>
        public static bool IsReady
        {
            get { lock (_lock) return _lastScan != DateTime.MinValue || _userExes.Count > 0; }
        }

        /// <summary>缓存快照（供 UI 展示；来源用 IsUserMarked 判断）</summary>
        public static List<(string exe, string game)> Snapshot(int max = 200)
        {
            lock (_lock)
                return _exeToGame
                    .Where(kv => !_excluded.Contains(kv.Key))
                    .Take(max)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
        }

        /// <summary>exe 是否为已知游戏</summary>
        public static bool Contains(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;
            var key = StripExt(exeName);
            lock (_lock) return _exeToGame.ContainsKey(key);
        }

        /// <summary>是否由用户手动添加</summary>
        public static bool IsUserMarked(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;
            var key = StripExt(exeName);
            lock (_lock) return _userExes.Contains(key);
        }

        /// <summary>用户手动标记为游戏（exe 名或游戏名）</summary>
        public static void Mark(string exeOrName, string gameName = null)
        {
            if (string.IsNullOrWhiteSpace(exeOrName)) return;
            AddUserGames("", new[] { exeOrName },
                string.IsNullOrWhiteSpace(gameName) ? exeOrName : gameName);
        }

        /// <summary>取消标记（写入排除表，重新扫描不会复活）</summary>
        public static void Unmark(string exeName) => RemoveEntries(new[] { exeName });

        // ═══════════ 手动目录（增补）═══════════

        /// <summary>枚举目录内的候选可执行文件（按体积降序，主程序通常最大）</summary>
        public static List<string> FindExecutables(string folder)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return list;
            foreach (var exe in EnumerateExes(folder, ManualScanDepth))
            {
                var fileName = Path.GetFileNameWithoutExtension(exe);
                // 手动添加时保留 launcher（不少游戏主入口就叫 Launcher.exe）；
                // 自动 Steam 扫描仍需过滤，避免多个游戏抢同一个 launcher.exe 名字。
                if (IsHelperExe(exe) && !fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fileName.Length < 3) continue;
                list.Add(exe);
            }
            list.Sort((a, b) => SizeOf(b).CompareTo(SizeOf(a)));
            if (list.Count > MaxManualScan) list.RemoveRange(MaxManualScan, list.Count - MaxManualScan);
            return list;

            static long SizeOf(string path)
            {
                try { return new FileInfo(path).Length; } catch { return 0; }
            }
        }

        /// <summary>把指定目录中的 exe 登记为游戏（持久化到 usergames.json）</summary>
        /// <param name="folder">游戏安装目录（可空，仅供展示/回溯）</param>
        /// <param name="exeFiles">用户勾选的 exe（完整路径或文件名均可）</param>
        /// <param name="gameName">游戏名（空则用 exe 名）</param>
        /// <returns>实际登记的数量</returns>
        public static int AddUserGames(string folder, IEnumerable<string> exeFiles, string gameName)
        {
            if (exeFiles == null) return 0;
            var name = string.IsNullOrWhiteSpace(gameName) ? "" : gameName.Trim();
            var dir = string.IsNullOrWhiteSpace(folder) ? "" : folder.Trim();
            int added = 0;
            lock (_lock)
            {
                foreach (var raw in exeFiles)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string file;
                    try { file = Path.GetFileName(raw.Trim()); } catch { continue; }
                    var key = StripExt(file);
                    if (key.Length < 2) continue;
                    _excluded.Remove(key);
                    _exeToGame[key] = name.Length > 0 ? name : key;
                    _userExes.Add(key);
                    if (dir.Length > 0) _userDirs[key] = dir;
                    added++;
                }
            }
            if (added > 0)
            {
                SaveUserData();
                SaveCache();
                AppLogger.Info($"🎮 手动加入 {added} 个游戏可执行文件（{(name.Length > 0 ? name : "未命名")}）");
            }
            return added;
        }

        /// <summary>批量移除游戏标记（手动项删除、Steam 项进排除表）</summary>
        public static int RemoveEntries(IEnumerable<string> exeNames)
        {
            if (exeNames == null) return 0;
            int removed = 0;
            lock (_lock)
            {
                foreach (var raw in exeNames)
                {
                    var key = StripExt(raw ?? "");
                    if (key.Length == 0) continue;
                    if (_exeToGame.Remove(key)) removed++;
                    _userExes.Remove(key);
                    _userDirs.Remove(key);
                    _excluded.Add(key);
                }
            }
            if (removed > 0) { SaveUserData(); SaveCache(); }
            return removed;
        }

        /// <summary>启动扫描（后台执行，幂等，24h 内不重复）</summary>
        public static void StartScan(bool force = false)
        {
            if (_scanning) return;
            if (!force && (DateTime.Now - _lastScan).TotalHours < 24 && Count > 0) return;
            _scanning = true;
            Task.Run(() =>
            {
                try
                {
                    LoadCache();
                    ScanSteamLibraries();
                    _lastScan = DateTime.Now;
                    SaveCache();
                    AppLogger.Info($"🎮 游戏目录: 识别 {Count} 个可执行文件（手动 {UserCount}）");
                }
                catch (Exception ex) { AppLogger.Warning($"游戏库扫描失败: {ex.Message}"); }
                finally { _scanning = false; }
            });
        }

        // ═══════════ 内部实现 ═══════════

        private static string StripExt(string name)
        {
            var n = (name ?? "").Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            return n;
        }

        private static string CachePath
            => Path.Combine(AppPaths.DataRoot, CacheFile);

        private static string UserPath
            => Path.Combine(AppPaths.DataRoot, UserFile);

        private static void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(CachePath, Encoding.UTF8));
                if (data == null) return;
                lock (_lock)
                    foreach (var kv in data)
                        if (!_exeToGame.ContainsKey(kv.Key) && !_excluded.Contains(kv.Key))
                            _exeToGame[kv.Key] = kv.Value;
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void SaveCache()
        {
            try
            {
                Dictionary<string, string> copy;
                lock (_lock)
                    copy = _exeToGame
                        .Where(kv => !_excluded.Contains(kv.Key))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                Directory.CreateDirectory(AppPaths.DataRoot);
                AtomicFile.Write(CachePath, JsonSerializer.Serialize(copy, _jsonOptions));
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ── 用户配置（手动项 + 排除项）──

        private static void LoadUserData()
        {
            try
            {
                if (!File.Exists(UserPath)) return;
                var data = JsonSerializer.Deserialize<GameCatalogUserData>(
                    File.ReadAllText(UserPath, Encoding.UTF8));
                if (data == null) return;
                if (data.Added != null)
                    foreach (var g in data.Added)
                    {
                        if (g == null || string.IsNullOrWhiteSpace(g.Exe)) continue;
                        var key = StripExt(g.Exe);
                        if (key.Length == 0) continue;
                        _userExes.Add(key);
                        _userDirs[key] = g.Dir ?? "";
                        _exeToGame[key] = string.IsNullOrWhiteSpace(g.Name) ? key : g.Name;
                    }
                if (data.Excluded != null)
                    foreach (var e in data.Excluded)
                    {
                        if (string.IsNullOrWhiteSpace(e)) continue;
                        _excluded.Add(StripExt(e));
                    }
            }
            catch (Exception ex) { AppLogger.Debug($"游戏目录读取失败: {ex.Message}"); }
        }

        private static void SaveUserData()
        {
            try
            {
                var data = new GameCatalogUserData();
                lock (_lock)
                {
                    foreach (var key in _userExes)
                        data.Added.Add(new GameCatalogUserGame
                        {
                            Exe = key,
                            Dir = _userDirs.TryGetValue(key, out var d) ? d : "",
                            Name = _exeToGame.TryGetValue(key, out var n) ? n : key
                        });
                    data.Excluded.AddRange(_excluded);
                }
                Directory.CreateDirectory(AppPaths.DataRoot);
                AtomicFile.Write(UserPath, JsonSerializer.Serialize(data, _jsonOptions));
            }
            catch (Exception ex) { AppLogger.Debug($"游戏目录保存失败: {ex.Message}"); }
        }

        private static void ScanSteamLibraries()
        {
            var steam = ReadSteamPath();
            if (string.IsNullOrEmpty(steam)) return;
            var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
            foreach (var vdf in new[]
            {
                Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                Path.Combine(steam, "config", "libraryfolders.vdf")
            })
                CollectLibraryPaths(vdf, libs);

            foreach (var lib in libs)
            {
                // 单个库不可读（离线盘/权限）不应中断整个扫描
                try
                {
                    var appsDir = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(appsDir)) continue;
                    foreach (var manifest in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
                        IndexManifest(appsDir, manifest);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Steam 库跳过 {lib}: {ex.Message}");
                }
            }
        }

        private static string ReadSteamPath()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var p = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p!.Replace('/', '\\');
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            return "";
        }

        private static void CollectLibraryPaths(string vdfPath, HashSet<string> libs)
        {
            try
            {
                if (!File.Exists(vdfPath)) return;
                var text = File.ReadAllText(vdfPath);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                    if (Directory.Exists(p)) libs.Add(p);
                }
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void IndexManifest(string appsDir, string manifest)
        {
            try
            {
                var text = File.ReadAllText(manifest);
                var name = MatchVal(text, "name");
                var dir = MatchVal(text, "installdir");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dir)) return;
                var gameDir = Path.Combine(appsDir, "common", dir);
                if (!Directory.Exists(gameDir)) return;
                int count = 0;
                foreach (var exe in EnumerateExes(gameDir, 3))
                {
                    if (IsHelperExe(exe)) continue;
                    var exeName = Path.GetFileNameWithoutExtension(exe);
                    if (exeName.Length < 3) continue;
                    lock (_lock)
                    {
                        if (!_excluded.Contains(exeName) && !_exeToGame.ContainsKey(exeName))
                            _exeToGame[exeName] = name;
                    }
                    if (++count >= MaxExePerGame) break;
                }
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static string MatchVal(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>有限深度枚举 exe（避免全盘递归）</summary>
        private static IEnumerable<string> EnumerateExes(string root, int maxDepth)
        {
            var queue = new Queue<(string dir, int depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                string[] files;
                try { files = Directory.GetFiles(dir, "*.exe"); } catch { files = Array.Empty<string>(); }
                foreach (var f in files) yield return f;
                if (depth >= maxDepth) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { subs = Array.Empty<string>(); }
                foreach (var s in subs)
                {
                    var leaf = Path.GetFileName(s);
                    if (leaf.Equals("redist", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Equals("support", StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Enqueue((s, depth + 1));
                }
            }
        }

        /// <summary>过滤安装器/崩溃上报/反作弊等辅助 exe</summary>
        private static bool IsHelperExe(string path)
        {
            var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            string[] bad =
            {
                "unins", "setup", "install", "crash", "report", "launcher",
                "redist", "dxsetup", "vcredist", "dotnet", "unitycrashhandler",
                "easyanticheat", "beservice", "vgc", "vgk", "anticheat",
                "ue4prereqsetup", "ue5prereqsetup", "epicwebhelper", "steamerrorreporter"
            };
            foreach (var b in bad) if (n.Contains(b)) return true;
            return false;
        }
    }

    /// <summary>usergames.json 结构（手动项 + 排除项）</summary>
    internal sealed class GameCatalogUserData
    {
        public List<GameCatalogUserGame> Added { get; set; } = new();
        public List<string> Excluded { get; set; } = new();
    }

    /// <summary>单条手动游戏记录</summary>
    internal sealed class GameCatalogUserGame
    {
        public string Dir { get; set; } = "";
        public string Exe { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
