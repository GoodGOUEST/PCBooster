# 更新日志

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循语义化版本。

## [3.9.8] - 2026-10-23

本轮为**生产化整理**，不改变调度行为的语义，目标是可运行、可维护、可追责。

### 工程化
- **版本统一到 3.9.8**：Constants.VERSION、PCBooster.csproj（含 FileVersion / Product / Company / Copyright）、全部 65 个源文件头、窗口与托盘元数据
- **全部源文件加 SPDX 标识**：SPDX-License-Identifier: GPL-3.0-or-later
- **注释规范化**：移除 316 处开发轮次标记（★ 3.6.8、★ R17 等），只保留“为什么这么做”的解释；修复清理过程留下的 15 处残渣
- **行尾统一 LF**，新增 .editorconfig（缩进、大括号、using 顺序、var 使用等）
- **csproj 生产化**：Deterministic=true、TreatWarningsAsErrors=true、补齐程序集元数据、排除 	ests/ 参与主项目编译

### 可靠性
- **零空 catch**：58 处 catch {} 全部补上上下文日志（日志子系统自身的 4 处改为显式注释，避免递归）
- **新增生产门禁** scripts/gate.ps1：构建（警告即错误）→ 单元测试 → 空 catch 计数 → 分层审计基线，CI 已接入

### 测试
- 新增 	ests/PCBooster.Tests（xUnit，59 项，禁止并行以适配静态状态）：
  - ProcessMatcher 分类判据（系统/显卡/存储链路、编译工具短名不误判、反作弊、浏览器、办公）
  - CpuTopologyEx 掩码不变量（游戏与后台互斥、并集恒定、同环开关不扩大核数）
  - WorkloadGuard 三类豁免窗口与 Reset 语义
  - 场景去抖（交替闪烁不产生连续切换事件）
  - 关键常量不变式（心跳、防抖、保护窗口）

### 隐私
- 保持**零网络请求**：不包含任何遥测、上报、更新检查；日志只写本地 %AppData%\PCBooster\logs
## [3.6.9] - 2026-10-22

本版核心目标：**消除"游戏中途卡住/黑屏"这一类事故**，并把日常办公/编译场景纳入保护。

### 新增
- `WorkloadGuard`：编译（msbuild/cl/ninja/rustc…）期免 Job 限速、免 Trim，退出后 30 秒收尾保护；办公前台提优先级、办公家族免修剪；浏览器活跃 5 分钟内不修剪后台标签页
- `CpuTopologyEx`：用 `GetLogicalProcessorInformationEx` 解析 **L3 域（AMD CCD / Intel ring）** 与**能效等级（P/E 核）**，选核优先同一域、混合架构优先 P 核，且始终给后台至少留 1 核
- **卡死看门狗**（`StallWatchdog`）：快心跳中断 >15 秒写一条带现场信息（场景/前台/内存/CPU/降级）的 WARN 日志
- `GameCoresSameRing` 配置项：单 L3 域平台（如双环至强）可把游戏核限制在半数以内

### 修复
- **电源方案反复重激活导致闪屏/黑屏**：`SetCoreParkingPercent` / `ApplyGamingPowerProfile` / `RestoreParkingBaseline` / 还原路径全部改为**幂等**（值不变不写、不重激活）
- **锁频/恢复无节流**：进入游戏等场景稳定 3 秒才锁频；任意方向的转换之间至少间隔 15 秒；被节流会自动改期重试
- **场景判定抖动**：`DataCollector.ConfirmScene` 去抖——进入游戏立即生效，其它转换需连续 2 次采样一致，交替闪烁不再产生切换事件
- **工作集下限无去重**：`WorkingSetLock.EnableForProcess` 按 PID 去重（此前每次前台事件都会重写一次工作集）
- **亲和性反复重写**：同进程同掩码 30 秒内不重写；后台核心罩全快照扫描至少间隔 5 秒
- **缓存链路被误伤**：新增存储链路豁免（PrimoCache `primoccsvc` / FlyDisk），与显卡链路同级
- **系统链路缺豁免**：补齐 NVIDIA 常驻组件、Defender（`MsMpEng`）、Windows 更新（`TiWorker`/`TrustedInstaller`）、输入法宿主等
- **未识别游戏被限速**：有可见窗口的程序（按尺寸判定，不依赖标题）一律不做 Job 限速、不做 Trim
- **拓扑探测降级隐患**：WMI 不可用时的回退改用 Win32 权威核数（此前 `Environment.ProcessorCount` 被作业限制时会把 16 核机器算成 4 核，导致游戏只绑 2 核）
- **CacheAware 的"只留 2 核"**：改为限制在同一 L3 域内，不再把缓存敏感进程砍到 2 个物理核
- 多核平台游戏核推荐从 `物理核/4` 调整为 `物理核/2`（上限 8）

## [3.6.8] - 2026-10-21

- 修复电源方案 GUID 使用错误（`平衡 → 高性能 → 平衡` 实测通过）
- 新增 `gaming-bg`（游戏后台）场景：保留核心隔离、释放频率锁定
- 影音场景只关核心停泊，不切高性能方案
- 交流/电池切换后重新评估电源状态
- 唤醒事件 10 秒去重；前台事件钩子失效时用 2 秒轮询兜底
- `ReleaseWorkingSetFloor` / `ReleaseAllFloors`：关闭开关时正确释放工作集下限
- 修复置信度归一化、重复 `DecayAll`、AutoJob PID 表清理

## [3.6.7] 及更早

早期版本主要完成了：双速心跳（2s/10s）、事件驱动 + 轮询兜底、按场景的指令集/核心隔离、规则引擎、日志队列落盘、原子写配置。详细历史请查看 git 提交记录。
