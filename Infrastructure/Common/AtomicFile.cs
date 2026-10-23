// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/AtomicFile.cs
// 原子写工具（tmp + Move 覆盖，#7 工具批单一实现）
// ============================================================
using System.IO;
namespace PCBooster.Infrastructure.Common
{
/// <summary>原子文件写（先 tmp 后替换，防半写损坏）</summary>
public static class AtomicFile
{
    public static bool Write(string path, string content)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true); // net8 原子替换
            return true;
        }
        catch (Exception ex) { AppLogger.Error($"原子写失败 {path}: {ex.Message}"); return false; }
    }
}
}
