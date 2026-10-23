function graphFor(ids, edges) {
  const outgoing = new Map(ids.map(id => [id, []]));
  const incoming = new Map(ids.map(id => [id, []]));
  for (const edge of edges) {
    if (!outgoing.has(edge.from) || !incoming.has(edge.to)) continue;
    if (outgoing.get(edge.from).includes(edge.to)) continue;
    outgoing.get(edge.from).push(edge.to);
    incoming.get(edge.to).push(edge.from);
  }
  return { outgoing, incoming };
}

function forwardGraph(ids, graph, nodes) {
  const byId = new Map(nodes.map(node => [node.id, node]));
  const terminal = id => !!byId.get(id)?.system && graph.outgoing.get(id).length === 0;
  const priority = id => byId.get(id)?.kind === 'Start' ? 0 : graph.incoming.get(id).length === 0 ? 1 : 2;
  const roots = [...ids].sort((a, b) => priority(a) - priority(b));
  const state = new Map(), parents = new Map(), branches = new Map(), order = [], edges = [];
  // 回到当前祖先的边仍显示，只从层级计算中排除，避免整个循环挤成一行。
  function visit(id) {
    state.set(id, 1); order.push(id);
    const targets = [...graph.outgoing.get(id)].sort((a, b) => Number(terminal(a)) - Number(terminal(b)));
    let branch = 0;
    for (const next of targets) {
      if (state.get(next) === 1) continue;
      edges.push({ from: id, to: next });
      if (state.has(next)) continue;
      parents.set(next, id); branches.set(next, branch++); visit(next);
    }
    state.set(id, 2);
  }
  for (const id of roots) if (!state.has(id)) visit(id);
  return { ...graphFor(ids, edges), parents, branches, order };
}

function orderedNodes(ids, graph) {
  const remaining = new Map(ids.map(id => [id, graph.incoming.get(id).length]));
  const queue = graph.order.filter(id => remaining.get(id) === 0);
  const priority = new Map(graph.order.map((id, index) => [id, index])), result = [];
  while (queue.length) {
    queue.sort((a, b) => priority.get(a) - priority.get(b));
    const id = queue.shift(); result.push(id);
    for (const next of graph.outgoing.get(id)) {
      remaining.set(next, remaining.get(next) - 1);
      if (remaining.get(next) === 0) queue.push(next);
    }
  }
  return result;
}

function occupy(occupied, row, column) {
  const used = occupied.get(row) || new Set();
  used.add(column); occupied.set(row, used);
}

function freePosition(row, preferred, parents, positions, occupied, maxColumns) {
  while (true) {
    const blocked = new Set(parents.filter(parent => [...positions.values()].some(point => point.column === parent.column && point.depth > parent.depth && point.depth < row)).map(parent => parent.column));
    // 已放置节点挡住同列入边时换列；极端汇合允许加列，避免无限寻找空行。
    const columns = Array.from({ length: maxColumns }, (_, offset) => (preferred + offset) % maxColumns).filter(column => !blocked.has(column));
    let extra = maxColumns;
    while (blocked.has(extra)) extra++;
    if (!columns.length) columns.push(extra);
    const column = columns.find(value => !occupied.get(row)?.has(value));
    if (column !== undefined) return { column, depth: row };
    row++;
  }
}

export function connectedLayout(ids, edges, nodes) {
  const graph = forwardGraph(ids, graphFor(ids, edges), nodes);
  const positions = new Map(), occupied = new Map(), maxColumns = 8;
  // 优先延续父列；过多入口换行，后续层级随真实前向边下移，不再无限横铺。
  for (const id of orderedNodes(ids, graph)) {
    const parents = graph.incoming.get(id).map(parent => positions.get(parent));
    const row = Math.max(-1, ...parents.map(parent => parent.depth)) + 1;
    const preferred = ((positions.get(graph.parents.get(id))?.column ?? 0) + (graph.branches.get(id) || 0)) % maxColumns;
    const point = freePosition(row, preferred, parents, positions, occupied, maxColumns);
    occupy(occupied, point.depth, point.column); positions.set(id, point);
    // 跨层直线经过的格子留空，后来安排的其它入口不能插入主链中间。
    for (const parent of parents.filter(parent => parent.column === point.column)) {
      for (let depth = parent.depth + 1; depth < point.depth; depth++) occupy(occupied, depth, point.column);
    }
  }
  return { positions, columns: Math.max(0, ...[...positions.values()].map(point => point.column)) + 1,
    rows: Math.max(0, ...[...positions.values()].map(point => point.depth)) + 1 };
}
