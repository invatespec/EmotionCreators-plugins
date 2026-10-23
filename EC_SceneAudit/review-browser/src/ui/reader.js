import { $, el, button, issueBadge } from './dom.js';
import { nodeSize, nodeById, marker, contextRows, edgeLabel, cutNumber } from './domain.js';
import { targetForLayout } from '../repairs.mjs';

export function mountReader({ data, state, actions, signal }) {
  for (const id of ['before-range', 'after-range']) {
    $(id).replaceChildren();
    for (const value of [0, 1, 2, 4, 8]) $(id).add(new Option(String(value), String(value)));
  }

  function card(nodeId, cutIndex, current = false) {
    const node = nodeById(data, nodeId), cut = node.cuts[cutIndex];
    const item = el('article', `cut-card${current ? ' current' : ''}`); item.dataset.node = nodeId; item.dataset.cut = cutIndex;
    const heading = el('div', 'cut-card-heading'); heading.append(el('span', '', `${nodeId === state.main ? '' : `${node.name} · `}CUT ${cutNumber(node, cutIndex)}`));
    const info = marker(data, nodeId, cutIndex, state.mode === 'report');
    if (info.count) heading.append(issueBadge(info, () => actions.openMarker(nodeId, cutIndex), `${node.name} CUT ${cutNumber(node, cutIndex)}`));
    item.append(heading);
    const preview = button('', () => openFulltext(nodeId, cutIndex), 'cut-preview');
    preview.append(el('span', 'preview-text', cut.boxes.map((box) => `${box.speaker ? box.speaker + '：' : ''}${box.text}`).join('\n')));
    if (cut.choices.length) preview.append(el('span', 'preview-choices', cut.choices.map((text) => `○ ${text}`).join('  ')));
    preview.append(el('span', 'preview-count', `${cut.boxes.length} 个文本框${cut.choices.length ? ` · ${cut.choices.length} 个选项` : ''}`)); item.append(preview);
    return item;
  }

  function renderContext(direction) {
    const container = $(direction === 'before' ? 'before-context' : 'after-context');
    const rows = contextRows(data, state.main, state.cut, direction, state[direction], state.choices);
    container.replaceChildren(...rows.map((row) => {
      if (row.kind === 'cut') return card(row.nodeId, row.cut);
      if (row.kind === 'notice') return el('p', 'context-notice', row.text);
      const label = el('label', 'route-selector', `${direction === 'before' ? '前驱' : '后继'} Part · 选择阅读路线`), select = el('select');
      select.dataset.routeKey = row.key; select.add(new Option('请选择路线（不代表运行轨迹）', ''));
      row.routes.forEach((route) => select.add(new Option(edgeLabel(data, route, direction), route.id)));
      select.value = state.choices[row.key] || '';
      select.onchange = () => { state.choices[row.key] = select.value; render(); actions.status('已切换上下文路线，主选 Part 不变'); };
      label.append(select); return label;
    }));
    if (direction === 'before') container.scrollTop = container.scrollHeight;
  }

  function position() {
    if (!state.main || $('cut-panel').hidden) return;
    const before = $('before-context');
    const followCurrent = before.scrollHeight - before.clientHeight - before.scrollTop < 2;
    const point = state.positions[state.main], stage = $('stage');
    const topInset = stage.querySelector('.canvas-toolbar').offsetHeight + 8;
    const bottomInset = stage.querySelector('.canvas-bottom').offsetHeight + 24;
    const width = Math.min(354, stage.clientWidth - 20), height = Math.min(650, stage.clientHeight - topInset - bottomInset);
    const compact = height < 460;
    const headerHeight = compact ? 40 : 48, rangeHeight = compact ? 24 : 28;
    const currentHeight = compact ? 108 : 130, footerHeight = compact ? 28 : 32;
    $('cut-panel').classList.toggle('compact', compact);
    $('cut-panel').style.width = `${width}px`; $('cut-panel').style.height = `${height}px`;
    const size = nodeSize(data, state, state.main);
    const nodeY = state.camera.y + (point.y + size.height / 2) * state.camera.zoom;
    const nodeRight = state.camera.x + (point.x + size.width) * state.camera.zoom;
    const cardRect = $('current-cut').getBoundingClientRect(), panelRect = $('cut-panel').getBoundingClientRect();
    const offset = cardRect.top + cardRect.height / 2 - panelRect.top;
    const top = Math.max(topInset, Math.min(stage.clientHeight - height - bottomInset, nodeY - offset));
    const contextHeight = Math.max(0, height - headerHeight - rangeHeight * 2 - currentHeight - footerHeight - 2);
    const minimumContext = Math.min(48, contextHeight / 2);
    const anchorOffset = 1 + headerHeight + rangeHeight + currentHeight / 2;
    const beforeHeight = Math.max(minimumContext, Math.min(contextHeight - minimumContext, nodeY - top - anchorOffset));
    $('cut-panel').style.gridTemplateRows = `${headerHeight}px ${beforeHeight}px ${rangeHeight}px ${currentHeight}px ${rangeHeight}px ${contextHeight - beforeHeight}px ${footerHeight}px`;
    $('cut-panel').style.left = `${Math.max(10, Math.min(stage.clientWidth - width - 10, nodeRight + 22))}px`;
    $('cut-panel').style.top = `${top}px`;
    if (followCurrent) before.scrollTop = before.scrollHeight;
  }

  function render() {
    const node = nodeById(data, state.main);
    $('cut-panel').hidden = !node; $('empty-selection').hidden = !!node;
    if (!node) return;
    $('part-name').textContent = node.name;
    $('before-range').value = state.before; $('after-range').value = state.after;
    $('range-status').textContent = state.before === state.defaults.before && state.after === state.defaults.after ? '默认范围' : '临时范围';
    for (const id of ['cut-slider', 'cut-number']) { $(id).disabled = !node.cuts.length; $(id).max = id === 'cut-slider' ? Math.max(0, node.cuts.length - 1) : Math.max(1, ...node.cuts.map((cut, index) => cutNumber(node, index))); }
    $('cut-slider').value = state.cut; $('cut-number').value = node.cuts.length ? cutNumber(node, state.cut) : ''; $('cut-total').textContent = node.cuts.length;
    $('before-context').replaceChildren(); $('after-context').replaceChildren();
    $('current-cut').replaceChildren(node.cuts.length ? card(node.id, state.cut, true) : el('p', 'empty-cut', '此 Part 没有可阅读的 CUT'));
    if (node.cuts.length) { renderContext('before'); renderContext('after'); }
    position();
  }

  let fulltextAt = null;
  function openFulltext(nodeId, cutIndex) {
    fulltextAt = { nodeId, cutIndex };
    const node = nodeById(data, nodeId), cut = node.cuts[cutIndex];
    $('fulltext-title').textContent = `${node.name} · CUT ${cutNumber(node, cutIndex)}`;
    const boxes = cut.boxes.map((box, i) => {
      const section = el('section', 'textbox');
      section.append(el('h3', '', `文本框 ${box.sourceIndex == null ? i + 1 : box.sourceIndex + 1}${box.option ? ' · 选项' : ''}${box.speaker ? ' · ' + box.speaker : ''}`));
      if (box.layouts) for (const layout of box.layouts) {
        const heading = el('div', 'layout-heading');
        heading.append(el('h4', 'muted', `槽位 ${layout.layoutIndex} · 原文`));
        let target = null;
        try { target = targetForLayout(data, node.id, cutIndex, box.rawIndex, layout.rawIndex); } catch { target = null; }
        if (target) heading.append(button(actions.repairStatus?.(target) ? '编辑修复 · 有草稿' : '编辑修复', () => actions.openRepair(target), 'repair-entry-button'));
        else heading.append(el('span', 'muted', '无原始索引，只读'));
        section.append(heading, el('pre', '', layout.rawText || '（空文本）'));
        if (target) {
          const effective = actions.previewRawText?.(target.reference.pointer);
          if (typeof effective === 'string' && effective !== layout.rawText) section.append(el('h4', 'muted', '当前 goto 预览文本'), el('pre', 'goto-draft-preview', effective));
        }
      }
      else section.append(el('p', '', box.text));
      return section;
    });
    if (cut.choices.length) { const choices = el('section', 'textbox'); choices.append(el('h3', '', '选项文字')); cut.choices.forEach((text) => choices.append(el('p', 'full-choice', text))); boxes.push(choices); }
    $('fulltext-content').replaceChildren(...boxes);
    if (!$('fulltext').open) $('fulltext').showModal();
  }
  $('close-fulltext').onclick = () => $('fulltext').close();
  $('close-cuts').onclick = () => { state.main = null; actions.render(); };
  $('cut-slider').oninput = (event) => actions.setCut(Number(event.target.value));
  $('cut-number').onchange = (event) => {
    const node = nodeById(data, state.main), value = Number(event.target.value);
    const index = node?.cuts.findIndex((cut, i) => cutNumber(node, i) === value) ?? -1;
    if (index >= 0) actions.setCut(index);
    else { actions.status('该 CUT 源序号未收录'); render(); }
  };
  for (const direction of ['before', 'after']) $(`${direction}-range`).onchange = (event) => { state[direction] = Number(event.target.value); render(); };
  $('save-ranges').onclick = () => { state.defaults = { before: state.before, after: state.after }; const saved = actions.save(false); render(); if (saved) actions.status('已保存前后默认范围'); };
  $('cut-panel').addEventListener('wheel', (event) => {
    event.stopPropagation();
    if (event.target.closest('.context-stack, select, input')) return;
    event.preventDefault(); actions.setCut(state.cut + Math.sign(event.deltaY));
  }, { passive: false, signal });
  // 草稿状态变化后重绘已打开的全文，避免「编辑修复」按钮停留在旧标签。
  function refreshFulltext() { if (fulltextAt && $('fulltext').open) openFulltext(fulltextAt.nodeId, fulltextAt.cutIndex); }
  return { render, position, refreshFulltext, openFulltext };
}
