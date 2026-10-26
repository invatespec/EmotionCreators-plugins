import { acceptsLink } from '../node-patch.mjs';

const CONNECTION_KEYS = ['targetUid', 'conditionType', 'conditionCount', 'fadeType', 'fadeTime'];

const normalize = value => String(value || '').normalize('NFKC').replace(/\s+/gu, '').toLocaleLowerCase();

export function targetCandidates(nodes, sourceUid, query) {
  const input = normalize(query);
  const uidOnly = input.startsWith('uid:'), nameOnly = input.startsWith('name:');
  const needle = uidOnly || nameOnly ? input.slice(4) : input;
  // 没有输入端口的节点（开始节点）不能作为连接目标，游戏里这种线接不上。
  return nodes.filter(node => node.id !== sourceUid && acceptsLink(node))
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
  // 断开项的 after.targetUid 是空字符串，不能被 || 回退成原目标。
  const entry = patch?.connections?.find(item => item.sourceUid === sourceUid && item.slot === slot);
  return entry ? entry.after.targetUid : original;
}

export function connectionDraft(patch, data, sourceUid, slot, targetUid) {
  const source = data.nodes.find(node => node.id === sourceUid);
  if (!source) throw new Error(`找不到来源节点：${sourceUid}`);
  const output = source.outputs?.find(item => item.slot === slot);
  if (!output || typeof output.targetUid !== 'string') throw new Error(`${source.name} 的出口 ${slot + 1} 不存在`);
  // 空目标表示断开：只对原本有连接的槽有效，没有连接可断的是无变化项。
  if (!targetUid) {
    if (!output.targetUid) throw new Error(`${source.name} 的出口 ${slot + 1} 本来就没有连接，无法断开`);
    const disconnected = (patch?.connections || []).filter(item => !(item.sourceUid === sourceUid && item.slot === slot));
    disconnected.push({ sourceUid, slot,
      before: Object.fromEntries(CONNECTION_KEYS.map(key => [key, output[key]])), after: { targetUid: '' } });
    return { ...patch, connections: disconnected };
  }
  const target = data.nodes.find(node => node.id === targetUid);
  if (!target) throw new Error(`连接目标不存在：${targetUid}`);
  if (targetUid === sourceUid) throw new Error(`${source.name} 的普通连接新目标不能是来源节点自身`);
  if (!acceptsLink(target)) throw new Error(`${target.name} 是开始节点，没有输入端口，不能作为连接目标`);
  const connections = (patch?.connections || []).filter(item => !(item.sourceUid === sourceUid && item.slot === slot));
  if (targetUid !== output.targetUid) connections.push({ sourceUid, slot,
    before: Object.fromEntries(CONNECTION_KEYS.map(key => [key, output[key]])), after: { targetUid } });
  return { ...patch, connections };
}

export function connectionOptions(node) {
  return (node?.outputs || []).filter(output => typeof output.targetUid === 'string' && output.targetUid);
}
