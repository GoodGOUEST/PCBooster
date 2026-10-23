// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// GlobalUsings.cs
// 全局 using 单一事实源策略：仅注入无冲突系统命名空间
// 项目命名空间一律显式 using（防跨层歧义 / Timer 歧义）
// ============================================================

global using System;
global using System.Collections.Generic;
global using System.Linq;

// ── 禁止全局注入（显式为单一事实源）──
// System.Threading / System.Windows.Forms ← Timer 歧义
// System.Drawing / InteropServices        ← 分层显式
// PCBooster.*                             ← 项目命名空间显式
