import { identityKey, writeDraft } from './api.mjs';
export const routeLabels = { '': '未分类', main: '主线', side: '支线' };
export const statusLabels = { '': '未标注', planned: '待编写', wip: '未完成', done: '已完成' };
export const nodeFields = ['route', 'groups', 'stage', 'status', 'note'];
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const fail = message => { throw new Error(message); };
const sourceOf = data => ({ bundleId: data.bundleId, manifestSha256: data.manifestSha256 });

export function emptyAnnotations(data) {
  return { schemaVersion: '1.1-author', source: sourceOf(data), scope: { summary: '', expected: '', unfinished: '' }, groups: [], nodes: [] };
}
function text(value, label, limit = 20000) {
  if (typeof value !== 'string' || value.length > limit) fail(`${label}须为不超过 ${limit} 字的文本`);
  return value;
}
function choice(value, labels, name) {
  if (!Object.hasOwn(labels, value)) fail(`${name}无效`);
  return value;
}
function readGroups(values) {
  if (!Array.isArray(values)) fail('分组列表无效');
  const seen = new Set();
  return values.map(value => {
    if (!object(value)) fail('分组格式无效');
    const name = text(value.name, '分组名', 120);
    if (!name || seen.has(name)) fail('分组名为空或重复');
    seen.add(name);
    return { name, note: text(value.note, '组备注') };
  });
}
function readNode(value, known, seen, groups) {
  if (!object(value) || !known.has(value.uid)) fail(`标注引用了当前资料不存在的节点：${String(value?.uid).slice(0, 80)}`);
  if (seen.has(value.uid)) fail(`节点标注重复：${value.uid}`);
  seen.add(value.uid);
  if (!Array.isArray(value.groups) || value.groups.some(name => typeof name !== 'string' || !groups.has(name)) || new Set(value.groups).size !== value.groups.length) fail('节点分组引用无效或重复');
  return { uid: value.uid, route: choice(value.route, routeLabels, '路线类型'), groups: [...value.groups],
    stage: text(value.stage, '阶段', 120), status: choice(value.status, statusLabels, '完成状态'), note: text(value.note, '备注') };
}
function point(value) {
  return object(value) && ['x', 'y'].every(key => Number.isFinite(value[key]) && Math.abs(value[key]) < 1000000);
}
function canvas(value) {
  return object(value) && ['width', 'height'].every(key => Number.isFinite(value[key]) && value[key] > 0 && Math.abs(value[key]) < 1000000);
}
function readLayout(layout, known) {
  if (!object(layout) || !object(layout.positions)) fail('布局格式无效');
  const entries = Object.entries(layout.positions);
  if (entries.some(([uid, value]) => !known.has(uid) || !point(value))) fail('布局包含失效节点或无效坐标');
  if (!point(layout.camera) || !Number.isFinite(layout.camera.zoom) || layout.camera.zoom < .04 || layout.camera.zoom > 2) fail('布局视角无效');
  if (!['game', 'automatic', 'ec'].includes(layout.layoutSource)) fail('布局来源无效');
  if (layout.canvas !== undefined && !canvas(layout.canvas)) fail('布局画布无效');
  return { positions: Object.fromEntries(entries.map(([uid, value]) => [uid, { x: value.x, y: value.y }])),
    camera: { x: layout.camera.x, y: layout.camera.y, zoom: layout.camera.zoom }, layoutSource: layout.layoutSource,
    ...(layout.canvas ? { canvas: { width: layout.canvas.width, height: layout.canvas.height } } : {}) };
}

export function validateAnnotations(input, data) {
  if (!object(input) || !['1.0-author', '1.1-author'].includes(input.schemaVersion)) fail('不支持的作者标注格式');
  if (input.source?.bundleId !== data.bundleId || input.source?.manifestSha256 !== data.manifestSha256) fail('标注属于另一批资料，未覆盖当前内容');
  if (!object(input.scope) || !Array.isArray(input.nodes)) fail('作者标注结构不完整');
  const result = emptyAnnotations(data), known = new Set(data.nodes.map(node => node.id)), seen = new Set();
  for (const field of Object.keys(result.scope)) result.scope[field] = text(input.scope[field], '范围说明');
  let nodes = input.nodes;
  if (input.schemaVersion === '1.0-author') {
    nodes = nodes.map(value => {
      if (!object(value)) fail('节点标注格式无效');
      const group = text(value.group, '分组', 120);
      return { ...value, groups: group ? [group] : [] };
    });
    result.groups = [...new Set(nodes.flatMap(node => node.groups))].map(name => ({ name, note: '' }));
  } else result.groups = readGroups(input.groups);
  const groups = new Set(result.groups.map(group => group.name));
  result.nodes = nodes.map(value => readNode(value, known, seen, groups));
  if (input.layout !== undefined) result.layout = readLayout(input.layout, known);
  return result;
}

