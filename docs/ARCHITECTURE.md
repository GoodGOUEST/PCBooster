# 架构说明

## 分层

```
Infrastructure (0)   ← Execution (1) ← Core / Intelligence (2) ← UI (3)
```

只允许**向下依赖**。反向依赖会被 `scripts/audit`（见下）记为 violation，基线是 10 条既有欠账，不允许新增。

| 层 | 目录 | 职责 |
|---|---|---|
| Infrastructure | `Infrastructure/Common|Native|Cache|Profiling` | 配置、日志、事件总线、进程匹配、Win32 P/Invoke、注册表/电源、CPU 拓扑与亲和性 |
| Execution | `Execution/Power|Memory|Jobs|Rules|Preload|Work` | 真正动手的模块：电源方案、内存回收、Job 限速、规则应用、预加载、日常负载保护 |
| Core | `Core/` | 调度协调器（双心跳）、降级引擎、决策留痕、模块健康、前台监听 |
| Intelligence | `Intelligence/` | 采集、场景识别、分类模型、预测、反馈闭环、学习数据持久化 |
| UI | `UI/` | WinForms 面板（总览/游戏/进程/规则/设置/决策日志）+ 自绘控件 |

## 运行时骨架

### 双速心跳

- **快心跳（2s）**：刷新系统指标快照 → 场景兜底检测 → 内存回收 / 预加载 → 生命周期事件（启动/退出/预测命中）→ 采集
- **慢心跳（10s）**：降级等级评估（带滞回）→ 电源唤醒条件 → 规则应用 → AutoJob 限速 → 指令集/核心隔离
- 两者都通过 `SafeTimer` 托管，异常记日志并计入模块健康度，不会变成未观察的 Task 异常。

### 场景 → 策略 → 执行

```
DataCollector.DetectScene  →  ConfirmScene（去抖）  →  SceneChangedEvent
        ↓
IntelligenceEngine.GetStrategy(进程名)  →  Strategy{BoostPriority, CoreIsolation, LockFrequency, ...}
        ↓
Execution: 电源档 / 亲和性 / Job / 工作集 / 预加载
```

`DetectScene` 的规则：游戏进程在场 → 前台是游戏则 `gaming`、否则 `gaming-bg`；其余场景以前台程序为准；前台是桌面时用"在场程序"兜底。

## 四条"写路径"与统一闸门

所有对其它进程/系统的写操作只走这四条路径，且都要先过闸门：

| 写路径 | 位置 | 闸门 |
|---|---|---|
| 优先级 | `ProcessSafeHelper.SetPriority` | `IsProtected` |
| 亲和性 | `ProcessSafeHelper.SetAffinity` / `AffinityResolver` | `IsProtected` + 拓扑就绪 |
| Job 限速 | `AutoJobManager.GetTargets` | `IsSystemProcess` + `HasVisibleWindow` + `IsUserFacing` + `WorkloadGuard` |
| 工作集 | `MemoryReclaimer.GetCandidates` | `IsSystemProcess` + `IsProtected` + `WorkloadGuard` + `HasVisibleWindow` |

`ProcessMatcher` 里集中维护几组名单：系统进程、**显示链路**（NVIDIA/AMD/Intel 显卡服务）、**存储缓存链路**（PrimoCache/FlyDisk）、安全与更新链路（Defender/TiWorker）、反作弊（`IsProtected`）。新增名单请只改这里，不要在调用点硬编码。

## 电源与频率

- `NativeApi` 负责所有 `PowerWrite*/PowerSetActiveScheme`；**全部幂等**：记录已写入的停泊值与"游戏电源档指纹"，值不变就不写、不重新激活方案
- `FrequencyManager` 是唯一的状态机：`LockMaxFrequency` / `RestoreAutoFrequency`，带场景稳定确认（3s）、转换间隔（15s）、离开游戏去抖（20s）与安全网
- 安全网只判断"场景是否仍是游戏 / 被锁进程是否存活"，**不使用时间超时**（用超时会导致长时间游戏中途掉回平衡方案）

## 关键常量

集中在 `Infrastructure/Common/Constants.cs`，并按注释分组（心跳/预加载/内存回收/Job/指令集/日常保护/看门狗）。新增魔法数字请先具名化放进这里。

## 审计与探针（仓库内的验证方式）

```powershell
# 在仓库根目录执行（脚本用 process.cwd() 作为根路径）
node scripts/parse_cs3.mjs      # 产出 cs_parse.json
node scripts/audit_report.mjs   # 产出 audit_report.txt（已 gitignore）
```

基线（不允许退化）：`cycles=2`、`violations=10`、`possibly_dead` 仅剩 P/Invoke 结构体字段、`braces_ok == files`、`bom=0`。

行为验证走独立的探针工程（不放在主工程里）：逻辑探针（豁免名单/去抖/掩码）、窗口判定探针、跨核延迟与吞吐探针等，思路见 `CONTRIBUTING.md` 的"改执行层"一节。
