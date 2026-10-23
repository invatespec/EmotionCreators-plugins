import { captureDirectory, openStoredDirectory } from './files.mjs';
import { createModel } from './model.mjs';
import { prepareProject, projectId } from './projects.mjs';
import { validateAnnotations } from './annotations.mjs';
import { emptyRepairs, validateRepairs, targetForLayout, resolveRepairTarget } from './repairs.mjs';
import { gameLayout } from './ui/game-layout.js';
import { automaticLayout } from './ui/domain.js';
import { ecFrames } from './ec-layout.mjs';
import { compareNodes, dependenciesChanged } from './migration-compare.mjs';
import { emptyNodePatch } from './node-patch.mjs';
export { discardMigrationRecord, validateMigration } from './migration-metadata.mjs';

const identity = data => ({ projectId: projectId(data), bundleId: data.bundleId, manifestSha256: data.manifestSha256 });
function record(source, kind, reason, nodeUid, details, suffix = '') {
  return { id: JSON.stringify([source.projectId, kind, reason, nodeUid, suffix]), kind, reason, source,
    nodeUid, status: 'pending', details: structuredClone(details) };
}
const hasNodePatch = patch => Boolean(patch.connections.length || patch.layout);
function migrateAnnotations(old, data, diff, source, pending) {
  const targetSource = { bundleId: data.bundleId, manifestSha256: data.manifestSha256 };
  const matched = new Set(diff.matched.map(item => item.uid));
  const targetGame = gameLayout(data), targetFrames = old.layout.layoutSource === 'ec' ? ecFrames(data) : null;
  let base, layoutSource;
  if (old.layout.layoutSource === 'ec' && targetFrames) {
    base = { positions: targetFrames, regions: [] }; layoutSource = 'ec';
  } else {
    base = targetGame || automaticLayout(data); layoutSource = targetGame ? 'game' : 'automatic';
    if (old.layout.layoutSource === 'ec') pending.push(record(source, 'review', 'layout-review', null, { ecLayout: old.layout }));
  }
  if (old.layout.layoutSource !== 'ec' || layoutSource === 'ec')
    for (const [uid, point] of Object.entries(old.layout.positions)) if (matched.has(uid)) base.positions[uid] = structuredClone(point);
  return validateAnnotations({ ...old, source: targetSource, nodes: old.nodes.filter(item => matched.has(item.uid)),
    layout: { positions: base.positions, camera: old.layout.camera, layoutSource,
      ...(layoutSource === 'ec' && old.layout.canvas ? { canvas: old.layout.canvas } : {}) } }, data);
}
function unique(values, predicate) {
  const found = values.filter(predicate);
  if (found.length !== 1) throw new Error('位置缺失或不唯一');
  return found[0];
}
function remapTarget(data, target) {
  const node = unique(data.nodes, item => item.id === target.nodeUid);
  const cut = unique(node.cuts, item => item.sourceIndex === target.cutSourceIndex);
  const box = unique(cut.boxes, item => item.sourceIndex === target.textBoxSourceIndex);
  const layout = unique(box.layouts, item => item.layoutIndex === target.layoutIndex);
  return targetForLayout(data, node.id, node.cuts.indexOf(cut), box.rawIndex, layout.rawIndex);
}
function migrateRepairs(old, data, diff, source, pending) {
  const plan = emptyRepairs(data), counts = { migrated: 0, conflicted: 0, applied: 0 };
  const deleted = new Set(diff.deleted.map(item => item.uid)), conflicted = new Set(diff.conflicted.map(item => item.uid));
  const structural = new Set(diff.changed.filter(item => item.reasons.includes('structure-changed')).map(item => item.uid));
  const contentChanged = new Set(diff.changed.filter(item => item.reasons.includes('content-changed')).map(item => item.uid));
  for (const entry of old.changes) {
    const uid = entry.target.nodeUid, details = { repair: entry };
    let reason = deleted.has(uid) ? 'deleted' : conflicted.has(uid) ? 'type-changed' : structural.has(uid) ? 'structure-changed' : null;
    if (!reason) {
      try {
        const target = remapTarget(data, entry.target), current = resolveRepairTarget(data, target);
        if (current.before === entry.after) reason = 'already-applied';
        else if (current.before !== entry.before) { reason = 'before-conflict'; details.currentBefore = current.before; }
        // 源序号不是永久身份；节点其它文本变化时，重复原文不能证明位置未移动。
        else if (contentChanged.has(uid)) reason = 'content-changed';
        else { plan.changes.push({ ...entry, target }); counts.migrated++; continue; }
      } catch { reason = 'target-unavailable'; }
    }
    counts[reason === 'already-applied' ? 'applied' : 'conflicted']++;
    pending.push(record(source, 'repair', reason, uid, details, entry.target.reference.pointer));
  }
  return { repairs: validateRepairs(plan, data), counts };
}
function nodeRecords(old, diff, source) {
  const pending = [];
  const notes = new Map(old.nodes.map(item => [item.uid, item]));
  for (const [items, reason] of [[diff.deleted, 'deleted'], [diff.conflicted, 'type-changed']]) {
    for (const item of items) {
      const details = { name: item.name };
      if (notes.has(item.uid)) details.annotation = notes.get(item.uid);
      if (Object.hasOwn(old.layout.positions, item.uid)) details.oldPosition = old.layout.positions[item.uid];
      pending.push(record(source, 'node', reason, item.uid, details));
    }
  }
  for (const item of diff.changed) for (const reason of item.reasons) pending.push(record(source, 'node', reason, item.uid, { name: item.name }));
  for (const item of diff.renamed) pending.push(record(source, 'node', 'renamed', item.uid, { name: item.name }));
  return pending;
}
export async function prepareMigration(sourceProject, targetFiles) {
  const sourceSnapshot = structuredClone(sourceProject), old = await prepareProject(sourceSnapshot);
  const files = await captureDirectory(targetFiles), result = await openStoredDirectory(files);
  if (!result.ok) throw new Error(`新资料未通过核验：${result.errors.map(error => error.message).join('；')}`);
  const data = createModel(result.bundle), source = identity(old.data);
  if (projectId(data) === sourceSnapshot.id) throw new Error('新旧资料身份相同，无需更新');
  const diff = compareNodes(old.result.bundle, result.bundle), pending = nodeRecords(old.annotations, diff, source);
  const dependencies = dependenciesChanged(old.result.bundle, result.bundle);
  if (dependencies) pending.push(record(source, 'review', 'dependencies-changed', null, {}));
  if (Object.values(old.annotations.scope).some(Boolean)) pending.push(record(source, 'review', 'scope-review', null, {}));
  const migrated = migrateRepairs(old.repairs, data, diff, source, pending);
  if (hasNodePatch(old.nodePatch)) pending.push(record(source, 'review', 'node-patch-review', null, { nodePatch: old.nodePatch }));
  if (old.nodePatchReview) pending.push(record(source, 'review', 'node-patch-review', null,
    { nodePatch: old.nodePatchReview }, 'historical-self'));
  const annotations = migrateAnnotations(old.annotations, data, diff, source, pending);
  const history = [...(old.migration?.history || []), { source, name: sourceSnapshot.name, report: sourceSnapshot.report,
    previous: sourceSnapshot.previous, snapshot: { annotations: old.annotations, repairs: old.repairs, defaults: sourceSnapshot.defaults,
      ...(hasNodePatch(old.nodePatch) ? { nodePatch: old.nodePatch } : {}) } }];
  const allPending = new Map([...pending, ...(old.migration?.pending || [])].map(item => [item.id, item]));
  const project = { version: 1, id: projectId(data), source: { bundleId: data.bundleId, manifestSha256: data.manifestSha256 },
    name: sourceSnapshot.name, updatedAt: new Date().toISOString(), files, report: null, previous: null,
    annotations, repairs: migrated.repairs, defaults: structuredClone(sourceSnapshot.defaults),
    nodePatch: emptyNodePatch(data),
    migration: { version: 1, source, createdAt: new Date().toISOString(), pending: [...allPending.values()], history } };
  await prepareProject(project);
  return { project, summary: { source: { ...source, title: old.data.title }, target: { ...identity(data), title: data.title },
    ...diff, repairs: migrated.counts, dependenciesChanged: dependencies } };
}
