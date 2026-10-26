import { validateAnnotations } from './annotations.mjs';
import { validateShape } from '../../review-core/src/schema.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const fail = () => { throw new Error('资料更新记录格式无效'); };
const text = value => { if (typeof value !== 'string') fail(); return value; };
const fields = (value, allowed) => {
  if (!object(value) || Object.keys(value).some(key => !allowed.includes(key))) fail();
};
const kinds = new Set(['node', 'repair', 'review']);
const reasons = new Set(['deleted', 'type-changed', 'content-changed', 'connections-changed', 'renamed',
  'structure-changed', 'target-unavailable', 'before-conflict', 'already-applied', 'dependencies-changed', 'scope-review',
  'node-patch-review', 'layout-review']);
export function migrationSource(value) {
  fields(value, ['projectId', 'bundleId', 'manifestSha256']);
  if (!object(value) || !text(value.bundleId) || !/^[a-f0-9]{64}$/u.test(text(value.manifestSha256))) fail();
  if (value.projectId !== JSON.stringify([value.bundleId, value.manifestSha256])) fail();
  return { projectId: value.projectId, bundleId: value.bundleId, manifestSha256: value.manifestSha256 };
}
function point(value) {
  if (!object(value) || !['x', 'y'].every(key => Number.isFinite(value[key]) && Math.abs(value[key]) < 1000000)) fail();
}
function repair(value) {
  const target = value?.target;
  if (!object(value) || !object(target) || !text(target.nodeUid)) fail();
  if (!['cutSourceIndex', 'textBoxSourceIndex', 'layoutIndex'].every(key => Number.isInteger(target[key]) && target[key] >= 0)) fail();
  if (target.reference?.fileId !== 'scene' || target.reference?.kind !== 'json-pointer' ||
      !/^\/nodes\/\d+\/part\/cuts\/\d+\/textBoxes\/\d+\/layouts\/\d+\/rawText$/u.test(text(target.reference.pointer))) fail();
  text(value.before); text(value.after);
  if (value.after.length > 1000000 || value.before === value.after) fail();
}
function nodePatch(value, source) {
  if (!object(value) || value.format !== 'ec-scene-node-patch' || value.version !== 1 || !Array.isArray(value.connections) ||
      value.source?.bundleId !== source.bundleId || value.source?.manifestSha256 !== source.manifestSha256) fail();
  for (const entry of value.connections) {
    // after.targetUid 允许为空，表示断开该槽；before 必须非空，否则无可断开的连接。
    if (!object(entry) || !text(entry.sourceUid) || !Number.isInteger(entry.slot) || entry.slot < 0 ||
        !object(entry.before) || !text(entry.before.targetUid) || !object(entry.after) || !text(entry.after.targetUid)) fail();
    if (!entry.before.targetUid && !entry.after.targetUid) fail();
    for (const key of ['conditionType', 'conditionCount', 'fadeType', 'fadeTime']) if (!Object.hasOwn(entry.before, key)) fail();
  }
  if (value.layout !== undefined) {
    const layout = value.layout;
    if (!object(layout) || !object(layout.canvas) || !Array.isArray(layout.nodes) || !layout.nodes.length ||
        !['width', 'height'].every(key => Number.isFinite(layout.canvas[key]) && layout.canvas[key] > 0)) fail();
    for (const entry of layout.nodes) {
      if (!object(entry) || !text(entry.uid) || !object(entry.before) || !object(entry.after)) fail();
      point(entry.before); point(entry.after);
    }
  }
  if (!value.connections.length && value.layout === undefined) fail();
}
function annotationLayout(value, source) {
  if (!object(value?.positions)) fail();
  const nodes = Object.keys(value.positions).map(id => ({ id }));
  const checked = validateAnnotations({ schemaVersion: '1.1-author', source, scope: { summary: '', expected: '', unfinished: '' },
    groups: [], nodes: [], layout: value }, { ...source, nodes });
  return checked.layout;
}
function notes(value, source) {
  if (!object(value?.layout?.positions)) fail();
  if (!Array.isArray(value.nodes)) fail();
  for (const node of value.nodes) if (!text(node?.uid)) fail();
  const nodes = [...new Set([...Object.keys(value.layout.positions), ...value.nodes.map(node => node?.uid)])].map(id => ({ id }));
  return validateAnnotations(value, { ...source, nodes });
}
function historyEntry(value) {
  fields(value, ['source', 'name', 'report', 'previous', 'snapshot']);
  const source = migrationSource(value?.source);
  text(value.name);
  if (!object(value.snapshot) || !object(value.snapshot.repairs)) fail();
  fields(value.snapshot, ['annotations', 'repairs', 'defaults', 'nodePatch']);
  notes(value.snapshot.annotations, source);
  const plan = value.snapshot.repairs;
  if (plan.format !== 'ec-scene-repair-plan' || plan.version !== 1 || !Array.isArray(plan.changes) || plan.changes.length > 1000 ||
      plan.source?.bundleId !== source.bundleId || plan.source?.manifestSha256 !== source.manifestSha256) fail();
  plan.changes.forEach(repair);
  if (new Set(plan.changes.map(item => item.target.reference.pointer)).size !== plan.changes.length) fail();
  if (value.snapshot.defaults !== undefined && (!object(value.snapshot.defaults) ||
      !['before', 'after'].every(key => [0, 1, 2, 4, 8].includes(value.snapshot.defaults[key])))) fail();
  if (value.snapshot.nodePatch !== undefined) nodePatch(value.snapshot.nodePatch, source);
  // 历史里的前版可来自更早批次，只核对结构；当前报告仍须属于该条记录的来源资料。
  if (value.report !== null) {
    if (!object(value.report) || validateShape('report', value.report).length ||
        value.report.source?.bundleId !== source.bundleId || value.report.source?.manifestSha256 !== source.manifestSha256) fail();
  }
  if (value.previous !== null && (!object(value.previous) || validateShape('report', value.previous).length)) fail();
}
function pendingEntry(value) {
  fields(value, ['id', 'kind', 'reason', 'source', 'nodeUid', 'status', 'details']);
  if (!object(value) || !text(value.id) || !kinds.has(value.kind) || !reasons.has(value.reason) ||
      !['pending', 'discarded'].includes(value.status) || (value.nodeUid !== null && !text(value.nodeUid))) fail();
  const source = migrationSource(value.source);
  if (!object(value.details)) fail();
  const details = value.details;
  if (Object.keys(details).some(key => !['name', 'currentBefore', 'oldPosition', 'repair', 'annotation', 'nodePatch', 'ecLayout'].includes(key))) fail();
  if (value.kind === 'repair' && details.repair === undefined) fail();
  if (details.name !== undefined) text(details.name);
  if (details.currentBefore !== undefined) text(details.currentBefore);
  if (details.oldPosition !== undefined) point(details.oldPosition);
  if (details.repair !== undefined) {
    repair(details.repair);
    if (details.repair.target.nodeUid !== value.nodeUid) fail();
  }
  if (details.nodePatch !== undefined) nodePatch(details.nodePatch, source);
  if (value.reason === 'node-patch-review' && details.nodePatch === undefined) fail();
  if (details.ecLayout !== undefined) {
    annotationLayout(details.ecLayout, source);
    if (details.ecLayout.layoutSource !== 'ec') fail();
  }
  if (value.reason === 'layout-review' && details.ecLayout === undefined) fail();
  if (details.annotation !== undefined) {
    const annotation = details.annotation;
    if (annotation.uid !== value.nodeUid || !Array.isArray(annotation.groups)) fail();
    validateAnnotations({ schemaVersion: '1.1-author', source, scope: { summary: '', expected: '', unfinished: '' },
      groups: annotation.groups.map(name => ({ name, note: '' })), nodes: [annotation] }, { ...source, nodes: [{ id: value.nodeUid }] });
  }
}
export function validateMigration(value) {
  fields(value, ['version', 'source', 'createdAt', 'pending', 'history']);
  if (!object(value) || value.version !== 1 || !Number.isFinite(Date.parse(text(value.createdAt))) ||
      !Array.isArray(value.pending) || !Array.isArray(value.history)) fail();
  migrationSource(value.source);
  value.pending.forEach(pendingEntry); value.history.forEach(historyEntry);
  const historicalSources = new Set(value.history.map(item => item.source.projectId));
  if (value.pending.some(item => !historicalSources.has(item.source.projectId))) fail();
  if (new Set(value.pending.map(item => item.id)).size !== value.pending.length) fail();
  if (!value.history.some(item => item.source.projectId === value.source.projectId)) fail();
  return structuredClone(value);
}
export function discardMigrationRecord(value, id) {
  const result = validateMigration(value), entry = result.pending.find(item => item.id === id);
  if (!entry) throw new Error('找不到待处理记录');
  entry.status = 'discarded';
  return result;
}
