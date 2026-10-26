import { automaticLayout } from './domain.js';
import { gameLayout } from './game-layout.js';
import { emptyNodePatch, createdEdges } from '../node-patch.mjs';
import { recoverStoredNodePatch } from '../node-patch-recovery.mjs';
import { ecFrames } from '../ec-layout.mjs';
import { syncNodePatchLayout } from './layout-state.js';
import { validateRouteOffsets } from '../web-layout.mjs';
import { identityKey, writeDrafts, deleteDraft } from '../api.mjs';

const finite = (value) => Number.isFinite(value) && Math.abs(value) < 1000000;
const validCamera = (camera) => finite(camera?.x) && finite(camera?.y) && camera.zoom >= .04 && camera.zoom <= 2;
const validCanvas = (canvas) => finite(canvas?.width) && finite(canvas?.height) && canvas.width > 0 && canvas.height > 0;
const mergePositions = (base, saved) => {
  const result = structuredClone(base || {});
  for (const [id, point] of Object.entries(saved || {})) if (finite(point?.x) && finite(point?.y)) result[id] = { x: point.x, y: point.y };
  return result;
};
const patchFrames = (data, patch) => ecFrames(data, Object.fromEntries((patch?.layout?.nodes || []).map(item => [item.uid, item.after])));

// 草稿由 mountView 之前一次性取好再注入,这里不再自行读取,保证 createState 仍是同步函数。
export function decodeNodePatch(stored, data) {
  if (stored === null || stored === undefined) return { patch: emptyNodePatch(data), review: null };
  try {
    const wrapped = typeof stored === 'object' && !Array.isArray(stored) && 'patch' in stored && 'review' in stored;
    const recovered = recoverStoredNodePatch(wrapped ? stored.patch : stored, data, wrapped ? stored.review : null);
    return { patch: recovered.nodePatch, review: recovered.nodePatchReview };
  } catch { return { patch: emptyNodePatch(data), review: null, unavailable: true }; }
}

export function createState(data, draft = {}) {
  const game = gameLayout(data), automatic = automaticLayout(data), legacyBase = game ?? automatic;
  const stored = draft.layout ?? null, storedPatch = decodeNodePatch(draft.nodePatch, data);
  // 存储可用性由 /api/session 决定;探测失败或未探测时按不可用降级为只读浏览。
  const storageUnavailable = draft.storage !== true;
  const saved = stored?.version === 1 && stored.identity === data.manifestSha256 ? stored : null;
  const savedEc = saved?.layoutSource === 'ec';
  const legacyPositions = mergePositions(legacyBase.positions, savedEc ? null : saved?.positions);
  const legacySource = saved && !savedEc && saved.layoutSource === 'game' && game ? 'game'
      : saved?.layoutSource === 'automatic' ? 'automatic' : (game ? 'game' : 'automatic');
  const legacyCamera = validCamera(saved?.camera) ? saved.camera : { x: 25, y: 25, zoom: .8 };
  const base = legacySource === 'game' && game ? game : automatic;
  const patch = storedPatch.patch || emptyNodePatch(data);
  let routeOffsets = {};
  // 补丁新建的空槽边同样允许保存路线；漏传会让整份草稿路线被丢弃。
  try { routeOffsets = validateRouteOffsets(saved?.routeOffsets, data, createdEdges(data.baseEdges, patch, data)); } catch { /* 损坏的网页路线不影响游戏补丁与布局恢复。 */ }
  let positions = mergePositions(base.positions, saved && saved.layoutSource !== 'ec' ? saved.positions : null);
  let regions = base.regions;
  let layoutSource = legacySource;
  let restored = !!saved;
  if (data.geometry?.available) {
    const patchLayout = patch?.layout;
    const fromCurrentFrames = savedEc ? mergePositions(ecFrames(data) || base.positions, saved.positions) : null;
    if (fromCurrentFrames) {
      positions = fromCurrentFrames; regions = []; layoutSource = 'ec'; restored = true;
    } else if (!saved && patchLayout) {
      const fromPatch = patchFrames(data, patch);
      if (fromPatch) { positions = fromPatch; regions = []; layoutSource = 'ec'; restored = true; }
    } else {
      const exact = ecFrames(data);
      if (exact) { positions = exact; regions = []; layoutSource = 'ec'; }
    }
  }
  const state = { mode: 'report', layer: 1, findingId: null, locationIndex: 0, filter: null, listScroll: 0, collapsed: false, selected: new Set(), main: null, cut: 0, memory: {}, before: 2, after: 2, defaults: { before: 2, after: 2 }, choices: {}, camera: { ...legacyCamera }, positions, regions, layoutSource, history: [], restored,
    legacyPositions, legacyLayoutSource: legacySource, legacyCamera: { ...legacyCamera },
    ecCanvas: validCanvas(saved?.canvas) ? { ...saved.canvas }
      : (patch?.layout?.canvas ? { ...patch.layout.canvas } : (data.geometry?.value?.canvas ? { ...data.geometry.value.canvas } : null)),
    nodePatch: patch, nodePatchReview: storedPatch.review, storageUnavailable,
    nodePatchUnavailable: !!storedPatch.unavailable || (draft.failed || []).includes('node-patch'),
    future: [], routeOffsets, snapEnabled: true };
  state.interactionMode = 'select';
  for (const direction of ['before', 'after']) if ([0, 1, 2, 4, 8].includes(saved?.defaults?.[direction])) state.defaults[direction] = state[direction] = saved.defaults[direction];
  syncNodePatchLayout(data, state);
  return state;
}

function editSnapshot(state, positions = state.positions) {
  return structuredClone({ positions, regions: state.regions, layoutSource: state.layoutSource,
    nodePatch: state.nodePatch, ecCanvas: state.ecCanvas, routeOffsets: state.routeOffsets || {} });
}
export function rememberLayout(state, positions, name = '移动节点') {
  state.history.push({ ...editSnapshot(state, positions), name });
  state.future = [];
  if (state.history.length > 20) state.history.shift();
}

export function undoLayout(state) {
  if (!state.history.length) return false;
  const { name, ...snapshot } = state.history.pop();
  (state.future ||= []).push({ ...editSnapshot(state), name });
  Object.assign(state, snapshot, { historyAction: name });
  return true;
}
export function redoLayout(state) {
  if (!state.future?.length) return false;
  const { name, ...snapshot } = state.future.pop();
  state.history.push({ ...editSnapshot(state), name });
  Object.assign(state, snapshot, { historyAction: name });
  return true;
}

export function persist(data, state) {
  const id = identityKey(data);
  const values = { layout: { version: 1, identity: data.manifestSha256, positions: state.positions,
    camera: state.camera, defaults: state.defaults, layoutSource: state.layoutSource, routeOffsets: state.routeOffsets, ...(validCanvas(state.ecCanvas) ? { canvas: state.ecCanvas } : {}) } };
  if (state.nodePatch?.connections.length || state.nodePatch?.layout || state.nodePatchReview) {
    values['node-patch'] = state.nodePatchReview ? { version: 1, patch: state.nodePatch, review: state.nodePatchReview } : state.nodePatch;
  }
  // 异步写入不阻塞画布操作;读取失败的旧补丁不能当成空草稿删掉,等玩家自己处理。
  const pending = [writeDrafts(id, values)];
  const keepPatch = state.nodePatchUnavailable;
  if (!values['node-patch'] && !keepPatch) pending.push(deleteDraft('node-patch', id).then(() => ({ status: 'fulfilled' }), error => ({ status: 'rejected', reason: error })));
  return Promise.all(pending).then(results => results.flat());
}
