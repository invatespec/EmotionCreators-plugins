# Scene review core

无 UI 的资料包、证据定位与报告校验模块，供 review-browser 网页工作台复用。普通用户按 F8 导出不需要安装 Node。

开发与命令行校验需要 Node 22+：

```sh
npm ci
npm test
node cli.mjs validate --bundle "<资料目录>" --report "<报告文件>"
node cli.mjs validate --bundle "<资料目录>" --report "<新版报告>" --previous "<前版报告>"
```

退出码 0 表示结构、来源和引用校验通过；仍可能有资料缺口 warning。退出码 1 表示失败。校验不执行游戏脚本，也不证明报告的推理正确。

- src/bundle.mjs：loadBundle({readFile, hash?})，文件读取由调用方提供。
- src/report.mjs：validateReport(report, verifiedBundle, {previousReport?})。
- src/evidence.mjs：标准引用解析、旧 rawRef 适配与 matchesEvidenceQuote(value, quote) 引文比较；文本兼容换行，结构化 JSON 兼容字符串外排版，仍保留内容与词法边界。
- src/locations.mjs：可读位置、按原始顺序恢复选项与出口的静态对应。
- src/node-files.mjs：仅 CLI 使用的文件系统适配器，浏览器端不导入。
- 契约唯一来源：../ec-scene-review/Reference/schemas/，随可移植 skill 分发。
- 示例生成入口：scripts/generate-examples.mjs。示例数据来自 test/fixtures.mjs；生成后由测试验证，不手工修改示例哈希。

核心依赖标准 TextDecoder 与 Web Crypto；浏览器/其他环境可注入 SHA-256 实现。文件错误返回 errors，采集缺口保留 warnings；未通过核验的包不得作为已验证报告来源。