export function updateNodeAnnotations(annotations, ids, changes, data) {
  const current = validateAnnotations(annotations, data), entries = new Map(current.nodes.map(node => [node.uid, node]));
  if (Object.hasOwn(changes, 'group')) changes = { ...changes, groups: changes.group ? [changes.group] : [] };
  const groups = new Map(current.groups.map(group => [group.name, group]));
  if (Array.isArray(changes.groups)) for (const name of changes.groups) if (!groups.has(name)) groups.set(name, { name, note: '' });
  for (const uid of ids) {
    const value = { uid, route: '', groups: [], stage: '', status: '', note: '', ...entries.get(uid) };
    for (const field of nodeFields) if (Object.hasOwn(changes, field)) value[field] = changes[field];
    if (nodeFields.some(field => Array.isArray(value[field]) ? value[field].length : value[field] !== '')) entries.set(uid, value); else entries.delete(uid);
  }
  return validateAnnotations({ ...current, groups: [...groups.values()], nodes: [...entries.values()] }, data);
}

export function updateGroupNote(annotations, name, note, data) {
  const groups = new Map(annotations.groups.map(group => [group.name, group]));
  groups.set(name, { name, note });
  return validateAnnotations({ ...annotations, groups: [...groups.values()] }, data);
}

// 草稿由调用方预取;这里只按当前包身份核验。
export function loadAnnotations(data, saved) {
  try {
    return { value: saved ? validateAnnotations(saved, data) : emptyAnnotations(data), warning: '' };
  } catch (error) { return { value: emptyAnnotations(data), warning: `已保存标注无法读取:${error.message}。可导入备份恢复。` }; }
}
export function saveAnnotations(data, value) {
  const { layout, ...notes } = value;
  return writeDraft('annotations', identityKey(data), notes).then(() => '')
    .catch(() => '标注未保存到数据目录,请导出标注 JSON 备份;当前内容仅保留在本页。');
}
export function exportAnnotations(data, state) {
  const layout = { positions: state.positions, camera: state.camera, layoutSource: state.layoutSource,
    ...(canvas(state.ecCanvas) ? { canvas: { width: state.ecCanvas.width, height: state.ecCanvas.height } } : {}) };
  return validateAnnotations({ ...state.annotations, layout }, data);
}
export function handoffText(data, annotations) {
  const lines = ['# Scene 作者说明', '', `Scene：${data.title}`, `bundleId：${data.bundleId}`, `manifestSha256：${data.manifestSha256}`, '',
    '请同时提供原 F8 资料包、author-annotations.json 及 ec-scene-review 审查 skill。先核对资料来源，再按既有报告规范输出 JSON。',
    '以下是作者意图与工作进度，不是脚本事实或可达性证明。未完成内容应结合范围说明判断，不能仅凭作者标记认定正确。', '',
    '## 审查范围', annotations.scope.summary || '未填写', '', '## 作者预期', annotations.scope.expected || '未填写', '', '## 未完成内容', annotations.scope.unfinished || '未填写', '', '## 分组共用备注'];
  for (const group of annotations.groups) lines.push('', `### ${group.name}`, group.note || '无组备注');
  lines.push('', '## Part 标注');
  for (const item of annotations.nodes) {
    const node = data.nodes.find(node => node.id === item.uid);
    lines.push('', `### ${node?.name || item.uid}`, `当前包节点 UID：${item.uid}`, `分组：${item.groups.join('、') || '未分组'}；路线：${routeLabels[item.route]}；阶段：${item.stage || '未填写'}；状态：${statusLabels[item.status]}`, `Part 独立备注：${item.note || '无备注'}`);
  }
  if (!annotations.nodes.length) lines.push('未添加 Part 标注');
  return lines.join('\n');
}
