import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { fixture, report, ref } from '../test/fixtures.mjs';

const directory = new URL('../../ec-scene-review/Reference/examples/', import.meta.url);
const f = await fixture();
if (!f.loaded.ok) throw new Error(JSON.stringify(f.loaded.errors));
for (const [relative, bytes] of f.data) {
  const target = new URL('bundle/' + relative, directory);
  await fs.mkdir(fileURLToPath(new URL('.', target)), { recursive: true });
  await fs.writeFile(target, bytes);
}
const first = report(f.loaded.bundle);
const second = structuredClone(first);
second.reportId = 'r2'; second.supersedesReportId = first.reportId;
second.summary = '补充核对后撤销示例问题。';
second.findings[0].status = 'withdrawn';
second.findings[0].revisionReason = '原疑点来自未读依赖，现已核对。';
// 精简撤销项：只写三个字段，其余从前版继承。
const compact = structuredClone(second);
compact.reportId = 'r3'; compact.supersedesReportId = second.reportId;
compact.summary = '再次确认撤销，使用精简写法。';
compact.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '已核对依赖正文，问题不成立。' }];
const wrongSource = structuredClone(first);
wrongSource.source.bundleId = 'wrong-bundle';
const wrongReference = structuredClone(first);
wrongReference.findings[0].locations = [{ ...ref, pointer: '/nodes/999' }];
for (const [name, value] of Object.entries({
  'report-r1.json': first, 'report-r2.json': second, 'report-r3-compact.json': compact,
  'invalid-wrong-source.json': wrongSource, 'invalid-reference.json': wrongReference,
})) {
  await fs.writeFile(new URL(name, directory), JSON.stringify(value, null, 2) + String.fromCharCode(10));
}
console.log('Generated portable contract examples.');
