import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { loadBundle, portablePath, sha256 } from '../src/bundle.mjs';
import { directoryReader } from '../src/node-files.mjs';
import { validateReport } from '../src/report.mjs';
import { resolveEvidence, resolvePointer, legacyReference } from '../src/evidence.mjs';
import { describeLocation, optionMappings } from '../src/locations.mjs';
import { fixture, report, ref, seal, encode } from './fixtures.mjs';

test('valid bundle binds a report and human locations to actual evidence', async () => {
  const { loaded } = await fixture();
  assert.deepEqual(loaded.errors, []);
  assert.equal(validateReport(report(loaded.bundle), loaded.bundle).ok, true);
  assert.match(describeLocation(loaded.bundle, ref).label, /起点 · CUT 1/u);
  const external = { fileId: 'dep-0001', kind: 'text-lines', startLine: 2, endLine: 2 };
  assert.equal(resolveEvidence(loaded.bundle, external), '<set name="x" value="1"/>');
  assert.equal(describeLocation(loaded.bundle, external).incoming.length, 1);
});

test('a moved directory keeps the same source identity', async () => {
  const { data } = await fixture();
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'scene-review-'));
  const first = path.join(root, 'first'), moved = path.join(root, 'moved');
  for (const [relative, bytes] of data) {
    const file = path.join(first, relative);
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, bytes);
  }
  const before = await loadBundle({ readFile: directoryReader(first) });
  await fs.rename(first, moved);
  const after = await loadBundle({ readFile: directoryReader(moved) });
  assert.equal(before.ok, true);
  assert.equal(after.ok, true);
  assert.equal(after.bundle.manifestSha256, before.bundle.manifestSha256);
  assert.equal(validateReport(report(before.bundle), after.bundle).ok, true);
});

test('modified dependency bytes are rejected even when title and timestamps match', async () => {
  const f = await fixture();
  f.data.set('dependencies/dep-0001.xml', encode('<script/>'));
  const loaded = await loadBundle(f);
  assert.equal(loaded.ok, false);
  assert(loaded.errors.some(e => e.code === 'member-hash'));
  assert.equal(validateReport(report(f.loaded.bundle), loaded.bundle).ok, false);
});

test('missing completion checksum and modified manifest are rejected', async () => {
  const f = await fixture();
  f.data.delete('bundle.sha256');
  assert.equal((await loadBundle(f)).ok, false);
  f.data.set('bundle.sha256', encode('0'.repeat(64)));
  assert((await loadBundle(f)).errors.some(e => e.code === 'manifest-hash'));
});

test('duplicate IDs, member traversal and duplicate paths are rejected before reading', async () => {
  for (const change of [
    f => { f.manifest.files[2].path = '../a.xml'; },
    f => { f.manifest.files[2].path = 'SCENE.json'; },
    f => { f.manifest.files[2].fileId = 'manifest'; },
    f => { f.manifest.files[2].fileId = 'scene'; },
  ]) {
    const f = await fixture();
    change(f);
    await seal(f.data, f.manifest);
    assert.equal((await loadBundle(f)).ok, false);
  }
  assert.equal(portablePath('C:/x'), false);
  assert.equal(portablePath('dependencies/a.xml'), true);
});

test('dependency gaps are visible and do not invalidate an otherwise valid package', async () => {
  const f = await fixture();
  f.manifest.dependencies.push({ from: ref, requestedPath: '#name#', targetFileId: null, status: 'dynamic', reason: '未知文件名', context: [] });
  await seal(f.data, f.manifest);
  const loaded = await loadBundle(f);
  assert.equal(loaded.ok, true);
  assert(loaded.warnings.some(w => w.code === 'dynamic'));
});

test('old packages remain readable and invalid optional geometry only disables exact layout', async () => {
  const old = await fixture();
  assert.equal(old.loaded.ok, true);
  assert.equal(old.loaded.bundle.geometry.available, false);
  assert.equal(old.loaded.warnings.some(w => w.code === 'geometry-invalid'), false);

  const bad = await fixture();
  bad.scene.geometry = { version: 1, coordinateSpace: 'grid-ui-top-left-y-down', canvas: { width: 100, height: 100 },
    nodes: [{ uid: 'p1', status: 'exact', frame: null, positionOffset: null, input: null, outputs: [] }] };
  bad.data.set('scene.json', encode(JSON.stringify(bad.scene)));
  const member = bad.manifest.files.find(file => file.role === 'scene');
  member.byteLength = bad.data.get('scene.json').length;
  member.sha256 = await sha256(bad.data.get('scene.json'));
  await seal(bad.data, bad.manifest);
  const loaded = await loadBundle(bad);
  assert.equal(loaded.ok, true);
  assert.equal(loaded.bundle.geometry.available, false);
  assert(loaded.warnings.some(w => w.code === 'geometry-invalid'));
});

