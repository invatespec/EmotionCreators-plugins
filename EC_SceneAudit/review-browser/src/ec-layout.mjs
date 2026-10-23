const finite = value => Number.isFinite(value) && Math.abs(value) < 1000000;
export const GEOMETRY_TOLERANCE = 0.001;
const nearlyEqual = (left, right) => Math.abs(left - right) <= GEOMETRY_TOLERANCE;
const normalizeZero = value => Object.is(value, -0) ? 0 : value;

export const nativeToCanvas = ({ x, y }) => ({ x, y: -y });
export const canvasToNative = ({ x, y }) => ({ x, y: -y });

export function frameForNative(geometry, nativePosition) {
  const base = geometry.frame, origin = nativeToCanvas(nativePosition);
  return { x: origin.x + geometry.positionOffset.x, y: origin.y + geometry.positionOffset.y,
    width: base.width, height: base.height };
}

export function nativeFromFrame(geometry, frame) {
  return canvasToNative({ x: frame.x - geometry.positionOffset.x, y: frame.y - geometry.positionOffset.y });
}

export function ecFrames(data, nativePositions = {}) {
  if (!data.geometry?.available) return null;
  const result = {};
  for (const geometry of data.geometry.value.nodes) {
    const node = data.nodes.find(item => item.id === geometry.uid);
    const position = nativePositions[geometry.uid] || node?.gamePosition;
    if (!node || !position || !finite(position.x) || !finite(position.y)) return null;
    result[geometry.uid] = frameForNative(geometry, position);
  }
  return result;
}

export function ecPorts(data, positions, nodeId, slot, direction) {
  if (!data.geometry?.available) return null;
  const geometry = data.geometry.value.nodes.find(item => item.uid === nodeId), point = positions[nodeId];
  if (!geometry || !point) return null;
  const port = direction === 'output' ? geometry.outputs.find(item => item.slot === slot) : geometry.input;
  return port ? { x: point.x + port.x, y: point.y + port.y } : null;
}

export function validateEcCanvas(data, nativePositions, canvas) {
  if (!data.geometry?.available) throw new Error('资料缺少完整 EC 几何，不能导出精确布局');
  if (!canvas || !finite(canvas.width) || !finite(canvas.height) || canvas.width <= 0 || canvas.height <= 0) throw new Error('画布宽高必须为有限正数');
  const frames = ecFrames(data, nativePositions);
  if (!frames) throw new Error('存在缺少原生位置的节点，不能检查完整画布边界');
  const outside = [];
  for (const node of data.nodes) {
    const frame = frames[node.id];
    if (frame.x < -GEOMETRY_TOLERANCE || frame.y < -GEOMETRY_TOLERANCE ||
      frame.x + frame.width > canvas.width + GEOMETRY_TOLERANCE ||
      frame.y + frame.height > canvas.height + GEOMETRY_TOLERANCE) outside.push(node.name);
  }
  if (outside.length) throw new Error(`以下节点完整矩形超出画布：${outside.join('、')}`);
  return true;
}

export function layoutFromFrames(data, frames, canvas) {
  if (!data.geometry?.available) throw new Error('资料缺少完整 EC 几何，不能导出精确布局');
  const nodes = [];
  for (const node of data.nodes) {
    const geometry = data.geometry.value.nodes.find(item => item.uid === node.id), before = node.gamePosition, frame = frames[node.id];
    if (!geometry || !before || !frame) throw new Error(`节点 ${node.name} 缺少可核验的原生位置或矩形`);
    const derived = nativeFromFrame(geometry, frame);
    const after = {
      x: nearlyEqual(before.x, derived.x) ? before.x : normalizeZero(derived.x),
      y: nearlyEqual(before.y, derived.y) ? before.y : normalizeZero(derived.y)
    };
    if (!nearlyEqual(before.x, after.x) || !nearlyEqual(before.y, after.y))
      nodes.push({ uid: node.id, before: { ...before }, after });
  }
  return nodes.length ? { canvas: { width: canvas.width, height: canvas.height }, nodes } : undefined;
}

export function convertLegacyLayout(data, legacyPositions, layoutSource) {
  if (layoutSource !== 'game') throw new Error('只有基于旧游戏位置的作者布局可以转换，请先恢复游戏布局');
  const baseline = data.nodes.filter(node => node.gamePosition);
  if (baseline.length !== data.nodes.length) throw new Error('存在缺少原生位置的节点，不能转换');
  const legacy = {};
  for (const node of baseline) {
    const base = { x: 80 + 2 * (node.gamePosition.x - Math.min(...baseline.map(item => item.gamePosition.x))),
      y: 85 + 2 * (Math.max(...baseline.map(item => item.gamePosition.y)) - node.gamePosition.y) };
    const current = legacyPositions[node.id];
    legacy[node.id] = { x: node.gamePosition.x + (current.x - base.x) / 2,
      y: node.gamePosition.y - (current.y - base.y) / 2 };
  }
  return legacy;
}
