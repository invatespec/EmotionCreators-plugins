export function frameQueue(flush, request = requestAnimationFrame, cancel = cancelAnimationFrame) {
  let frame = null, camera = false, selection = false;
  const nodes = new Set();
  function run() {
    frame = null;
    const work = { camera, selection, nodes: new Set(nodes) };
    camera = false; selection = false; nodes.clear(); flush(work);
  }
  return {
    schedule(work = {}) {
      camera ||= !!work.camera; selection ||= !!work.selection;
      for (const id of work.nodes || []) nodes.add(id);
      if (frame === null) frame = request(run);
    },
    flush() { if (frame !== null) { cancel(frame); run(); } },
    destroy() { if (frame !== null) cancel(frame); frame = null; nodes.clear(); }
  };
}
