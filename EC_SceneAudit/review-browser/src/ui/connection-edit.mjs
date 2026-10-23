const CONNECTION_KEYS = ['targetUid', 'conditionType', 'conditionCount', 'fadeType', 'fadeTime'];

const normalize = value => String(value || '').normalize('NFKC').replace(/\s+/gu, '').toLocaleLowerCase();

export function targetCandidates(nodes, sourceUid, query) {
  const input = normalize(query);
  const uidOnly = input.startsWith('uid:'), nameOnly = input.startsWith('name:');
  const needle = uidOnly || nameOnly ? input.slice(4) : input;
  return nodes.filter(node => node.id !== sourceUid)
    .map((node, index) => {
      const name = normalize(node.name), uid = normalize(node.id);
      const rank = name === needle || uid === needle ? 0 : name.startsWith(needle) || uid.startsWith(needle) ? 1 : 2;
      const match = !needle || (uidOnly ? uid.includes(needle) : nameOnly ? name.includes(needle) : name.includes(needle) || uid.includes(needle));
      return { node, index, rank, match };
    })
    .filter(item => item.match)
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map(item => item.node);
}

export function currentTarget(patch, sourceUid, slot, original) {
  return patch?.connections?.find(item => item.sourceUid === sourceUid && item.slot === slot)?.after?.targetUid || original;
}

export function connectionDraft(patch, data, sourceUid, slot, targetUid) {
  const source = data.nodes.find(node => node.id === sourceUid);
  if (!source) throw new Error(`找不到来源节点：${sourceUid}`);
  const output = source.outputs?.find(item => item.slot === slot);
  if (!output || typeof output.targetUid !== 'string' || !output.targetUid) throw new Error(`${source.name} 的出口 ${slot + 1} 不是已有非空连接`);
  const target = data.nodes.find(node => node.id === targetUid);
  if (!target) throw new Error(`连接目标不存在：${targetUid}`);
  if (targetUid === sourceUid) throw new Error(`${source.name} 的普通连接新目标不能是来源节点自身`);
  const connections = (patch?.connections || []).filter(item => !(item.sourceUid === sourceUid && item.slot === slot));
  if (targetUid !== output.targetUid) connections.push({ sourceUid, slot,
    before: Object.fromEntries(CONNECTION_KEYS.map(key => [key, output[key]])), after: { targetUid } });
  return { ...patch, connections };
}

export function connectionOptions(node) {
  return (node?.outputs || []).filter(output => typeof output.targetUid === 'string' && output.targetUid);
}
