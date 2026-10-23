export function sidePoint(position, size, side, t = .5, gap = 2) {
  if (side === 'left') return { x: position.x - gap, y: position.y + size.height * t };
  if (side === 'right') return { x: position.x + size.width + gap, y: position.y + size.height * t };
  if (side === 'top') return { x: position.x + size.width * t, y: position.y - gap };
  return { x: position.x + size.width * t, y: position.y + size.height + gap };
}

// 端口固定方向：普通连接右出左入，脚本跳转底出左入；入线共用目标左侧中心。
// 出点按同节点同侧数量均匀分布，顺序为 normal(slot 升序) / script(输入数组次序)。
export function edgeRoutes(edges, positions, sizeOf) {
  const routes = new Map();
  const groups = { normal: new Map(), script: new Map() };
  edges.forEach((edge, index) => {
    if (!positions[edge.from]) return;
    const kind = edge.kind === 'script' ? 'script' : 'normal';
    // 普通连接契约要求两端都在；脚本跳转只要有源就参与底部出口分布（含未解析目标），
    // 否则未收录目标的出口会漏出分布，违反"同节点脚本始终底部出口"。
    if (kind === 'normal' && !positions[edge.to]) return;
    const bucket = groups[kind];
    if (!bucket.has(edge.from)) bucket.set(edge.from, []);
    bucket.get(edge.from).push({ edge, index });
  });
  for (const kind of ['normal', 'script']) {
    for (const [, list] of groups[kind]) {
      // normal 按 slot 升序对应选项顺序；script 保留 data.edges 原始次序。
      const ordered = kind === 'normal'
        ? [...list].sort((a, b) => (a.edge.slot ?? 0) - (b.edge.slot ?? 0) || a.index - b.index)
        : list;
      const fromSide = kind === 'script' ? 'bottom' : 'right';
      ordered.forEach(({ edge }, i) => {
        routes.set(edge.id, { fromSide, toSide: 'left', fromT: (i + 1) / (ordered.length + 1), toT: .5 });
      });
    }
  }
  return routes;
}

export function routedShape(from, a, to, b, route, offset = null) {
  const start = sidePoint(from, a, route.fromSide, route.fromT ?? .5);
  const end = sidePoint(to, b, route.toSide, route.toT ?? .5);
  const sameSide = route.fromSide === route.toSide;
  const downGap = route.fromSide === 'bottom' && route.toSide === 'top' && end.y >= start.y;
  const horizontalGap = Math.max(to.x - from.x - a.width, from.x - to.x - b.width);
  const control = (point, side, size) => {
    const horizontal = side === 'left' || side === 'right', key = horizontal ? 'x' : 'y';
    const distance = Math.abs(horizontal ? end.x - start.x : end.y - start.y);
    const crossDistance = Math.abs(horizontal ? end.y - start.y : end.x - start.x);
    const mixedSides = (route.fromSide === 'left' || route.fromSide === 'right') !== (route.toSide === 'left' || route.toSide === 'right');
    const minimum = mixedSides ? Math.max(horizontal ? size.width / 2 : size.height * 2, crossDistance * .5) : 24;
    let bend = sameSide ? Math.max(64, Math.hypot(end.x - start.x, end.y - start.y) * .6) : Math.min(360, Math.max(minimum, distance * .5));
    if (mixedSides && horizontal && horizontalGap > 0) bend = Math.max(24, Math.min(360, horizontalGap / 2));
    if (downGap) bend = Math.max(.1, distance / 2);
    const direction = side === 'left' || side === 'top' ? -1 : 1;
    // 偏移仅调曲率，保持固定端点和朝框外的起末切线。
    const shift = Number.isFinite(offset?.[key]) ? offset[key] : 0;
    return { ...point, [key]: point[key] + direction * Math.max(downGap ? .1 : 24, bend + direction * shift) };
  };
  const c1 = control(start, route.fromSide, a), c2 = control(end, route.toSide, b);
  const x = (start.x + 3 * c1.x + 3 * c2.x + end.x) / 8, y = (start.y + 3 * c1.y + 3 * c2.y + end.y) / 8;
  return { path: `M${start.x} ${start.y} C${c1.x} ${c1.y},${c2.x} ${c2.y},${end.x} ${end.y}`, x, y, start, end };
}

export function placeLabel(shape, text, positions, sizeOf, placed) {
  if (!text) return { x: shape.x, y: shape.y - 8, crowded: false };
  const width = Math.min(240, text.length * 8 + 12), height = 18;
  const clear = (x, y) => {
    const rect = { x: x - width / 2, y: y - height, width, height };
    const overlap = b => rect.x < b.x + b.width + 5 && rect.x + width + 5 > b.x && rect.y < b.y + b.height + 5 && rect.y + height + 5 > b.y;
    return !Object.entries(positions).some(([id, p]) => overlap({ ...p, ...sizeOf(id) })) && !placed.some(overlap);
  };
  for (const [dx, dy] of [[0, -8], [0, 20], [-width / 2 - 12, 0], [width / 2 + 12, 0], [0, -36], [0, 48]]) {
    const x = shape.x + dx, y = shape.y + dy;
    if (!clear(x, y)) continue;
    placed.push({ x: x - width / 2, y: y - height, width, height }); return { x, y, crowded: false };
  }
  return { x: shape.x, y: shape.y - 8, crowded: true };
}
