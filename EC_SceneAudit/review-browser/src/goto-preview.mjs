import { extractScriptBlock, parseGotoCommands } from './goto-script.mjs';

// goto 预览派生：原始与草稿共用同一解析器；只做纯数据，不碰 DOM、存储或原证据。
// 有草稿的精确 layout 先按 origin.pointer 删除全部基线边，再用 after 重建。

const isIndex = value => Number.isInteger(value) && value >= 0;
const STATUS_LABEL = {
  'dynamic-target': '动态目标', 'dynamic-index': '动态落点', 'ambiguous-part': '同名目标',
  'missing-part': '目标缺失', 'missing-cut': '落点缺失', 'missing-source': '来源缺失',
  'invalid-index': '索引无效', 'invalid-name': '名称无效', 'rejected-system-part': '系统 Part 不可跳转',
  'next-at-last': '末尾 next', 'prev-at-first': '首个 CUT 无 prev', 'unsupported-negative': '未支持负数',
  'unordered-cuts': 'CUT 索引不可定位', 'cut-gap': 'CUT 缺口',
};

function iterLayouts(data) {
  const result = [];
  for (const node of data && Array.isArray(data.nodes) ? data.nodes : []) {
    if (!node || !isIndex(node.rawIndex) || !Array.isArray(node.cuts)) continue;
    for (const cut of node.cuts) {
      if (!cut || !isIndex(cut.rawIndex) || !Array.isArray(cut.boxes)) continue;
      for (const box of cut.boxes) {
        if (!box || !isIndex(box.rawIndex) || !Array.isArray(box.layouts)) continue;
        for (const layout of box.layouts) {
          if (!layout || !isIndex(layout.rawIndex)) continue;
          result.push({ node, cut, box, layout,
            pointer: `/nodes/${node.rawIndex}/part/cuts/${cut.rawIndex}/textBoxes/${box.rawIndex}/layouts/${layout.rawIndex}/rawText` });
        }
      }
    }
  }
  return result;
}

function changeIndex(plan) {
  const result = new Map();
  for (const change of plan && Array.isArray(plan.changes) ? plan.changes : []) {
    const pointer = change?.target?.reference?.pointer;
    if (typeof pointer === 'string' && !result.has(pointer)) result.set(pointer, change);
  }
  return result;
}

function cutNumber(node, index) {
  const cut = node && Array.isArray(node.cuts) ? node.cuts[index] : null;
  if (cut && isIndex(cut.sourceIndex)) return cut.sourceIndex + 1;
  return isIndex(index) ? index + 1 : null;
}

function labelFor(command, sourceNode, targetNode) {
  const status = command.status === 'resolved' ? '' : ` · ${STATUS_LABEL[command.status] || command.status}`;
  if (command.command === 'goto-part') {
    const cut = command.toCut !== null && targetNode ? ` · CUT ${cutNumber(targetNode, command.toCut)}` : '';
    return `goto-part · ${command.targetName || '未知目标'}${cut}${status}`;
  }
  const cut = command.toCut !== null && sourceNode ? ` · CUT ${cutNumber(sourceNode, command.toCut)}` : '';
  return `goto-cut · ${command.index ?? ''}${cut}${status}`;
}

function edgeFor(command, draft, sourceNode, targetNode) {
  const origin = command.origin || {};
  return {
    id: `goto:${origin.pointer}:${origin.blockIndex}:${origin.commandIndex}`,
    kind: 'script', layer: 'goto', sourceKind: 'inline-goto',
    command: command.command, from: origin.nodeId, fromCut: origin.cutIndex,
    to: command.to ?? null, toCut: command.toCut ?? null,
    targetName: command.targetName || '', label: labelFor(command, sourceNode, targetNode),
    reason: command.status === 'resolved' ? null : command.reason,
    status: command.status, hint: command.hint || null, dynamic: !!command.dynamic,
    uncertain: !!command.uncertain, context: command.context, draft: !!draft,
    origin: { ...origin, path: [...(command.path || [])], context: command.context },
  };
}

// 调用签名同时包含首块有效性与全部嵌套 load-script；任一项变化都让旧依赖边失效，
// 避免嵌套 load 增删或首块变无效时 before/after 都是空签名而误判未变。
function loadScriptsOf(rawText) {
  const block = extractScriptBlock(rawText);
  return JSON.stringify({ found: block.found, valid: block.valid, loads: block.loadScripts });
}

