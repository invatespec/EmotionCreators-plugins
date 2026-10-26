import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { fixture, report, seal, ref } from './fixtures.mjs';
import { loadBundle } from '../src/bundle.mjs';
import { directoryReader } from '../src/node-files.mjs';
import { validateReport } from '../src/report.mjs';

test('object key order does not require a revision reason', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const current = structuredClone(previous);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings[0] = Object.fromEntries(Object.entries(current.findings[0]).reverse());
  assert.equal(validateReport(current, loaded.bundle, { previousReport: previous }).ok, true);
});

test('withdrawn IDs must exist in the supplied previous report', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle), current = structuredClone(previous);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings.push({ ...current.findings[0], id: 'fake-history', status: 'withdrawn', revisionReason: '测试' });
  assert.equal(validateReport(current, loaded.bundle, { previousReport: previous }).ok, false);
});

test('unreferenced members and invalid limitation pointers are rejected', async () => {
  const f = await fixture();
  f.manifest.dependencies = [];
  await seal(f.data, f.manifest);
  assert((await loadBundle(f)).errors.some(e => e.code === 'unreferenced-dependency'));
  const g = await fixture();
  g.manifest.limitations.push({ code: 'invalid-xml', source: { ...ref, pointer: '/nodes/404' }, message: '测试' });
  await seal(g.data, g.manifest);
  assert((await loadBundle(g)).errors.some(e => e.code === 'limitation-reference'));
});

test('portable generated examples accept current/revised reports and reject mismatches', async () => {  const root = new URL('../../ec-scene-review/Reference/examples/', import.meta.url);
  const read = async name => JSON.parse(await fs.readFile(new URL(name, root), 'utf8'));
  const loaded = await loadBundle({ readFile: directoryReader(fileURLToPath(new URL('bundle/', root))) });
  assert.equal(loaded.ok, true, JSON.stringify(loaded.errors));
  const first = await read('report-r1.json');
  assert.equal(validateReport(first, loaded.bundle).ok, true);
  const second = await read('report-r2.json');
  assert.equal(validateReport(second, loaded.bundle, { previousReport: first }).ok, true);
  // 精简撤销示例：只写 id/status/revisionReason，其余取前版。
  const compact = await read('report-r3-compact.json');
  const checked = validateReport(compact, loaded.bundle, { previousReport: second });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert.equal(checked.expanded.findings[0].title, second.findings[0].title);
  assert.equal(validateReport(await read('invalid-wrong-source.json'), loaded.bundle).ok, false);
  assert.equal(validateReport(await read('invalid-reference.json'), loaded.bundle).ok, false);
});

test('previous report from another bundle still verifies revision by finding id', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  previous.source = { bundleId: 'older-bundle', manifestSha256: 'a'.repeat(64) };
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  // 跨批次沿用旧问题必须把证据改写为新批次位置，这一步不应再要求修订理由。
  current.findings[0].evidence = [{ ...current.findings[0].evidence[0], quote: '<load-script file="a.xml"/>', explanation: '新批次同一脚本。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert(checked.warnings.some(item => item.code === 'previous-other-bundle'));

  const dropped = structuredClone(current);
  dropped.findings = [];
  assert.equal(validateReport(dropped, loaded.bundle, { previousReport: previous }).ok, false,
    'old active finding still cannot vanish without a reason');

  const changed = structuredClone(current);
  changed.findings[0].impact = '改写后的结论';
  assert.equal(validateReport(changed, loaded.bundle, { previousReport: previous }).ok, false,
    'changed conclusion still needs a revision reason');
});

test('previous report from another bundle must keep a valid shape', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  previous.source = { bundleId: 'older-bundle', manifestSha256: 'a'.repeat(64) };
  delete previous.summary;
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, false, 'broken previous report still rejected');
  assert(checked.errors.some(item => item.code === 'revision-parent'));
});

// 撤销项只写三个字段，靠在场的同 id 前版补全：AI 不必重抄整段旧结论。
test('a withdrawn finding may inherit every other field from the previous report', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '补充证据后撤销。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  const filled = checked.expanded.findings[0];
  assert.equal(filled.title, previous.findings[0].title, '未写的字段取前版值');
  assert.equal(filled.impact, previous.findings[0].impact);
  assert.deepEqual(filled.locations, previous.findings[0].locations);
  assert.equal(filled.revisionReason, '补充证据后撤销。', '写了的字段覆盖前版值');
});

test('overridden fields win while the rest stays inherited', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f1', status: 'withdrawn', title: '改名后的旧疑点', revisionReason: '标题需要更准确。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert.equal(checked.expanded.findings[0].title, '改名后的旧疑点');
  assert.equal(checked.expanded.findings[0].recommendation, previous.findings[0].recommendation);
});

test('a compact withdrawal without a previous report is refused with one clear reason', async () => {
  const { loaded } = await fixture();
  const current = report(loaded.bundle);
  current.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '撤销。' }];
  const checked = validateReport(current, loaded.bundle);
  assert.equal(checked.ok, false);
  assert(checked.errors.some(item => item.code === 'inherits-without-previous'));
});

