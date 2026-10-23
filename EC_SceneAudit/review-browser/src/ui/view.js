import { $, el, resetCanvasFrame } from './dom.js';
import { automaticLayout, marker, nodeById, nodeSize, validLocation, cutNumber, scriptEdgeLocation } from './domain.js';
import { createState, persist, rememberLayout, undoLayout, redoLayout } from './state.js';
import { mountToolbar, setSaveMode } from './toolbar.js';
import { gameLayout } from './game-layout.js';
import { mountGraph } from './graph.js';
import { mountSidebar } from './sidebar.js';
import { mountReader } from './reader.js';
import { mountSearch } from './search.js';
import { mountAnnotations } from './annotations.js';
import { mountRepairs } from './repairs.js';
import { mountNodePatches } from './node-patches.js';
import { mountEditorPanel } from './editor-panel.js';
import { mountConnectionEditor } from './connection-editor.js';
import { connectionDraft } from './connection-edit.mjs';
import { alignPositions, distributePositions } from './layout-actions.mjs';
import { recoverStoredNodePatch } from '../node-patch-recovery.mjs';
import { convertLegacyLayout, ecFrames, layoutFromFrames, validateEcCanvas } from '../ec-layout.mjs';
import { applyLayoutPatch, syncNodePatchLayout } from './layout-state.js';
import { emptyNodePatch, patchEdges, validateNodePatch } from '../node-patch.mjs';
import { buildGotoPreview, effectiveRawText } from '../goto-preview.mjs';

