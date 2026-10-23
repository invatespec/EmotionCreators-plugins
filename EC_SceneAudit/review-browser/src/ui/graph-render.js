import { $, el, button, issueBadge } from './dom.js';
import { NODE, nodeSize } from './domain.js';
import { markerCache } from './marker-cache.js';
import { sidePoint, routedShape, edgeRoutes, placeLabel } from './edge-geometry.mjs';
export { edgeRoutes } from './edge-geometry.mjs';

const ns = 'http://www.w3.org/2000/svg';
const svgElement = tag => document.createElementNS(ns, tag);
const finite = value => Number.isFinite(value) && Math.abs(value) < 1000000;

export function edgeShape(edge, from, to, ports = null) {
  const fromSize = ports?.fromSize || NODE;
  // 无目标时也要用路由出侧起点：脚本未收录目标仍从底部按分布出，不退回右侧 stub。
  const fallbackSide = edge.kind === 'script' ? 'bottom' : 'right';
  const start = ports?.from || sidePoint(from, fromSize, ports?.route?.fromSide || fallbackSide, ports?.route?.fromT ?? .5);
  if (!to) {
    // 不捏造目标：目标侧只画到框外一小段 stub；脚本 stub 沿底部起点向下。
    const stub = edge.kind === 'script'
      ? `M${start.x} ${start.y} v68`
      : `M${start.x} ${start.y} h68`;
    return { path: stub, x: start.x + (edge.kind === 'script' ? 0 : 76), y: start.y + (edge.kind === 'script' ? 76 : 4) };
  }
  const toSize = ports?.toSize || NODE;
  if (ports?.route) return routedShape(from, fromSize, to, toSize, ports.route, ports.offset);
  const rightEnd = ports?.to || { x: to.x + toSize.width + 1, y: to.y + toSize.height / 2 };
  if (edge.kind === 'script' || to.y <= from.y) {
    const x1 = start.x, y1 = start.y, x2 = rightEnd.x, y2 = rightEnd.y;
    const bend = Math.max(x1, x2) + (edge.kind === 'script' ? 92 : 48);
    // 第二控制点留在 rightEnd 右侧，末段才从框外朝右边界进入。
    if (edge.from === edge.to) return { path: `M${x1} ${y1} C${bend} ${y1 - 72},${bend} ${y2},${x2} ${y2}`, x: bend - 28, y: (y1 + y2) / 2 };
    if (Math.abs(y1 - y2) < 1) {
      const bendY = y1 + (x1 < x2 ? -72 : 72);
      return { path: `M${x1} ${y1} C${x1 + 48} ${bendY},${x2 + 48} ${bendY},${x2} ${y2}`, x: bend - 28, y: bendY };
    }
    return { path: `M${x1} ${y1} C${bend} ${y1},${bend} ${y2},${x2} ${y2}`, x: bend - 28, y: (y1 + y2) / 2 };
  }
  const x1 = from.x + fromSize.width / 2, y1 = from.y + fromSize.height;
  const x2 = to.x + toSize.width / 2, y2 = to.y - 2;
  if (y1 >= y2) {
    // 源底到达或越过目标顶外侧终点时先向下、再从目标顶外进入，避免箭头基座翻入目标框。
    const lift = Math.max(24, (y1 - y2) * .5);
    return { path: `M${x1} ${y1} C${x1} ${y1 + lift},${x2} ${y2 - lift},${x2} ${y2}`,
      x: (x1 + x2) / 2 + 8, y: (y1 + y2) / 2 };
  }
  return { path: `M${x1} ${y1} C${x1} ${(y1 + y2) / 2},${x2} ${(y1 + y2) / 2},${x2} ${y2}`,
    x: (x1 + x2) / 2 + 8, y: (y1 + y2) / 2 };
}

function arrowDefinitions() {
  const defs = svgElement('defs');
  for (const [id, color] of [['normal', '#8998a5'], ['script', '#b58a53']]) {
    const arrow = svgElement('marker');
    for (const [key, value] of Object.entries({ id: `arrow-${id}`, markerWidth: 8, markerHeight: 8, refX: 7, refY: 4, orient: 'auto', markerUnits: 'userSpaceOnUse' })) arrow.setAttribute(key, value);
    const shape = svgElement('path'); shape.setAttribute('d', 'M0 0 L8 4 L0 8 Z'); shape.setAttribute('fill', color); arrow.append(shape); defs.append(arrow);
  }
  return defs;
}

