import { describeLocation } from '../../review-core/src/locations.mjs';
import { legacyReference, resolveEvidence, resolvePointer } from '../../review-core/src/evidence.mjs';

export function sceneLocation(bundle, model, reference) {
  if (reference.fileId !== 'scene' || reference.kind !== 'json-pointer') return null;
  const match = reference.pointer.match(/^\/nodes\/(0|[1-9][0-9]*)(?=\/|$)(?:\/part\/cuts\/(0|[1-9][0-9]*)(?=\/|$))?/u);
  if (!match) return null;
  const node = model.nodes[Number(match[1])];
  if (!node) return null;
  const rawCut = match[2] === undefined ? null : Number(match[2]);
  const cut = rawCut === null ? null : node.cuts.findIndex(item => item.rawIndex === rawCut);
  if (cut === -1) return null;
  return { node: node.id, cut, label: describeLocation(bundle, reference).label, reference };
}

function sourceReference(bundle, reference) {
  if (reference.kind !== 'json-pointer') return null;
  const document = reference.fileId === 'manifest' ? bundle.manifest : bundle.files.get(reference.fileId)?.json;
  let pointer = reference.pointer;
  while (pointer) {
    const value = resolvePointer(document, pointer);
    if (value?.rawRef) return legacyReference(value.rawRef).reference;
    if (value?.source?.fileId) return value.source;
    if (value?.from?.fileId) return value.from;
    pointer = pointer.slice(0, pointer.lastIndexOf('/'));
  }
  return null;
}

export function dependencyLocations(bundle, model, fileId, visited = new Set()) {
  if (visited.has(fileId)) return [];
  const next = new Set([...visited, fileId]);
  return bundle.manifest.dependencies.filter(edge => edge.status === 'included' && edge.targetFileId === fileId).flatMap(edge => {
    const direct = sceneLocation(bundle, model, edge.from);
    if (direct) return [{ ...direct, associated: true, dependency: fileId }];
    return dependencyLocations(bundle, model, edge.from.fileId, next);
  });
}

export function mapLocation(bundle, model, reference, visited = new Set()) {
  try {
    resolveEvidence(bundle, reference);
    const key = JSON.stringify(reference);
    if (visited.has(key)) return [];
    const next = new Set([...visited, key]);
    const direct = sceneLocation(bundle, model, reference);
    if (direct) return [direct];
    if (bundle.files.get(reference.fileId)?.meta.role === 'dependency') {
      const calls = dependencyLocations(bundle, model, reference.fileId);
      if (calls.length) return calls;
    }
    const source = sourceReference(bundle, reference);
    if (source) return mapLocation(bundle, model, source, next);
    return [{ unavailable: true, label: describeLocation(bundle, reference).label, reason: '该证据没有可对应的节点/CUT位置', reference }];
  } catch (error) {
    return [{ unavailable: true, label: reference.fileId, reason: error.message, reference }];
  }
}

const certainty = { confirmed: '静态事实已确认', conditional: '取决于触发条件', unknown: '仍需运行验证' };
export function adaptFindings(bundle, model, report) {
  return report.findings.map(finding => {
    // 精简撤销项在补不全时可能缺位置与证据；撤销区仍要能列出它，不能因为缺字段整页崩掉。
    const locations = (finding.locations || []).flatMap(reference => mapLocation(bundle, model, reference));
    return { ...finding, certainty: certainty[finding.certainty] || '', locations,
      external: locations.some(item => item.associated),
      quote: (finding.evidence || []).map(item => `${describeLocation(bundle, item.reference).label}\n${item.explanation}\n${item.quote}`).join('\n\n'),
      revision: finding.revisionReason || '', evidence: finding.evidence || [] };
  });
}
