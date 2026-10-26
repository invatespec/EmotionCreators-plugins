# EC_SceneAudit 导出读取说明

本文描述完整证据 `schemaVersion=1.0` 和摘要 `schemaVersion=1.0-ai`。用于读取现有导出，不要求安装导出插件或访问开发仓库。其他版本以实际字段为准。

新 F8 资料包先按 [bundle-format.md](bundle-format.md) 核验；以下配对规则供旧独立 JSON 使用，不能替代新包的文件哈希核验。

## 配对文件

1. 优先使用用户明确指定的两份文件。
2. 摘要的 `sourceEvidenceFile` 指向完整证据，当前可能是导出电脑的绝对路径。文件搬家后，取该路径的文件名，在摘要所在目录寻找同名文件；提取文件名时同时识别 `/` 和 `\`，不能依赖当前操作系统只识别一种分隔符。
3. 比较两份文件的 `scene.title` 和 `scene.exportedAt`，再核对节点/Part/CUT 数量。原路径和本地候选都存在时，也需要核对元数据。
4. 同目录可能保留多次完整导出，而 `ai-summary.json` 是后来一次覆盖的结果。不要把摘要和任意最新文件混用；有歧义时报告候选并询问用户。
5. 时间字段可使用 UTC，日期目录可使用本地日期；目录日期与时间字符串日期不同，不足以认定配对错误。

没有同批次完整证据时，保留摘要分析并标记原文不可核验；不要静默换用其他导出。无法定位游戏目录不妨碍对现有 JSON 做静态分析。

## 摘要字段

| 路径 | 用途与边界 |
|---|---|
| `scene` | `title`、`exportedAt` |
| `statistics` | `nodes`、`parts`、`cuts`、`textBoxes`、`scriptBlocks`、`warnings` 数量 |
| `parts[]` | Part 索引：`nodeUid`、`uid`、`name`、`type`、`cutCount`、`textBoxCount`、`rawRef` |
| `scripts[]` | 脚本的 `rawRef`、`parseStatus`、`commands`、`declarations`、`reads`、`writes`、`steps`、`events`、`findings` |
| `findings[]` | 静态提示：通常只有 `code`、`message`、`rawRef`；不是完整审查结果，也不保证含严重程度 |

当前索引名是 `parts`，不是 `partIndex`。脚本通常没有独立 `scriptId`、Part 名称或 `logicSummary`；使用 `rawRef` 与 Part 来源关联。数组下标可供本次阅读定位，不能作为跨导出稳定 ID。

`declarations/reads/writes` 是包含 `name/category/operation/expression/sourceOffset/rawRef` 的对象数组，不是字符串数组。它们由扫描规则产生，可能包含声明属性中的引用或遗漏特殊命令的写入。作用域、类型、初始值需看声明动作属性或原文；不能仅因 `reads` 中有变量而 `declarations` 没有，就认定使用了未声明变量。

逻辑节点在 `steps[]` 中递归组织：

```text
kind / name
attributes[]: { key, value }
targetStatus / sourceOffset / rawRef
children[]: 嵌套逻辑节点
```

条件通常保存在 `if` 的 `value1/operator/value2` 属性中，没有必要假定存在 `when` 字段。事件体也保存在这个树中；`events[]` 是事件引用索引，不是第二份需要再次执行的脚本。保留 `sub-script` 的定义边界。

状态解释：

| 状态 | 可以得出的结论 |
|---|---|
| `valid`（parseStatus） | XML 已解析，不代表逻辑正确或命令一定可执行 |
| `invalid`（parseStatus） | 无可靠逻辑树，回查原文；空 steps/events 不代表没有脚本 |
| `exists` | 导出时目标检查通过，不证明分支可达或依赖执行成功 |
| `missing` | 导出范围内没有找到静态目标；核对原文、目标类型和运行时回退语义 |
| `dynamic` | 目标依赖运行时值 |
| `unverified` | 静态检查无法确认，不等于错误 |

`findings[]` 中的 `part-no-exit` 表示 ADV Part 在最后 CUT 的可用出口没有目标，且脚本没有跳转命令。可用槽由最后 CUT 的选项数决定：无选项只有第一个出口，有选项时槽位对应选项序号；`NotUsed` 槽不参与 ADV 运行时选路。系统节点（开始节点只有输出、结束节点只有输入）与 H Part 不写该 finding。

## 完整证据字段

```text
schemaVersion / scene / nodes[] / crossPartReferences[] / warnings[]
nodes[i]: uid, name, kind, isSystemNode, outputs[], part
  outputs[]: slot, targetUid, conditionType, conditionCount, fadeType, fadeTime
  part: uid, name, kind, type, cuts[]
    cuts[j]: sourceIndex, exportOrder, endCut, optionCount, textBoxes[]
      textBoxes[k]: sourceIndex, exportOrder, sortOrder, id, option, layouts[]
        layouts[m]: layoutIndex, rawText, containsScript, scriptBlocks[], variableReferences[], warnings[]
          scriptBlocks[n]: raw, sourceOffset, commands[], references[]
