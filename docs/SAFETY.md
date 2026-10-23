# 安全设计：为什么这类工具会让游戏黑屏，以及我们怎么避免

本文记录一次真实的线上事故与由此建立的全部约束。**修改执行层之前请务必读完。**

## 事故：游戏中途卡住、黑屏约 4 分钟

### 现象
游戏运行中画面卡住、黑屏，随后自行恢复。系统事件日志里**没有** TDR（显示驱动重置）、没有蓝屏、没有睡眠唤醒记录 —— 属于整机级 stall。

### 根因（从日志里能直接看到）
1. **电源方案在 9 秒内被重新激活三次**（`恢复 → 锁频 → 恢复 → 锁频`）。
   每次 "游戏电源档" 是 5 次 `PowerWriteACValueIndex` + 1 次 `PowerSetActiveScheme`；
   每次恢复是 10+ 次写入 + 切方案。这样密集的电源状态切换会打断 CPU 电源状态与 DXGI 提交节奏。
2. **显卡链路进程被反复施加 Job CPU 限速**（`NVDisplay.Container` / `RadeonSoftware` / `AMDRSServ` / `AMDRSSrcExt`）。
   这些进程一旦被限速或绑核，呈现代理会超时。
3. **工作集下限无去重**：前台事件每次都会再调一次 `SetProcessWorkingSetSizeEx`，一整局游戏调用上百次。
4. **亲和性反复重写**：前台钩子最快 200ms 一次，每次都写 `SetProcessAffinityMask`。
5. **场景判定自身在抖动**：全屏游戏切换显示模式时，场景在 `gaming ↔ gaming-bg` 之间秒级来回翻。

## 由此建立的六条不变量

改动执行层前，逐条对照：

### 1. 幂等（Idempotent）
写系统状态前先判断"值是否已经是目标值"。
- 停泊值：`_parkingWritten[scheme] == target` → 直接返回
- 游戏电源档：`_lastGamingProfile == fingerprint` → 直接返回
- 还原：`!_settingsDirty` → 不重写 10+ 项电源项

### 2. 去抖（Debounce / Hysteresis）
- 场景切换：非游戏转换需连续 2 次采样一致（约 4 秒）；回到已确认场景直接清候选
- 进入游戏：立即生效，但**锁频**要等场景稳定 3 秒
- 离开游戏：20 秒宽限期，回到游戏则取消恢复

### 3. 全局限速（Rate limit）
任意方向的"锁频/恢复"之间至少 **15 秒**；被节流时写 DEBUG 并**改期重试**（不能因为节流而永久停在锁定状态）。

### 4. 去重 + 冷却（Per-PID）
- 工作集下限：按 PID 记录，已锁过直接返回
- 亲和性：同进程同掩码 30 秒内不重写
- 后台核心罩：全快照扫描至少间隔 5 秒

### 5. 豁免（Exemptions）
统一由 `ProcessMatcher` 判定，四条写路径都要过：
- `IsProtected`（反作弊）—— 绝对不碰
- `IsSystemProcess` = 系统进程 + **显示链路** + **存储缓存链路** + 安全/更新链路
- `HasVisibleWindow`（有可见顶层窗口的用户程序，按尺寸判定，不依赖标题）—— 不限速、不 Trim
- `WorkloadGuard`（编译工具链、办公保护窗口、浏览器活跃期）

### 6. 可观测（Observability）
- `StallWatchdog`：快心跳中断 >15 秒 → WARN + 现场（场景/前台/内存/CPU/降级等级）
- 决策日志：每次场景切换、指令集优化、Job 施加/失败都有记录
- 电源失败、Job 失败都进入冷却，避免刷屏

## 还原策略

| 被改的东西 | 何时还原 | 怎么还原 |
|---|---|---|
| 进程优先级 / 亲和性 | 退出、场景离开、一键还原 | `ProcessSafeHelper.RestoreAll()`（校验进程名+启动时间，防 PID 复用） |
| 电源方案 / 停泊 / 睿频档 | 退出游戏（去抖后）、退出程序 | `NativeApi.RestorePowerSettings()`（写回基线） |
| 工作集下限 | 关闭开关 / 一键还原 / 退出 | `WorkingSetLock.ReleaseAllFloors()` |
| Job 限速 | 负载回落 / 切换前台 / 退出 | `AutoJobManager.ReleaseAllLimits()` |

## 自测清单（改动执行层必须过）

1. 进游戏 → 只出现 **1 次** `频率锁定`、**1 次** `游戏电源档`
2. 快速 Alt-Tab 5 次 → **不出现**反复的 `频率恢复自动` / `频率锁定`
3. 显卡链路进程（NVDisplay/Radeon/AMDRSServ）**没有** Job 记录
4. PrimoCache / FlyDisk 进程**没有** Trim 或绑核记录
5. 退出程序后 `powercfg /getactivescheme` 回到用户原方案，`powercfg /q` 的处理器项与改动前一致
6. 观察日志里是否有 `心跳中断` 记录

## 已知边界

- 无内核驱动 → 无法做真正的 I/O 优先级、无法直接控制频率（由固件 Turbo 决定）
- 未签名 exe 会被 SmartScreen 拦截；杀软可能对"改优先级/亲和性/Job"行为敏感（这是本类工具的固有摩擦，请用白名单）
