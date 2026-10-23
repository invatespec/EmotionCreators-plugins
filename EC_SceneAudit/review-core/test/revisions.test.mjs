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

test('portable generated examples accept current/revised reports and reject mismatches', async () => {
  const root = new URL('../../ec-scene-review/Reference/examples/', import.meta.url);
  const read = async name => JSON.parse(await fs.readFile(new URL(name, root), 'utf8'));
  const loaded = await loadBundle({ readFile: directoryReader(fileURLToPath(new URL('bundle/', root))) });
  assert.equal(loaded.ok, true, JSON.stringify(loaded.errors));
  const first = await read('report-r1.json');
  assert.equal(validateReport(first, loaded.bundle).ok, true);
  const second = await read('report-r2.json');
  assert.equal(validateReport(second, loaded.bundle, { previousReport: first }).ok, true);
  assert.equal(validateReport(await read('invalid-wrong-source.json'), loaded.bundle).ok, false);
  assert.equal(validateReport(await read('invalid-reference.json'), loaded.bundle).ok, false);
});