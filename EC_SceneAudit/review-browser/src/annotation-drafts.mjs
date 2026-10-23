import { validateAnnotations, updateNodeAnnotations, updateGroupNote } from './annotations.mjs';

// 草稿只记录编辑字段，交叉选区按最后编辑顺序应用，避免旧表单覆盖新值。
export function createAnnotationDrafts(data, initial) {
  let base = validateAnnotations(initial, data), sequence = 0;
  const nodes = new Map(), groups = new Map(), scope = new Map();
  const keyOf = ids => JSON.stringify([...ids].sort());
  function editNodes(ids, field, value, checked = true) {
    const key = keyOf(ids);
    if (!nodes.has(key)) nodes.set(key, { ids: [...ids], fields: new Map() });
    nodes.get(key).fields.set(field, { value: structuredClone(value), checked, sequence: ++sequence });
  }
  function collect() {
    let result = structuredClone(base);
    const edits = [...nodes.values()].flatMap(entry => [...entry.fields].map(([field, edit]) => ({ ...edit, ids: entry.ids, field })));
    for (const edit of edits.sort((a, b) => a.sequence - b.sequence)) {
      if (edit.checked) result = updateNodeAnnotations(result, edit.ids, { [edit.field]: edit.value }, data);
    }
    for (const [name, note] of groups) result = updateGroupNote(result, name, note, data);
    for (const [field, value] of scope) result.scope[field] = value;
    return validateAnnotations(result, data);
  }
  function reset(value) { base = validateAnnotations(value, data); nodes.clear(); groups.clear(); scope.clear(); }
  return {
    collect, reset, editNodes,
    nodeEdits: ids => nodes.get(keyOf(ids))?.fields ?? new Map(),
    editGroup: (name, note) => groups.set(name, note),
    editScope: (field, value) => scope.set(field, value),
    dirty: () => scope.size > 0 || groups.size > 0 || [...nodes.values()].some(entry => [...entry.fields.values()].some(edit => edit.checked))
  };
}
