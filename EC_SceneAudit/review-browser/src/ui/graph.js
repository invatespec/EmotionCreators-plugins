import { $ } from './dom.js';
import { nodeSize } from './domain.js';
import { mountGraphRender } from './graph-render.js';
import { mountGestures } from './gestures.js';
import { frameQueue } from './frame-queue.js';
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

export function mountGraph(ctx) {
  const { state, actions, signal } = ctx, viewport = $('viewport');
  // 仅框选手势延迟下游面板同步；高亮与计数仍每帧更新。
  let selectionDeferred = false;
  const renderer = mountGraphRender({ ...ctx, deferSelection: () => selectionDeferred });
  const frames = frameQueue(work => {
    if (work.camera) renderer.camera();
    if (work.nodes.size) renderer.positions(work.nodes);
    if (work.selection) renderer.selection();
    actions.positionReader();
  });
  function local(event) { const rect = viewport.getBoundingClientRect(); return { x: event.clientX - rect.left, y: event.clientY - rect.top }; }
  function world(point) { return { x: (point.x - state.camera.x) / state.camera.zoom, y: (point.y - state.camera.y) / state.camera.zoom }; }
  function viewChanged() { renderer.camera(); actions.positionReader(); }
  function zoom(factor, point = { x: viewport.clientWidth / 2, y: viewport.clientHeight / 2 }) {
    const origin = world(point), scale = clamp(state.camera.zoom * factor, .04, 2);
    state.camera = { x: point.x - origin.x * scale, y: point.y - origin.y * scale, zoom: scale };
    frames.schedule({ camera: true });
  }
  function fit() {
    const values = Object.values(state.positions); if (!values.length) return;
    const minX = Math.min(...values.map(p => p.x)) - 40, minY = Math.min(...values.map(p => p.y)) - 60;
    const right = Math.max(...Object.entries(state.positions).map(([id, p]) => p.x + nodeSize(ctx.data, state, id).width));
    const bottom = Math.max(...Object.entries(state.positions).map(([id, p]) => p.y + nodeSize(ctx.data, state, id).height));
    const width = right - minX + 80;
    const height = bottom - minY + 50;
    const scale = clamp(Math.min((viewport.clientWidth - 70) / width, (viewport.clientHeight - 120) / height), .04, 1);
    state.camera = { x: (viewport.clientWidth - width * scale) / 2 - minX * scale, y: 75 - minY * scale, zoom: scale }; viewChanged();
  }
  function focusNode(id) {
    const position = state.positions[id]; if (!position) return;
    const size = nodeSize(ctx.data, state, id);
    if (state.camera.zoom < .5) state.camera.zoom = .7;
    const x = Math.max(110, Math.min(viewport.clientWidth * .28, viewport.clientWidth - 470));
    state.camera.x = x - (position.x + size.width / 2) * state.camera.zoom;
    state.camera.y = viewport.clientHeight / 2 - (position.y + size.height / 2) * state.camera.zoom;
    viewChanged(); alignReader();
  }
  function alignReader() {
    if (!state.main || $('cut-panel').hidden) return;
    const card = $('current-cut').getBoundingClientRect(), frame = viewport.getBoundingClientRect();
    const size = nodeSize(ctx.data, state, state.main);
    const nodeY = state.camera.y + (state.positions[state.main].y + size.height / 2) * state.camera.zoom;
    const delta = card.top + card.height / 2 - frame.top - nodeY;
    if (Math.abs(delta) < .5) return;
    state.camera.y += delta; viewChanged();
  }
  const gestures = mountGestures({ ...ctx, viewport, local, world, schedule: frames.schedule, flush: frames.flush, renderSelection: renderer.selection,
    previewEdge: renderer.previewEdge, renderGuides: renderer.renderGuides, previewRoute: renderer.previewRoute,
    setSelectionDeferred: value => { selectionDeferred = value; } });
  viewport.addEventListener('wheel', event => { event.preventDefault(); zoom(Math.exp(-event.deltaY * .0015), local(event)); }, { passive: false, signal });
  let previousSize = { width: viewport.clientWidth, height: viewport.clientHeight };
  const resize = new ResizeObserver(() => {
    const current = { width: viewport.clientWidth, height: viewport.clientHeight };
    state.camera.x += (current.width - previousSize.width) / 2; state.camera.y += (current.height - previousSize.height) / 2;
    previousSize = current; renderer.camera(); actions.positionReader(); alignReader();
  });
  resize.observe(viewport); resize.observe($('stage').querySelector('.canvas-toolbar'));
  return { render: renderer.render, selection: renderer.selection, fit, zoom, focusNode, cancelGesture: gestures.cancel, previewRoute: renderer.previewRoute,
    invalidateMarkers: renderer.invalidateMarkers,
    destroy() { resize.disconnect(); frames.destroy(); renderer.destroy(); } };
}
