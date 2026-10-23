import { optionMappings } from '../../review-core/src/locations.mjs';
import { scriptEdges } from './scripts.mjs';
import { previewText, optionText } from './presentation.mjs';
import { readGamePosition } from './ui/game-layout.js';
import { validateGeometry } from '../../review-core/src/geometry.mjs';
import { buildGotoPreview } from './goto-preview.mjs';

function adaptCut(cut, rawIndex) {
  if (!cut || !Array.isArray(cut.textBoxes)) throw new Error('CUT 的文本框结构不完整。');
  const boxes = cut.textBoxes.map((box, index) => {
    if (!box || !Array.isArray(box.layouts)) throw new Error('文本框的布局结构不完整。');
    return { sourceIndex: box.sourceIndex, rawIndex: index, option: !!box.option,
      layouts: box.layouts.map((layout, layoutRawIndex) => ({ layoutIndex: layout.layoutIndex, rawIndex: layoutRawIndex, rawText: layout.rawText })),
      text: box.layouts.map(layout => previewText(layout.rawText)).filter(Boolean).join('\n') || '（空文本或仅含脚本）' };
  });
  return { sourceIndex: cut.sourceIndex, rawIndex, boxes, choices: boxes.filter(box => box.option).map(box => optionText(box.layouts.find(layout => layout.layoutIndex === 0)?.rawText) || '（选项文字为空或仅含脚本）'), endCut: cut.endCut };
}

function adaptNode(node, rawIndex) {
  if (!node || typeof node.uid !== 'string' || !node.uid || !Array.isArray(node.outputs)) throw new Error('节点标识或连接结构不完整。');
  if (node.part && !Array.isArray(node.part.cuts)) throw new Error('Part 的 CUT 结构不完整。');
  const cuts = (node.part?.cuts || []).map(adaptCut);
  const indices = cuts.map(cut => cut.sourceIndex);
  const ordered = indices.every(index => Number.isInteger(index) && index >= 0) && new Set(indices).size === indices.length;
  if (ordered) cuts.sort((a, b) => a.sourceIndex - b.sourceIndex);
  return { id: node.uid, name: node.name || '未命名节点', kind: node.kind, system: !!node.isSystemNode, rawIndex, cuts, ordered,
    gamePosition: readGamePosition(node.position), outputs: node.outputs.map(output => ({ ...output })) };
}

function normalEdges(scene, model) {
  return scene.nodes.flatMap((raw, nodeIndex) => {
    const node = model.nodes[nodeIndex];
    const endCuts = (raw.part?.cuts || []).map((cut, index) => ({ index, cut, mapping: optionMappings(raw, cut) })).filter(item => item.cut.endCut);
    return raw.outputs.filter(output => typeof output.targetUid === 'string' && output.targetUid).map((output, index) => {
      const target = model.nodes.find(item => item.id === output.targetUid);
      const choices = endCuts.flatMap(item => item.mapping.options.filter(option => option.slot === output.slot).map(option => ({ ...option, cut: item.index })));
      const sourceIndices = [...new Set(choices.map(choice => choice.cut))];
      const sourceRaw = sourceIndices.length === 1 ? sourceIndices[0] : sourceIndices.length === 0 && endCuts.length === 1 ? endCuts[0].index : null;
      const source = sourceRaw === null ? -1 : node.cuts.findIndex(cut => cut.rawIndex === sourceRaw);
      const text = [...new Set(choices.map(option => optionText(option.text)).filter(Boolean))].join(' / ');
      return { id: `normal:${nodeIndex}:${index}`, kind: 'normal', from: node.id, to: target?.id || null, slot: output.slot,
        fromCut: source >= 0 ? source : null, toCut: target?.cuts.findIndex(cut => cut.sourceIndex === 0) ?? null,
        // 边标签只显示选项文本；无文本的边不上图（出口编号/条件类型属冗余信息）。
        label: text,
        targetName: target ? target.name : '未收录节点', inactive: output.conditionType === 'NotUsed',
        reason: target ? null : '连接目标未收录，停止展开' };
    });
  });
}

export function createModel(bundle) {
  if (!bundle.verified) throw new Error('资料尚未通过核验。');
  const scene = bundle.files.get('scene').json;
  const model = { bundleId: bundle.manifest.bundleId, manifestSha256: bundle.manifestSha256,
    title: bundle.manifest.scene.title, exportedAt: bundle.manifest.scene.exportedAt,
    geometry: bundle.geometry || validateGeometry(scene), nodes: scene.nodes.map(adaptNode), findings: [], edges: [], notices: [] };
  if (new Set(model.nodes.map(node => node.id)).size !== model.nodes.length) throw new Error('节点 UID 重复，无法可靠定位。');
  for (const node of model.nodes) if (!node.ordered) model.notices.push(`${node.name} 的 CUT 源索引缺失或重复，不能推断脚本落点。`);
  model.baseEdges = normalEdges(scene, model);
  model.gotoBaseEdges = scriptEdges(bundle, model);
  const preview = buildGotoPreview({ data: model, plan: { changes: [] }, baseEdges: model.gotoBaseEdges });
  model.edges = [...model.baseEdges, ...preview.edges];
  model.gotoDiagnostics = preview.diagnostics;
  return model;
}
