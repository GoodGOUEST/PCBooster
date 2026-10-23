# PC Booster

> Windows 上的**可解释、可回滚、不碰内核**的系统资源调度器 —— 场景化电源/核心调度，而不是"一键加速"的蛇油。

[English summary](#english) · [安全承诺](#安全承诺我们不做的事) · [构建](#从源码构建) · [分层架构图](docs/LAYERS.md) · [架构](docs/ARCHITECTURE.md) · [安全设计](docs/SAFETY.md)

---

## 它是什么

PC Booster 是一个常驻托盘的 Windows 调度器。它识别你**正在做什么**，然后只调整对应的系统状态，并在离开场景后**还原**：

| 场景 | 它做什么 |
|---|---|
| 游戏（前台/后台） | 高性能电源方案 + 睿频档；把游戏收进独立核心组；后台进程压到另一组 |
| 影音播放 | 只关核心停泊（避免解码线程被停泊拖抖），不动电源方案 |
| 办公 / 浏览 | 前台程序提优先级；办公家族免修剪、免限速 |
| 编译 / 设计 / 开发 | 编译工具链免限速、免 Trim，退出后还有 30 秒收尾保护 |
| 空闲待命 | 分三档（活跃 / 轻待命 / 深睡）逐步降核，检测到输入或负载立刻恢复 |

配套能力：按进程的规则引擎（优先级/亲和性/限速）、内存回收（按"生命迹象"筛选）、预加载与学习、决策日志（每个决策都能看到原因）。

## 安全承诺：我们不做的事

这是本项目最重要的部分，也是它和大多数"优化工具"的区别：

- ❌ **不做进程注入**
- ❌ **不做内核驱动**
- ❌ **不做 D3D / 图形 API hooking**
- ❌ **绝不触碰反作弊进程**（`ProcessMatcher.IsProtected` 在所有 4 条写路径上都做了检查：优先级、亲和性、Job、工作集）
- ❌ **不卸载、不"优化"系统服务**，不改注册表里的"优化项"

以及为了不出事故而专门做的工程约束：

- **所有电源写入都是幂等的**（值没变就不重写、不重新激活方案）
- **场景切换有去抖**（全屏游戏切显示模式时的秒级抖动不会触发反复切换电源）
- **锁频/恢复之间有 15 秒最小间隔**，进入游戏后等场景稳定 3 秒才锁频
- **工作集/亲和性写入按 PID 去重并带冷却**，不给正在跑的程序"搬核"
- **有卡死看门狗**：心跳中断超过 15 秒会写一条带现场信息的日志
- **一键还原**：退出时还原所有被改过的优先级/亲和性/电源项

> 这些不是宣传语，每一条都是踩过真实事故（游戏中途黑屏）之后补上的。细节见 [docs/SAFETY.md](docs/SAFETY.md)。

## 系统要求

- Windows 10 / 11 x64
- 建议以管理员身份运行（核心隔离、工作集保护、部分电源项需要提权；不提权也能用，功能会自动降级）
- 发布包是**单文件自包含**的，不需要预装 .NET 运行时

## 安装 / 使用

1. 从 [Releases](../../releases) 下载 `PCBooster.exe`（单文件）
2. 双击运行（会弹 UAC），托盘出现图标即生效
3. 右键托盘图标 → 打开面板 / 设置 / 还原
4. 卸载：托盘退出 → 删除 exe（配置在 `%AppData%\PCBooster`，可一并删除）

> ⚠️ 未签名的 exe 会触发 SmartScreen 提示，这是正常的，选择"仍要运行"即可。将来会接代码签名。

## 从源码构建

```powershell
# 需要 .NET SDK 8.0+
dotnet build PCBooster.csproj -c Release

# 单文件发布（自包含，体积约 70MB）
dotnet publish PCBooster.csproj -c Release -p:PublishProfile=SingleFileX64
# 产物：out/sc-x64/PCBooster.exe
```

开发约定（欢迎 PR 前先读）：

- 源文件 **UTF-8 无 BOM**，`Nullable=disable`，C# 12
- UI 所有尺寸走 `Theme.S()/Sf()`，页面行实现 `IRow.MeasureHeight()`，禁止绝对坐标栅格
- 改动执行层必须过红线检查：先 `IsProtected` 再动手，任何写系统状态都要能还原
- 改完请保证：`dotnet build` 0 警告 0 错误

## 已知限制

- 亲和性管理只覆盖前 64 个逻辑核（多处理器组 / >64 核尚未支持）
- 混合 DPI 下仍有发虚（当前 SystemAware，未做 PerMonitorV2）
- 没有内核驱动 → 无法做真正的 I/O 优先级与频率控制（频率由固件 Turbo 决定）
- 未做多用户/RDP 配置隔离；未做崩溃残留标记
- 不做"框架时间"级别的验证：**能改什么、改了多少，请用帧时间工具自己复测**

完整的已知问题与路线见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 设计边界

这个项目有意保持"小"和"可审查"：

- 65 个源文件、约 1.15 万行 C#，唯一第三方依赖是 `System.Management`
- 分层依赖有脚本化审计（循环依赖、反向依赖、死代码）
- 每个执行动作都有日志，且决策日志可以从 UI 里实时看到

## License

本项目以 **GPL-3.0-or-later** 发布，全文见 [LICENSE](LICENSE)。

- **你可以**：自由使用、修改、分发（包括商用），前提是分发时同样以 GPL 开源并保留版权声明与免责声明。
- **商业授权**：如果你要在**闭源产品 / 商业发行版 / 硬件厂商软件**中集成本项目（GPL 的传染性会要求你的产品同样开源），需要另行取得商业授权。请通过本仓库的 Discussion 或 issue 联系维护者。
- **名称与图标**：许可不授予商标权。“PC Booster” 名称与图标仅用于本项目官方发行版；**衍生发行版必须改名换图标**，且不得暗示作者背书。
- **参与贡献**：提交 PR 前请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 并签署 [CLA](CLA-Individual.md)（项目保留双许可能力所必需）。

作者与任何反作弊厂商、硬件厂商无关联，不提供任何担保。

---

<a name="english"></a>
## English summary

PC Booster is a tray-resident Windows scheduler that adapts system state to what you are doing (gaming / video / office / compiling / idle) and **reverts everything on exit**.

It deliberately avoids process injection, kernel drivers and graphics hooking, never touches anti-cheat processes, and makes every system write idempotent, debounced and reversible. Licensed under **GPL-3.0-or-later**; commercial licensing available on request. No warranty.
