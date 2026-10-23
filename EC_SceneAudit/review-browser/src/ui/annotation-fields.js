import { $, el } from './dom.js';
import { nodeFields, routeLabels, statusLabels } from '../annotations.mjs';
import { chipField } from './chips.js';

const labels = { route: '路线类型', groups: '所属分组(可多选)', stage: '阶段', status: '完成状态', note: 'Part 独立备注' };
const optionsOf = labels => Object.entries(labels).filter(([value]) => value).map(([value, label]) => ({ value, label }));

export function mountAnnotationFields({ signal, changed }) {
  const controls = new Map();
  function emit(field) { changed(field, controls.get(field).value, $(`author-use-${field}`).checked); }
  for (const field of nodeFields) {
    const row = el('div', 'author-field'), check = el('input');
    check.type = 'checkbox'; check.id = `author-use-${field}`;
    const heading = el('label', 'author-field-heading'); heading.append(check, document.createTextNode(labels[field])); row.append(heading);
    if (field === 'note') {
      const control = el('textarea'); control.rows = 4; control.maxLength = 20000;
      control.addEventListener('input', () => { check.checked = true; emit(field); }, { signal }); row.append(control); controls.set(field, control);
    } else {
      // 路线、完成状态是固定枚举,禁止自建;阶段与分组可自由新建。
      const kind = { route: routeLabels, status: statusLabels }[field];
      const chips = chipField({
        multiple: field === 'groups', allowNew: !kind,
        placeholder: field === 'groups' ? '输入分组名后回车' : field === 'stage' ? '输入阶段名后回车' : '选择',
        options: kind ? optionsOf(kind) : [],
        onChange: () => { check.checked = true; emit(field); }
      });
      row.append(chips.root); controls.set(field, chips);
    }
    check.onchange = () => emit(field); $('author-fields').append(row);
  }
  function fill(ids, notes, edits) {
    for (const field of nodeFields) {
      const values = ids.map(id => notes.get(id)?.[field] ?? (field === 'groups' ? [] : ''));
      const same = values.length && values.every(value => JSON.stringify(value) === JSON.stringify(values[0]));
      const value = edits.get(field)?.checked === false ? edits.get(field).value : (same ? values[0] : field === 'groups' ? [] : '');
      const control = controls.get(field);
      if (control.setDisabled) control.value = field === 'groups' ? [...value] : value; else control.value = value;
      $(`author-use-${field}`).checked = edits.get(field)?.checked ?? ids.length === 1;
    }
    for (const control of $('author-fields').querySelectorAll('input,select,textarea,button')) control.disabled = !ids.length;
    for (const field of nodeFields) controls.get(field)?.setDisabled?.(!ids.length);
  }
  function choices(document) {
    const stages = [...new Set(document.nodes.map(node => node.stage).filter(Boolean))].sort();
    controls.get('stage')?.setOptions(stages.map(name => ({ value: name, label: name })));
    controls.get('groups')?.setOptions(document.groups.map(group => ({ value: group.name, label: group.name })));
  }
  return { fill, choices };
}