```

系统节点可能没有 Part；H Part 没有 ADV CUT 不算读取失败。保留五个输出槽，空槽本身不等于缺失连接。`crossPartReferences` 当前是简短字符串列表，不能当成完整的可执行流程图。

JSON 数组位置、`sourceIndex`、`exportOrder` 含义不同：对白框按显示层级 sortOrder 导出，数组位置不一定等于其原始索引。显示给用户时回查这些字段，避免只凭 pointer 中的数字推算界面位置。

完整脚本在 `scriptBlocks[].raw`，整个对白布局在 `rawText`。`variableReferences[].raw` 只是变量引用文本。读取失败和扫描 warning 可能出现在多个层级，按来源去重。

## rawRef 定位

普通引用使用以 `#` 开头的 JSON pointer，例如：

```text
#/nodes/2/part/cuts/0/textBoxes/1/layouts/0/scriptBlocks/0
```

它指向**完整证据**中的脚本对象，读取其 `raw` 可获取原文。按 `/` 分段遍历 JSON；数组段用实际数组下标，属性名中的 `~1` 解码成 `/`、`~0` 解码成 `~`。标准 URI fragment 若包含百分号编码，先解码 fragment。

动作引用在脚本 pointer 后追加自定义后缀，例如：

```text
#/nodes/2/part/cuts/0/textBoxes/1/layouts/0/scriptBlocks/0/command:add@35
#/nodes/2/part/cuts/0/textBoxes/1/layouts/0/scriptBlocks/0/reference@35
```

最后的 `/command:...@...` 或 `/reference@...` **不是 JSON 键，也不是标准 pointer 段**。去掉这个末尾后缀，先定位所属脚本；再结合命令名、属性、相对顺序和偏移核对原文。重复同名命令要保留各自位置。`sourceOffset` 不是行号，来源层级不同，不能盲目当作 UTF-8 字节偏移裁剪。

建议使用任意可用的 JSON 解析器进行这些小范围读取：

1. 摘要的元数据、统计、Part 索引和 findings。
2. 按 Part 的 pointer 前缀选择 `scripts[]`，分批读取完整逻辑树。
3. 对待核验问题在完整证据中解析脚本 pointer，读取原文和所属对白框信息。
4. 需要追踪变量时搜索所有脚本的声明/引用，再回查实际命令；不要只看一个 Part。

这些操作只减少输出到 AI 上下文的内容；普通 JSON 解析器仍可能把整个文件读入内存。Scene 包含角色、贴图等资源，其文件体积不能直接当作 JSON 或脚本体积。

## 外部依赖

完整 JSON 记录 `load-script` 引用和目标状态，不保证包含外部文件正文。依据用户提供的 `UserData` 根目录或明确的文件映射寻找依赖；移动过的导出包不能直接复用原电脑绝对路径。需要内容却未提供时，列出引用路径、引用位置和受影响的结论。

外部脚本正文必须自带字面小写 `<script>…</script>`：游戏用 `IndexOf("<script>")` 定位后再按 XML 解析根元素 `script`。文件里没有该字面标签（含 `<script >`、`<script 属性=…>`、`<SCRIPT>`、只有脚本体）时，游戏把整份当普通文本，既不执行也不报错；不要替它补包装再解读。一个文件里出现多段 `<script>` 时只有第一段执行，其余按普通文本处理。

附带 `Reference/variablemod/` 是 Variable Mod 用法文档，不是用户 Scene 的外部脚本，也不包括所有扩展插件文档。
