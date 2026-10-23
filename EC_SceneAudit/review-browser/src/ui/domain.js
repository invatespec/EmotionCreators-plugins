import { connectedLayout } from './layout.js';

export const NODE = { width: 184, height: 66 };
// 框选每帧按 uid 取尺寸，原实现逐次线性查找导致 O(N²)。索引按 available 与 nodes 引用失效，
// 外层 geometry 对象被原地改动（value.nodes 替换、available 翻转）也能重建。
let sizeIndex = { available: false, nodes: null, sizes: new Map() };
function geometrySizes(data) {
  const available = Boolean(data.geometry?.available), nodes = available ? data.geometry.value.nodes : null;
  if (sizeIndex.available === available && sizeIndex.nodes === nodes) return sizeIndex.sizes;
  const sizes = new Map();
  if (available) for (const node of nodes || []) sizes.set(node.uid, { width: node.frame.width, height: node.frame.height });
  sizeIndex = { available, nodes, sizes };
  return sizes;
}
export function nodeSize(data, _state, id) {
  // 即使不可用也要过一遍索引记录 false；否则重新可用且 nodes 引用不变时会命中旧尺寸。
  const size = geometrySizes(data).get(id);
  return size ? { width: size.width, height: size.height } : NODE;
}
export const severityRank = { blocker: 4, major: 3, minor: 2, info: 1 };
export const severityLabel = { blocker: '阻断风险', major: '影响较大', minor: '影响较小', info: '资料提示' };
export function nodeById(data, id) { return data.nodes.find((node) => node.id === id); }
export function scriptEdgeLocation(data, edge) {
  const source = nodeById(data, edge?.from);
  if (!source || !Number.isInteger(edge?.fromCut) || edge.fromCut < 0 || edge.fromCut >= source.cuts.length) return null;
  return { nodeId: source.id, cutIndex: edge.fromCut };
}
export function cutNumber(node, index) { return Number.isInteger(node.cuts[index]?.sourceIndex) ? node.cuts[index].sourceIndex + 1 : index + 1; }
export function validLocation(data, location) {
  if (!location || location.unavailable) return false;
  const node = nodeById(data, location.node);
  return !!node && (location.cut == null || (Number.isInteger(location.cut) && location.cut >= 0 && location.cut < node.cuts.length));
}
export function marker(data, nodeId, cutIndex, enabled = true) {
  const entries = new Map();
  if (enabled) for (const finding of data.findings) {
    const locations = finding.locations.filter((loc) => loc.node === nodeId && (cutIndex === undefined || loc.cut === cutIndex) && validLocation(data, loc));
    if (finding.status === 'active' && locations.length) entries.set(finding.id, { finding, direct: locations.some((loc) => !loc.associated) });
  }
  const values = [...entries.values()];
  const severity = values.reduce((best, value) => severityRank[value.finding.severity] > severityRank[best] ? value.finding.severity : best, 'info');
  return { ids: [...entries.keys()], count: entries.size, severity, associated: values.length > 0 && values.every((value) => !value.direct) };
}

function components(ids, edges) {
  const remaining = new Set(ids);
  const result = [];
  while (remaining.size) {
    const group = [remaining.values().next().value];
    remaining.delete(group[0]);
    for (let i = 0; i < group.length; i++) {
      const neighbors = edges.flatMap((edge) => edge.from === group[i] ? [edge.to] : edge.to === group[i] ? [edge.from] : []);
      for (const id of neighbors) if (remaining.delete(id)) group.push(id);
    }
    result.push(group);
  }
  return result;
}