test('unknown versions and invalid report fields fail schema validation', async () => {
  const { loaded } = await fixture();
  for (const modify of [
    r => { r.version = 2; },
    r => { r.findings[0].severity = 'certain'; },
    r => { delete r.coverage; },
    r => { r.findings[0].evidence = []; },
  ]) {
    const r = report(loaded.bundle);
    modify(r);
    assert.equal(validateReport(r, loaded.bundle).ok, false);
  }
});

test('wrong source and invalid pointers, quotes or line ranges cannot claim a location', async () => {
  const { loaded } = await fixture();
  for (const modify of [
    r => { r.source.bundleId = 'other'; },
    r => { r.source.manifestSha256 = 'a'.repeat(64); },
    r => { r.findings[0].locations = [{ ...ref, pointer: '/nodes/999' }]; },
    r => { r.findings[0].evidence[0].quote = '不存在的内容'; },
    r => { r.findings[0].locations = [{ fileId: 'dep-0001', kind: 'text-lines', startLine: 3, endLine: 2 }]; },
  ]) {
    const r = report(loaded.bundle);
    modify(r);
    assert.equal(validateReport(r, loaded.bundle).ok, false);
  }
});

test('current report revisions require explicit withdrawal, replacement or retention', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle), current = structuredClone(previous);
  current.reportId = 'r2';
  current.supersedesReportId = 'r1';
  current.findings = [];
  assert(validateReport(current, loaded.bundle, { previousReport: previous }).errors.some(e => e.code === 'revision-omission'));
  current.findings = [{ ...previous.findings[0], status: 'withdrawn', revisionReason: '补充证据后撤销。' }];
  assert.equal(validateReport(current, loaded.bundle, { previousReport: previous }).ok, true);
  const standalone = validateReport(current, loaded.bundle);
  assert.equal(standalone.ok, true);
  assert(standalone.warnings.some(w => w.code === 'history-unavailable'));
});

test('duplicate, self-replacing and inconsistent replacement identities fail', async () => {
  const { loaded } = await fixture();
  const previous = report(loaded.bundle), current = structuredClone(previous);
  current.reportId = 'r2'; current.supersedesReportId = 'r1';
  current.findings.push({ ...current.findings[0], id: 'f2', replacesFindingIds: ['f1'], revisionReason: '合并问题。' });
  assert.equal(validateReport(current, loaded.bundle, { previousReport: previous }).ok, false);
  current.findings.shift();
  assert.equal(validateReport(current, loaded.bundle, { previousReport: previous }).ok, true);
  current.supersedesReportId = 'r2';
  assert.equal(validateReport(current, loaded.bundle).ok, false);
});

test('source order, not rendering order, assigns option slots; gaps stay unknown', async () => {
  const { scene } = await fixture();
  const node = scene.nodes[0], cut = node.part.cuts[0];
  const mapped = optionMappings(node, cut);
  assert.equal(mapped.status, 'conditional');
  assert.deepEqual(mapped.options.map(o => [o.sourceIndex, o.slot, o.targetUid]), [[1, 0, 'a'], [2, 1, 'b']]);
  cut.textBoxes[0].sourceIndex = 8;
  assert.equal(optionMappings(node, cut).status, 'unknown');
});

test('standard pointers and old action suffixes retain distinct semantics', () => {
  assert.equal(resolvePointer({ 'a/b': { '~': 2 } }, '/a~1b/~0'), 2);
  assert.throws(() => resolvePointer([], '/01'));
  assert.throws(() => resolvePointer({}, '/__proto__'));
  const converted = legacyReference('#/nodes/0/command:add@35');
  assert.equal(converted.reference.pointer, '/nodes/0');
  assert.equal(converted.action, 'command:add@35');
  assert.throws(() => resolvePointer({}, '/nodes/0/command:add@35'));
});
