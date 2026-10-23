import test from 'node:test';
import assert from 'node:assert/strict';
import { data } from './data.js';
import { automaticLayout, marker, contextRows, validLocation, nodeSize, NODE } from './domain.js';

test('问题按 ID 去重，Part 级不扩散到 CUT，撤销不显示', () => {
  assert.equal(marker(data, 'market').count, 2);
  assert.equal(marker(data, 'market', 1).count, 2);
  assert.equal(marker(data, 'draft').count, 1);
  assert.equal(marker(data, 'draft', 0).count, 0);
  assert.equal(marker(data, 'station', 2).count, 0);
  assert.equal(marker(data, 'market', 1, false).count, 0);
});
test('共享依赖标记与失效位置有边界', () => {
  assert.equal(marker(data, 'station', 0).associated, true);
  assert.equal(marker(data, 'market', 1).associated, false);
  assert.equal(validLocation(data, { node: 'missing-node', cut: 0 }), false);
  assert.equal(validLocation(data, { node: 'market', cut: 500 }), false);
});
test('先显示本 Part 边界，显式选择后才跨 Part，跳到真实落点', () => {
  const first = contextRows(data, 'station', 2, 'after', 3, {});
  assert.equal(first[0].kind, 'choice');
  assert.equal(first.filter((row) => row.kind === 'cut').length, 0);
  const selected = contextRows(data, 'station', 2, 'after', 3, { 'after:station': 's1' });
  assert.deepEqual(selected.filter((row) => row.kind === 'cut').map((row) => row.cut), [1, 2, 3]);
  assert.ok(selected.filter((row) => row.kind === 'cut').every((row) => row.nodeId === 'memory'));
});
test('缺失目标和循环停止展开', () => {
  assert.ok(contextRows(data, 'crossing', 2, 'after', 8, { 'after:crossing': 's3' }).some((row) => row.text?.includes('未收录')));
  const rows = contextRows(data, 'square', 1, 'after', 30, { 'after:square': 'e7', 'after:market': 'e4' });
  assert.ok(rows.some((row) => row.text?.includes('停止展开循环')));
});
test('前驱从实际来源 CUT 展开，前后为零不扩展', () => {
  const rows = contextRows(data, 'memory', 0, 'before', 2, { 'before:memory': 's1' });
  assert.deepEqual(rows.filter((row) => row.kind === 'cut').map((row) => row.cut), [1, 2]);
  assert.deepEqual(contextRows(data, 'memory', 0, 'before', 0, {}), []);
});
function sizedData(nodes, available = true) {
  return { geometry: { available, value: { nodes } } };
}
test('nodeSize 读 geometry 尺寸，缺省回退默认值且不写缓存', () => {
  const geometry = sizedData([{ uid: 'a', frame: { width: 100, height: 50 } }]);
  assert.deepEqual(nodeSize(geometry, null, 'a'), { width: 100, height: 50 });
  assert.deepEqual(nodeSize(geometry, null, 'b'), NODE);
  assert.deepEqual(nodeSize({}, null, 'a'), NODE);
  const missing = sizedData([]);
  assert.deepEqual(nodeSize(missing, null, 'b'), NODE);
  // 缺失 uid 不缓存默认值：nodes 换引用后同一 uid 立即返回真实尺寸。
  missing.geometry.value.nodes = [{ uid: 'b', frame: { width: 120, height: 70 } }];
  assert.deepEqual(nodeSize(missing, null, 'b'), { width: 120, height: 70 });
});
test('nodeSize 缓存按 available 与 value.nodes 引用失效', () => {
  const data = sizedData([{ uid: 'a', frame: { width: 100, height: 50 } }]);
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 100, height: 50 });
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 100, height: 50 });
  // 同外层 geometry 对象：available 翻转后回退默认值，再翻回读取原尺寸。
  data.geometry.available = false;
  assert.deepEqual(nodeSize(data, null, 'a'), NODE);
  data.geometry.available = true;
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 100, height: 50 });
  // 同外层 geometry 对象：value.nodes 换引用后缓存失效。
  data.geometry.value.nodes = [{ uid: 'a', frame: { width: 200, height: 80 } }];
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 200, height: 80 });
  // 返回值是副本，调用方修改不污染缓存。
  const copy = nodeSize(data, null, 'a'); copy.width = 999;
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 200, height: 80 });
});
test('nodeSize 真→假→真同数组且 frame 更新后重新建索引', () => {
  const nodes = [{ uid: 'a', frame: { width: 100, height: 50 } }];
  const data = { geometry: { available: true, value: { nodes } } };
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 100, height: 50 });
  data.geometry.available = false;
  assert.deepEqual(nodeSize(data, null, 'a'), NODE);
  // 不可用期间原地更新同一 nodes 数组的 frame，引用不变。
  nodes[0].frame = { width: 300, height: 120 };
  data.geometry.available = true;
  assert.deepEqual(nodeSize(data, null, 'a'), { width: 300, height: 120 });
});
test('分叉、汇合和循环可布局，节点不重叠，脚本不合并普通组', () => {
  const result = automaticLayout(data), entries = Object.entries(result.positions);
  assert.equal(entries.length, data.nodes.length);
  for (const [, position] of entries) assert.ok(Number.isFinite(position.x) && Number.isFinite(position.y));
  for (let i = 0; i < entries.length; i++) for (let j = i + 1; j < entries.length; j++) {
    const a = entries[i][1], b = entries[j][1];
    assert.ok(a.x + NODE.width <= b.x || b.x + NODE.width <= a.x || a.y + NODE.height <= b.y || b.y + NODE.height <= a.y);
  }
  assert.ok(result.positions.memory.x > result.positions.entry.x);
  assert.ok(result.regions.some((region) => region.label.includes('仅脚本')));
  assert.ok(result.regions.some((region) => region.label.includes('未发现连接')));
  const scriptless = automaticLayout({ ...data, edges: data.edges.filter((edge) => edge.kind === 'normal') });
  for (const node of ['entry', 'crossing', 'market', 'station', 'square', 'ending', 'memory', 'letter', 'epilogue']) assert.deepEqual(result.positions[node], scriptless.positions[node]);
});
