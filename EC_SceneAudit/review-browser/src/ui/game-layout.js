const validCoordinate = value => Number.isFinite(value) && Math.abs(value) < 100000;

export function readGamePosition(position) {
  if (!validCoordinate(position?.x) || !validCoordinate(position?.y)) return null;
  return { x: position.x, y: position.y };
}

export function gameLayout(data) {
  const placed = data.nodes.filter(node => node.gamePosition);
  if (!placed.length) return null;
  const left = Math.min(...placed.map(node => node.gamePosition.x));
  const top = Math.max(...placed.map(node => node.gamePosition.y));
  const positions = {}, regions = [];
  // 游戏向下为负 Y；统一放大间距以容纳网页卡片，不改变作者的相对排布。
  for (const node of placed) positions[node.id] = {
    x: 80 + (node.gamePosition.x - left) * 2,
    y: 85 + (top - node.gamePosition.y) * 2
  };
  const missing = data.nodes.filter(node => !node.gamePosition);
  if (missing.length) {
    const y = Math.max(...Object.values(positions).map(point => point.y)) + 180;
    regions.push({ label: '缺少游戏位置 · 可拖动整理', x: 60, y: y - 52, width: Math.min(4, missing.length) * 230 + 20, height: Math.ceil(missing.length / 4) * 112 + 60 });
    missing.forEach((node, i) => { positions[node.id] = { x: 80 + i % 4 * 230, y: y + Math.floor(i / 4) * 112 }; });
  }
  return { positions, regions };
}