function sameLoadScripts(before, after) {
  return loadScriptsOf(before) === loadScriptsOf(after);
}

// 精确 pointer 的有效文本：通过验证的草稿 after 覆盖原文，其余情况返回原文。
export function effectiveRawText(data, plan, pointer) {
  const item = iterLayouts(data).find(entry => entry.pointer === pointer);
  if (!item) return null;
  const change = changeIndex(plan).get(pointer);
  if (change && typeof change.before === 'string' && change.before === item.layout.rawText
    && typeof change.after === 'string')
    return change.after;
  return item.layout.rawText;
}

// 输入当前 F8 模型与已核验 plan；输出完整 goto 预览、诊断与被替换的 pointer。
// 不修改 data/plan/baseEdges；inline goto 一律由原文/after 重算，不信任旧摘要。
export function buildGotoPreview({ data, plan, baseEdges } = {}) {
  const nodes = data && Array.isArray(data.nodes) ? data.nodes : [];
  const changes = changeIndex(plan);
  const dependencyEdges = (baseEdges || []).filter(edge => edge && edge.sourceKind === 'dependency-goto');
  const edges = [];
  const diagnostics = [];
  const replaced = new Set();
  const effective = new Map();

  for (const { node, cut, box, layout, pointer } of iterLayouts(data)) {
    const change = changes.get(pointer);
    let text = layout.rawText, draft = false;
    if (change) {
      if (typeof change.before !== 'string' || change.before !== layout.rawText)
        diagnostics.push({ code: 'stale-draft', severity: 'warning', origin: { pointer },
          message: '草稿原文与当前资料不一致，已按原文预览。' });
      else if (typeof change.after === 'string') { text = change.after; draft = true; replaced.add(pointer); }
    }
    effective.set(pointer, { before: layout.rawText, after: text, draft });
    const cutIndex = node.cuts.indexOf(cut);
    const origin = { pointer, nodeId: node.id, nodeRawIndex: node.rawIndex, cutIndex,
      cutRawIndex: cut.rawIndex, boxRawIndex: box.rawIndex, layoutRawIndex: layout.rawIndex };
    const parsed = parseGotoCommands(text, origin, nodes);
    for (const command of parsed.commands)
      edges.push(edgeFor(command, draft, node, nodes.find(item => item.id === command.to) || null));
    for (const item of parsed.diagnostics) diagnostics.push({ ...item, draft });
  }

  const externalChanged = new Set();
  const loosePointers = new Set();
  // 仅来自宽松 summary 的依赖边只能作候选，不能与严格原文解析同等确定。
  const isLoose = edge => edge.summary === true || edge.certainty === 'candidate' || edge.incomplete === true;
  const passthrough = (base, pointer) => {
    const loose = isLoose(base);
    if (loose && typeof pointer === 'string') loosePointers.add(pointer);
    return { ...base, layer: 'goto', certainty: loose ? 'candidate' : (base.certainty || 'strict'),
      ...(loose ? { incomplete: true } : {}) };
  };
  for (const base of dependencyEdges) {
    const pointer = base?.origin?.pointer;
    if (typeof pointer === 'string' && changes.has(pointer)) {
      const info = effective.get(pointer);
      // load-script 未变：草稿只改同 layout 的 inline goto，仍有效的依赖边保留。
      if (info && sameLoadScripts(info.before, info.after)) edges.push(passthrough(base, pointer));
      else externalChanged.add(pointer);
      continue;
    }
    edges.push(passthrough(base, pointer));
  }

  // 同一 layout 的多个 load-script 调用保守整体失效：该调用点全部依赖边一起移除。
  for (const pointer of externalChanged)
    diagnostics.push({ code: 'external-changed', severity: 'warning', origin: { pointer },
      message: '草稿改动或移除了该调用点的 load-script，该调用点全部依赖跳转边已失效，需重新核验。' });
  for (const pointer of loosePointers)
    if (!externalChanged.has(pointer))
      diagnostics.push({ code: 'summary-incomplete', severity: 'info', origin: { pointer },
        message: '旧摘要只提供候选依赖跳转，未与严格原文解析同等确认。' });
  return { edges, diagnostics, replacedPointers: [...replaced], externalChangedPointers: [...externalChanged] };
}
