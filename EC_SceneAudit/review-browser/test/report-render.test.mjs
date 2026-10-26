import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, report } from '../../review-core/test/fixtures.mjs';
import { checkReport } from '../src/files.mjs';
import { adaptFindings } from '../src/locations.mjs';

const model = { nodes: [{ id: 'p1', name: '起点', cuts: [{ rawIndex: 0, sourceIndex: 0 }] }] };

test('checkReport exposes the expanded findings and keeps the original report', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const compact = report(loaded.bundle);
  compact.reportId = 'r2'; compact.supersedesReportId = 'r1';
  compact.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '补充证据后撤销。' }];
  const checked = checkReport(compact, loaded.bundle, previous);
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert.equal(checked.mode, 'report');
  // 原始报告保持精简，展开只在结果里，落盘内容不受影响。
  assert.equal(compact.findings[0].title, undefined);
  assert.equal(checked.expanded.findings[0].title, previous.findings[0].title);
});

test('the withdrawn area renders an expanded finding with its inherited title', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle);
  const compact = report(loaded.bundle);
  compact.reportId = 'r2'; compact.supersedesReportId = 'r1';
  compact.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '补充证据后撤销。' }];
  // app.js 交给展示层的是展开结果；这里验证它足以渲染撤销区。
  const findings = adaptFindings(loaded.bundle, model, checkReport(compact, loaded.bundle, previous).expanded);
  const withdrawn = findings.find(item => item.status === 'withdrawn');
  assert.equal(withdrawn.title, previous.findings[0].title);
  assert.equal(withdrawn.revision, '补充证据后撤销。');
  assert.equal(withdrawn.locations.length, 1);
});

test('a withdrawal that cannot be filled in still renders without crashing', async () => {
  const { loaded } = await fixture();
  const second = report(loaded.bundle);
  second.reportId = 'r2'; second.supersedesReportId = 'r1';
  second.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '第一次撤销。' }];
  const third = report(loaded.bundle);
  third.reportId = 'r3'; third.supersedesReportId = 'r2';
  third.findings = [{ id: 'f1', status: 'withdrawn', revisionReason: '再次确认撤销。' }];
  const checked = checkReport(third, loaded.bundle, second);
  assert.equal(checked.ok, true, JSON.stringify(checked.errors));
  assert(checked.warnings.some(item => item.code === 'inherits-incomplete'));
  const withdrawn = adaptFindings(loaded.bundle, model, checked.expanded).find(item => item.status === 'withdrawn');
  assert.equal(withdrawn.title, undefined);
  assert.deepEqual(withdrawn.locations, []);
});
