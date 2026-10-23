const selectedIds = (positions, ids) => [...ids].filter(id => positions[id]);

export function alignPositions(positions, ids, sizeOf, mode, anchorId = null) {
  const selected = selectedIds(positions, ids);
  if (selected.length < 2) return null;
  const points = selected.map(id => ({ id, size: sizeOf(id), point: positions[id] }));
  const bounds = anchorId && selected.includes(anchorId) ? points.filter(item => item.id === anchorId) : points;
  const minLeft = Math.min(...bounds.map(({ point }) => point.x));
  const maxRight = Math.max(...bounds.map(({ point, size }) => point.x + size.width));
  const minTop = Math.min(...bounds.map(({ point }) => point.y));
  const maxBottom = Math.max(...bounds.map(({ point, size }) => point.y + size.height));
  const result = structuredClone(positions);
  for (const { id, size, point } of points) {
    if (mode === 'left') result[id].x = minLeft;
    if (mode === 'center') result[id].x = (minLeft + maxRight - size.width) / 2;
    if (mode === 'right') result[id].x = maxRight - size.width;
    if (mode === 'top') result[id].y = minTop;
    if (mode === 'middle') result[id].y = (minTop + maxBottom - size.height) / 2;
    if (mode === 'bottom') result[id].y = maxBottom - size.height;
  }
  return result;
}

export function distributePositions(positions, ids, sizeOf, axis, gap = null) {
  const selected = selectedIds(positions, ids);
  if (selected.length < 3) return null;
  const horizontal = axis === 'horizontal';
  const points = selected.map(id => ({ id, position: positions[id], size: sizeOf(id) }))
    .sort((a, b) => (horizontal ? a.position.x - b.position.x : a.position.y - b.position.y));
  const first = points[0], last = points.at(-1);
  const key = horizontal ? 'x' : 'y', dimension = horizontal ? 'width' : 'height';
  const total = points.reduce((sum, item) => sum + item.size[dimension], 0);
  const available = (last.position[key] + last.size[dimension] - first.position[key] - total) / (points.length - 1);
  const spacing = gap == null ? Math.max(24, available) : gap;
  if (!Number.isFinite(spacing) || spacing < 0 || spacing > 100000) return null;
  const result = structuredClone(positions);
  let cursor = first.position[key];
  for (const { id, size } of points) { result[id][key] = cursor; cursor += size[dimension] + spacing; }
  return result;
}

function closestAnchors(left, right, limit) {
  let best = { distance: limit, adjustment: 0, value: null };
  for (const a of left) for (const b of right) {
    if (Math.abs(b - a) < best.distance) best = { distance: Math.abs(b - a), adjustment: b - a, value: b };
  }
  return best;
}

export function snapMove(positions, ids, sizeOf, dx, dy, tolerance) {
  const selected = selectedIds(positions, ids), moving = new Set(selected), guides = [];
  if (!selected.length) return { dx, dy, guides };
  const anchors = (id, axis, shift) => {
    const key = axis === 'x' ? 'width' : 'height', start = positions[id][axis] + shift;
    return [start, start + sizeOf(id)[key] / 2, start + sizeOf(id)[key]];
  };
  const stationary = Object.keys(positions).filter(uid => !moving.has(uid));
  const pairs = selected.flatMap(id => stationary.map(other => [id, other]));
  for (const axis of ['x', 'y']) {
    let best = { distance: tolerance + 1, adjustment: 0, value: null };
    for (const [id, other] of pairs) {
      const candidate = closestAnchors(anchors(id, axis, axis === 'x' ? dx : dy), anchors(other, axis, 0), best.distance);
      if (candidate.distance < best.distance) best = candidate;
    }
    if (best.distance > tolerance) continue;
    if (axis === 'x') dx += best.adjustment; else dy += best.adjustment;
    guides.push({ axis, value: best.value });
  }
  return { dx, dy, guides };
}