export function automaticLayout(data) {
  const known = new Set(data.nodes.map((node) => node.id));
  const normal = data.edges.filter((edge) => edge.kind === 'normal' && known.has(edge.from) && known.has(edge.to));
  const hasNormal = (id) => normal.some((edge) => edge.from === id || edge.to === id);
  const hasAny = (id) => data.edges.some((edge) => edge.from === id || edge.to === id);
  const ordinary = data.nodes.filter((node) => hasNormal(node.id)).map((node) => node.id);
  const scriptOnly = data.nodes.filter((node) => !hasNormal(node.id) && hasAny(node.id));
  const isolated = data.nodes.filter((node) => !hasAny(node.id));
  const sizes = new Map(data.nodes.map(node => [node.id, nodeSize(data, null, node.id)]));
  const maxWidth = Math.max(NODE.width, ...[...sizes.values()].map(size => size.width));
  const maxHeight = Math.max(NODE.height, ...[...sizes.values()].map(size => size.height));
  const columnStep = maxWidth + Math.max(96, maxWidth * .34);
  const rowStep = maxHeight + Math.max(72, maxHeight * .42);
  const groupGap = Math.max(118, maxWidth * .44);
  const positions = {}, regions = []; let left = 80, bottom = 0;
  for (const group of components(ordinary, normal)) {
    const layout = connectedLayout(group, normal, data.nodes);
    for (const [id, point] of layout.positions) positions[id] = { x: left + point.column * columnStep, y: 85 + point.depth * rowStep };
    bottom = Math.max(bottom, 85 + layout.rows * rowStep); left += layout.columns * columnStep + groupGap;
  }
  if (scriptOnly.length) {
    regions.push({ label: '仅脚本连接', x: left - 18, y: 24, width: maxWidth + 60, height: scriptOnly.length * rowStep + 70 });
    scriptOnly.forEach((node, i) => { positions[node.id] = { x: left, y: 85 + i * rowStep }; });
    bottom = Math.max(bottom, 85 + scriptOnly.length * rowStep);
  }
  if (isolated.length) {
    const top = bottom + 78;
    regions.push({ label: '未发现连接 · 可拖动整理', x: 60, y: top, width: Math.min(4, isolated.length) * columnStep + 20, height: Math.ceil(isolated.length / 4) * rowStep + 60 });
    isolated.forEach((node, i) => { positions[node.id] = { x: 80 + i % 4 * columnStep, y: top + 56 + Math.floor(i / 4) * rowStep }; });
  }
  return { positions, regions };
}

export function candidates(data, nodeId, direction) {
  return data.edges.filter((edge) => edge[direction === 'after' ? 'from' : 'to'] === nodeId);
}
export function edgeDestination(edge, direction) { return edge[direction === 'after' ? 'to' : 'from']; }
export function edgeLabel(data, edge, direction) {
  if (edge.sourceKind === 'inline-goto') return edge.label || '脚本跳转';
  const node = nodeById(data, edgeDestination(edge, direction));
  const index = edge[direction === 'after' ? 'toCut' : 'fromCut'];
  return `${node?.name || edge.targetName || '未知目标'} · ${edge.label || (edge.kind === 'script' ? '脚本跳转' : '普通连接')}${edge.inactive ? ' · 原始未启用出口' : ''} · ${index == null || index < 0 ? '落点未知' : `CUT ${node ? cutNumber(node, index) : index + 1}`}`;
}

export function contextRows(data, nodeId, currentCut, direction, count, choices) {
  const step = direction === 'after' ? 1 : -1, rows = [], visited = new Set([nodeId]);
  let node = nodeById(data, nodeId), index = currentCut + step, remaining = count, entering = false;
  while (node && remaining > 0) {
    if (index >= 0 && index < node.cuts.length) {
      const previous = node.cuts[index - step], current = node.cuts[index];
      if (!entering && previous && Number.isInteger(previous.sourceIndex) && Number.isInteger(current.sourceIndex) && current.sourceIndex - previous.sourceIndex !== step) {
        rows.push({ kind: 'notice', text: '相邻 CUT 源索引有缺口，停止展开' }); break;
      }
      rows.push({ kind: 'cut', nodeId: node.id, cut: index }); entering = false; index += step; remaining--; continue;
    }
    const routes = candidates(data, node.id, direction), key = `${direction}:${node.id}`;
    if (!routes.length) { rows.push({ kind: 'notice', text: '已到达已知路线边界' }); break; }
    rows.push({ kind: 'choice', key, routes, nodeId: node.id });
    const edge = routes.find((route) => route.id === choices[key]);
    if (!edge) break;
    const target = nodeById(data, edgeDestination(edge, direction));
    const landing = edge[direction === 'after' ? 'toCut' : 'fromCut'];
    if (target?.system) { rows.push({ kind: 'notice', text: `到达系统节点「${target.name}」，停止展开` }); break; }
    if (!target || !Number.isInteger(landing) || landing < 0 || landing >= target.cuts.length) {
      const reason = edge.reason || '目标或落点未收录';
      rows.push({ kind: 'notice', text: /停止展开/u.test(reason) ? reason : `${reason}，停止展开` }); break;
    }
    if (visited.has(target.id)) { rows.push({ kind: 'notice', text: `再次回到「${target.name}」，停止展开循环` }); break; }
    visited.add(target.id); node = target; index = landing; entering = true;
  }
  return direction === 'before' ? rows.reverse() : rows;
}
