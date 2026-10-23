import { $, el, button } from './dom.js';

export function searchParts(nodes, query) {
  const normalize = value => String(value || '').normalize('NFKC').replace(/\s+/gu, '').toLocaleLowerCase();
  const needle = normalize(query);
  return nodes.filter(node => !node.system && normalize(node.name).includes(needle))
    .map((node, index) => ({ node, index, rank: normalize(node.name) === needle ? 0 : normalize(node.name).startsWith(needle) ? 1 : 2 }))
    .sort((a, b) => a.rank - b.rank || a.index - b.index).map(item => item.node);
}

export function mountSearch({ data, actions, signal }) {
  const input = $('part-search'), popup = $('part-search-results'), container = $('part-search-control');
  if (!input || !popup || !container) return;
  let candidates = [], active = 0;
  const names = new Map();
  for (const node of data.nodes.filter(node => !node.system)) {
    if (!names.has(node.name)) names.set(node.name, []);
    names.get(node.name).push(node.id);
  }
  function close() { popup.hidden = true; input.setAttribute('aria-expanded', 'false'); input.removeAttribute('aria-activedescendant'); }
  function choose(node) {
    close(); input.blur(); actions.selectNode(node.id); actions.status(`已定位 Part「${node.name}」`);
  }
  function render() {
    popup.hidden = false; input.setAttribute('aria-expanded', 'true');
    popup.replaceChildren(el('p', 'search-count', `${candidates.length} 个 Part${candidates.length ? ' · ↑↓ 选择，Enter 跳转' : ' · 没有匹配名称'}`));
    candidates.forEach((node, index) => {
      const item = button('', () => choose(node), 'search-result'); item.dataset.node = node.id;
      item.id = `part-search-option-${index}`; item.setAttribute('role', 'option'); item.setAttribute('aria-selected', String(index === active));
      const duplicates = names.get(node.name), duplicate = duplicates.length > 1 ? ` · 同名 ${duplicates.indexOf(node.id) + 1}/${duplicates.length}` : '';
      item.append(el('strong', '', node.name), el('span', 'muted', `${node.kind || 'Part'} · ${node.cuts.length} CUT${duplicate}`));
      popup.append(item);
    });
    if (candidates.length) input.setAttribute('aria-activedescendant', `part-search-option-${active}`);
    else input.removeAttribute('aria-activedescendant');
  }
  function filter() { candidates = searchParts(data.nodes, input.value); active = 0; render(); }
  input.addEventListener('input', filter, { signal }); input.addEventListener('focus', filter, { signal });
  input.addEventListener('keydown', event => {
    if (event.isComposing) return;
    if (event.key === 'Escape') { event.preventDefault(); close(); input.blur(); return; }
    if (event.key === 'Enter') { if (!popup.hidden && candidates[active]) { event.preventDefault(); choose(candidates[active]); } return; }
    if (!['ArrowDown', 'ArrowUp'].includes(event.key)) return;
    event.preventDefault();
    if (popup.hidden) filter();
    else if (candidates.length) { active = (active + (event.key === 'ArrowDown' ? 1 : -1) + candidates.length) % candidates.length; render(); }
    $(`part-search-option-${active}`)?.scrollIntoView({ block: 'nearest' });
  }, { signal });
  document.addEventListener('pointerdown', event => { if (!container.contains(event.target)) close(); }, { capture: true, signal });
  container.addEventListener('focusout', event => { if (!container.contains(event.relatedTarget)) close(); }, { signal });
  signal?.addEventListener('abort', () => { close(); input.value = ''; popup.replaceChildren(); }, { once: true });
}
