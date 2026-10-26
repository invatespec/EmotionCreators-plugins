import test from 'node:test';
import assert from 'node:assert/strict';
import { usableSlots, validateNodePatch, patchEdges, createdEdges, acceptsLink, emitsLink } from '../src/node-patch.mjs';
import { validateRouteOffsets } from '../src/web-layout.mjs';

const slotValue = (slot, type) => ({ slot, targetUid: '', conditionType: type, conditionCount: 0, fadeType: 0, fadeTime: 0 });
// 与 createModel 一致：CUT 同时带 optionCount（游戏字段）和 choices（选项文案）。
const cut = (optionCount, endCut = true, counted = optionCount) => ({
  sourceIndex: 0, endCut, optionCount: counted,
  choices: Array.from({ length: optionCount }, (_, index) => `c${index}`)
});
const node = (id, kind, optionCount, outputs, cuts = [cut(optionCount)]) => ({
  id, name: id, kind, gamePosition: { x: 0, y: 0 }, cuts, outputs
});
const data = {
  bundleId: 'b', manifestSha256: '0'.repeat(64),
  nodes: [
    node('A', 'ADV', 0, [0, 1, 2, 3, 4].map(slot => slotValue(slot, slot === 0 ? 'ConnectPart' : 'NotUsed'))),
    node('B', 'ADV', 2, [0, 1, 2, 3, 4].map(slot => slotValue(slot, 'ConnectPart'))),
    node('C', 'ADV', 0, []),
    // 末尾 CUT 不是 endCut：运行时按无选项处理，只读 slot0。
    node('D', 'ADV', 3, [0, 1, 2, 3, 4].map(slot => slotValue(slot, 'NotUsed')), [cut(3), cut(0, false)]),
    // 系统节点只有单向端口：Start 无输入，End 无输出。
    { id: 'node_start', name: '開始', kind: 'Start', gamePosition: { x: 0, y: 0 }, cuts: [],
      outputs: [0, 1, 2, 3, 4].map(slot => slotValue(slot, slot === 0 ? 'ConnectPart' : 'NotUsed')) },
    { id: 'node_end', name: '終了', kind: 'End', gamePosition: { x: 0, y: 0 }, cuts: [],
      outputs: [0, 1, 2, 3, 4].map(slot => slotValue(slot, slot === 0 ? 'ConnectPart' : 'NotUsed')) }
  ]
};
const patchOn = (sourceUid, slot, conditionType, targetUid) => ({
  format: 'ec-scene-node-patch', version: 1,
  source: { bundleId: 'b', manifestSha256: '0'.repeat(64) },
  connections: [{ sourceUid, slot,
    before: { targetUid: '', conditionType, conditionCount: 0, fadeType: 0, fadeTime: 0 },
    after: { targetUid } }]
});

test('usable slots follow the last cut options', () => {
  assert.deepEqual(usableSlots(data.nodes[0]), [0], 'no options exposes slot 0 only');
  assert.deepEqual(usableSlots(data.nodes[1]), [0, 1], 'options expose 0..count-1');
  assert.deepEqual(usableSlots(data.nodes[3]), [0], 'non-end last cut exposes slot 0 only');
  assert.deepEqual(usableSlots({ kind: 'ADV', cuts: [] }), [], 'missing cuts give no empty-slot entry');
  assert.deepEqual(usableSlots({ kind: 'ADV', cuts: [cut(4, true, 5)] }), [0, 1, 2, 3, 4],
    'snapshot optionCount wins over option texts, matching the exporter');
  assert.deepEqual(usableSlots({ kind: 'H', outputs: [{ slot: 0, conditionType: 'ConnectPart' }, { slot: 1, conditionType: 'NotUsed' }] }), [0], 'H filters NotUsed');
});

test('empty usable slot accepts a new target and renders an edge', () => {
  const safe = validateNodePatch(patchOn('A', 0, 'ConnectPart', 'B'), data, { requireChanges: true });
  const edges = patchEdges([], safe, data);
  assert.equal(edges.length, 1);
  assert.equal(edges[0].from, 'A');
  assert.equal(edges[0].to, 'B');
  assert.equal(edges[0].fromCut, 0);
});

test('option slot accepts a new target within the option count', () => {
  const safe = validateNodePatch(patchOn('B', 1, 'ConnectPart', 'C'), data, { requireChanges: true });
  assert.equal(patchEdges([], safe, data)[0].to, 'C');
});

