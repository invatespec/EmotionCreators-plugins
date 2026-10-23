# F8 审查资料包 v1

读取本文件后，再按 [export-format.md](export-format.md) 理解完整证据和摘要；包内使用快照 1.0、摘要 1.0-ai。

## 目录与来源

每次 F8 输出一个独立目录：

```text
{SceneTitle}/{yyyyMMdd}/{时间戳_批次后缀}/
  scene.json
  ai-summary.json
  bundle.json
  bundle.sha256
  dependencies/dep-0001.xml
```

这些目录可以整体移动。先识别 bundle.json，不要把某天目录下不同批次的文件拼在一起。bundle.sha256 是 bundle.json 原始字节的 SHA-256；哈希时不要重新格式化 JSON。摘要 sourceEvidenceFile 指向包内 scene.json。

bundle.json 的 format 为 ec-scene-review-bundle、version 为 1。精确约束见 [bundle.schema.json](schemas/bundle.schema.json)，共享引用结构见 [common.schema.json](schemas/common.schema.json)。

- bundleId：本次批次 ID，不代表跨导出稳定的 Scene 身份。
- scene：显示标题、导出时间。
- files：fileId、role、包内相对 path、原始字节 sha256、byteLength。role 为 scene / summary / dependency。
- dependency 成员额外记录相对于 UserData 的 sourcePath、capturedAt、encoding、parseStatus。
- dependencies：每次 load-script 的 from、requestedPath、targetFileId、status、reason 和父级 context。同一文件可以被引用多次，不要重复解释为同一时刻执行多次。
- limitations：无法解析 XML、编码不明等缺口及其来源。

fileId=manifest 保留给 bundle.json，清单不会把自身加入 files。未知版本不能自动解释成 v1。

## 核验顺序

1. 核对版本与结构，重新计算清单哈希。
2. 按 files 读取包内成员，核对字节数与 SHA-256；不接受绝对成员路径、越界路径或链接。
3. 核对清单、摘要和完整证据的 Scene 标题/导出时间及摘要来源。
4. 报告 source 必须携带该 bundleId 和实际 manifestSha256，引用必须能在这些文件内找到。

可以使用任意可用解析器完成核验。如果仓库中的 review-core 工具也已提供，可以运行其 cli.mjs；本 skill 不依赖该工具、特定 AI 客户端或游戏安装位置。工具不足时明确哪些核验未完成，不虚构“校验通过”。

缺少 bundle.sha256 的目录按未完成导出处理。成员缺失、哈希不符或引用失效时，保留可读资料作有限分析，指出故障；不要给出声称绑定成功的正式报告。旧独立 JSON 仍可有限阅读，新的正式报告交接需要重新按 F8 生成资料包。

## 依赖范围与状态

只使用当前 Scene 及依赖清单沿引用到达的正文。不要遍历其他 XML 寻找旁证；共享脚本因当前 Scene 引用而纳入，不因目录名而排除。

| 状态 | 含义 |
|---|---|
| included | 正文字节已经收录，仍需看编码与 parseStatus |
| missing | 采集时未找到文件 |
| dynamic | 路径依赖变量，未推算 |
| outside-userdata / link | 超出自动收集范围或包含链接 |
| invalid-path / unreadable | 路径无效或文件无法读取 |
| limit | 达到文件数、字节数或深度保护限制 |
| unstable | 采集期间文件发生变化，未收录 |

单文件 2 MiB、合计 16 MiB、256 个文件、32 层是当前收集界限；到达限制会留下状态，不表示依赖不存在。循环引用保留引用边，不无限展开。

parseStatus 为 valid / invalid / unreadable。valid 只说明 XML 可解析，不证明脚本语义正确。encoding=null 时正文仍按原字节保存；不要假定空脚本。坏 XML 不自动展开其中的候选依赖。

清单与原始快照可能分别记录“导出时 missing”和“采集时 included”，需保留时间差异。包外后来找到的文件不能静默替换包内证据；需要更新包及报告来源。

## 引用位置

JSON 引用：

```json
{"fileId":"scene","kind":"json-pointer","pointer":"/nodes/0/part/cuts/0/textBoxes/0/layouts/0/scriptBlocks/0/raw"}
```

文本引用：

```json
{"fileId":"dep-0001","kind":"text-lines","startLine":2,"endLine":5}
```

JSON 使用标准 Pointer，不带 # 或旧动作后缀；数组段是实际数组位置。文本从 1 开始，CRLF/LF/CR 均按一个换行解释。依赖缺口可引用 fileId=manifest 及 /dependencies/序号。

人类说明用 Part 名、sourceIndex+1 对应的 CUT 序号、选项文字/对白摘录。共享 XML 通过 dependencies.from 回查所有使用位置，不能擅自指定唯一所属 Part。报告修订格式见 [report-format.md](report-format.md)。
