const canonical = value => {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  return value;
};
export const fingerprint = value => JSON.stringify(canonical(value));
const sorted = (values, key) => [...values].sort((a, b) => a[key] - b[key]);
function partShape(node, includeText) {
  return sorted(node.part?.cuts || [], 'sourceIndex').map(cut => ({
    sourceIndex: cut.sourceIndex, endCut: cut.endCut, optionCount: cut.optionCount,
    boxes: sorted(cut.textBoxes, 'sourceIndex').map(box => ({
      sourceIndex: box.sourceIndex, id: box.id, option: box.option, sortOrder: box.sortOrder,
      layouts: sorted(box.layouts, 'layoutIndex').map(layout => ({
        layoutIndex: layout.layoutIndex, ...(includeText ? { rawText: layout.rawText } : {})
      }))
    }))
  }));
}
function compatible(a, b) {
  if (typeof a.kind !== 'string' || !a.kind || typeof b.kind !== 'string' || !b.kind) return false;
  for (const node of [a, b]) if (node.part && (!Number.isInteger(node.part.kind) || typeof node.part.type !== 'string' || !node.part.type)) return false;
  return fingerprint([a.kind, !!a.isSystemNode, !!a.part, a.part?.kind, a.part?.type]) ===
    fingerprint([b.kind, !!b.isSystemNode, !!b.part, b.part?.kind, b.part?.type]);
}
function connectionIndex(nodes) {
  const incoming = new Map(nodes.map(node => [node.uid, []])), outgoing = new Map();
  for (const node of nodes) {
    const slots = node.outputs.map(output => output.slot);
    if (slots.some(slot => !Number.isInteger(slot) || slot < 0) || new Set(slots).size !== slots.length) throw new Error(`节点 ${node.uid} 出口槽位无效或重复`);
    outgoing.set(node.uid, sorted(node.outputs, 'slot'));
    for (const output of node.outputs) if (incoming.has(output.targetUid)) incoming.get(output.targetUid).push({ fromUid: node.uid, ...output });
  }
  for (const entries of incoming.values()) entries.sort((a, b) => a.fromUid.localeCompare(b.fromUid) || a.slot - b.slot);
  return { incoming, outgoing };
}
export function compareNodes(oldBundle, newBundle) {
  const oldNodes = oldBundle.files.get('scene').json.nodes, newNodes = newBundle.files.get('scene').json.nodes;
  const oldMap = new Map(oldNodes.map(node => [node.uid, node])), newMap = new Map(newNodes.map(node => [node.uid, node]));
  const oldConnections = connectionIndex(oldNodes), newConnections = connectionIndex(newNodes);
  const result = { matched: [], added: [], deleted: [], conflicted: [], changed: [], renamed: [] };
  for (const node of oldNodes) {
    const next = newMap.get(node.uid), item = { uid: node.uid, name: next?.name || node.name || node.uid };
    if (!next) { result.deleted.push(item); continue; }
    if (!compatible(node, next)) { result.conflicted.push(item); continue; }
    result.matched.push(item);
    const reasons = [];
    if (fingerprint(partShape(node, false)) !== fingerprint(partShape(next, false))) reasons.push('structure-changed');
    if (fingerprint(partShape(node, true)) !== fingerprint(partShape(next, true))) reasons.push('content-changed');
    const connections = index => [index.incoming.get(node.uid), index.outgoing.get(node.uid)];
    if (fingerprint(connections(oldConnections)) !== fingerprint(connections(newConnections))) reasons.push('connections-changed');
    if (reasons.length) result.changed.push({ ...item, reasons });
    if (node.name !== next.name) result.renamed.push(item);
  }
  result.added = newNodes.filter(node => !oldMap.has(node.uid)).map(node => ({ uid: node.uid, name: node.name || node.uid }));
  return result;
}
export function dependenciesChanged(oldBundle, newBundle) {
  const signature = bundle => {
    const members = bundle.manifest.files.filter(file => file.role === 'dependency');
    const files = members.map(file => ({ sourcePath: file.sourcePath, sha256: file.sha256, parseStatus: file.parseStatus }))
      .sort((a, b) => fingerprint(a).localeCompare(fingerprint(b)));
    const dependencies = bundle.manifest.dependencies.map(item => ({
      requestedPath: item.requestedPath, status: item.status, reason: item.reason,
      target: members.find(file => file.fileId === item.targetFileId)?.sourcePath ?? item.targetFileId
    })).sort((a, b) => fingerprint(a).localeCompare(fingerprint(b)));
    return { files, dependencies };
  };
  return fingerprint(signature(oldBundle)) !== fingerprint(signature(newBundle));
}
