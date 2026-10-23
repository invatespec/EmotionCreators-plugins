import { marker } from './domain.js';

export function markerCache(data) {
  let findings, enabled, values = new Map();
  return {
    get(nodeId, show) {
      if (findings !== data.findings || enabled !== show) { findings = data.findings; enabled = show; values.clear(); }
      if (!values.has(nodeId)) values.set(nodeId, marker(data, nodeId, undefined, show));
      return values.get(nodeId);
    },
    invalidate() { values.clear(); }
  };
}
