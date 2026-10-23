import { el, button } from './dom.js';
import './chips.css';

// 胶囊选择控件:options 为 {value,label};只存 value,渲染时再解析标签,setOptions 后已选项标签能同步更新。
export function chipField({ multiple = false, allowNew = false, placeholder = '', options = [], value = null, onChange }) {
  let pool = options.map(option => (typeof option === 'string' ? { value: option, label: option } : option));
  const labelOf = item => pool.find(option => option.value === item)?.label ?? item;
  let chosen = multiple ? [...(value || [])] : (value === '' || value == null ? [] : [value]);

  const root = el('div', 'chip-field');
  const chips = el('div', 'chip-list');
  const input = el('input', 'chip-input');
  input.type = 'text'; input.placeholder = placeholder; input.maxLength = 120;
  input.setAttribute('aria-label', placeholder || '输入');
  const optionsBox = el('div', 'chip-options'); optionsBox.hidden = true;
  let focused = false;

  function read() { return multiple ? [...chosen] : (chosen[0] ?? ''); }
  function emit() { onChange?.(read()); }
  function renderChips() {
    chips.replaceChildren(...chosen.map(item => {
      const chip = el('span', 'chip', labelOf(item));
      const remove = button('×', () => {
        chosen = chosen.filter(other => other !== item);
        renderChips(); renderOptions(); emit();
      }, 'chip-remove');
      remove.setAttribute('aria-label', `移除 ${labelOf(item)}`); chip.append(remove); return chip;
    }));
  }
  function renderOptions() {
    const query = input.value.trim();
    const matches = pool.filter(option => !chosen.includes(option.value) && (!query || option.label.includes(query)));
    // 聚焦即列出全部候选,便于直接点选已有分组/阶段;输入时再按关键字过滤。
    optionsBox.hidden = !focused || !matches.length;
    optionsBox.replaceChildren(...matches.map(option => button(option.label, () => pick(option.value), 'chip-option')));
  }
  function pick(item) {
    if (item === '' || item == null) return;
    if (multiple) { if (!chosen.includes(item)) chosen = [...chosen, item]; }
    else chosen = [item];
    input.value = ''; renderChips(); renderOptions(); emit();
  }
  input.addEventListener('input', renderOptions);
  input.addEventListener('keydown', event => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    const text = input.value.trim();
    if (!text) return;
    const existing = pool.find(option => option.label === text || option.value === text);
    if (existing) pick(existing.value);
    else if (allowNew) pick(text);
  });
  // 失焦后 120ms 再收起,给点击候选留时间;若期间已重新聚焦则不收起。
  input.addEventListener('blur', () => setTimeout(() => {
    if (input === document.activeElement) return;
    focused = false; optionsBox.hidden = true;
  }, 120));
  input.addEventListener('focus', () => { focused = true; renderOptions(); });
  root.addEventListener('click', () => { if (!input.disabled) input.focus(); });
  root.append(chips, input, optionsBox);
  renderChips();
  return {
    root,
    get value() { return read(); },
    set value(next) { chosen = next === '' || next == null ? [] : (multiple ? [...next] : [next]); renderChips(); renderOptions(); },
    setOptions(list) { pool = list.map(option => (typeof option === 'string' ? { value: option, label: option } : option)); renderChips(); renderOptions(); },
    setDisabled(disabled) { input.disabled = disabled; }
  };
}