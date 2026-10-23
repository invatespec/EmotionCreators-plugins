import { emptyNodePatch, validateNodePatch, validateNodePatchForRecovery } from './node-patch.mjs';

const hasHistoricalSelf = patch => patch.connections.some(entry => entry.after.targetUid === entry.sourceUid);

function checkedReview(review, data) {
  const normalized = validateNodePatchForRecovery(review, data);
  if (!hasHistoricalSelf(normalized)) throw new Error('历史节点补丁快照不包含已禁止的普通自连');
  return structuredClone(review);
}

export function recoverStoredNodePatch(value, data, review = null, { allowLegacyValue = true } = {}) {
  const reviewProvided = review !== null && review !== undefined;
  const nodePatchReview = reviewProvided ? checkedReview(review, data) : null;
  if (value === null || value === undefined)
    return { nodePatch: emptyNodePatch(data), nodePatchReview };

  try {
    return { nodePatch: validateNodePatch(value, data), nodePatchReview };
  } catch (strictError) {
    if (!allowLegacyValue) throw strictError;
    const legacy = validateNodePatchForRecovery(value, data);
    if (!hasHistoricalSelf(legacy)) throw strictError;
    if (reviewProvided) throw new Error('活动节点补丁不能包含历史普通自连');
    const connections = legacy.connections.filter(entry => entry.after.targetUid !== entry.sourceUid);
    const active = validateNodePatch({
      ...emptyNodePatch(data), connections, ...(legacy.layout ? { layout: legacy.layout } : {})
    }, data);
    return { nodePatch: active, nodePatchReview: structuredClone(value) };
  }
}