export function mountView(data, initialMode = 'report', draft = {}, projectSaver = null) {
const lifetime = new AbortController();
const state = createState(data, draft);
state.mode = initialMode;
// 保存按钮形态可在运行时切换:首次保存成功后转项目态,不需要重挂载视图。
let saveTarget = projectSaver;
state.projectSave = Boolean(projectSaver);
// 连线显示:0 全部,1 仅隐藏脚本跳转,2 全部隐藏。只影响视图,不写进草稿。
state.edgeView = 0;
state.editEpoch = 0; state.savedEpoch = 0;
$('draft-state').textContent = state.restored ? '服务端草稿已恢复' : '尚无草稿';
$('patch-export-state').textContent = '补丁尚未导出';
let graph, sidebar, reader, annotations, repairs, nodePatches, editorPanel, connectionEditor, toolbar;
let layoutPreview = null;
let currentRepairPlan = { changes: [] };
function status(text) { $('status').textContent = text; }
// 页脚项目状态只由 epoch 比较派生:失败、迟到事件与成功文字不会互相覆盖。
function refreshSaveState() {
  if (lifetime.signal.aborted) return;
  const element = $('project-save-state');
  if (!state.projectSave) element.textContent = '尚未保存为项目';
  else element.textContent = state.editEpoch === state.savedEpoch ? '项目已保存' : '项目有未保存修改';
}
function markEdited() {
  // 子模块可能在视图销毁后才派发事件,不再计入新视图的状态。
  if (lifetime.signal.aborted) return;
  state.editEpoch += 1;
  document.dispatchEvent(new CustomEvent('scene-draft-changed'));
  refreshSaveState();
}
// 恢复期只改内存:并发 persist 会在同一路径上争抢 rename,由调用方收尾一次落盘。
let restoring = false;
function save(announce = true, edited = true) {
  if (edited) markEdited();
  if (restoring) return true;
  persist(data, state).then(results => {
    if (lifetime.signal.aborted) return;
    const ok = results.every(entry => entry.status === 'fulfilled');
    if (ok) $('draft-state').textContent = '草稿已保存到数据目录';
    if (announce || !ok) status(ok ? '布局与阅读默认值已保存到数据目录' : '草稿保存失败,当前修改仅保留在本页');
  });
  return true;
}
function render() {
  sidebar?.render(); graph?.render(); reader?.render();
  $('source-warning').hidden = state.mode !== 'mismatch';
}
// 隐藏只切 CSS 状态,不重建边;切回来时的路线与标签保持原样。
function applyEdgeView() {
  const mode = state.edgeView;
  $('edges').classList.toggle('hide-script', mode === 1);
  $('edges').classList.toggle('hide-all', mode === 2);
  $('edge-handles').classList.toggle('hide-all', mode === 2);
  toolbar?.edgeView();
}
function updateGoto(plan) {
  currentRepairPlan = plan;
  const preview = buildGotoPreview({ data, plan, baseEdges: data.gotoBaseEdges });
  data.gotoEdges = preview.edges; data.gotoDiagnostics = preview.diagnostics;
  data.edges = [...patchEdges(data.baseEdges, state.nodePatch, data), ...preview.edges];
  nodePatches?.refresh(); graph?.render();
}
function selectNode(id, cutIndex, focus = true) {
  const node = nodeById(data, id); if (!node) return;
  graph?.cancelGesture();
  state.main = id; state.selected = new Set([id]);
  state.cut = Math.max(0, Math.min(node.cuts.length - 1, cutIndex ?? state.memory[id] ?? 0));
  state.memory[id] = state.cut; state.before = state.defaults.before; state.after = state.defaults.after; state.choices = {};
  render(); if (focus) graph.focusNode(id); reader.position();
}
function locate(location) {
  if (!validLocation(data, location)) { status('该位置无法解析，未猜测替代节点'); return; }
  const finding = data.findings.find((item) => item.id === state.findingId);
  state.locationIndex = finding?.locations.indexOf(location) ?? -1;
  selectNode(location.node, location.cut ?? state.memory[location.node] ?? 0);
  status(`已定位 ${nodeById(data, location.node).name}${location.cut == null ? ' · 节点级问题' : ` · CUT ${cutNumber(nodeById(data, location.node), location.cut)}`}`);
}
function openFinding(id) {
  if (state.mode !== 'report') return;
  if (state.layer === 1) state.listScroll = $('list-page').scrollTop;
  state.findingId = id; state.layer = 2; state.collapsed = false;
  state.locationIndex = -1;
  const finding = data.findings.find((item) => item.id === id);
  const location = finding.locations.find((item) => !item.associated && validLocation(data, item));
  render();
  if (location) locate(location);
  else status(finding.external ? '请选择共享脚本的调用位置' : '问题位置不可用，画布保持不变');
  $('detail-page').scrollTop = 0;
}
function openMarker(nodeId, cutIndex) {
  const info = marker(data, nodeId, cutIndex, state.mode === 'report');
  if (info.count === 1) { openFinding(info.ids[0]); return; }
  state.filter = { ids: info.ids, label: `${nodeById(data, nodeId).name}${cutIndex === undefined ? '' : ` · CUT ${cutNumber(nodeById(data, nodeId), cutIndex)}`}` };
  state.layer = 1; state.collapsed = false; state.listScroll = 0; render(); $('list-page').scrollTop = 0;
}
function setCut(index) {
  const node = nodeById(data, state.main); if (!node?.cuts.length) return;
  state.cut = Math.max(0, Math.min(node.cuts.length - 1, index)); state.memory[node.id] = state.cut;
  reader.render(); if (state.layer === 2) sidebar.renderDetail();
}
const actions = {
  render, status, save, selectNode, locate, openFinding, openMarker, setCut,
  draftChanged: markEdited,
  setEdgeView: mode => { state.edgeView = ((mode % 3) + 3) % 3; applyEdgeView(); },
  openAnnotations: id => annotations.open(id),
  openRepair: target => { $('fulltext').close(); repairs.open(target); },
  openConnectionEditor: target => connectionEditor.open(target),
  openEdgeText: edge => {
    const location = scriptEdgeLocation(data, edge);
    if (!location) { status('该脚本边缺少可定位的源 CUT，未按目标或 CUT 0 猜测'); return; }
    selectNode(location.nodeId, location.cutIndex);
    reader?.openFulltext?.(location.nodeId, location.cutIndex);
  },
  repairStatus: target => repairs.statusOf(target),
  refreshFulltext: () => reader?.refreshFulltext?.(),
  repairChanged: updateGoto,
  previewRawText: pointer => effectiveRawText(data, currentRepairPlan, pointer),
  setNodePatch: (value, name = '修改连接') => {
    graph?.cancelGesture(); actions.clearLayoutPreview();
    const safe = value.connections.length || value.layout ? validateNodePatch(value, data, { checkBounds: false }) : emptyNodePatch(data);
    rememberLayout(state, state.positions, name);
    state.nodePatch = safe; state.nodePatchUnavailable = false;
    if (safe.layout) applyLayoutPatch(state, data, safe);
    syncNodePatchLayout(data, state);
    data.edges = [...patchEdges(data.baseEdges, safe, data), ...(data.gotoEdges || [])];
    nodePatches?.refresh(); render(); save(false);
  },
  setConnectionTarget: (sourceUid, slot, targetUid) => {
    try {
      const next = connectionDraft(state.nodePatch, data, sourceUid, slot, targetUid);
      if (JSON.stringify(next.connections) === JSON.stringify(state.nodePatch.connections)) return { ok: true };
      actions.setNodePatch(next);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: error.message };
    }
  },
  layoutPatch: canvas => {
    const layout = layoutFromFrames(data, state.positions, canvas);
    return layout ? { canvas: { ...canvas }, nodes: layout.nodes } : undefined;
  },
  flushNodePatch: () => structuredClone(state.nodePatch),
  selectionChanged: () => { toolbar?.selection(); nodePatches?.selection(); annotations?.selection?.(); },
  setCanvas: canvas => {
    if (![canvas.width, canvas.height].every(value => Number.isFinite(value) && value > 0 && value < 1000000)) return { ok: false, error: '画布尺寸须大于 0 且小于 1000000' };
    if (state.ecCanvas?.width === canvas.width && state.ecCanvas?.height === canvas.height) return { ok: true };
    graph?.cancelGesture(); actions.clearLayoutPreview();
    rememberLayout(state, state.positions, '调整画布'); state.ecCanvas = { ...canvas };
    syncNodePatchLayout(data, state); render(); save(false); nodePatches?.refresh();
    let warning = '';
    try { validateEcCanvas(data, Object.fromEntries((state.nodePatch.layout?.nodes || []).map(item => [item.uid, item.after])), canvas); }
    catch (error) { warning = error.message; }
    return { ok: true, warning };
  },
  restoreConnections: () => actions.setNodePatch({ ...state.nodePatch, connections: [] }, '恢复原始连接'),
  restoreLayout: (all = false) => {
    graph?.cancelGesture(); actions.clearLayoutPreview();
    const frames = ecFrames(data), game = gameLayout(data), layout = frames ? { positions: frames, regions: [] } : game || (all ? automaticLayout(data) : null);
    if (!layout && !all) return;
    rememberLayout(state, state.positions, all ? '清除全部节点修改' : '恢复游戏布局');
    if (layout) Object.assign(state, layout, { layoutSource: frames ? 'ec' : game ? 'game' : 'automatic' });
    if (all) { state.nodePatch = emptyNodePatch(data); state.routeOffsets = {}; }
    syncNodePatchLayout(data, state); data.edges = [...patchEdges(data.baseEdges, state.nodePatch, data), ...(data.gotoEdges || [])];
    render(); save(false); nodePatches?.refresh();
  },
  restoreNodePosition: uid => {
    const point = ecFrames(data)?.[uid] || gameLayout(data)?.positions[uid]; if (!point) return;
    graph?.cancelGesture(); actions.clearLayoutPreview(); const previous = structuredClone(state.positions);
    state.positions = { ...state.positions, [uid]: { ...point } }; actions.layoutCommitted(previous, '恢复节点位置'); render(); nodePatches?.refresh();
  },
  routeCommitted: previous => {
    const next = state.routeOffsets; state.routeOffsets = previous;
    rememberLayout(state, state.positions, '调整网页连线路线'); state.routeOffsets = next; save(false);
    graph?.previewRoute(); graph?.selection();
  },
  resetRoute: () => {
    if (!state.routeOffsets?.[state.selectedEdge]) return;
    rememberLayout(state, state.positions, '恢复自动路线'); delete state.routeOffsets[state.selectedEdge]; graph?.previewRoute(); save(false); graph?.selection();
  },
  clearLayoutPreview: () => {
    if (!layoutPreview) return;
    state.positions = layoutPreview; layoutPreview = null; graph?.render();
  },
  previewLayout: (mode, value, gap, anchor) => {
    actions.clearLayoutPreview();
    const positions = mode === 'align' ? alignPositions(state.positions, state.selected, id => nodeSize(data, state, id), value, anchor)
      : distributePositions(state.positions, state.selected, id => nodeSize(data, state, id), value, gap);
    if (!positions) return;
    layoutPreview = state.positions; state.positions = positions; graph?.render();
    const canvas = state.ecCanvas;
    const outside = canvas && Object.entries(positions).some(([id, p]) => p.x < 0 || p.y < 0 || p.x + nodeSize(data, state, id).width > canvas.width || p.y + nodeSize(data, state, id).height > canvas.height);
    let spacing = '';
    if (mode === 'distribute') {
      const key = value === 'horizontal' ? 'x' : 'y', dimension = value === 'horizontal' ? 'width' : 'height';
      const ids = [...state.selected].sort((a, b) => positions[a][key] - positions[b][key]);
      spacing = ` · 间隔 ${Math.round((positions[ids[1]][key] - positions[ids[0]][key] - nodeSize(data, state, ids[0])[dimension]) * 100) / 100}`;
    }
    status(`排列预览${spacing}${outside ? ' · 有节点超出画布范围' : ''}`);
  },
  undo: () => restoreHistory(undoLayout, '撤销'),
  redo: () => restoreHistory(redoLayout, '重做'),
  discardNodePatchReview: () => { state.nodePatchReview = null; state.nodePatchUnavailable = false; save(false); nodePatches?.refresh(); return true; },
  importLayout: (layout, edited = true) => {
    if (layout.canvas) state.ecCanvas = { ...layout.canvas };
    if (data.geometry?.available && layout.layoutSource === 'game') {
      const converted = convertLegacyLayout(data, layout.positions, 'game');
      const frames = ecFrames(data, converted);
      if (frames) applyLayout({ positions: frames, regions: [] }, 'ec', '已导入并转换标注中的旧网页布局，可撤销', edited);
    } else if (data.geometry?.available && layout.layoutSource === 'ec') {
      applyLayout({ positions: layout.positions, regions: [] }, 'ec', '已导入标注中的 EC 布局，可撤销', edited);
    } else {
      const base = layout.layoutSource === 'game' ? gameLayout(data) ?? automaticLayout(data) : automaticLayout(data);
      applyLayout({ ...base, positions: { ...base.positions, ...layout.positions } }, layout.layoutSource, '已导入标注中的布局，可撤销', edited);
    }
    state.camera = layout.camera; render(); save(false, edited); nodePatches?.refresh();
  },
  positionReader: () => reader?.position(),
  clearFilter: () => { state.filter = null; state.listScroll = 0; sidebar.render(); },
  restoreFindingLocation: () => {
    const finding = data.findings.find((item) => item.id === state.findingId);
    const location = finding?.locations[state.locationIndex] ?? finding?.locations.find((item) => validLocation(data, item) && !item.associated);
    if (location) locate(location); else status('请从对应位置中选择有效的调用处');
  },
  layoutCommitted: (previous, name = '移动节点') => {
    rememberLayout(state, previous, name);
    if (data.geometry?.available) {
      state.regions = []; state.layoutSource = 'ec';
      syncNodePatchLayout(data, state);
    }
    save(false);
    graph?.selection();
    nodePatches?.refresh();
    status(`${name} · ${state.selected.size} 个节点，可撤销`);
  },
  alignSelection: (mode, anchor) => {
    actions.clearLayoutPreview(); graph?.cancelGesture();
    const previous = structuredClone(state.positions);
    const positions = alignPositions(state.positions, state.selected, id => nodeSize(data, state, id), mode, anchor);
    if (!positions) return;
    state.positions = positions;
    actions.layoutCommitted(previous, '对齐节点');
    render();
  },
  distributeSelection: (axis, gap) => {
    actions.clearLayoutPreview(); graph?.cancelGesture();
    const previous = structuredClone(state.positions);
    const positions = distributePositions(state.positions, state.selected, id => nodeSize(data, state, id), axis, gap);
    if (!positions) return;
    state.positions = positions;
    actions.layoutCommitted(previous, '等间隔排列');
    render();
    const canvas = state.ecCanvas;
    if (canvas && [...state.selected].some(id => positions[id].x + nodeSize(data, state, id).width > canvas.width || positions[id].y + nodeSize(data, state, id).height > canvas.height)) status('已等间隔排列；部分节点超出画布，请调整画布后导出');
  }
};
const ctx = { data, state, actions, signal: lifetime.signal };
// 面板可由顶栏按钮以外的路径打开(标注定位/修复页),统一在此同步按钮按下态。
editorPanel = mountEditorPanel({ ...ctx, onChange: opened => $('editor-open').setAttribute('aria-pressed', String(opened)) });
annotations = mountAnnotations({ ...ctx, panel: editorPanel, draft });
reader = mountReader(ctx); sidebar = mountSidebar(ctx); graph = mountGraph(ctx);
mountSearch(ctx);
repairs = mountRepairs({ ...ctx, panel: editorPanel, draft });
nodePatches = mountNodePatches({ ...ctx, panel: editorPanel });
connectionEditor = mountConnectionEditor(ctx);
toolbar = mountToolbar(ctx);
updateGoto(repairs.flush());
function restoreHistory(operation, verb) {
  actions.clearLayoutPreview(); graph.cancelGesture(); if (!operation(state)) return;
  syncNodePatchLayout(data, state); data.edges = [...patchEdges(data.baseEdges, state.nodePatch, data), ...(data.gotoEdges || [])];
  render(); nodePatches?.refresh(); save(false); status(`已${verb}：${state.historyAction || '编辑'}`);
}
function applyLayout(layout, source, message, edited = true) {
  actions.clearLayoutPreview(); graph.cancelGesture(); rememberLayout(state, state.positions, message.split('，')[0]);
  Object.assign(state, layout, { layoutSource: source, main: null });
  syncNodePatchLayout(data, state);
  render(); graph.fit(); save(false, edited); status(message);
}
$('fit').onclick = () => { state.main = null; render(); graph.fit(); };
// 审查问题面板宽度可拖拽,会话内保持。
{
  const handle = $('issues-resize'), workspace = $('workspace');
  const MIN = 240, MAX = 620;
  handle.addEventListener?.('pointerdown', event => {
    event.preventDefault(); handle.setPointerCapture?.(event.pointerId);
    const startX = event.clientX, startWidth = $('issues').getBoundingClientRect().width;
    const move = moveEvent => {
      const width = Math.max(MIN, Math.min(MAX, startWidth + (moveEvent.clientX - startX)));
      workspace.style.setProperty('--issues-width', `${width}px`);
    };
    const up = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); };
    handle.addEventListener('pointermove', move); handle.addEventListener('pointerup', up);
  });
}
// 顶栏按钮是面板开关:未打开则打开作者标注页,已打开则收起。
$('editor-open').onclick = () => {
  const opened = editorPanel.toggle('annotations');
  $('editor-open').setAttribute('aria-pressed', String(opened));
  if (opened) annotations.open();
};
$('arrange').onclick = () => applyLayout(automaticLayout(data), 'automatic', '已按普通连接重新排列，脚本线保留，可撤销');
$('game-layout').disabled = !data.nodes.some(node => node.gamePosition);
$('game-layout').title = $('game-layout').disabled ? '资料未包含有效的游戏节点位置' : '恢复资料中作者排列的节点位置，可撤销';
$('game-layout').onclick = () => {
  if (data.geometry?.available) { const frames = ecFrames(data); if (frames) applyLayout({ positions: frames, regions: [] }, 'ec', '已恢复 EC 原生布局，可撤销'); }
  else { const layout = gameLayout(data); if (layout) applyLayout(layout, 'game', '已恢复游戏布局，可撤销；缺少位置的节点单独排列'); }
};
$('undo').onclick = actions.undo;
$('redo').onclick = actions.redo;
$('save').onclick = () => saveTarget ? saveTarget() : save();
$('zoom-in').onclick = () => graph.zoom(1.2); $('zoom-out').onclick = () => graph.zoom(1 / 1.2);
function setReport(findings, mode) {
  data.findings = findings; state.mode = mode; state.layer = 1; state.findingId = null;
  graph.invalidateMarkers();
  state.filter = null; state.listScroll = 0; state.locationIndex = -1;
  render();
}
render();
applyEdgeView();
refreshSaveState();
if (!state.restored) graph.fit();
if (state.storageUnavailable) status('服务端存储不可用,当前修改不会保存');
return { state, data, actions, setReport,
  flushAnnotations: () => annotations.flushDrafts(),
  restoreAnnotations: value => annotations.replace(value),
  flushRepairs: () => repairs.flush(),
  restoreRepairs: value => repairs.replace(value),
  flushNodePatch: () => actions.flushNodePatch(),
  restoreNodePatch: (value, review = null) => {
    const recovered = recoverStoredNodePatch(value, data, review);
    state.nodePatch = recovered.nodePatch;
    state.nodePatchReview = recovered.nodePatchReview;
    state.nodePatchUnavailable = false;
    data.edges = [...patchEdges(data.baseEdges, recovered.nodePatch, data), ...(data.gotoEdges || [])];
    if (recovered.nodePatch.layout?.canvas) state.ecCanvas = { ...recovered.nodePatch.layout.canvas };
    render(); save(false, false); nodePatches.refresh();
  },
  setProjectSaver(fn, savedEpoch = state.editEpoch) {
    saveTarget = fn; state.projectSave = Boolean(fn); state.savedEpoch = savedEpoch;
    setSaveMode(state.projectSave); refreshSaveState();
  },
  setRestoring: value => { restoring = Boolean(value); },
  editEpoch: () => state.editEpoch,
  destroy() {
  lifetime.abort(); graph.destroy(); $('nodes').replaceChildren(); $('edges').replaceChildren(); $('edge-handles').replaceChildren();
  $('edges').classList.remove('hide-script', 'hide-all'); $('edge-handles').classList.remove('hide-all');
  $('fulltext').close();
  resetCanvasFrame();
  const selectionFrame = $('selection-frame'); selectionFrame.hidden = true;
  selectionFrame.style.left = ''; selectionFrame.style.top = ''; selectionFrame.style.width = ''; selectionFrame.style.height = '';
  for (const id of ['fit', 'arrange', 'game-layout', 'undo', 'redo', 'save', 'zoom-in', 'zoom-out', 'editor-open', 'back', 'collapse', 'expand', 'close-fulltext', 'close-cuts', 'save-ranges']) $(id).onclick = null;
  for (const id of ['cut-slider', 'cut-number', 'before-range', 'after-range']) { $(id).onchange = null; $(id).oninput = null; }
} };
}
