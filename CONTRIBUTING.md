# 参与贡献

欢迎 issue 和 PR。这个项目会**修改真实系统状态**，所以对改动的审查比一般项目严格。

> ⚠️ 提交代码 PR 前请先签署 [CLA](CLA-Individual.md)（企业请用 [CLA-Corporate.md](CLA-Corporate.md)）。
> CLA **不是版权转让**：你保留著作权，只是允许本项目在 GPL 之外也能以其他许可（含商业许可）分发你的贡献。
> issue / 讨论 / bug 报告**不需要**签。首次 PR 会由 CLA Assistant 自动提示，回复一行即完成签署。

## 快速开始

```powershell
git clone https://github.com/<你的账号>/PCBooster.git
cd PCBooster
dotnet build PCBooster.csproj -c Release    # 要求 0 警告 0 错误
```

要求：Windows 10/11 x64、.NET SDK 8.0+、VS 2022 17.10+（或 Rider）。

## 代码约定

- **编码**：所有源文件 UTF-8 **无 BOM**；保存时请确认（PowerShell 写文件用 `New-Object System.Text.UTF8Encoding($false)`）
- **语言特性**：C# 12，`Nullable=disable`，`ImplicitUsings=disable`
- **分层**：`Infrastructure(0) < Execution(1) < Core / Intelligence(2) < UI(3)`，只允许**向下**依赖。新增类放错层会破坏审计
- **UI**：尺寸全部走 `Theme.S()/Sf()`；列表行实现 `IRow.MeasureHeight()`；不要用绝对坐标栅格
- **日志**：执行动作要有可读日志，但要避免高频刷屏（用 DEBUG 级别 + 节流）

## 改执行层（重点）

任何会写系统状态的改动，PR 必须说明：

1. **先检查红线**：`ProcessMatcher.IsProtected`（反作弊）、`IsSystemProcess`（系统/显卡/存储链路）、`HasVisibleWindow`（用户正在用的程序）
2. **是否幂等**：值没变会不会重复写？重复写会不会引起系统抖动？
3. **怎么还原**：退出/切换场景时靠什么还原？会不会漏？
4. **有没有节流/去重**：这段代码会被多高的频率调用？
5. **实测数据**：至少给出改动前后的日志或测量结果，不要只写"应该会更快"

## 提交前自查

```powershell
dotnet build PCBooster.csproj -c Release --no-incremental -v q   # 0 警告 0 错误
```

- [ ] 构建 0 警告 0 错误
- [ ] 没有新增 TODO/FIXME
- [ ] 文件是大括号平衡、UTF-8 无 BOM
- [ ] 手动跑一遍相关场景（进游戏 / 退游戏 / 切窗口 / 待机唤醒）
- [ ] 没有把 `bin/ obj/ out/` 或本地配置带进提交

## Commit / PR 风格

- Commit 用一句话说清"改了什么 + 为什么"，正文可附实测数据
- PR 描述里写：动机 / 改动点 / 验证方式 / 风险与回滚方式
- 破坏性改动（影响默认行为）请单独 PR，并在 PR 里标注 `behavior-change`

## 不接受的改动

- 进程注入、内核驱动、图形 API hooking
- 对反作弊进程的任何写操作
- "优化注册表""禁用系统服务"这类不可预期、难回滚的改动
- 没有实测依据的"性能优化"（尤其是把核数一刀切改小、把优先级一律拉高）

## 许可与 CLA

本项目以 **GPL-3.0-or-later** 发布。为了让项目保留“开源 + 商业双许可”的能力，**所有贡献都需要签署 CLA**（贡献者许可协议）：

- 个人：[CLA-Individual.md](CLA-Individual.md)
- 公司/雇主拥有代码权利：[CLA-Corporate.md](CLA-Corporate.md)

操作方式：在 PR 描述里写一句 `I have read the CLA Document and I hereby sign the CLA`（个人），或附上已签署的扫描件/邮件确认（企业）。

### 自动化（维护者一次性配置）

仓库里已经有 [`.github/workflows/cla.yml`](.github/workflows/cla.yml)，基于 `contributor-assistant/github-action`。启用只需三步：

1. **建 secret**：Settings → Secrets and variables → Actions → 新建 `CLA_PAT`
   （fine-grained PAT，仅需仓库 `Contents: Read and write` 权限；fork PR 无法用 `GITHUB_TOKEN` 写签名文件）
2. **改 allowlist**：在 workflow 的 `allowlist` 里把 `<你的GitHub用户名>` 换成你的账号（否则你自己的 PR 也会被拦）
3. 首次有人提 PR 时，Action 会自动创建 `cla-signatures` 分支并在 PR 下留评论；贡献者回复签署语句后即生效

> 也可改用零代码方案：[CLA Assistant](https://cla-assistant.io/) GitHub App（需把 CLA 文本放到 Gist 里）。

### 源码头部 SPDX 约定

新建的 `.cs` 文件请在首行加 SPDX 标识（已有文件可逐步补）：

```csharp
// SPDX-License-Identifier: GPL-3.0-or-later
```

第三方代码必须保留其原许可，并在 PR 中说明来源与许可证（当前唯一依赖 `System.Management` 为 MIT，兼容）。
