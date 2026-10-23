// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// tests/PCBooster.Tests/AssemblyInfo.cs
// 测试程序集配置：被测代码大量使用静态状态，禁止并行以免相互干扰
// ============================================================
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
