// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/StringHelper.cs
// ============================================================
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
namespace PCBooster.Infrastructure.Common
{
/// <summary>字符串工具</summary>
public static class StringHelper
{
    private static readonly ConcurrentDictionary<string, Regex> _wildcardCache = new();
    private const int WILDCARD_CACHE_MAX = 256;
    /// <summary>进程名归一化（去扩展名/路径，小写）</summary>
    public static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var n = name.Trim().ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n[..^4];
        int idx = n.LastIndexOf('\\'); // 
        if (idx >= 0) n = n[(idx + 1)..];
        return n;
    }
    /// <summary>通配符匹配（* ?，编译缓存）</summary>
    public static bool MatchWildcard(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        try
        {
            if (_wildcardCache.Count >= WILDCARD_CACHE_MAX) _wildcardCache.Clear();
            var regex = _wildcardCache.GetOrAdd(pattern, p =>
            {
                var expr = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return new Regex(expr, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            });
            return regex.IsMatch(input);
        }
        catch { return false; }
    }
}
}
