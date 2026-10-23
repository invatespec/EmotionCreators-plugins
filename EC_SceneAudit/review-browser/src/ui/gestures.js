import { $ } from './dom.js';
import { NODE, nodeSize } from './domain.js';
import { snapMove } from './layout-actions.mjs';

export function gestureKind(mode, button, nodeId) {
  if (![0, 2].includes(button)) return null;
  return mode === 'pan' || button === 2 ? 'pan' : nodeId ? 'nodes' : 'marquee';
}

export function resolveGestureEdge(data, edgeData) {
  if (!edgeData?.edge) return null;
  const full = data?.edges?.find(edge => edge.id === edgeData.edge);
  return full ? { ...full, source: full.from } : {
    id: edgeData.edge, source: edgeData.source, to: edgeData.target,
    slot: Number(edgeData.slot), kind: edgeData.kind
  };
}

export function shortcutMode(event, modalOpen = false) {
  if (modalOpen || event.repeat || event.isComposing || event.keyCode === 229 || event.ctrlKey || event.metaKey || event.altKey) return null;
  if (event.target?.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return null;
  return { h: 'pan', v: 'select' }[event.key?.toLowerCase()] || null;
}

export function hasOpenModal(documentRef = document) {
  return [...documentRef.querySelectorAll('dialog[open]')].some(dialog =>
    dialog.matches?.(':modal') || (!dialog.matches && !dialog.classList?.contains('embedded')));
}

export function contextMenuGuard(now = () => performance.now()) {
  let active = false, until = -1;
  return {
    start() { active = true; until = -1; },
    finish() { if (active) until = now() + 1000; active = false; },
    reset() { active = false; until = -1; },
    consume() { if (!active && now() > until) return false; if (!active) until = -1; return true; }
  };
}

export function mountGestures({ data, state, actions, signal, viewport, local, world, schedule, flush, renderSelection, previewEdge = () => {}, renderGuides = () => {}, previewRoute = () => {}, setSelectionDeferred = () => {} }) {
  let gesture = null;
  viewport.tabIndex = 0;
  const menu = contextMenuGuard();
  function release(previous) {
    if (viewport.hasPointerCapture(previous.pointerId)) viewport.releasePointerCapture(previous.pointerId);
    if (previous.button === 2) menu.finish();
    $('marquee').hidden = true; viewport.classList.remove('gesture-active');
    previewEdge(null); renderGuides([]);
  }
  function cancel() {
    if (!gesture) return;
    const previous = gesture; gesture = null;
    state.camera = previous.camera; state.selected = previous.selection; state.selectedEdge = previous.selectedEdge;
    if (previous.oldPositions) state.positions = previous.oldPositions;
    if (previous.kind === 'edgeRoute') { state.routeOffsets = previous.routeOffsets; previewRoute(); }
    if (previous.kind === 'marquee') setSelectionDeferred(false);
    release(previous); schedule({ camera: true, selection: true, nodes: Object.keys(state.positions) }); flush();
  }
  function setMode(mode) {
    if (mode !== 'pan' && mode !== 'select') return;
    cancel(); state.interactionMode = mode;
    viewport.classList.toggle('pan-mode', mode === 'pan');
    $('mode-select')?.setAttribute('aria-pressed', String(mode === 'select'));
    $('mode-pan')?.setAttribute('aria-pressed', String(mode === 'pan'));
    actions.status(mode === 'pan' ? '拖拽模式：左右键均平移画布；按 V 恢复选择' : '选择模式：左键选择/移动节点，右键平移画布');
  }
  function pointerDown(event) {
    if (gesture || event.button === 0 && event.target.closest('.issue-badge, .annotation-badge') && state.interactionMode !== 'pan') return;
    const nodeId = event.target.closest('.node-body')?.dataset.node;
    const edgeTarget = event.target.closest('.edge-target-handle'), edgeHit = event.target.closest('.edge-hit'), routeHandle = event.target.closest('.edge-route-handle');
    const edgeNode = event.target.closest('.edge-target-handle, .edge-hit'), edgeData = routeHandle?.dataset || edgeTarget?.dataset || edgeNode?.closest('.edge')?.dataset;
    const edge = resolveGestureEdge(data, edgeData);
    const frame = event.target.closest('#selection-frame');
    let kind = gestureKind(state.interactionMode, event.button, nodeId);
    if (event.button === 0 && state.interactionMode === 'select' && frame && state.selected.size) kind = 'selectionFrame';
    else if (event.button === 0 && state.interactionMode === 'select' && edgeTarget && edge?.kind === 'normal') kind = 'edgeTarget';
    else if (event.button === 0 && state.interactionMode === 'select' && routeHandle && edge) kind = 'edgeRoute';
    else if (event.button === 0 && state.interactionMode === 'select' && edgeHit && edge) kind = 'edgeSelect';
    if (!kind) return;
    const point = local(event);
    const contextEdit = event.button === 2 && (nodeId ? { type: 'node', sourceUid: nodeId }
      : edge?.kind === 'script' ? { type: 'script', edge } : edge ? { type: 'edge', sourceUid: edge.source, slot: edge.slot } : null);
    gesture = { kind, nodeId, edge, contextEdit, point, pointerId: event.pointerId, button: event.button, camera: { ...state.camera }, moved: false,
      oldPositions: kind === 'nodes' || kind === 'selectionFrame' ? structuredClone(state.positions) : null,
      selection: new Set(state.selected), selectedEdge: state.selectedEdge ?? null, additive: event.ctrlKey || event.metaKey };
    if (kind === 'edgeRoute') gesture.routeOffsets = structuredClone(state.routeOffsets || {});
    if (kind === 'nodes') {
      if (gesture.additive) state.selected.has(nodeId) ? state.selected.delete(nodeId) : state.selected.add(nodeId);
      else if (!state.selected.has(nodeId)) state.selected = new Set([nodeId]);
    }
    if (kind === 'nodes' || kind === 'selectionFrame') gesture.moveIds = new Set(state.selected);
    if (kind === 'edgeSelect') state.selectedEdge = edge.id;
    if (kind === 'marquee') { if (!gesture.additive) { state.selected.clear(); state.selectedEdge = null; } setSelectionDeferred(true); }
    if (event.button === 2) menu.start();
    viewport.focus({ preventScroll: true }); viewport.setPointerCapture(event.pointerId); viewport.classList.add('gesture-active');
    event.preventDefault(); renderSelection();
  }
  function marquee(point) {
    const origin = gesture.point, left = Math.min(point.x, origin.x), top = Math.min(point.y, origin.y);
    Object.assign($('marquee').style, { left: `${left}px`, top: `${top}px`, width: `${Math.abs(point.x - origin.x)}px`, height: `${Math.abs(point.y - origin.y)}px` }); $('marquee').hidden = false;
    const a = world({ x: left, y: top }), b = world({ x: Math.max(point.x, origin.x), y: Math.max(point.y, origin.y) });
    state.selected = new Set(gesture.additive ? gesture.selection : []);
    for (const [id, p] of Object.entries(state.positions)) {
      const size = nodeSize(data, state, id);
      if (p.x < b.x && p.x + size.width > a.x && p.y < b.y && p.y + size.height > a.y) state.selected.add(id);
    }
  }
  function pointerMove(event) {
    if (!gesture || event.pointerId !== gesture.pointerId) return;
    const point = local(event), dx = point.x - gesture.point.x, dy = point.y - gesture.point.y;
    if (Math.hypot(dx, dy) > 3) gesture.moved = true;
    if (gesture.kind === 'pan') {
      state.camera = { ...gesture.camera, x: gesture.camera.x + dx, y: gesture.camera.y + dy }; schedule({ camera: true }); return;
    }
    if (gesture.kind === 'edgeTarget') {
      const target = document.elementFromPoint?.(event.clientX, event.clientY)?.closest?.('.node-body')?.dataset.node;
      previewEdge({ edge: gesture.edge, point: world(point), target: target !== gesture.edge.source ? target : null }); return;
    }
    if (gesture.kind === 'edgeRoute') {
      if (!gesture.moved) return;
      const original = gesture.routeOffsets[gesture.edge.id] || { x: 0, y: 0 };
      state.routeOffsets[gesture.edge.id] = { x: original.x + dx / state.camera.zoom, y: original.y + dy / state.camera.zoom };
      previewRoute(gesture.edge.id); return;
    }
    if (gesture.kind === 'marquee') { marquee(point); schedule({ selection: true }); return; }
    if (gesture.kind === 'edgeSelect') return;
    if (!gesture.moved) return;
    const move = state.snapEnabled && !event.altKey
      ? snapMove(gesture.oldPositions, gesture.moveIds, id => nodeSize(data, state, id), dx / state.camera.zoom, dy / state.camera.zoom, 6 / state.camera.zoom)
      : { dx: dx / state.camera.zoom, dy: dy / state.camera.zoom, guides: [] };
    renderGuides(move.guides);
    for (const id of gesture.moveIds) state.positions[id] = { x: gesture.oldPositions[id].x + move.dx, y: gesture.oldPositions[id].y + move.dy };
    schedule({ nodes: gesture.moveIds });
  }
  function finish(event) {
    if (!gesture || event.pointerId !== gesture.pointerId) return;
    const previous = gesture; gesture = null; release(previous);
    // 框选结束补一次待处理帧再 flush，保证面板只同步一次且覆盖无移动的点击。
    if (previous.kind === 'marquee') { setSelectionDeferred(false); schedule({ selection: true }); }
    flush();
    if ((previous.kind === 'nodes' || previous.kind === 'selectionFrame') && previous.moved) actions.layoutCommitted(previous.oldPositions);
    else if (previous.kind === 'edgeRoute' && previous.moved) actions.routeCommitted?.(previous.routeOffsets);
    else if (previous.kind === 'edgeTarget') {
      const targetNode = document.elementFromPoint?.(event.clientX, event.clientY)?.closest?.('.node-body')?.dataset.node;
      if (targetNode && targetNode !== previous.edge.source) {
        const result = actions.setConnectionTarget?.(previous.edge.source, previous.edge.slot, targetNode);
        if (result?.ok === false) actions.status?.(`连接未修改：${result.error}`);
        else if (result?.ok) actions.status?.('连接目标已更新；槽位条件保持不变');
      } else if (targetNode === previous.edge.source) actions.status?.('普通连接目标不能是来源节点自身');
    } else if (previous.kind === 'edgeSelect' && !previous.moved && previous.edge.kind === 'script') actions.openEdgeText?.(previous.edge);
    else if (previous.kind === 'pan' && !previous.moved && previous.contextEdit) {
      if (previous.contextEdit.type === 'script') actions.openEdgeText?.(previous.contextEdit.edge);
      else actions.openConnectionEditor?.({ sourceUid: previous.contextEdit.sourceUid, slot: previous.contextEdit.slot });
    }
    else if (previous.kind === 'nodes' && !previous.additive) actions.selectNode(previous.nodeId);
    else if (previous.kind === 'pan' && previous.moved) actions.save(false, false);
  }
  viewport.addEventListener('pointerdown', pointerDown, { signal });
  viewport.addEventListener('pointermove', pointerMove, { signal });
  viewport.addEventListener('pointerup', finish, { signal });
  viewport.addEventListener('pointercancel', cancel, { signal });
  viewport.addEventListener('lostpointercapture', cancel, { signal });
  viewport.addEventListener('click', event => {
    if (state.interactionMode !== 'pan' || event.detail === 0) return;
    event.preventDefault(); event.stopImmediatePropagation();
  }, { capture: true, signal });
  window.addEventListener('blur', cancel, { signal });
  document.addEventListener('pointerdown', event => { if (!viewport.contains(event.target)) menu.reset(); }, { capture: true, signal });
  document.addEventListener('contextmenu', event => {
    if (event.button === 2 && (viewport.contains(event.target) || menu.consume())) event.preventDefault();
  }, { capture: true, signal });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && gesture) { event.preventDefault(); cancel(); return; }
    if (!hasOpenModal(document) && !event.isComposing && !event.target?.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"])') && (event.ctrlKey || event.metaKey) && !event.altKey) {
      const key = event.key?.toLowerCase();
      if (key === 'z' || key === 'y') { event.preventDefault(); cancel(); (key === 'y' || event.shiftKey ? actions.redo : actions.undo)?.(); return; }
    }
    const mode = shortcutMode(event, hasOpenModal(document));
    if (mode) { event.preventDefault(); setMode(mode); }
  }, { signal });
  $('mode-select')?.addEventListener('click', () => setMode('select'), { signal });
  $('mode-pan')?.addEventListener('click', () => setMode('pan'), { signal });
  setMode(state.interactionMode);
  signal?.addEventListener('abort', () => {
    const previous = gesture; gesture = null;
    // 销毁只清标记，挂起帧由 frameQueue.destroy 取消，不再调用已销毁视图的下游。
    setSelectionDeferred(false);
    if (previous) release(previous);
    menu.reset(); viewport.classList.remove('gesture-active', 'pan-mode');
  }, { once: true });
  return { cancel, setMode };
}
