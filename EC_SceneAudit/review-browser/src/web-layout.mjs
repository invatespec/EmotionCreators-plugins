export function validateRouteOffsets(value, data) {
  if (value === undefined) return {};
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('网页路线数据无效');
  const known = new Set((data.baseEdges || data.edges || []).filter(edge => edge.kind === 'normal').map(edge => edge.id));
  const result = {};
  for (const [id, point] of Object.entries(value)) {
    if (!known.has(id) || !point || Object.keys(point).some(key => key !== 'x' && key !== 'y') || ![point.x, point.y].every(n => Number.isFinite(n) && Math.abs(n) < 1000000)) throw new Error('网页路线数据无效');
    result[id] = { x: point.x, y: point.y };
  }
  return result;
}
