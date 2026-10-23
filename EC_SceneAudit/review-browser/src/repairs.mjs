import { identityKey, writeDraft } from './api.mjs';
const PLAN_FORMAT = 'ec-scene-repair-plan';
const PLAN_VERSION = 1;
const MAX_CHANGES = 1000;
const MAX_TEXT = 1000000;
// 只允许替换 scene 的 layout.rawText，位置一律用真实数组下标。
const pointerPattern = /^\/nodes\/(0|[1-9][0-9]*)\/part\/cuts\/(0|[1-9][0-9]*)\/textBoxes\/(0|[1-9][0-9]*)\/layouts\/(0|[1-9][0-9]*)\/rawText$/u;
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const fail = message => { throw new Error(message); };
const validIndex = value => Number.isInteger(value) && value >= 0;
const sourceOf = data => ({ bundleId: data.bundleId, manifestSha256: data.manifestSha256 });
// textarea 会把换行归一为 LF；比较时统一，避免只差 CRLF/LF 被当成修改。
const normalizeEol = value => value.replace(/\r\n|\r/gu, '\n');

export function emptyRepairs(data) {
  return { format: PLAN_FORMAT, version: PLAN_VERSION, source: sourceOf(data), changes: [] };
}

function locate(data, reference) {
  if (!object(reference) || reference.fileId !== 'scene' || reference.kind !== 'json-pointer' || typeof reference.pointer !== 'string') fail('修复目标引用无效');
  const match = reference.pointer.match(pointerPattern);
  if (!match) fail('修复目标必须指向 scene 的 layout.rawText');
  const nodeIndex = Number(match[1]), cutRawIndex = Number(match[2]), boxRawIndex = Number(match[3]), layoutRawIndex = Number(match[4]);
  const node = data.nodes[nodeIndex];
  if (!node || node.rawIndex !== nodeIndex) fail('修复目标指向不存在的 Part');
  const cut = node.cuts.find(item => item.rawIndex === cutRawIndex);
  if (!cut) fail('修复目标指向不存在的 CUT');
  const box = cut.boxes[boxRawIndex];
  if (!box || box.rawIndex !== boxRawIndex) fail('修复目标指向不存在的文本框');
  const layout = box.layouts[layoutRawIndex];
  if (!layout || layout.rawIndex !== layoutRawIndex) fail('修复目标指向不存在的布局');
  return { node, cut, box, layout, cutIndex: node.cuts.indexOf(cut), boxRawIndex, layoutRawIndex };
}

function labelFor(node, cut, box, layout) {
  const cutNumber = validIndex(cut.sourceIndex) ? cut.sourceIndex + 1 : node.cuts.indexOf(cut) + 1;
  const boxNumber = validIndex(box.sourceIndex) ? box.sourceIndex + 1 : box.rawIndex + 1;
  return `${node.name} · CUT ${cutNumber} · 文本框 ${boxNumber}${box.option ? ' · 选项' : ''} · 槽位 ${layout.layoutIndex}`;
}

function inspect(data, target) {
  if (!object(target)) fail('修复目标格式无效');
  const { node, cut, box, layout, cutIndex, boxRawIndex, layoutRawIndex } = locate(data, target.reference);
  if (!validIndex(cut.sourceIndex) || !validIndex(box.sourceIndex) || !validIndex(layout.layoutIndex) || typeof layout.rawText !== 'string') fail('该位置缺少原始文本或源索引，只能只读查看');
  if (target.nodeUid !== node.id) fail('修复目标的 Part 标识与位置不一致');
  if (target.cutSourceIndex !== cut.sourceIndex) fail('修复目标的 CUT 源序号与位置不一致');
  if (target.textBoxSourceIndex !== box.sourceIndex) fail('修复目标的文本框源序号与位置不一致');
  if (target.layoutIndex !== layout.layoutIndex) fail('修复目标的布局序号与位置不一致');
  return { before: layout.rawText, name: node.name, label: labelFor(node, cut, box, layout), nodeId: node.id, cutIndex, boxRawIndex, layoutRawIndex,
    key: target.reference.pointer,
    target: { reference: { fileId: 'scene', kind: 'json-pointer', pointer: target.reference.pointer }, nodeUid: node.id,
      cutSourceIndex: cut.sourceIndex, textBoxSourceIndex: box.sourceIndex, layoutIndex: layout.layoutIndex } };
}

