import { resolveEvidence } from './evidence.mjs';

export function describeLocation(bundle, reference) {
  resolveEvidence(bundle, reference);
  if (reference.fileId === 'manifest') return { label: '资料清单', reference };
  const member = bundle.files.get(reference.fileId);
  if (member.meta.role === 'dependency') {
    return { label: member.meta.sourcePath, reference, incoming: bundle.manifest.dependencies.filter(d => d.targetFileId === reference.fileId).map(d => d.from) };
  }
  const match = reference.pointer?.match(/^\/nodes\/([0-9]+)(?:\/part\/cuts\/([0-9]+))?(?:\/textBoxes\/([0-9]+))?(?:\/layouts\/([0-9]+))?/u);
  if (member.meta.role !== 'scene' || !match) return { label: member.meta.path, reference };
  const node = member.json.nodes[Number(match[1])];
  const cut = match[2] === undefined ? null : node.part.cuts[Number(match[2])];
  const box = match[3] === undefined ? null : cut.textBoxes[Number(match[3])];
  const layout = match[4] === undefined ? box?.layouts?.find(l => l?.rawText) : box.layouts[Number(match[4])];
  const excerpt = (layout?.rawText ?? '').replace(/<script\b[\s\S]*?<\/script\s*>/giu, '').trim().slice(0, 100);
  const labels = [node.name || '未命名 Part'];
  if (cut) labels.push(Number.isInteger(cut.sourceIndex) ? 'CUT ' + (cut.sourceIndex + 1) : 'CUT 位置未知');
  if (box) labels.push(box.option ? '选项' : '对白');
  if (excerpt) labels.push(excerpt);
  return { label: labels.join(' · '), reference, nodeUid: node.uid, excerpt };
}

export function optionMappings(node, cut) {
  const reasons = [];
  if (!cut?.endCut) return { status: 'not-end-cut', options: [], reasons };
  const boxes = cut.textBoxes ?? [];
  const sourceIndices = boxes.map(box => box?.sourceIndex);
  if (sourceIndices.some(i => !Number.isInteger(i) || i < 0)
    || new Set(sourceIndices).size !== sourceIndices.length) reasons.push('对白框源索引缺失或重复。');
  const ordered = [...boxes].sort((a, b) => a.sourceIndex - b.sourceIndex);
  if (ordered.some((box, i) => box.sourceIndex !== i)) reasons.push('对白框源序列存在读取缺口。');
  const options = ordered.filter(box => box.option);
  if (options.length !== cut.optionCount || options.length > 5) reasons.push('选项数量与快照或五个出口槽不一致。');
  if (reasons.length) return { status: 'unknown', options: [], reasons };
  return {
    status: 'conditional',
    reasons: ['按原版规则且选项资源正常创建；未验证其他插件对点击行为的改写。'],
    options: options.map((box, slot) => ({
      sourceIndex: box.sourceIndex, slot,
      text: box.layouts?.find(l => l?.layoutIndex === 0)?.rawText ?? '',
      targetUid: node.outputs?.find(output => output.slot === slot)?.targetUid ?? null,
    })),
  };
}
