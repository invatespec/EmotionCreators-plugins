const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const finite = value => Number.isFinite(value) && Math.abs(value) < 1000000;
const point = value => object(value) && finite(value.x) && finite(value.y);
const close = (a, b) => Math.abs(a - b) < 0.01;

function fail(reason) { return { available: false, reason, value: null }; }

export function validateGeometry(scene) {
  if (!object(scene) || scene.geometry === undefined) return fail('资料未包含原生几何');
  const geometry = scene.geometry;
  if (!object(geometry) || geometry.version !== 1 || geometry.coordinateSpace !== 'grid-ui-top-left-y-down') return fail('几何格式或坐标空间不受支持');
  if (!object(geometry.canvas) || !finite(geometry.canvas.width) || !finite(geometry.canvas.height) || geometry.canvas.width <= 0 || geometry.canvas.height <= 0) return fail('画布尺寸无效');
  if (!Array.isArray(scene.nodes) || !Array.isArray(geometry.nodes) || geometry.nodes.length !== scene.nodes.length) return fail('几何节点集合不完整');

  const rawByUid = new Map(scene.nodes.map(node => [node?.uid, node]));
  const seen = new Set(), nodes = [];
  for (const entry of geometry.nodes) {
    if (!object(entry) || typeof entry.uid !== 'string' || !rawByUid.has(entry.uid) || seen.has(entry.uid)) return fail('几何节点 UID 缺失、重复或不属于当前 Scene');
    seen.add(entry.uid);
    if (!['exact', 'unavailable'].includes(entry.status)) return fail(`节点 ${entry.uid} 的几何状态无效`);
    if (entry.status === 'unavailable') return fail(`节点 ${entry.uid} 缺少可靠几何`);
    const frame = entry.frame, offset = entry.positionOffset, raw = rawByUid.get(entry.uid);
    if (!object(frame) || !['x', 'y', 'width', 'height'].every(key => finite(frame[key])) || frame.width <= 0 || frame.height <= 0 || !point(offset)) return fail(`节点 ${entry.uid} 的矩形或位置偏移无效`);
    if (!point(raw.position) || !close(frame.x, raw.position.x + offset.x) || !close(frame.y, -raw.position.y + offset.y)) return fail(`节点 ${entry.uid} 的矩形与原生位置不一致`);
    if (entry.input !== null && (!object(entry.input) || typeof entry.input.enabled !== 'boolean' || !point(entry.input))) return fail(`节点 ${entry.uid} 的输入端口无效`);
    if (!Array.isArray(entry.outputs)) return fail(`节点 ${entry.uid} 的输出端口列表无效`);
    const slots = new Set();
    for (const port of entry.outputs) {
      if (!object(port) || !Number.isInteger(port.slot) || port.slot < 0 || typeof port.enabled !== 'boolean' || !point(port) || slots.has(port.slot)) return fail(`节点 ${entry.uid} 的输出端口坐标或槽位无效`);
      slots.add(port.slot);
    }
    const expected = new Set((Array.isArray(raw.outputs) ? raw.outputs : []).map(output => output?.slot));
    if ([...slots].some(slot => !expected.has(slot))) return fail(`节点 ${entry.uid} 的输出端口与连接槽不匹配`);
    nodes.push({ uid: entry.uid, status: entry.status, frame: { x: frame.x, y: frame.y, width: frame.width, height: frame.height },
      positionOffset: { x: offset.x, y: offset.y }, input: entry.input && { ...entry.input }, outputs: entry.outputs.map(port => ({ ...port })) });
  }
  return { available: true, reason: '', value: { version: 1, coordinateSpace: geometry.coordinateSpace,
    canvas: { width: geometry.canvas.width, height: geometry.canvas.height }, nodes } };
}