export function targetForLayout(data, nodeId, cutIndex, boxRawIndex, layoutRawIndex) {
  const node = data.nodes.find(item => item.id === nodeId);
  if (!node) fail('找不到要修复的 Part');
  if (!validIndex(node.rawIndex)) fail('该资料没有原始索引，只能只读查看');
  if (!validIndex(cutIndex) || cutIndex >= node.cuts.length) fail('CUT 位置无效');
  const cut = node.cuts[cutIndex];
  if (!validIndex(cut.rawIndex) || !validIndex(cut.sourceIndex)) fail('该 CUT 没有源索引，只能只读查看');
  if (!validIndex(boxRawIndex) || boxRawIndex >= cut.boxes.length) fail('文本框位置无效');
  const box = cut.boxes[boxRawIndex];
  if (!validIndex(box.sourceIndex) || box.rawIndex !== boxRawIndex) fail('该文本框没有源索引，只能只读查看');
  if (!validIndex(layoutRawIndex) || layoutRawIndex >= box.layouts.length) fail('布局位置无效');
  const layout = box.layouts[layoutRawIndex];
  if (!validIndex(layout.layoutIndex) || layout.rawIndex !== layoutRawIndex || typeof layout.rawText !== 'string') fail('该布局没有原始文本或索引，只能只读查看');
  return { reference: { fileId: 'scene', kind: 'json-pointer', pointer: `/nodes/${node.rawIndex}/part/cuts/${cut.rawIndex}/textBoxes/${boxRawIndex}/layouts/${layoutRawIndex}/rawText` },
    nodeUid: node.id, cutSourceIndex: cut.sourceIndex, textBoxSourceIndex: box.sourceIndex, layoutIndex: layout.layoutIndex };
}

export function resolveRepairTarget(data, target) {
  const { before, name, label, nodeId, cutIndex, boxRawIndex, layoutRawIndex } = inspect(data, target);
  return { before, name, label, nodeId, cutIndex, boxRawIndex, layoutRawIndex };
}

export function validateRepairs(value, data) {
  if (!object(value) || value.format !== PLAN_FORMAT) fail('不支持的修复清单格式');
  if (value.version !== PLAN_VERSION) fail('不支持的修复清单版本');
  if (value.source?.bundleId !== data.bundleId || value.source?.manifestSha256 !== data.manifestSha256) fail('修复清单属于另一批资料，未覆盖当前内容');
  if (!Array.isArray(value.changes)) fail('修复清单结构不完整');
  if (value.changes.length > MAX_CHANGES) fail(`修复清单最多 ${MAX_CHANGES} 项`);
  const seen = new Set();
  const changes = value.changes.map(entry => {
    if (!object(entry)) fail('修复条目格式无效');
    const info = inspect(data, entry.target);
    if (seen.has(info.key)) fail(`修复清单包含重复位置：${info.label}`);
    seen.add(info.key);
    if (typeof entry.before !== 'string' || entry.before !== info.before) fail(`修复原文与当前资料不一致：${info.label}`);
    if (typeof entry.after !== 'string') fail(`修复新文必须是文本：${info.label}`);
    if (entry.after.length > MAX_TEXT) fail(`单项新文超过 ${MAX_TEXT} 字符：${info.label}`);
    if (entry.after === entry.before) fail(`修复内容与原文相同：${info.label}`);
    return { target: info.target, before: info.before, after: entry.after };
  });
  return { format: PLAN_FORMAT, version: PLAN_VERSION, source: sourceOf(data), changes };
}

export function setRepair(plan, data, target, after) {
  const safe = validateRepairs(plan, data), info = inspect(data, target);
  if (typeof after !== 'string') fail('修复新文必须是文本');
  if (after.length > MAX_TEXT) fail(`单项新文超过 ${MAX_TEXT} 字符`);
  const changes = safe.changes.filter(entry => entry.target.reference.pointer !== info.key);
  if (normalizeEol(after) !== normalizeEol(info.before)) changes.push({ target: info.target, before: info.before, after });
  return validateRepairs({ ...safe, changes }, data);
}

export function removeRepair(plan, data, target) {
  const safe = validateRepairs(plan, data), info = inspect(data, target);
  return validateRepairs({ ...safe, changes: safe.changes.filter(entry => entry.target.reference.pointer !== info.key) }, data);
}

function key(data) { return identityKey(data); }
// 草稿由调用方预取;这里只按当前包身份核验。
export function loadRepairs(data, saved) {
  try {
    return { value: saved ? validateRepairs(saved, data) : emptyRepairs(data), warning: '' };
  } catch (error) { return { value: emptyRepairs(data), warning: `已保存修复草稿无法读取:${error.message}。可重新编辑或导入备份。` }; }
}
export function saveRepairs(data, value) {
  let safe;
  try { safe = validateRepairs(value, data); } catch (error) { return Promise.resolve(`修复草稿未保存(${error.message}),当前内容仅保留在本页。`); }
  return writeDraft('repairs', key(data), safe).then(() => '')
    .catch(error => `修复草稿未保存到数据目录(${error.message}),当前内容仅保留在本页。`);
}
