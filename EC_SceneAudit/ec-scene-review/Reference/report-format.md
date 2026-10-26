# AI 审查报告 v1

输出一段简短说明和独立的 review-report.json。只有在会话无法创建附件时才用单个 JSON 代码块交付，并注明文件名；不要在 JSON 内加入 Markdown。

精确结构见 [report.schema.json](schemas/report.schema.json)。可参考 [首版示例](examples/report-r1.json)、[撤销后的修订版](examples/report-r2.json)、[精简撤销示例](examples/report-r3-compact.json) 与它们的 [合成资料包](examples/bundle/bundle.json)。示例只是格式演示，不能当用户 Scene 的审查结论。

## 顶层字段

| 字段 | 必需内容 |
|---|---|
| format / version | ec-scene-review-report / 1 |
| reportId | 当前报告唯一 ID，可自拟；同一批次中不复用旧报告 ID |
| supersedesReportId | 被替换报告 ID；首次为 null |
| source | bundleId、manifestSha256，来自已核对资料；不能用标题代替 |
| summary | 简短当前结论 |
| coverage | reviewed 引用数组、excluded 范围与原因数组、limitations 字符串数组 |
| findings | 当前完整问题集合；没有发现问题时为空数组 |

excluded 元素包含 scope 和 reason，可附 reference。reviewed 必须对应实际读过的范围；只读索引不能把整个 Scene 当作已审查。资料缺口、未完成阶段、路线切片和未知前提应进入 coverage，避免把作者未完成内容直接当作故障。

## 问题字段

每项必需 id、status、revisionReason。status=withdrawn 时其余字段可省略，由前版同 id 条目补齐（见[精简撤销项](#精简撤销项)）；status=active 时还必须写 severity、certainty、title、locations、trigger、evidence、impact、recommendation。

- status：active / withdrawn。
- severity：blocker / major / minor / info，表示影响大小。
- certainty：confirmed / conditional / unknown，表示证据充分程度。严重程度高不等于已经确定。
- locations：至少一个 [标准证据引用](bundle-format.md#引用位置)。主说明展示可读位置，UID 与内部索引只保留在必要的详细证据中；tb 是对白框，不能称为出口。
- trigger：成立所需入口、变量状态、选择或事件；未知前提明确写出。
- evidence：至少一项 {reference, explanation}，可附 quote。quote 必须来自指定证据，不能把转述装作引文。文本允许 CRLF/LF/CR 换行等价；JSON 对象/数组引文允许字符串外的排版空白差异，不忽略字符串内容、数字或标点变化。
- impact：按证据推导的后果，不能把“目标未确认”自动写成“必然卡死”。
- recommendation：最小修正方向或最短核验动作，不直接执行修改。
- revisionReason：首次为 null；修改既有问题、撤销或替换时写理由和依据。

JSON 字段承载结构，展示标签从真实证据推导。外部 XML 问题可把 XML 引用和 Scene 使用位置一同放入 locations。

优先把文本引文指向具体 raw/rawText 字段或外部文本行。引用 JSON 对象时可用 `"name": "Part A"` 这样的连续片段：它可匹配 `"name":"Part A"`，不能匹配 `"name":"PartA"`。数字 `1` 不能匹配 `10`；不靠删除所有空白、重排字段或拼接不连续片段使引文通过。

## 更新与撤销

每次生成一份自包含的当前报告。不要将新结论追加到旧 Markdown 后让用户自行合并。

- 同一问题继续沿用 finding.id，新的 reportId 指向旧版 supersedesReportId。
- 确认撤销的旧问题仍列出，status=withdrawn，并保留撤销依据；有前版时可只写三个字段（见下节）。
- 合并/拆分可添加 replacesFindingIds；被替换 ID 不能同时作为有效问题。它们必须来自指定的前版报告。
- 前版有效问题不能无说明地消失：保留、明确撤销或显式替换。
- 前版未提供时当前报告仍可读，但修订关系未核验。不要伪造旧 ID。
- 同一 Scene 修改后重导出的新批次可以直接沿用旧报告作为前版：新报告 source 指向新批次，supersedesReportId 填旧报告 ID，问题 ID 保持不变，证据与引文全部改写为新证据里的位置。网页会按问题 ID 核对修订关系并提示前版来自另一批资料。
- 换了 Scene（不是同一 Scene 的再次导出）时，不把它当同一来源内的报告修订，重新分析。

### 精简撤销项

提供前版时，撤销项可以只写三个字段，其余从前版同 id 条目继承，不必重抄：

```json
{ "id": "f3", "status": "withdrawn", "revisionReason": "重新核对后确认该分支有脚本出口。" }
```

- 写了的字段覆盖前版值，没写的保留前版值。需要改标题就只写 title。
- 继承来的 locations 与 evidence 属于前版批次。新批次里解析不到会报错，必须改写为新位置；其余字段不用动。
- 只有 withdrawn 能这样写。修改既有问题、replacesFindingIds 替换项仍须写全字段（撤销同时带 replacesFindingIds 也算替换项）。
- 没有前版时撤销项必须写全，不能只写三个字段。
- 连续两轮撤销同一问题时前版本身也可能精简；此时补不全的字段保留缺省并以 `inherits-incomplete` 提示，不判失败。

核对方法是：拿到新资料包先逐条看旧问题，找到对应位置判断是否仍成立。节点位置或内容变了就整条重写，没变才用继承。

## 确定程度与静态边界

confirmed 只针对证据能证明的具体命题与明确前提，不代表游戏所有状态都验证通过。顺序执行的独立 if 要使用前一步更新后的值；事件注册与执行分开；跨 Part 跳转是否终止当前脚本仍要查明。

例如余额 100 先扣 80 变成 20，后续独立 if 可能因此成立。写出状态变化，而不是假定它们互斥；两个跳转条件重叠也不能自动推出两个跳转都生效。

附带 [错来源示例](examples/invalid-wrong-source.json) 与 [坏引用示例](examples/invalid-reference.json) 应被拒绝。格式与来源校验只验证结构和定位，不能证明 AI 的逻辑结论正确。
