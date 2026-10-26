import { validateEcCanvas } from './ec-layout.mjs';

const FORMAT = 'ec-scene-node-patch';
const VERSION = 1;
const MAX_BYTES = 64 * 1024 * 1024;
const MAX_CONNECTIONS = 1000;
const MAX_LAYOUT_NODES = 1000;
const CONDITION_TYPES = new Set(['NotUsed', 'ConnectPart', 'NextPart_Root01', 'NextPart_Root02', 'NextPart_Root03',
  'NextPart_Root04', 'Select_Root01', 'Select_Root02', 'Select_Root03', 'Select_Root04', 'AllRoot']);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const finiteFloat = value => Number.isFinite(value) && Number.isFinite(Math.fround(value));
const finitePoint = value => object(value) && ['x', 'y'].every(key => finiteFloat(value[key]));
const int32 = value => Number.isInteger(value) && value >= -2147483648 && value <= 2147483647;
const fail = message => { throw new Error(message); };
const identity = data => ({ bundleId: data.bundleId, manifestSha256: data.manifestSha256 });
const connectionKey = (uid, slot) => JSON.stringify([uid, slot]);
const exactKeys = (value, allowed, label) => {
  if (!object(value) || Object.keys(value).some(key => !allowed.includes(key))) fail(`${label} 包含未知字段或格式无效`);
};

export function assertNodePatchFileSize(bytes) {
  if (!Number.isInteger(bytes) || bytes <= 0) fail('节点补丁文件必须非空');
  if (bytes > MAX_BYTES) fail('节点补丁文件超过 64 MiB');
}

export function emptyNodePatch(data) {
  return { format: FORMAT, version: VERSION, source: identity(data), connections: [] };
}

function currentOutput(data, uid, slot) {
  const node = data.nodes.find(item => item.id === uid);
  if (!node) fail(`找不到源节点：${uid}`);
  const output = node.outputs?.find(item => item.slot === slot);
  if (!output || typeof output.targetUid !== 'string') fail(`${node.name} 的出口 ${slot + 1} 不存在`);
  return { node, output };
}

function validateConnection(entry, data, allowLegacySelfTarget = false) {
  exactKeys(entry, ['sourceUid', 'slot', 'before', 'after'], '连接补丁条目');
  if (typeof entry.sourceUid !== 'string' || !entry.sourceUid || !Number.isInteger(entry.slot) || entry.slot < 0 || entry.slot > 4)
    fail('连接补丁来源或 slot 必须在 0..4');
  exactKeys(entry.before, ['targetUid', 'conditionType', 'conditionCount', 'fadeType', 'fadeTime'], '连接 before');
  exactKeys(entry.after, ['targetUid'], '连接 after');
  // after 为空表示断开该槽：只允许断开原本有目标的连接，没有目标可断的是无变化项。
  if (typeof entry.after.targetUid !== 'string') fail('连接补丁条目格式无效');
  if (typeof entry.before.targetUid !== 'string' ||
    !CONDITION_TYPES.has(entry.before.conditionType) || !int32(entry.before.conditionCount) ||
    !int32(entry.before.fadeType) || !finiteFloat(entry.before.fadeTime)) fail('连接 before 字段类型或 conditionType 无效');
  const { node, output } = currentOutput(data, entry.sourceUid, entry.slot);
  const disconnects = entry.after.targetUid === '';
  if (disconnects && !entry.before.targetUid) fail(`${node.name} 出口 ${entry.slot + 1} 本来就没有连接，无法断开`);
  if (!disconnects && !entry.before.targetUid && !usableSlots(node).includes(entry.slot)) fail(`${node.name} 的出口 ${entry.slot + 1} 当前不可用`);
  const keys = ['targetUid', 'conditionType', 'conditionCount', 'fadeType', 'fadeTime'];
  if (keys.some(key => !Object.is(entry.before[key], output[key]))) fail(`${node.name} 出口 ${entry.slot + 1} 的原连接或槽参数已变化`);
  if (entry.after.targetUid === output.targetUid) fail(`${node.name} 出口 ${entry.slot + 1} 的新目标与原目标相同`);
  if (!disconnects) {
    const targetNode = data.nodes.find(item => item.id === entry.after.targetUid);
    if (!targetNode) fail(`连接目标不存在：${entry.after.targetUid}`);
    // Start 没有输入端口，游戏里连线会停在这一步；此处在导出前就拒绝，避免生成接不上的补丁。
    if (!acceptsLink(targetNode)) fail(`${targetNode.name} 是开始节点，没有输入端口，不能作为连接目标`);
    if (!allowLegacySelfTarget && entry.after.targetUid === entry.sourceUid) fail(`${node.name} 出口 ${entry.slot + 1} 的普通连接新目标不能是来源节点自身`);
  }
  return { sourceUid: entry.sourceUid, slot: entry.slot,
    before: Object.fromEntries(keys.map(key => [key, output[key]])), after: { targetUid: entry.after.targetUid } };
}

