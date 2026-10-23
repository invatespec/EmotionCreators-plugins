// 性能夹具：固定 seed 的合成大量节点场景，只用于交互耗时测量，不参与正式构建。
// 节点/边数量与形状固定，保证优化前后在同一夹具上对比。
const SEED = 0x5eed2026;
const NODE_COUNT = 320;
const COLUMNS = 20;
const NODE_WIDTH = 272;
const NODE_HEIGHT = 158;
const WIDE_WIDTH = 360;
const COLUMN_STEP = 380;
const ROW_STEP = 220;
const CANVAS = { width: 7900, height: 4100 };

function mulberry32(seed) {
  let value = seed >>> 0;
  return () => {
    value = (value + 0x6d2b79f5) >>> 0;
    let t = value;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const random = mulberry32(SEED);
const uid = index => `P${String(index).padStart(3, '0')}`;
const frameFor = index => ({
  width: index % 17 === 0 ? WIDE_WIDTH : NODE_WIDTH,
  height: NODE_HEIGHT
});
const gridPosition = index => ({
  x: 80 + (index % COLUMNS) * COLUMN_STEP,
  y: 85 + Math.floor(index / COLUMNS) * ROW_STEP
});

const nodes = [];
const geometryNodes = [];
for (let index = 0; index < NODE_COUNT; index++) {
  const id = uid(index), frame = frameFor(index), position = gridPosition(index);
  const kind = index === 0 ? 'Start' : index === NODE_COUNT - 1 ? 'End' : 'Part';
  const cuts = Array.from({ length: 3 }, (_, cut) => ({
    sourceIndex: cut,
    boxes: [{ speaker: '旁白', text: `${id} 第 ${cut + 1} 段性能夹具文本，长度用于稳定渲染。` }],
    choices: []
  }));
  nodes.push({
    id, name: `场景 ${String(index).padStart(3, '0')}`, kind, system: kind !== 'Part',
    cuts, gamePosition: { x: position.x, y: -position.y }, outputs: []
  });
  geometryNodes.push({
    uid: id, frame: { width: frame.width, height: frame.height }, positionOffset: { x: 0, y: 0 },
    input: { x: -2, y: frame.height / 2 },
    outputs: [{ slot: 0, x: frame.width + 2, y: frame.height / 2 }, { slot: 1, x: frame.width + 2, y: frame.height / 2 }]
  });
}

const normalEdges = [], scriptEdges = [];
const slotCount = new Map();
function addNormal(fromIndex, toIndex, label) {
  if (fromIndex === toIndex || fromIndex < 0 || toIndex < 0 || fromIndex >= NODE_COUNT || toIndex >= NODE_COUNT) return;
  const from = uid(fromIndex), to = uid(toIndex), slot = slotCount.get(from) ?? 0;
  if (slot >= 2) return;
  slotCount.set(from, slot + 1);
  const target = nodes[toIndex];
  nodes[fromIndex].outputs.push({ slot, targetUid: to, conditionType: 'ConnectPart', conditionCount: 0, fadeType: 2, fadeTime: 0.5 });
  normalEdges.push({
    id: `normal:${fromIndex}:${slot}`, from, to, kind: 'normal', label: label || '',
    slot, fromCut: 0, toCut: 0
  });
  if (!target) return;
}
let labelIndex = 0;
for (let index = 0; index < NODE_COUNT - 1; index++) {
  addNormal(index, index + 1, random() < .28 ? `继续 ${labelIndex++}` : '');
}
for (let index = 0; index + 11 < NODE_COUNT; index += 9) addNormal(index, index + 11, random() < .5 ? `分支 ${labelIndex++}` : '');
for (let index = 7; index < NODE_COUNT; index += 13) addNormal(index, index - 7, random() < .4 ? `回环 ${labelIndex++}` : '');
for (let index = 0; index + 4 < NODE_COUNT; index += 11) {
  scriptEdges.push({
    id: `script:${index}:0`, from: uid(index), to: uid(index + 4), kind: 'script',
    label: `脚本跳转 ${index}`, fromCut: 0, toCut: 0
  });
}

export const PERF_META = {
  seed: `0x${SEED.toString(16)}`,
  nodeCount: nodes.length,
  normalEdgeCount: normalEdges.length,
  scriptEdgeCount: scriptEdges.length,
  canvas: { ...CANVAS }
};

export const data = {
  // 合成身份也须符合补丁校验格式，才能走正式连接编辑与撤销路径。
  bundleId: 'node-pool-perf-v1', manifestSha256: '5eed2026'.repeat(8),
  title: '性能夹具 · 320 节点', exportedAt: '2026-10-21',
  nodes, findings: [], notices: [],
  geometry: {
    available: true,
    value: { nodes: geometryNodes, canvas: { ...CANVAS } }
  },
  // 夹具没有可解析的 goto 原文，脚本边直接随 baseEdges 保留（patchEdges 对非普通边原样透传）。
  baseEdges: [...normalEdges, ...scriptEdges],
  gotoBaseEdges: [],
  edges: [...normalEdges, ...scriptEdges]
};