test('a compact withdrawal must point at an id the previous report actually has', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f9', status: 'withdrawn', revisionReason: '撤销。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, false);
  assert(checked.errors.some(item => item.code === 'inherits-unknown-id'));
});

// 精简只开放给撤销项：有效结论缺字段仍由 schema 拒绝，不能被继承悄悄补成旧结论。
test('an active finding still has to write every field', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f1', status: 'active', revisionReason: '补充证据。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, false);
  assert(checked.errors.some(item => item.code === 'schema'));
});

// 跨批次继承来的位置属于旧包；在新包解析不到就要报错，改写成新位置由 AI 负责。
test('inherited locations that no longer resolve in the current bundle are rejected', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  previous.source = { bundleId: 'older-bundle', manifestSha256: 'a'.repeat(64) };
  previous.findings[0].locations = [{ ...ref, pointer: '/nodes/999' }];
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '撤销。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, false);
  assert(checked.errors.some(item => item.code === 'evidence-reference'), JSON.stringify(checked.errors));
});

// 只改位置就能救回来：撤销项仍不必重抄标题、影响和建议。
test('rewriting only the stale locations is enough for a compact withdrawal', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  previous.source = { bundleId: 'older-bundle', manifestSha256: 'a'.repeat(64) };
  previous.findings[0].locations = [{ ...ref, pointer: '/nodes/999' }];
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f1', status: 'withdrawn', locations: [ref], revisionReason: '撤销。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert.equal(checked.expanded.findings[0].recommendation, previous.findings[0].recommendation);
});

// 前版自身可能是上一轮的撤销项；更早的前版没传进来，不能因此判当前报告失败。
test('a previous report that is itself a compact withdrawal still validates', async () => {
  const { loaded } = await fixture();
  const first = report(loaded.bundle);
  const second = structuredClone(first);
  second.reportId = 'r2'; second.supersedesReportId = 'r1';
  second.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '补充证据后撤销。' }];
  const third = structuredClone(first);
  third.reportId = 'r3'; third.supersedesReportId = 'r2';
  third.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '再次确认撤销。' }];
  const checked = validateReport(third, loaded.bundle, { previousReport: second });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
});

// 补不全只放开没写的字段；写出来的坏位置仍要逐条核验，不能被整条跳过。
test('an incomplete withdrawal still has its own references checked', async () => {
  const { loaded } = await fixture();
  const first = report(loaded.bundle);
  const second = structuredClone(first);
  second.reportId = 'r2'; second.supersedesReportId = 'r1';
  second.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '补充证据后撤销。' }];
  const third = structuredClone(first);
  third.reportId = 'r3'; third.supersedesReportId = 'r2';
  third.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '再次确认撤销。',
    locations: [{ ...ref, pointer: '/nodes/999' }], evidence: [{ reference: { ...ref, pointer: '/nodes/999' }, explanation: '坏位置。' }] }];
  const checked = validateReport(third, loaded.bundle, { previousReport: second });
  assert.equal(checked.ok, false, 'explicit stale references must still fail');
  assert.equal(checked.errors.filter(item => item.code === 'evidence-reference').length, 2, JSON.stringify(checked.errors));
});

// replacesFindingIds 描述与旧问题的关系，不跟着继承；继承来的旧 ID 在新前版里找不到，会误报替换缺失。
test('a compact withdrawal never inherits replacesFindingIds', async () => {
  const { loaded } = await fixture();
  const first = report(loaded.bundle);
  first.findings.push({ ...first.findings[0], id: 'f2', title: '第二个疑点' });
  const second = structuredClone(first);
  second.reportId = 'r2'; second.supersedesReportId = 'r1';
  second.findings = [{ ...first.findings[1], replacesFindingIds: ['f1'], revisionReason: '合并两个疑点。' }];
  assert.equal(validateReport(second, loaded.bundle, { previousReport: first }).ok, true);
  const third = structuredClone(second);
  third.reportId = 'r3'; third.supersedesReportId = 'r2';
  third.findings = [{ id: 'f2', status: 'withdrawn', revisionReason: '合并后的疑点也不成立。' }];
  const checked = validateReport(third, loaded.bundle, { previousReport: second });
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert.equal(checked.expanded.findings[0].replacesFindingIds, undefined);
});

// 前版是用户选的任意文件，结构损坏时不能把当前报告校验抛成异常。
test('a structurally broken previous report fails validation instead of throwing', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  previous.findings = 'oops';
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '撤销。' }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, false);
});

// 撤销 + 替换同时出现时仍属替换项，必须写全字段。
test('a withdrawn entry that also replaces still has to write every field', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const current = report(loaded.bundle);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings = [{ id: 'f2', status: 'withdrawn', revisionReason: '合并后撤销。', replacesFindingIds: ['f1'] }];
  const checked = validateReport(current, loaded.bundle, { previousReport: previous });
  assert.equal(checked.ok, false);
  assert(checked.errors.some(item => item.code === 'schema'), JSON.stringify(checked.errors));
});