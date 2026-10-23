import { $, el, button } from './dom.js';
import { currentTarget, targetCandidates, connectionOptions } from './connection-edit.mjs';

export function mountConnectionEditor({ data, state, actions, signal }) {
  const dialog = el('dialog', 'connection-editor'); dialog.id = 'connection-editor'; dialog.setAttribute('aria-labelledby', 'connection-editor-title');
  dialog.innerHTML = `<header><div><h2 id="connection-editor-title">修改普通连接</h2><p>只替换已有出口的目标；条件和槽参数保持原值。</p></div><button id="connection-editor-close" type="button" aria-label="关闭连接编辑器">×</button></header>
    <div class="connection-editor-body"><p id="connection-editor-source" class="connection-editor-source"></p>
      <label>出口<select id="connection-editor-slot"></select></label>
      <label>目标搜索<input id="connection-editor-search" type="search" role="combobox" aria-autocomplete="list" aria-controls="connection-editor-results" aria-expanded="false" placeholder="输入节点名或 UID" autocomplete="off"></label>
      <div id="connection-editor-results" role="listbox" class="connection-editor-results"></div>
    </div>
    <footer><p id="connection-editor-message" role="status" aria-live="polite"></p><button id="connection-editor-cancel" type="button">取消</button><button id="connection-editor-apply" type="button" disabled>应用目标</button></footer>`;
  document.body.append(dialog);
  let sourceUid = '', slot = 0, targetUid = '', candidates = [], active = 0, opener = null;

  const sourceNode = () => data.nodes.find(node => node.id === sourceUid);
  const outputs = () => connectionOptions(sourceNode());
  function output() { return outputs().find(item => item.slot === slot); }
  function close() { dialog.close(); opener = null; }
  function announce(text) { $('connection-editor-message').textContent = text; }
  function renderSlots() {
    const select = $('connection-editor-slot');
    select.replaceChildren(...outputs().map(item => { const option = el('option', '', `出口 ${item.slot + 1} · 原目标 ${data.nodes.find(node => node.id === item.targetUid)?.name || item.targetUid}`); option.value = String(item.slot); return option; }));
    select.value = String(slot);
  }
  function renderCandidates() {
    const popup = $('connection-editor-results'); popup.replaceChildren();
    $('connection-editor-search').setAttribute('aria-expanded', 'true');
    if (!candidates.length) { popup.append(el('p', 'connection-editor-empty', '没有可用的目标节点')); $('connection-editor-apply').disabled = true; return; }
    candidates.forEach((node, index) => {
      const item = button('', () => { targetUid = node.id; active = index; renderCandidates(); }, 'connection-editor-result');
      item.id = `connection-editor-option-${index}`; item.dataset.node = node.id; item.setAttribute('role', 'option'); item.setAttribute('aria-selected', String(node.id === targetUid || index === active));
      item.append(el('strong', '', node.name), el('span', 'muted', `${node.system ? node.kind : `${node.cuts?.length || 0} 个 CUT`} · UID ${node.id}`));
      popup.append(item);
    });
    if (targetUid) $('connection-editor-apply').disabled = false;
  }
  function filter() { candidates = targetCandidates(data.nodes, sourceUid, $('connection-editor-search').value); active = 0; renderCandidates(); }
  function open({ sourceUid: source, slot: requestedSlot }) {
    const node = data.nodes.find(item => item.id === source), available = connectionOptions(node);
    if (!node || !available.length) { actions.status?.(`${node?.name || '该节点'}没有可编辑的已有连接`); return false; }
    sourceUid = source; slot = available.some(item => item.slot === requestedSlot) ? requestedSlot : available[0].slot;
    targetUid = currentTarget(state.nodePatch, sourceUid, slot, output().targetUid);
    $('connection-editor-source').textContent = `${node.name} · UID ${node.id}`;
    $('connection-editor-search').value = ''; renderSlots(); filter(); announce('选择目标后应用；取消不会修改补丁。');
    if (!dialog.open) dialog.showModal();
    $('connection-editor-search').focus();
    return true;
  }

  $('connection-editor-slot').onchange = () => { slot = Number($('connection-editor-slot').value); targetUid = currentTarget(state.nodePatch, sourceUid, slot, output().targetUid); filter(); };
  $('connection-editor-search').addEventListener('input', filter, { signal });
  $('connection-editor-search').addEventListener('keydown', event => {
    if (event.isComposing) return;
    if (event.key === 'Escape') { event.preventDefault(); close(); return; }
    if (event.key === 'Enter') { if (candidates[active]) { event.preventDefault(); targetUid = candidates[active].id; renderCandidates(); $('connection-editor-apply').click(); } return; }
    if (!['ArrowDown', 'ArrowUp'].includes(event.key) || !candidates.length) return;
    event.preventDefault(); active = (active + (event.key === 'ArrowDown' ? 1 : -1) + candidates.length) % candidates.length; renderCandidates();
    $(`connection-editor-option-${active}`)?.scrollIntoView?.({ block: 'nearest' });
  }, { signal });
  $('connection-editor-close').onclick = close; $('connection-editor-cancel').onclick = close;
  $('connection-editor-apply').onclick = () => {
    if (!targetUid) return;
    const result = actions.setConnectionTarget(sourceUid, slot, targetUid);
    if (result?.ok === false) announce(result.error); else close();
  };
  dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }, { signal });
  signal.addEventListener('abort', () => { dialog.close(); dialog.remove(); }, { once: true });
  return { open, close };
}