test('unavailable or out-of-range empty slots are rejected', () => {
  assert.throws(() => validateNodePatch(patchOn('A', 1, 'NotUsed', 'B'), data), /当前不可用/, 'NotUsed slot rejected');
  assert.throws(() => validateNodePatch(patchOn('B', 2, 'ConnectPart', 'C'), data), /当前不可用/, 'slot beyond option count rejected');
  assert.throws(() => validateNodePatch(patchOn('D', 1, 'NotUsed', 'B'), data), /当前不可用/, 'slot beyond non-end last cut rejected');
});

test('empty after target on an already empty slot and self target stay rejected', () => {
  // 空 after 现在表示「设为空槽」，但 A 的 slot0 本来就没有连接，属于无变化项。
  assert.throws(() => validateNodePatch(patchOn('A', 0, 'ConnectPart', ''), data), /无法断开/, 'empty after on an empty slot rejected');
  assert.throws(() => validateNodePatch(patchOn('A', 0, 'ConnectPart', 'A'), data), /来源节点自身/, 'self target rejected');
});

test('system nodes expose only their one-way port', () => {
  assert.deepEqual(usableSlots(data.nodes.find(node => node.id === 'node_end')), [],
    'end node has no output port, so no empty slot is offered');
  assert.deepEqual(usableSlots(data.nodes.find(node => node.id === 'node_start')), [0],
    'start node keeps its output slot');
  assert.equal(acceptsLink(data.nodes.find(node => node.id === 'node_start')), false, 'start node accepts no inbound link');
  assert.equal(emitsLink(data.nodes.find(node => node.id === 'node_end')), false, 'end node emits nothing');
  assert.throws(() => validateNodePatch(patchOn('A', 0, 'ConnectPart', 'node_start'), data), /开始节点/,
    'start node is rejected as a target');
});

test('an existing connection can be set back to an empty slot', () => {
  const slot = (slot, targetUid, type) => ({ slot, targetUid, conditionType: type, conditionCount: 0, fadeType: 0, fadeTime: 0 });
  const connected = [{ id: 'X', name: 'X', kind: 'H', gamePosition: { x: 0, y: 0 }, cuts: [],
    outputs: [slot(0, 'B', 'ConnectPart'), ...Array.from({ length: 4 }, (_, i) => slot(i + 1, '', 'NotUsed'))] }];
  const local = { bundleId: 'b', manifestSha256: '0'.repeat(64), nodes: [...connected, data.nodes[1]] };
  const disconnect = { format: 'ec-scene-node-patch', version: 1,
    source: { bundleId: 'b', manifestSha256: '0'.repeat(64) },
    connections: [{ sourceUid: 'X', slot: 0,
      before: { targetUid: 'B', conditionType: 'ConnectPart', conditionCount: 0, fadeType: 0, fadeTime: 0 },
      after: { targetUid: '' } }] };
  const safe = validateNodePatch(disconnect, local, { requireChanges: true });
  assert.deepEqual(safe.connections[0].after.targetUid, '', 'empty after target survives validation');
  const baseEdge = { id: 'normal:0:0', kind: 'normal', from: 'X', to: 'B', slot: 0 };
  assert.deepEqual(patchEdges([baseEdge], safe, local), [], 'the preview edge disappears after disconnecting');
  assert.deepEqual(createdEdges([baseEdge], safe, local), [], 'a disconnect never creates an edge');
  // 没有连接可空的槽不能声称断开，否则就是无变化项。
  assert.throws(() => validateNodePatch({ ...disconnect, connections: [{ ...disconnect.connections[0],
    before: { ...disconnect.connections[0].before, targetUid: '' } }] }, local), /无法断开/);
});

test('created edges are accepted by web route validation', () => {  const baseEdges = [{ id: 'normal:0:0', kind: 'normal', from: 'B', to: 'A', slot: 0 }];
  const safe = validateNodePatch(patchOn('B', 1, 'ConnectPart', 'A'), data, { requireChanges: true });
  const added = createdEdges(baseEdges, safe, data);
  assert.deepEqual(added.map(edge => edge.id), ['patch:B:1']);
  const routes = validateRouteOffsets({ 'patch:B:1': { x: 1, y: 2 } }, { baseEdges, edges: [...baseEdges, ...added] }, added);
  assert.deepEqual(routes, { 'patch:B:1': { x: 1, y: 2 } });
  assert.throws(() => validateRouteOffsets({ 'patch:B:1': { x: 1, y: 2 } }, { baseEdges }, []), /网页路线数据无效/,
    'without the created edge list the saved route is rejected');
});
