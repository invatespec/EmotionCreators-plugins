import { $, el, button } from './dom.js';
import { createElement, RotateCcw, LocateFixed } from 'lucide';
import { assertNodePatchFileSize, emptyNodePatch, usableSlots, validateNodePatch, acceptsLink } from '../node-patch.mjs';
import { currentTarget } from './connection-edit.mjs';

function download(value, name = 'ec-scene-node-patch.json') {
  const json = JSON.stringify(value, null, 2);
  assertNodePatchFileSize(new TextEncoder().encode(json).byteLength);
  const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
  const anchor = el('a'); anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function mountNodePatches({ data, state, actions, signal, panel }) {
  const dialog = el('dialog', 'node-patch-dialog'); dialog.id = 'node-patch-dialog';
  dialog.setAttribute('aria-labelledby', 'node-patch-title');
  dialog.innerHTML = `<header><div><h2 id="node-patch-title">节点布局与连接补丁</h2><p>可修改已有连接目标、给可用空槽指定目标，并调整节点原生位置。</p></div><button id="node-patch-close" type="button" aria-label="关闭节点补丁">×</button></header>
    <div class="node-patch-scroll"><p id="node-patch-source" role="status"></p>
      <section id="node-patch-review" class="node-patch-review" hidden><h3>历史普通自连待复核</h3><p id="node-patch-review-summary"></p><div id="node-patch-review-list"></div><div class="node-patch-review-actions"><button id="node-patch-review-download" type="button">下载原始旧补丁</button><button id="node-patch-review-discard" type="button">丢弃待复核记录</button></div></section>
      <section id="ec-layout-controls"><h3>画布</h3><label>画布大小<select id="ec-canvas-scale" aria-label="画布大小"></select></label></section>
      <section><h3>出口连接 <span id="node-patch-connection-count" class="muted"></span></h3><div class="node-patch-filters"><select id="node-patch-filter" aria-label="连接范围"><option value="selection">当前选区</option><option value="all">全部连接</option><option value="modified">仅已修改</option></select><input id="node-patch-search" type="search" placeholder="搜索名称或 UID" aria-label="搜索连接"></div><div id="node-patch-connections"></div></section>
      <section><h3>位置修改</h3><div id="node-patch-positions"></div></section><section><h3>goto 预览诊断</h3><div id="goto-diagnostics"></div></section>
    </div><footer><p id="node-patch-message" role="status" aria-live="polite"></p><div class="node-patch-resets"><button id="node-patch-restore-connections" type="button">恢复原始连接</button><button id="node-patch-restore-layout" type="button">恢复游戏布局</button><button id="node-patch-clear" type="button">清除全部</button></div><div class="node-patch-files"><label class="node-patch-import">导入补丁<input id="node-patch-import" type="file" accept=".json,application/json"></label><button id="node-patch-export" type="button">导出补丁</button></div></footer>`;
  if (panel) { dialog.classList.add('embedded'); dialog.open = true; panel.page('node-patches').append(dialog); }
  else document.body.append(dialog);
  // 导入预览用独立模态框,避免与面板内容混在一起。
  const importDialog = el('dialog', 'node-patch-import-dialog'); importDialog.id = 'node-patch-import-dialog';
  importDialog.setAttribute('aria-labelledby', 'node-patch-import-title');
  importDialog.innerHTML = `<header><div><h2 id="node-patch-import-title">导入补丁预览</h2><p>确认前不会改变当前修改。</p></div><button id="node-patch-import-close" type="button" aria-label="关闭导入预览">×</button></header>
    <div class="node-patch-import-body"><p id="node-patch-import-summary"></p><details open><summary>替换明细</summary><div id="node-patch-import-details"></div></details></div>
    <footer><p id="node-patch-import-message" role="status" aria-live="polite"></p><button id="node-patch-import-cancel" type="button">取消</button><button id="node-patch-import-apply" type="button">应用导入</button></footer>`;
  document.body.append(importDialog);
  const opener = panel ? null : button('节点补丁', () => open(), 'node-patch-open'); if (opener) { opener.id = 'node-patch-open'; document.querySelector('.canvas-actions').append(opener); }
  const message = value => { $('node-patch-message').textContent = value; };
  // EC 节点画布的原始尺寸：游戏未放大时的 rtfGrid 尺寸，跨资料一致(实测多份资料包均为
  // 3384×1968，导出尺寸 = 原始 × 游戏内放大倍数)。资料包只带放大后的尺寸，故以该常量作基准。
  const NATIVE_CANVAS = { width: 3384, height: 1968 };
  const round = value => Math.round(value * 100) / 100;
  const currentCanvas = () => state.ecCanvas || state.nodePatch.layout?.canvas || data.geometry?.value?.canvas || null;
  const canvas = () => {
    const value = $('ec-canvas-scale').value;
    if (value === 'custom') return currentCanvas() || { width: 0, height: 0 };
    const scale = Number(value) || 1;
    return { width: NATIVE_CANVAS.width * scale, height: NATIVE_CANVAS.height * scale };
  };
  function fillCanvasSelect() {
    const select = $('ec-canvas-scale'), current = currentCanvas();
    const entries = [];
    for (let scale = 1; scale <= 10; scale++) entries.push({ value: scale, width: NATIVE_CANVAS.width * scale, height: NATIVE_CANVAS.height * scale });
    const label = entry => `${entry.value === 1 ? '原始' : `×${entry.value}`} · ${round(entry.width)} × ${round(entry.height)}`;
    select.replaceChildren(...entries.map(entry => new Option(label(entry), String(entry.value))));
    if (!current) { select.value = '1'; return; }
    // 按四舍五入比较，避免倍数除法带来的浮点尾差把同一档位拆成两行。
    const match = entries.find(entry => round(entry.width) === round(current.width) && round(entry.height) === round(current.height));
    if (match) select.value = String(match.value);
    // 导入画布不是整数倍时(游戏内放大倍数可任意)，单独一行显示资料包里的实际数值。
    else { select.add(new Option(`实际值 · ${round(current.width)} × ${round(current.height)}`, 'custom')); select.value = 'custom'; }
  }
  const names = new Map(data.nodes.map(node => [node.id, node.name]));
  const nameOf = uid => names.get(uid) || uid;
  const connections = $('node-patch-connections');
  let selectionKey = '', connectionKey = '', candidate = null, exported = null, exportedAt = '';
  let expandedTarget = null;
  function collapseTargets() {
    if (!expandedTarget) return;
    const select = expandedTarget, selected = select.selectedOptions[0];
    expandedTarget = null;
    select.replaceChildren(...(selected ? [new Option(selected.textContent, selected.value)] : []));
  }
  function expandTargets(event) {
    const select = event.target.closest('select');
    if (!select || !connections.contains(select) || select === expandedTarget) return;
    collapseTargets();
    const row = select.closest('.node-patch-connection'), source = row.dataset.source, value = select.value;
    // 每次只展开正在编辑的一行，避免连接数 × 节点数的 option 常驻。
    const options = data.nodes.filter(node => node.id !== source && acceptsLink(node)).map(node => new Option(node.name, node.id));
    // 有连接的槽也能改回空槽，占位项必须在列表里，否则收起后 selectedIndex 变 -1，下拉会显示成空。
    options.push(new Option('（空槽）', ''));
    select.replaceChildren(...options);
    select.value = value; expandedTarget = select;
  }
  function changeTarget(event) {
    const select = event.target.closest('select');
    if (!select || !connections.contains(select)) return;
    const { source, slot, original } = select.closest('.node-patch-connection').dataset;
    // 选中占位项：原本为空的槽撤回该行草稿，原本有连接的槽生成断开项。
    if (!select.value) {
      if (original) {
        const result = actions.setConnectionTarget(source, Number(slot), '');
        if (result?.ok === false) {
          message(`修改失败：${result.error}`);
          select.value = currentTarget(state.nodePatch, source, Number(slot), original);
        } else message('该出口已设为空槽；应用后游戏内会移除这条连线。');
        return;
      }
      const next = { ...state.nodePatch, connections: state.nodePatch.connections.filter(item => item.sourceUid !== source || item.slot !== Number(slot)) };
      actions.setNodePatch(next, '恢复单项连接'); message('该连接已恢复，可撤销。'); return;
    }
    const result = actions.setConnectionTarget(source, Number(slot), select.value);
    if (result?.ok === false) {
      message(`修改失败：${result.error}`);
      select.value = currentTarget(state.nodePatch, source, Number(slot), original);
    } else message('连接草稿已更新；槽位条件、计数和淡入淡出参数保持原值。');
  }
  // 只在稳定容器注册监听，替换连接行后不保留旧 select 的回调。
  connections.addEventListener('pointerdown', expandTargets, { signal });
  connections.addEventListener('focusin', expandTargets, { signal });
  connections.addEventListener('focusout', collapseTargets, { signal });
  connections.addEventListener('change', changeTarget, { signal });
  function iconButton(icon, label, click) {
    const control = button('', click, 'patch-row-tool'); control.title = label; control.setAttribute('aria-label', label);
    control.append(createElement(icon, { width: 15, height: 15, 'aria-hidden': 'true' })); return control;
  }
  function importDetails(value) {
    const items = patch => [
      ...patch.connections.map(item => `${nameOf(item.sourceUid)} · 出口 ${item.slot + 1}：${item.before.targetUid ? nameOf(item.before.targetUid) : '空'} → ${item.after.targetUid ? nameOf(item.after.targetUid) : '空槽'}`),
      ...(patch.layout?.nodes || []).map(item => `${nameOf(item.uid)}：(${item.before.x}, ${item.before.y}) → (${item.after.x}, ${item.after.y})`)
    ];
    const sections = [
      { title: '当前将替换', patch: value.layout ? state.nodePatch : { connections: state.nodePatch.connections } },
      { title: '导入内容', patch: value }
    ];
    $('node-patch-import-details').replaceChildren(...sections.map(({ title, patch }) => {
      const section = el('section'), list = el('ul'), lines = items(patch);
      list.append(...(lines.length ? lines : ['无修改']).map(text => el('li', '', text)));
      section.append(el('strong', '', title), list); return section;
    }));
  }
  function scopeFilter() {
    const filter = $('node-patch-filter').value, query = $('node-patch-search').value.trim().toLowerCase();
    const edgeSource = data.edges.find(edge => edge.id === state.selectedEdge)?.from;
    return (uid, modified, text) => (filter !== 'selection' || state.selected.has(uid) || uid === edgeSource)
      && (filter !== 'modified' || modified) && (!query || text.toLowerCase().includes(query));
  }

  function renderConnections() {
    const filter = $('node-patch-filter').value;
    const key = JSON.stringify([filter, state.selectedEdge, [...state.selected], $('node-patch-search').value, state.nodePatch.connections]);
    if (key === connectionKey) { renderPositions(); return; }
    connectionKey = key;
    collapseTargets();
    const rows = [], inScope = scopeFilter();
    for (const node of data.nodes) {
      // 已有连接全部列出；空槽只列当前选中节点的可用槽，避免全图铺开。
      const slots = new Map((node.outputs || []).filter(output => typeof output.targetUid === 'string' && output.targetUid)
        .map(output => [output.slot, output.targetUid]));
      if (state.selected.has(node.id)) for (const slot of usableSlots(node)) if (!slots.has(slot)) slots.set(slot, '');
      for (const [slot, original] of [...slots].sort((a, b) => a[0] - b[0])) {
        const targetUid = currentTarget(state.nodePatch, node.id, slot, original), modified = targetUid !== original;
        if (!inScope(node.id, modified, `${node.name} ${node.id} ${nameOf(original)} ${nameOf(targetUid)} ${targetUid}`)) continue;
        const label = el('label', 'node-patch-connection');
        label.dataset.source = node.id; label.dataset.slot = slot; label.dataset.original = original;
        const info = el('span'); info.append(el('strong', '', `${node.name} · 出口 ${slot + 1}`),
          el('span', 'connection-difference', modified
            ? `${original ? nameOf(original) : '空'} → ${nameOf(targetUid)}`
            : original ? `原目标 ${nameOf(original)}` : '空槽，可选择目标'));
        label.classList.toggle('modified', modified); label.classList.toggle('empty', !original); label.append(info);
        const select = el('select');
        if (targetUid !== node.id && names.has(targetUid)) select.add(new Option(nameOf(targetUid), targetUid));
        // 占位项统一叫「空槽」：原本为空的槽选它是撤回草稿，原本有连接的槽选它是断开。
        if (!targetUid || original) select.add(new Option('（空槽）', ''));
        select.value = targetUid; select.setAttribute('aria-label', `${node.name} 出口 ${slot + 1} 目标`);
        const tools = el('div', 'patch-row-tools');
        tools.append(iconButton(LocateFixed, '定位来源节点', () => actions.selectNode(node.id)));
        const restore = iconButton(RotateCcw, '恢复该连接', () => {
          const next = { ...state.nodePatch, connections: state.nodePatch.connections.filter(item => item.sourceUid !== node.id || item.slot !== slot) };
          actions.setNodePatch(next, '恢复单项连接'); message('该连接已恢复，可撤销。');
        }); restore.disabled = !modified; tools.append(restore);
        label.append(select, tools); rows.push(label);
      }
    }
    connections.replaceChildren(...rows);
    if (!rows.length) connections.append(el('p', 'muted', filter === 'selection' ? '当前选区没有匹配连接' : '没有匹配连接'));
    $('node-patch-connection-count').textContent = `${rows.length} 项`;
    renderPositions();
  }
  function renderPositions() {
    const inScope = scopeFilter();
    const positions = (state.nodePatch.layout?.nodes || []).filter(item => inScope(item.uid, true, `${nameOf(item.uid)} ${item.uid}`));
    $('node-patch-positions').replaceChildren(...positions.map(item => {
      const row = el('div', 'node-position-change');
      row.dataset.node = item.uid;
      const point = p => `${Math.round(p.x * 100) / 100}, ${Math.round(p.y * 100) / 100}`;
      row.append(el('strong', '', nameOf(item.uid)), el('span', 'muted', `${point(item.before)} → ${point(item.after)}`),
        iconButton(LocateFixed, '定位节点', () => actions.selectNode(item.uid)), iconButton(RotateCcw, '恢复该位置', () => actions.restoreNodePosition(item.uid)));
      return row;
    }));
    if (!positions.length) $('node-patch-positions').append(el('p', 'muted', '没有匹配的位置修改'));
  }

  function refresh() {
    if (exported) $('patch-export-state').textContent = exported === JSON.stringify(state.nodePatch) ? exportedAt : '补丁导出后有修改';
    $('ec-canvas-scale').disabled = !data.geometry?.available;
    fillCanvasSelect();
    $('node-patch-source').textContent = `${state.nodePatch.connections.length} 项连接修改 · ${state.nodePatch.layout?.nodes.length || 0} 项位置修改${data.geometry?.available ? '' : ` · 布局不可用：${data.geometry?.reason || '缺少几何证据'}`}${state.nodePatchUnavailable ? ' · 旧补丁无法读取' : ''}`;
    $('node-patch-restore-connections').disabled = !state.nodePatch.connections.length;
    $('node-patch-restore-layout').disabled = !data.nodes.some(node => node.gamePosition);
    renderReview();
    $('goto-diagnostics').replaceChildren(...(data.gotoDiagnostics || []).map(item => el('p', `goto-diagnostic ${item.severity || 'info'}`, `${item.message}${item.origin?.pointer ? ` · ${item.origin.pointer}` : ''}`)));
    renderConnections();
  }

  function renderReview() {
    const review = state.nodePatchReview, section = $('node-patch-review');
    section.hidden = !review;
    if (!review) { $('node-patch-review-list').replaceChildren(); return; }
    const entries = review.connections.filter(entry => entry.after.targetUid === entry.sourceUid);
    $('node-patch-review-summary').textContent = `发现 ${entries.length} 项旧普通自连目标。活动补丁未应用这些连接；其它合法连接和布局已正常恢复。`;
    $('node-patch-review-list').replaceChildren(...entries.map(entry => {
      const source = data.nodes.find(node => node.id === entry.sourceUid);
      const before = data.nodes.find(node => node.id === entry.before.targetUid);
      const row = el('div', 'node-patch-review-entry');
      row.append(el('strong', '', `${source?.name || entry.sourceUid} · 出口 ${entry.slot + 1}`),
        el('span', '', `原目标 ${before?.name || entry.before.targetUid} · 旧草稿目标 ${source?.name || entry.sourceUid}`),
        el('code', '', `UID ${entry.sourceUid} / slot ${entry.slot}`));
      return row;
    }));
  }

  let importing = 0;
  function open() { refresh(); if (panel) { panel.open('node-patches'); return; } dialog.showModal(); }

  $('node-patch-close').onclick = () => panel ? panel.close() : dialog.close();
  function updateCanvas() {
    const result = actions.setCanvas(canvas());
    if (!result.ok) { refresh(); message(result.error); }
    else message(result.warning || '画布范围已更新，可撤销。');
  }
  $('ec-canvas-scale').addEventListener('change', updateCanvas, { signal });
  $('node-patch-filter').addEventListener('change', renderConnections, { signal });
  $('node-patch-search').addEventListener('input', renderConnections, { signal });
  $('node-patch-clear').onclick = () => {
    actions.restoreLayout(true); refresh(); message('节点位置与连接已恢复；画布尺寸保持当前值，可撤销。');
  };
  $('node-patch-restore-connections').onclick = () => { actions.restoreConnections(); message('原始连接已恢复，可撤销。'); };
  $('node-patch-restore-layout').onclick = () => { actions.restoreLayout(); message('游戏布局已恢复，连接草稿保留，可撤销。'); };
  $('node-patch-review-download').onclick = () => {
    if (!state.nodePatchReview) return;
    download(state.nodePatchReview, 'ec-scene-node-patch-review.json');
    message('已下载历史待复核原始补丁；该文件不能直接作为新补丁导入。');
  };
  $('node-patch-review-discard').onclick = () => {
    if (!state.nodePatchReview || !confirm('丢弃历史自连待复核记录？活动补丁和布局不会改变。')) return;
    actions.discardNodePatchReview?.(); message('历史待复核记录已丢弃；活动补丁未改变。');
  };
  $('node-patch-export').onclick = () => {
    try {
      const layout = data.geometry?.available ? actions.layoutPatch(canvas()) : state.nodePatch.layout;
      const value = validateNodePatch({ ...state.nodePatch, ...(layout ? { layout } : {}) }, data, { requireChanges: true });
      download(value); message('节点补丁已导出，并通过完整画布边界检查。');
      exported = JSON.stringify(state.nodePatch); exportedAt = `补丁已导出 ${new Date().toLocaleTimeString()}`;
      $('patch-export-state').textContent = exportedAt;
    } catch (error) { message(`无法导出：${error.message}`); }
  };
  $('node-patch-import').onchange = async event => {
    const file = event.target.files[0], token = ++importing; if (!file) return;
    try {
      assertNodePatchFileSize(file.size);
      const value = validateNodePatch(JSON.parse(await file.text()), data, { requireChanges: true });
      if (signal.aborted || token !== importing) return;
      candidate = value;
      importDetails(value);
      $('node-patch-import-summary').textContent = `连接修改 ${state.nodePatch.connections.length} → ${value.connections.length} 项;位置修改 ${state.nodePatch.layout?.nodes.length || 0} → ${value.layout ? value.layout.nodes.length : '保留当前'} 项。${value.layout ? `画布替换为 ${value.layout.canvas.width} × ${value.layout.canvas.height}。` : '当前画布与位置保留。'}`;
      $('node-patch-import-message').textContent = '补丁已核验,确认后应用。';
      importDialog.showModal(); message('补丁已核验,等待应用。');
    } catch (error) { if (!signal.aborted && token === importing) message(`导入失败：${error.message}`); }
    finally { if (!signal.aborted && token === importing) event.target.value = ''; }
  };
  $('node-patch-import-apply').onclick = () => {
    if (!candidate) return;
    actions.setNodePatch(candidate, '导入节点补丁'); candidate = null; importDialog.close();
    refresh(); message('节点补丁已导入,可撤销。');
  };
  const cancelImport = () => { candidate = null; importDialog.close(); message('导入已取消,当前修改保持不变。'); };
  $('node-patch-import-cancel').onclick = cancelImport;
  $('node-patch-import-close').onclick = cancelImport;
  signal.addEventListener('abort', () => { importing++; expandedTarget = null; dialog.close(); dialog.remove(); importDialog.close(); importDialog.remove(); opener?.remove(); }, { once: true });
  refresh();
  return { open, refresh, selection: () => {
    const key = [...state.selected, state.selectedEdge || ''].join('\0');
    if (key === selectionKey) return; selectionKey = key; renderConnections();
  } };
}