function validateLayout(layout, data, checkBounds) {
  exactKeys(layout, ['canvas', 'nodes'], '布局');
  exactKeys(layout.canvas, ['width', 'height'], '布局画布');
  if (!Array.isArray(layout.nodes) || !layout.nodes.length || layout.nodes.length > MAX_LAYOUT_NODES) fail('布局节点数量须在 1..1000');
  if (!finiteFloat(layout.canvas.width) || layout.canvas.width <= 0 || !finiteFloat(layout.canvas.height) || layout.canvas.height <= 0)
    fail('布局画布宽高必须是有限正数');
  const seen = new Set(), positions = {};
  const nodes = layout.nodes.map(entry => {
    exactKeys(entry, ['uid', 'before', 'after'], '布局节点');
    exactKeys(entry.before, ['x', 'y'], '布局 before');
    exactKeys(entry.after, ['x', 'y'], '布局 after');
    if (!object(entry) || typeof entry.uid !== 'string' || seen.has(entry.uid) || !finitePoint(entry.before) || !finitePoint(entry.after)) fail('布局节点条目无效或重复');
    seen.add(entry.uid);
    const node = data.nodes.find(item => item.id === entry.uid);
    if (!node || !node.gamePosition || !Object.is(entry.before.x, node.gamePosition.x) || !Object.is(entry.before.y, node.gamePosition.y)) fail(`节点 ${entry.uid} 的原生位置与当前资料不一致`);
    if (Object.is(entry.before.x, entry.after.x) && Object.is(entry.before.y, entry.after.y)) fail(`节点 ${entry.uid} 的布局没有变化`);
    positions[entry.uid] = { ...entry.after };
    return { uid: entry.uid, before: { ...entry.before }, after: { ...entry.after } };
  });
  for (const node of data.nodes) positions[node.id] ||= { ...node.gamePosition };
  if (checkBounds) validateEcCanvas(data, positions, layout.canvas);
  return { canvas: { width: layout.canvas.width, height: layout.canvas.height }, nodes };
}

function validateNodePatchValue(value, data, { checkBounds = true, requireChanges = false, allowLegacySelfTarget = false } = {}) {
  exactKeys(value, ['format', 'version', 'source', 'connections', 'layout'], '节点补丁');
  exactKeys(value.source, ['bundleId', 'manifestSha256'], '节点补丁来源');
  if (!object(value) || value.format !== FORMAT || value.version !== VERSION) fail('不支持的节点补丁格式');
  if (value.source?.bundleId !== data.bundleId || value.source?.manifestSha256 !== data.manifestSha256) fail('节点补丁属于另一批资料，未覆盖当前内容');
  if (typeof value.source.bundleId !== 'string' || !value.source.bundleId || typeof value.source.manifestSha256 !== 'string' || !/^[a-f0-9]{64}$/iu.test(value.source.manifestSha256))
    fail('节点补丁来源字段无效');
  const rawConnections = value.connections == null ? [] : value.connections;
  if (!Array.isArray(rawConnections) || rawConnections.length > MAX_CONNECTIONS) fail('连接补丁列表须为最多 1000 项的数组');
  const seen = new Set();
  const connections = rawConnections.map(entry => {
    const key = connectionKey(entry?.sourceUid, entry?.slot);
    if (seen.has(key)) fail('同一输出槽不能重复修改');
    seen.add(key);
    return validateConnection(entry, data, allowLegacySelfTarget);
  });
  const layout = value.layout == null ? undefined : validateLayout(value.layout, data, checkBounds);
  if (requireChanges && connections.length === 0 && !layout) fail('节点补丁文件至少需要一项连接或布局修改');
  return { format: FORMAT, version: VERSION, source: identity(data), connections, ...(layout ? { layout } : {}) };
}

