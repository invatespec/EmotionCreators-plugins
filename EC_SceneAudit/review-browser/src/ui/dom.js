export const $ = (id) => document.getElementById(id);
export function el(tag, className, text) {
  const result = document.createElement(tag);
  if (className) result.className = className;
  if (text != null) result.textContent = text;
  return result;
}
export function button(text, handler, className = '') {
  const result = el('button', className, text); result.type = 'button'; result.onclick = handler; return result;
}
export function resetCanvasFrame() {
  const frame = $('canvas-frame');
  frame.hidden = true; frame.replaceChildren(); frame.style.width = ''; frame.style.height = '';
}
export function issueBadge(info, handler, label) {
  const result = button(`${info.associated ? '◇' : '!'} ${info.count}`, handler, `issue-badge ${info.severity}${info.associated ? ' associated' : ''}`);
  result.setAttribute('aria-label', `${label}：${info.count} 个${info.associated ? '外部脚本关联' : ''}问题`);
  return result;
}
