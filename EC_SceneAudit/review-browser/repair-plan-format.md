# 修复清单格式

`ec-scene-repair-plan` version 1 表示某一份已核验 F8 资料的文本修改草稿。网页可以导入、编辑和导出；游戏内修复窗口按本清单预检并在重新核验后写回内存，不写 Scene 文件。清单不改变原报告的结论或问题状态。

## 结构

```json
{
  "format": "ec-scene-repair-plan",
  "version": 1,
  "source": {
    "bundleId": "与资料包一致的标识",
    "manifestSha256": "与资料包一致的清单 SHA-256"
  },
  "changes": [{
    "target": {
      "reference": {
        "fileId": "scene",
        "kind": "json-pointer",
        "pointer": "/nodes/0/part/cuts/0/textBoxes/0/layouts/0/rawText"
      },
      "nodeUid": "与该位置一致的节点 UID",
      "cutSourceIndex": 0,
      "textBoxSourceIndex": 0,
      "layoutIndex": 0
    },
    "before": "完整原文",
    "after": "完整修改稿"
  }]
}
```

示例身份及正文为占位内容，不能直接用于实际资料。

## 核验规则

- `source` 两项都须与当前包完全一致；不按标题或相似文本跨包迁移。
- `reference` 只允许指向 `scene` 的 `layout.rawText`。Pointer 中的下标是原始 JSON 数组位置，从 0 开始；不能填网页排序后的 CUT 位置。
- `nodeUid`、两个 `sourceIndex` 与 `layoutIndex` 必须和引用位置一致，源序号均为非负整数。多个布局各自形成独立目标。
- `before` 是完整原文，必须逐字符匹配，包括空白和换行。原文缺失或非字符串的布局不可修复。
- `after` 是完整替换文本，允许空字符串；每项最多 1,000,000 个 JavaScript 字符串长度单位（UTF-16 code unit），清单最多 1000 项。`changes: []` 有效。
- 未知版本、无效位置/元数据、错来源、错原文、重复位置或 `after === before` 都会拒绝整份导入。其他未定义字段不参与处理，导出时不保留。

导入通过后整体替换当前草稿，可先导出备份。失败不部分合并，也不清除现有草稿。

## 编辑与换行

网页编辑器的新输入使用浏览器 textarea 的 LF 换行；`before` 始终保留证据原样。仅打开、关闭已有草稿不会改写其 `after` 的换行。编辑回到原文（比较时统一 CRLF/CR/LF）会删除此项。

对白和内嵌脚本作为同一段文本编辑。清单只声明修改意图，不能证明修改后的脚本有效；它不包含连线增删、外部文件写入或游戏执行状态。