export function mountGraphRender({ data, state, actions, deferSelection = () => false }) {
  const nodes = new Map(), edges = new Map(), adjacent = new Map(), badges = markerCache(data);
  const sizeOf = id => nodeSize(data, state, id);
  let labels = [];
  const preview = svgElement('path'), guides = el('div', 'alignment-guides');
  preview.classList.add('connection-preview'); preview.setAttribute('marker-end', 'url(#arrow-normal)');
  $('world').append(guides);
  let regions, edgeSource, edgeCount = -1, highlightedMain, frameSize = '', frameText = '', routeCache = null, routedSource = null, routedPositions = null;
  for (const node of data.nodes) {
    const item = el('div', 'pool-node'); item.dataset.node = node.id;
    if (String(node.kind || '').toUpperCase() === 'H') item.classList.add('h-part');
    const body = button('', event => { if (event.detail === 0) actions.selectNode(node.id); }, 'node-body');
    body.dataset.node = node.id;
    body.append(el('strong', '', node.name), el('span', 'node-meta', node.system ? `${node.kind} · 系统节点` : `${node.cuts.length} 个 CUT`));
    item.append(body); $('nodes').append(item); nodes.set(node.id, { item, body, position: null, badge: null });
  }
  function ensureEdges() {
    if (edgeSource === data.edges && edgeCount === data.edges.length) return;
    edgeSource = data.edges; edgeCount = data.edges.length; edges.clear(); adjacent.clear(); highlightedMain = undefined; routeCache = null;
    $('edges').replaceChildren(arrowDefinitions()); $('edge-handles').replaceChildren();
    for (const edge of data.edges) {
      const group = svgElement('g'), hit = svgElement('path'), line = svgElement('path'), text = svgElement('text'), title = svgElement('title'), handle = svgElement('circle'), routeHandle = svgElement('circle');
      group.classList.add('edge', edge.kind); group.dataset.edge = edge.id;
      group.dataset.source = edge.from; group.dataset.slot = edge.slot; group.dataset.kind = edge.kind; group.dataset.target = edge.to;
      hit.classList.add('edge-hit');
      line.setAttribute('marker-end', `url(#arrow-${edge.kind})`);
      title.textContent = `${edge.kind === 'script' ? '脚本跳转' : '普通连接'}${edge.label ? ` · ${edge.label}` : ''}`;
      line.append(title); group.append(line, text); $('edges').append(group);
      handle.classList.add('edge-target-handle'); Object.assign(handle.dataset, { edge: edge.id, source: edge.from, slot: String(edge.slot), kind: edge.kind, target: edge.to });
      handle.setAttribute('r', '6'); handle.style.display = 'none'; group.prepend(hit); $('edge-handles').append(handle);
      routeHandle.classList.add('edge-route-handle'); Object.assign(routeHandle.dataset, handle.dataset);
      routeHandle.setAttribute('r', '5'); routeHandle.style.display = 'none'; $('edge-handles').append(routeHandle);
      const routeTitle = svgElement('title'); routeTitle.textContent = '调整路线'; routeHandle.append(routeTitle);
      edges.set(edge.id, { edge, group, hit, line, text, handle, routeHandle, shape: null });
      for (const id of [edge.from, edge.to]) { if (!adjacent.has(id)) adjacent.set(id, new Set()); adjacent.get(id).add(edge.id); }
    }
  }
  function routes() {
    if (routeCache && routedSource === data.edges && routedPositions === state.positions) return routeCache;
    routedSource = data.edges; routedPositions = state.positions;
    routeCache = edgeRoutes(data.edges, state.positions, sizeOf);
    return routeCache;
  }
  function positionEdge(record) {
    const { edge, group, line, text } = record, from = state.positions[edge.from], to = state.positions[edge.to];
    group.style.display = from ? '' : 'none'; if (!from) return;
    const fromSize = sizeOf(edge.from), toSize = to ? sizeOf(edge.to) : null;
    const route = routes().get(edge.id) || null;
    const shape = edgeShape(edge, from, to, { fromSize, toSize, route, offset: state.routeOffsets?.[edge.id] });
    const previousPath = record.shape?.path; record.shape = shape;
    const label = placeLabel(shape, edge.label || '', state.positions, sizeOf, labels); record.crowded = label.crowded;
    record.labelBox = label.crowded ? null : labels.at(-1);
    if (previousPath !== shape.path) { line.setAttribute('d', shape.path); record.hit.setAttribute('d', shape.path); }
    text.setAttribute('x', label.x); text.setAttribute('y', label.y); text.setAttribute('text-anchor', 'middle');
    if (!edge.label) text.style.display = 'none';
    record.routeHandle.setAttribute('cx', shape.x); record.routeHandle.setAttribute('cy', shape.y);
    if (shape.end) { record.handle.setAttribute('cx', shape.end.x); record.handle.setAttribute('cy', shape.end.y); }
  }
  function positions(ids = nodes.keys()) {
    const affected = new Set();
    for (const id of ids) {
      const record = nodes.get(id), point = state.positions[id]; if (!record || !point) continue;
      if (record.position?.x === point.x && record.position?.y === point.y) continue;
      record.item.style.transform = `translate(${point.x}px,${point.y}px)`; record.position = { ...point };
      for (const edgeId of adjacent.get(id) || []) affected.add(edgeId);
    }
    if (affected.size) routeCache = null;
    if (affected.size) {
      labels = [...edges.values()].filter(record => !affected.has(record.edge.id) && record.labelBox).map(record => record.labelBox);
      for (const edgeId of affected) positionEdge(edges.get(edgeId));
    }
  }
  function selectionFrame() {
    const frame = $('selection-frame'); if (!frame) return;
    const selected = [...state.selected].filter(id => state.positions[id]);
    if (!selected.length) { frame.hidden = true; return; }
    const points = selected.map(id => ({ point: state.positions[id], size: sizeOf(id) }));
    const left = Math.min(...points.map(item => item.point.x)) - 12, top = Math.min(...points.map(item => item.point.y)) - 12;
    const right = Math.max(...points.map(item => item.point.x + item.size.width)) + 12;
    const bottom = Math.max(...points.map(item => item.point.y + item.size.height)) + 12;
    frame.hidden = false; Object.assign(frame.style, { left: `${left}px`, top: `${top}px`, width: `${right - left}px`, height: `${bottom - top}px` });
  }
  function selection() {
    for (const [id, { item, body }] of nodes) {
      item.classList.toggle('selected', state.selected.has(id)); item.classList.toggle('primary-node', state.main === id);
      body.setAttribute('aria-pressed', String(state.selected.has(id)));
    }
    if (highlightedMain !== state.main) {
      for (const { edge, group, text } of edges.values()) {
        const selected = state.main === edge.from || state.main === edge.to, known = !!state.positions[edge.to];
        group.classList.toggle('dimmed', !!state.main && !selected && edge.kind === 'script');
        text.style.display = ''; text.textContent = known ? edge.label : `${edge.targetName || '未知目标'} · ${edge.reason || '未收录'}`;
        // 无兜底标签的边清空文本并隐藏，避免残留上一个高亮状态的文字。
        if (known && !edge.label) { text.textContent = ''; text.style.display = 'none'; }
      }
      highlightedMain = state.main;
    }
    for (const record of edges.values()) {
      const selected = state.selectedEdge === record.edge.id;
      record.group.classList.toggle('selected', selected);
      record.handle.style.display = selected && record.edge.kind === 'normal' && record.edge.from !== record.edge.to && state.positions[record.edge.to] ? '' : 'none';
      record.routeHandle.style.display = selected && record.edge.kind === 'normal' && state.positions[record.edge.to] ? '' : 'none';
      record.text.style.display = !record.edge.label || record.crowded && !selected && !state.selected.has(record.edge.from) && !state.selected.has(record.edge.to) ? 'none' : '';
    }
    $('selection-label').textContent = state.selected.size ? `已选 ${state.selected.size} 个节点` : `${data.nodes.length} 个节点`;
    $('undo').disabled = !state.history.length;
    $('undo').title = state.history.length ? `撤销：${state.history.at(-1).name || '编辑'}` : '撤销';
    if ($('redo')) { $('redo').disabled = !state.future?.length; $('redo').title = state.future?.length ? `重做：${state.future.at(-1).name || '编辑'}` : '重做'; }
    for (const id of ['align-left', 'align-hcenter', 'align-right', 'align-top', 'align-vcenter', 'align-bottom']) { const item = $(id); if (item) item.disabled = state.selected.size < 2; }
    for (const id of ['distribute-h', 'distribute-v']) { const item = $(id); if (item) item.disabled = state.selected.size < 3; }
    selectionFrame();
    if (!deferSelection()) actions.selectionChanged?.();
  }
  function camera() {
    const { x, y, zoom } = state.camera;
    $('world').style.transform = `translate(${x}px,${y}px) scale(${zoom})`;
    $('zoom-value').textContent = `${Math.round(zoom * 100)}%`;
  }
  function canvasFrame() {
    const canvas = state.ecCanvas || data.geometry?.value?.canvas, frame = $('canvas-frame');
    if (!data.geometry?.available || !finite(canvas?.width) || !finite(canvas?.height) || canvas.width <= 0 || canvas.height <= 0) {
      frame.hidden = true; frame.replaceChildren(); frameSize = ''; frameText = ''; return;
    }
    frame.hidden = false;
    const size = `${canvas.width}x${canvas.height}`;
    if (frameSize !== size) {
      Object.assign(frame.style, { left: '0px', top: '0px', width: `${canvas.width}px`, height: `${canvas.height}px` });
      frameSize = size;
    }
    const text = `导出画布 ${canvas.width} × ${canvas.height}`;
    if (frameText !== text) { frame.replaceChildren(el('span', 'canvas-frame-label', text)); frameText = text; }
  }
  function render() {
    const rebuilding = edgeSource !== data.edges || edgeCount !== data.edges.length;
    ensureEdges(); positions();
    labels = []; for (const record of edges.values()) positionEdge(record);
    selection(); camera(); canvasFrame();
    for (const node of data.nodes) {
      const record = nodes.get(node.id), size = nodeSize(data, state, node.id), info = badges.get(node.id, state.mode === 'report');
      if (record.size?.width !== size.width || record.size?.height !== size.height) {
        Object.assign(record.item.style, { width: `${size.width}px`, height: `${size.height}px` });
        record.size = size;
      }
      const note = state.authorNotes?.get(node.id);
      if (record.annotation !== note) {
        record.item.querySelector('.annotation-badge')?.remove(); record.annotation = note;
        if (note) {
          const badge = button(['注', ...note.groups].join(' · '), () => actions.openAnnotations(node.id), 'annotation-badge');
          badge.title = ['作者标注', ...note.groups, note.stage, note.note].filter(Boolean).join(' · ');
          badge.setAttribute('aria-label', `${node.name}：作者标注`); record.item.append(badge);
        }
      }
      if (record.badge === info) continue;
      record.item.querySelector('.issue-badge')?.remove(); record.badge = info;
      if (info.count) record.item.append(issueBadge(info, () => actions.openMarker(node.id), node.name));
    }
    if (regions === state.regions) return;
    regions = state.regions;
    $('regions').replaceChildren(...regions.map(region => {
      const item = el('div', 'node-region', region.label);
      Object.assign(item.style, { left: `${region.x}px`, top: `${region.y}px`, width: `${region.width}px`, height: `${region.height}px` }); return item;
    }));
  }
  function previewEdge(value) {
    for (const record of nodes.values()) record.item.classList.toggle('connection-candidate', record.item.dataset.node === value?.target);
    if (!value) { preview.remove(); return; }
    const source = state.positions[value.edge.source]; if (!source) return;
    const start = edges.get(value.edge.id)?.shape?.start || sidePoint(source, sizeOf(value.edge.source), 'right');
    const bend = Math.max(24, Math.abs(value.point.y - start.y) / 2);
    preview.setAttribute('d', `M${start.x} ${start.y} C${start.x} ${start.y + bend},${value.point.x} ${value.point.y - bend},${value.point.x} ${value.point.y}`);
    preview.classList.toggle('valid', !!value.target); $('edge-handles').append(preview);
  }
  function renderGuides(values) {
    guides.replaceChildren(...values.map(({ axis, value }) => {
      const line = el('div', `alignment-guide ${axis}`); line.style[axis === 'x' ? 'left' : 'top'] = `${value}px`; return line;
    }));
  }
  // 拖动单边时只重摆该边；已摆好的标签按原顺序保留，避免相邻边与全量标签避让。
  function previewRoute(edgeId) {
    if (edgeId && edges.has(edgeId)) {
      labels = [];
      for (const record of edges.values()) {
        if (record.edge.id === edgeId) break;
        if (record.labelBox) labels.push(record.labelBox);
      }
      positionEdge(edges.get(edgeId));
      return;
    }
    labels = []; for (const record of edges.values()) positionEdge(record);
  }
  return { render, positions, selection, camera, previewEdge, renderGuides, previewRoute,
    destroy: () => { preview.remove(); guides.remove(); }, invalidateMarkers: () => badges.invalidate() };
}
