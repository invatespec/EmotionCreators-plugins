import { $, el, button } from './dom.js';
import { loadRepairs, saveRepairs, validateRepairs, setRepair, removeRepair, resolveRepairTarget } from '../repairs.mjs';
import './repairs.css';

function download(name, value, type) {
  const url = URL.createObjectURL(new Blob([value], { type })), anchor = el('a');
  anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

const normalizeEol = value => value.replace(/\r\n|\r/gu, '\n');

// 中间变化按整段比较并限制展示行数，避免长脚本生成大量 DOM。
function diffLines(before, after) {
  const a = normalizeEol(before).split('\n'), b = normalizeEol(after).split('\n');
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tailA = a.length, tailB = b.length;
  while (tailA > head && tailB > head && a[tailA - 1] === b[tailB - 1]) { tailA--; tailB--; }
  const rows = [];
  if (head > 3) rows.push({ kind: 'skip', text: `… 前面 ${head - 3} 行未变化` });
  for (let i = Math.max(0, head - 3); i < head; i++) rows.push({ kind: 'same', text: a[i] });
  for (const [kind, lines, end] of [['del', a, tailA], ['add', b, tailB]]) {
    for (let i = head; i < Math.min(end, head + 140); i++) rows.push({ kind, text: lines[i] });
    if (end - head > 140) rows.push({ kind: 'skip', text: `… 省略 ${end - head - 140} 行，完整文本见上方` });
  }
  const tail = a.length - tailA;
  for (let i = tailA; i < Math.min(a.length, tailA + 3); i++) rows.push({ kind: 'same', text: a[i] });
  if (tail > 3) rows.push({ kind: 'skip', text: `… 后面 ${tail - 3} 行未变化` });
  return rows;
}

export function mountRepairs({ data, actions, signal, panel, draft }) {  const loaded = loadRepairs(data, draft?.repairs);
  let plan = loaded.value, editing = null, timer = 0, importing = 0, startupWarning = loaded.warning;
  let storageWarning = '', draftError = '', dirty = false;

  const dialog = el('dialog', 'repair-dialog'); dialog.id = 'repair-dialog'; dialog.setAttribute('aria-labelledby', 'repair-title');
  dialog.innerHTML = `<header><div><h2 id="repair-title">修复清单</h2><p>草稿，尚未应用到游戏；原始证据与问题标记不受影响。</p></div><button id="repair-close" type="button" aria-label="关闭修复清单">×</button></header>
    <div class="repair-scroll"><p id="repair-count" class="repair-count"></p><div id="repair-list"></div></div>
    <footer><button id="repair-export" type="button">导出修复 JSON</button><label class="repair-import">导入修复 JSON<input id="repair-import" type="file" accept=".json,application/json"></label><p id="repair-message" role="status" aria-live="polite"></p></footer>`;
  if (panel) { dialog.classList.add('embedded'); dialog.open = true; panel.page('repairs').append(dialog); }
  else document.body.append(dialog);

  const editor = el('dialog', 'repair-editor'); editor.id = 'repair-editor'; editor.setAttribute('aria-labelledby', 'repair-editor-title');
  // 编辑页无底栏:状态消息当副标题随操作更新,工具行收在右侧。
  editor.innerHTML = `<header><div><h2 id="repair-editor-title">编辑修复</h2><p id="repair-editor-message" role="status" aria-live="polite"></p></div>
    <div class="repair-editor-tools"><button id="repair-restore" type="button">恢复原文</button><button id="repair-save" type="button">保存</button><button id="repair-back" type="button">返回</button></div></header>
    <div class="repair-scroll"><p id="repair-target-label" class="repair-target"></p>
      <div class="repair-columns"><label>原文（只读）<pre id="repair-original"></pre></label><label>新文<textarea id="repair-new" spellcheck="false"></textarea></label></div>
      <section class="repair-diff-section"><h3>前后差异</h3><div id="repair-diff" class="repair-diff"></div></section>
    </div>`;
  if (panel) { editor.classList.add('embedded'); editor.hidden = true; panel.page('repairs').append(editor); }
  else document.body.append(editor);

  const opener = panel ? null : button('修复清单', () => open(), 'repair-open');
  if (opener) { opener.id = 'repair-open'; document.querySelector('.top-actions').append(opener); }

  const message = text => { $('repair-message').textContent = text; };
  const editorMessage = text => { $('repair-editor-message').textContent = text; };
  const changeFor = pointer => plan.changes.find(change => change.target.reference.pointer === pointer);
  // 保存是异步的:同步路径只标记脏;失败提示在下一次刷新时带出。
  function persist() {
    clearTimeout(timer); timer = 0;
    if (!dirty) return;
    saveRepairs(data, plan).then(warning => {
      storageWarning = warning || '';
      if (!warning) dirty = false;
      if (warning) actions.status(warning);
    });
  }
  function schedulePersist() {
    actions.draftChanged?.();
    clearTimeout(timer);
    timer = setTimeout(() => {
      persist();
      if (storageWarning && !draftError) (editing ? editorMessage : message)(storageWarning);
    }, 400);
  }

  function updateSummary() {
    const count = plan.changes.length;
    if (opener) opener.textContent = count ? `修复清单 · ${count}` : '修复清单';
    $('repair-count').textContent = count ? `${count} 项修改待应用` : '尚无修复草稿。打开 CUT 全文，在具体布局上点击「编辑修复」。';
  }

  // 清单项只作展示与定位,编辑一律进入编辑页。
  function renderList() {
    const list = $('repair-list');
    list.replaceChildren(...plan.changes.map((change, index) => {
      let info = null; try { info = resolveRepairTarget(data, change.target); } catch { info = null; }
      const row = el('div', 'repair-entry'); row.dataset.pointer = change.target.reference.pointer;
      const head = el('div', 'repair-entry-head');
      head.append(el('strong', 'repair-entry-name', info?.name || `修复 ${index + 1}`));
      const group = el('div', 'repair-entry-actions');
      if (info) group.append(button('定位', () => locate(info), 'repair-locate'));
      group.append(button('编辑', () => open(change.target)), button('移除', () => remove(change.target)));
      head.append(group); row.append(head);
      return row;
    }));
  }

  function refresh() { updateSummary(); if (dialog.open) renderList(); }

  function locate(info) {
    if (!stopEditing()) return;
    if (editor.open) editor.close(); if (panel) panel.close(); else dialog.close();
    actions.selectNode(info.nodeId, info.cutIndex);
    actions.status(storageWarning || `已定位 ${info.label}`);
  }

  function remove(target) {
    try { plan = removeRepair(plan, data, target); dirty = true; actions.repairChanged?.(plan); }
    catch (error) { message(`移除失败：${error.message}`); return; }
    persist(); refresh();
    if (editing?.pointer === target.reference.pointer) closeEditor();
    actions.refreshFulltext?.();
    message(storageWarning || '已移除该项修复草稿');
  }

  function renderDiff(value) {
    const container = $('repair-diff');
    if (!editing) { container.replaceChildren(); return; }
    if (normalizeEol(value) === normalizeEol(editing.before)) {
      container.replaceChildren(el('p', 'muted', '与原文一致，保存后不生成修复项（视为恢复原文）。')); return;
    }
    container.replaceChildren(...diffLines(editing.before, value).map(row => el('div', `diff-line ${row.kind}`, row.text === '' ? ' ' : row.text)));
  }

  function applyDraft(value, force = false) {
    if (!editing) return;
    if (!force && value === editing.inputValue) return;
    editing.inputValue = value;
    try {
      plan = setRepair(plan, data, editing.target, value);
      draftError = ''; dirty = true;
      actions.repairChanged?.(plan);
      editorMessage(storageWarning || '草稿已记录，尚未应用到游戏。');
      schedulePersist();
    } catch (error) {
      draftError = `当前文本无法保存：${error.message}`;
      editorMessage(`${draftError} goto 预览仍对应上一份有效草稿。`);
    }
    renderDiff(value); refresh();
  }

  function captureDraft() {
    if (!editing) return;
    applyDraft($('repair-new').value);
    if (draftError) { editorMessage(draftError); throw new Error(draftError); }
  }

  function openEditor(target) {
    if (!stopEditing()) return;
    let info;
    try { info = resolveRepairTarget(data, target); }
    catch (error) { message(`无法编辑该布局：${error.message}。可只读查看。`); open(); return; }
    editing = { target, pointer: target.reference.pointer, before: info.before, label: info.label, nodeId: info.nodeId, cutIndex: info.cutIndex };
    const change = changeFor(editing.pointer);
    $('repair-target-label').textContent = info.label;
    $('repair-original').textContent = info.before || '（空文本）';
    $('repair-new').value = change ? change.after : info.before;
    editing.inputValue = $('repair-new').value;
    renderDiff($('repair-new').value);
    editorMessage(storageWarning || (change ? '正在编辑已有草稿。' : '输入新文本；与原文一致时会自动移除该项。'));
    if (panel) { dialog.hidden = true; editor.hidden = false; editor.open = true; panel.open('repairs'); }
    else if (!editor.open) editor.showModal();
    $('repair-new').focus();
  }

  function stopEditing() {
    try { captureDraft(); } catch { return false; }
    if (editing) { persist(); editing = null; }
    draftError = ''; renderDiff(''); refresh(); actions.refreshFulltext?.();
    return true;
  }
  function closeEditor() {
    if (!stopEditing()) return;
    if (panel) { editor.open = false; editor.hidden = true; dialog.hidden = false; }
    else if (editor.open) editor.close();
  }

  function open(target) {
    if (target) { openEditor(target); return; }
    if (panel) { panel.open('repairs'); editor.hidden = true; dialog.hidden = false; dialog.open = true; }
    else if (!dialog.open) dialog.showModal();
    refresh();
    const warning = startupWarning; startupWarning = '';
    message(storageWarning || warning || (plan.changes.length ? '草稿只保存在数据目录，导出 JSON 可备份。' : '打开 CUT 全文，在具体布局上点击「编辑修复」。'));
  }

  function flush() { captureDraft(); persist(); return plan; }
  function replace(value) {
    const safe = validateRepairs(value, data);
    editing = null; draftError = '';
    if (panel) { editor.open = false; editor.hidden = true; dialog.hidden = false; }
    else if (editor.open) editor.close();
    plan = safe; dirty = true; persist(); refresh();
    actions.repairChanged?.(plan);
    actions.refreshFulltext?.();
    message(storageWarning || '修复清单已导入');
    return plan;
  }
  function statusOf(target) { return target && changeFor(target.reference.pointer) ? 'changed' : ''; }

  // 面板内对话框常驻打开,首次显示就要出清单;独立对话框由 open() 触发渲染。
  updateSummary();
  if (dialog.open) renderList();
  $('repair-close').onclick = () => { if (panel) panel.close(); else dialog.close(); actions.refreshFulltext?.(); };
  $('repair-back').onclick = () => closeEditor();
  // 保存只落草稿不关闭,由玩家自行返回。
  $('repair-save').onclick = () => {
    if (!stopEditing()) return;
    editorMessage(storageWarning || '修复草稿已保存');
  };
  $('repair-restore').onclick = () => { if (!editing) return; $('repair-new').value = editing.before; applyDraft($('repair-new').value, true); };
  $('repair-new').addEventListener('input', event => applyDraft(event.target.value), { signal });
  editor.addEventListener('cancel', event => { if (!stopEditing()) event.preventDefault(); }, { signal });
  $('repair-export').onclick = () => {
    try { flush(); } catch (error) { message(error.message); return; }
    download('ec-scene-repair-plan.json', JSON.stringify(plan, null, 2), 'application/json');
    message(storageWarning ? `已导出当前修复清单。${storageWarning}` : '已导出当前修复清单');
  };
  $('repair-import').onchange = async event => {
    const file = event.target.files[0], token = ++importing; if (!file) return;
    try {
      const input = JSON.parse(await file.text());
      if (signal.aborted || token !== importing) return;
      replace(input);
      actions.draftChanged?.();
    } catch (error) { if (!signal.aborted && token === importing) message(`导入失败：${error.message}`); }
    finally { if (!signal.aborted && token === importing) event.target.value = ''; }
  };
  window.addEventListener('pagehide', () => persist(), { signal });
  signal.addEventListener('abort', () => {
    clearTimeout(timer); importing++;
    persist(); editing = null;
    for (const id of ['repair-close', 'repair-back', 'repair-save', 'repair-restore', 'repair-export']) $(id).onclick = null;
    $('repair-import').onchange = null;
    dialog.close(); dialog.remove(); editor.close(); editor.remove(); opener?.remove();
  }, { once: true });

  return { open, openEditor, statusOf, flush, replace };
}
