import { $, el, button } from './dom.js';
import { loadAnnotations, saveAnnotations, validateAnnotations, exportAnnotations, handoffText, routeLabels, statusLabels } from '../annotations.mjs';
import { createAnnotationDrafts } from '../annotation-drafts.mjs';
import { mountAnnotationFields } from './annotation-fields.js';
import './annotations.css';

function download(name, value, type) {
  const url = URL.createObjectURL(new Blob([value], { type })), anchor = el('a');
  anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function mountAnnotations({ data, state, actions, signal, panel, draft }) {
  const loaded = loadAnnotations(data, draft?.annotations), drafts = createAnnotationDrafts(data, loaded.value), dialog = el('dialog', 'author-dialog');
  dialog.id = 'author-dialog'; dialog.setAttribute('aria-labelledby', 'author-title');
  dialog.innerHTML = `<header><div><h2 id="author-title">作者标注</h2><p>说明作者意图与进度，不改变游戏流程。</p></div><div class="author-window-actions"><button id="author-minimize" type="button" aria-label="最小化作者标注">最小化</button><button id="author-close" type="button" aria-label="关闭作者标注">×</button></div></header>
    <nav class="author-tabs" aria-label="作者标注页面"><button id="author-tab-scope" type="button" aria-pressed="true">审查范围</button><button id="author-tab-nodes" type="button" aria-pressed="false">标注内容</button><button id="author-tab-list" type="button" aria-pressed="false">已有标注 <span id="author-count"></span></button></nav>
    <div class="author-scroll">
      <section id="author-page-scope"><div class="author-scope">
        <label>本轮审查范围<textarea id="author-summary" maxlength="20000" rows="4"></textarea></label>
        <label>作者预期<textarea id="author-expected" maxlength="20000" rows="4"></textarea></label>
        <label>未完成内容<textarea id="author-unfinished" maxlength="20000" rows="4"></textarea></label>
      </div></section>
      <section id="author-page-nodes" hidden><h3 id="author-selection"></h3><p class="muted">仅应用勾选的字段；清空已勾选字段可移除标注。切换节点时保留草稿。</p><div id="author-fields" class="author-fields"></div>
        <section class="author-group-notes"><h3>组共用备注</h3><p class="muted">同组 Part 共用此说明，各自的独立备注保留。</p><label>选择分组<select id="author-group-note-select"></select></label><label>组备注<textarea id="author-group-note" maxlength="20000" rows="4" disabled></textarea></label></section>
      </section>
      <section id="author-page-list" hidden><label>按分组筛选<select id="author-group-filter"></select></label><div id="author-list"></div></section>
    </div>
    <footer><button id="author-save" type="button">保存全部编辑</button><button id="author-export" type="button">导出标注 JSON</button><button id="author-handoff" type="button">下载 AI 交接说明</button><label class="author-import">导入标注 JSON<input id="author-import" type="file" accept=".json,application/json"></label><p id="author-message" role="status" aria-live="polite"></p></footer>`;
  if (panel) { dialog.classList.add('embedded'); dialog.open = true; panel.page('annotations').append(dialog); }
  else document.body.append(dialog);
  const opener = panel ? null : button('作者标注', () => open());
  if (opener) { opener.id = 'author-open'; document.querySelector('.top-actions').append(opener); }
  const minimized = panel ? null : button('', () => restore(), 'author-minimized'); if (minimized) minimized.id = 'author-restore'; if (minimized) minimized.hidden = true;
  if (minimized) {
  minimized.title = '恢复作者标注'; minimized.setAttribute('aria-label', '恢复作者标注');
  minimized.innerHTML = '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M13 4H5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h13a2 2 0 0 0 2-2v-8M15 4l3-3 4 4-3 3M8 15l1-5 9-9 4 4-9 9-5 1Z"/></svg>';
  const zoom = document.querySelector('.canvas-bottom .zoom'); zoom.before(minimized);
  }
  let selected = [], importing = 0, tab = 'scope';
  const fields = mountAnnotationFields({ signal, changed: (field, value, checked) => {
    drafts.editNodes(selected, field, value, checked); markDirty(); refreshChoices();
  } });
  function announce(message) { $('author-message').textContent = message; }
  function markDirty() { announce('有未保存编辑；切换页面或最小化会保留，保存项目也会包含这些编辑。'); actions.draftChanged?.(); }
  function attempt(fn) { try { return fn(); } catch (error) { announce(error.message); return null; } }
  function assign(value) { state.annotations = value; state.authorNotes = new Map(value.nodes.map(node => [node.uid, node])); }
  assign(loaded.value);
  function flushDrafts() {
    const value = drafts.collect();
    assign(value); drafts.reset(value);
    // 保存是异步的;先按乐观值刷新界面,失败再改口提示。
    saveAnnotations(data, value).then(warning => {
      if (warning) announce(warning); else loaded.warning = '';
    });
    actions.render(); renderList(); fillSelection(); announce('全部编辑已保存');
    return value;
  }
  function replace(input) {
    const value = validateAnnotations(input, data);
    assign(value); drafts.reset(value); fillScope(); fillSelection(); refreshChoices(); renderList(); actions.render();
  }
  function fillScope() { for (const [field, value] of Object.entries(drafts.collect().scope)) $(`author-${field}`).value = value; }
  function fillSelection() {
    const document = drafts.collect(), notes = new Map(document.nodes.map(node => [node.uid, node]));
    fields.fill(selected, notes, drafts.nodeEdits(selected));
    $('author-selection').textContent = selected.length ? `标注 ${selected.length} 个选中节点${selected.length === 1 ? '：' + data.nodes.find(node => node.id === selected[0]).name : ''}` : '先在节点池选择一个或多个节点';
    refreshChoices();
  }
  function refreshChoices() {
    const document = drafts.collect(); fields.choices(document);
    const picker = $('author-group-note-select'), previous = picker.value;
    picker.replaceChildren(el('option', '', '选择要编辑备注的分组')); picker.firstChild.value = '';
    for (const group of document.groups) { const option = el('option', '', group.name); option.value = group.name; picker.append(option); }
    picker.value = document.groups.some(group => group.name === previous) ? previous : ''; fillGroupNote(document);
  }
  function fillGroupNote(document = drafts.collect()) {
    const group = document.groups.find(group => group.name === $('author-group-note-select').value);
    $('author-group-note').disabled = !group; $('author-group-note').value = group?.note ?? '';
  }
  function renderList() {
    const document = drafts.collect(), filter = $('author-group-filter'), previous = filter.options.length ? filter.value : '*';
    $('author-count').textContent = `(${document.nodes.length})`;
    filter.replaceChildren(el('option', '', '全部分组')); filter.firstChild.value = '*';
    for (const group of ['', ...document.groups.map(group => group.name)]) { const option = el('option', '', group || '未分组'); option.value = group; filter.append(option); }
    filter.value = [...filter.options].some(option => option.value === previous) ? previous : '*'; renderEntries(document);
  }
  function renderEntries(document = drafts.collect()) {
    const filter = $('author-group-filter').value;
    const entries = document.nodes.filter(node => filter === '*' || (filter ? node.groups.includes(filter) : !node.groups.length));
    $('author-list').replaceChildren(...entries.map(note => {
      const node = data.nodes.find(node => node.id === note.uid), row = el('details', 'author-entry');
      const summary = el('summary', 'author-entry-head');
      summary.append(el('strong', '', node.name),
        el('span', 'author-entry-tags', [note.groups.join('、'), routeLabels[note.route], note.stage, statusLabels[note.status]].filter(Boolean).join(' · ')),
        button('定位', () => { minimize(); actions.selectNode(note.uid); }, 'author-entry-locate'));
      const body = el('div', 'author-entry-body');
      for (const group of document.groups.filter(group => note.groups.includes(group.name) && group.note)) body.append(el('p', '', `组备注 · ${group.name}:${group.note}`));
      if (note.note) body.append(el('p', '', `Part 备注:${note.note}`));
      if (!body.children.length) body.append(el('p', 'muted', '无备注'));
      row.append(summary, body); return row;
    }));
  }
  function switchTab(next) {
    tab = next;
    for (const name of ['scope', 'nodes', 'list']) { $(`author-page-${name}`).hidden = name !== tab; $(`author-tab-${name}`).setAttribute('aria-pressed', String(name === tab)); }
    if (tab === 'list') renderList();
  }
  function restore() { if (panel) { panel.open('annotations'); return; } minimized.hidden = true; if (!dialog.open) dialog.showModal(); }
  function minimize() { if (panel) { panel.close(); return; } dialog.close(); minimized.hidden = false; }
  // 选区由 view 的 selectionChanged 驱动;选区变化时只刷新字段,不重建草稿。
  function syncSelection() { selected = [...state.selected]; fillSelection(); }
  function open(id) {
    if (id) actions.selectNode(id);
    selected = [...state.selected]; fillSelection(); if (id) switchTab('nodes'); restore();
  }
  fillScope(); fillSelection(); renderList(); announce(loaded.warning || '保存全部编辑后记录到数据目录；导出文件可备份或交给 AI。');
  for (const field of ['summary', 'expected', 'unfinished']) $(`author-${field}`).addEventListener('input', event => { drafts.editScope(field, event.target.value); markDirty(); }, { signal });
  for (const name of ['scope', 'nodes', 'list']) $(`author-tab-${name}`).onclick = () => attempt(() => switchTab(name));
  $('author-minimize').onclick = minimize;
  $('author-close').onclick = () => { if (panel) panel.close(); else { dialog.close(); minimized.hidden = true; } };
  $('author-save').onclick = () => attempt(flushDrafts);
  $('author-group-filter').onchange = () => attempt(() => renderEntries());
  $('author-group-note-select').onchange = () => attempt(() => fillGroupNote());
  $('author-group-note').oninput = event => { drafts.editGroup($('author-group-note-select').value, event.target.value); markDirty(); };
  $('author-export').onclick = () => attempt(() => { flushDrafts(); download('author-annotations.json', JSON.stringify(exportAnnotations(data, state), null, 2), 'application/json'); });
  $('author-handoff').onclick = () => attempt(() => { flushDrafts(); download('author-handoff.md', handoffText(data, state.annotations), 'text/markdown;charset=utf-8'); });
  $('author-import').onchange = async event => {
    const file = event.target.files[0], token = ++importing; if (!file) return;
    try {
      const input = JSON.parse(await file.text());
      if (signal.aborted || token !== importing) return;
      const value = validateAnnotations(input, data);
      if (drafts.dirty() && !window.confirm('导入会替换当前未保存的标注编辑，是否继续？')) return;
      if (value.layout) actions.importLayout(value.layout);
      const { layout, ...notes } = value; replace(notes);
      markDirty();
      saveAnnotations(data, notes).then(warning => announce(warning || '标注已导入并保存'));
    } catch (error) { if (!signal.aborted && token === importing) announce(`导入失败：${error.message}`); }
    finally { if (!signal.aborted && token === importing) event.target.value = ''; }
  };
  signal.addEventListener('abort', () => { importing++; dialog.close(); dialog.remove(); opener?.remove(); minimized?.remove(); }, { once: true });
  return { open, flushDrafts, replace, selection: syncSelection };
}
