## 这个 PR 做了什么

<!-- 一句话概括，然后列要点 -->

## 动机

<!-- 解决什么问题？关联 issue：#123 -->

## 涉及层与文件

<!-- 例：Execution/AutoJobManager.cs（执行层，第四类写路径） -->

## 验证方式

<!-- 必填：怎么证明它是对的/有效的。日志片段、探针输出、帧时间对比都可以 -->

- [ ] `dotnet build PCBooster.csproj -c Release` 0 警告 0 错误
- [ ] 手动跑过相关场景（进游戏 / 退游戏 / 切窗口 / 待机唤醒 / 编译中）
- [ ] 审计无新增问题（`node scripts/parse_cs3.mjs` + `node scripts/audit_report.mjs`）

## 如果改动了执行层（写系统状态）

- [ ] 写入是**幂等**的（值没变不重复写）
- [ ] 有**去抖/节流**，且知道自己会被多高频调用
- [ ] 过了红线检查：`IsProtected` / `IsSystemProcess` / `HasVisibleWindow`
- [ ] 退出/切换场景时能**完整还原**（说明靠什么还原）

## 风险与回滚

<!-- 最坏情况是什么？出问题怎么退回？（例如：删掉这个开关/还原默认值） -->

## 行为变更

- [ ] 本 PR 改变了默认行为（请在标题或正文标注 `behavior-change`）

## 许可（CLA）

- [ ] 我已阅读并同意 [CLA](CLA-Individual.md)（首次贡献必需；企业贡献请用 [CLA-Corporate.md](CLA-Corporate.md)）

> 首次贡献时，CLA Assistant 会在 PR 下自动提示。在那条评论里回复下面这一行即完成签署：
>
> `I have read the CLA Document and I hereby sign the CLA`
