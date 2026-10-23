import { ecFrames, layoutFromFrames } from '../ec-layout.mjs';

export function applyLayoutPatch(state, data, patch) {
  if (!patch.layout) return false;
  const frames = ecFrames(data, Object.fromEntries(patch.layout.nodes.map(item => [item.uid, item.after])));
  if (!frames) return false;
  state.positions = frames;
  state.regions = [];
  state.layoutSource = 'ec';
  state.ecCanvas = { ...patch.layout.canvas };
  return true;
}

export function syncNodePatchLayout(data, state) {
  if (!data.geometry?.available || state.layoutSource !== 'ec') {
    delete state.nodePatch.layout;
    return;
  }
  const canvas = state.ecCanvas || data.geometry.value.canvas;
  try {
    const layout = layoutFromFrames(data, state.positions, canvas);
    if (layout) state.nodePatch.layout = layout;
    else delete state.nodePatch.layout;
  } catch {
    delete state.nodePatch.layout;
  }
}