export function validateNodePatch(value, data, { checkBounds = true, requireChanges = false } = {}) {
  return validateNodePatchValue(value, data, { checkBounds, requireChanges });
}

export function validateNodePatchForRecovery(value, data, { checkBounds = true, requireChanges = false } = {}) {
  return validateNodePatchValue(value, data, { checkBounds, requireChanges, allowLegacySelfTarget: true });
}

// ADV 的运行时出口由最后一个 CUT 的选项数决定（有选项走选项槽，否则只读 slot0）；H 用 endConditionType。
// 系统节点只有单向端口：Start 无输入、End 无输出。漏判会让网页给出游戏里接不上的连接。
export function acceptsLink(node) {
  return Boolean(node) && node.kind !== 'Start';
}

export function emitsLink(node) {
  return Boolean(node) && node.kind !== 'End';
}

export function usableSlots(node) {
  if (!node || !emitsLink(node)) return [];
  if (node.kind === 'ADV') {
    // 优先用快照里的 optionCount，与导出侧同源；缺失时退回按选项文案计数。
    const cuts = node.cuts || [];
    const last = cuts.length ? cuts[cuts.length - 1] : null;
    // CUT 缺失说明 Part 没读到，无法判定运行时出口；与导出侧一致地不给空槽入口。
    if (!last) return [];
    const optionCount = !last.endCut ? 0
      : Number.isInteger(last.optionCount) ? last.optionCount : last.choices?.length || 0;
    return Array.from({ length: Math.min(5, optionCount > 0 ? optionCount : 1) }, (_, index) => index);
  }
  return (node.outputs || []).filter(output => output.conditionType !== 'NotUsed').map(output => output.slot);
}

export function patchEdges(edges, patch, data) {
  const replacements = new Map(patch.connections.map(entry => [connectionKey(entry.sourceUid, entry.slot), entry.after.targetUid]));
  const mapped = edges.flatMap(edge => {
    if (edge.kind !== 'normal' || edge.slot == null) return [edge];
    const key = connectionKey(edge.from, edge.slot);
    if (!replacements.has(key)) return [edge];
    const targetUid = replacements.get(key);
    // 断开项没有目标边，直接从预览里移除。
    if (!targetUid) return [];
    const target = data.nodes.find(node => node.id === targetUid);
    return [{ ...edge, to: targetUid, toCut: target?.cuts?.findIndex(cut => cut.sourceIndex === 0) ?? null,
      targetName: target?.name || '未收录节点', reason: null }];
  });
  const added = createdEdges(edges, patch, data);
  return added.length === 0 ? mapped : [...mapped, ...added];
}

// 空槽补丁会新建普通边；路线偏移与项目恢复都要把它们当作已知边，否则整份路线被判为未知。
export function createdEdges(edges, patch, data) {
  if (!patch?.connections?.length || !Array.isArray(data?.nodes)) return [];
  const existing = new Set((edges || []).filter(edge => edge.kind === 'normal').map(edge => connectionKey(edge.from, edge.slot)));
  return patch.connections
    .filter(entry => entry.after?.targetUid && !entry.before?.targetUid && !existing.has(connectionKey(entry.sourceUid, entry.slot)))
    .map(entry => createdEdge(entry, data));
}

function createdEdge(entry, data) {
  const source = data.nodes.find(node => node.id === entry.sourceUid);
  const target = data.nodes.find(node => node.id === entry.after.targetUid);
  const endCuts = (source?.cuts || []).map((cut, index) => (cut.endCut ? index : -1)).filter(index => index >= 0);
  return { id: `patch:${entry.sourceUid}:${entry.slot}`, kind: 'normal', from: entry.sourceUid,
    to: entry.after.targetUid, slot: entry.slot, fromCut: endCuts.length === 1 ? endCuts[0] : null,
    toCut: target?.cuts?.findIndex(cut => cut.sourceIndex === 0) ?? null,
    label: '', targetName: target?.name || '未收录节点', inactive: false, reason: null };
}
