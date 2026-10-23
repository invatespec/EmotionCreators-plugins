import { el, button } from './dom.js';
import './migration.css';

const reasons = {
  deleted: '节点已删除', 'type-changed': '节点类型变化', 'content-changed': '节点文本变化',
  'connections-changed': '相关连线变化', renamed: '节点改名', 'structure-changed': 'CUT 或文本框结构变化',
  'target-unavailable': '草稿位置缺失或不唯一', 'before-conflict': '草稿原文与新资料不一致',
  'already-applied': '新资料已经包含目标文本', 'dependencies-changed': '外部依赖变化', 'scope-review': '全局审查范围需要复核',
  'node-patch-review': '节点补丁待复核', 'layout-review': 'EC 布局未应用，待复核'
};
function details(title, value) {
  const box = el('details', 'migration-detail');
  box.append(el('summary', '', title), el('pre', '', typeof value === 'string' ? value : JSON.stringify(value, null, 2)));
  return box;
}

function download(value, name) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' }));
  const link = el('a'); link.href = url; link.download = name; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function mountMigration({ preview, commit, records, discard }) {
  const dialog = el('dialog', 'project-dialog migration-dialog'); dialog.id = 'migration-dialog';
  dialog.setAttribute('aria-labelledby', 'migration-title');
  const header = el('header'), title = el('h2', '', '更新资料包'); title.id = 'migration-title';
  const close = button('×', () => dialog.close()); close.setAttribute('aria-label', '关闭资料更新');
  header.append(title, close);
  const status = el('p'); status.id = 'migration-status'; status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
  const picker = el('input'); picker.type = 'file'; picker.multiple = true; picker.setAttribute('webkitdirectory', '');
  picker.id = 'migration-files';
  const label = el('label', '', '选择新 F8 资料包文件夹'); label.append(picker);
  const content = el('div', 'migration-content');
  const confirm = button('确认更新并保存新项目', save); confirm.id = 'migration-confirm';
  dialog.append(header, label, status, content, confirm); document.body.append(dialog);
  let generation = 0, candidate = null, saving = false, reviewing = false;
  function reset() { generation++; candidate = null; content.replaceChildren(); confirm.disabled = true; picker.value = ''; }
  function announce(text) { status.textContent = text; }
  function stale(token) { return token !== generation || !dialog.open; }
  function setSaving(value) {
    saving = value; close.disabled = value; picker.disabled = value; confirm.disabled = value || !candidate;
  }
  dialog.addEventListener('cancel', event => { if (saving) event.preventDefault(); });
  dialog.addEventListener('close', () => { generation++; candidate = null; });
  picker.onchange = async () => {
    const token = ++generation; candidate = null; content.replaceChildren(); confirm.disabled = true;
    const files = [...picker.files]; if (!files.length) { announce('尚未选择新资料'); return; }
    announce('正在核验新资料并计算迁移预览…');
    try {
      const result = await preview(files);
      if (stale(token)) return;
      candidate = result;
      renderPreview(result); confirm.disabled = false; announce('预览完成，尚未保存或替换当前资料。');
    } catch (error) { if (!stale(token)) announce(`无法预览：${error.message}。当前资料保持不变。`); }
  };
  function renderPreview(result) {
    const source = result.source, target = result.project;
    const summary = result.summary;
    content.append(details(`原资料：${summary.source.title} · ${source.name}`, source.source),
      details(`新资料：${summary.target.title} · ${target.name}`, target.source));
    const counts = el('dl', 'migration-counts');
    for (const [field, label] of [['matched', '匹配继承'], ['added', '新增节点'], ['deleted', '删除节点'],
      ['conflicted', '类型冲突'], ['changed', '内容或连线变化'], ['renamed', '改名']]) {
      counts.append(el('dt', '', label), el('dd', '', String(summary[field].length)));
    }
    content.append(counts, el('p', '', `修复草稿：可迁移 ${summary.repairs.migrated}，冲突 ${summary.repairs.conflicted}，已满足 ${summary.repairs.applied}。`));
    content.append(details('节点差异明细', summary));
    content.append(el('p', '', '新项目当前 AI 报告为空。旧报告保留在历史记录；已有旧项目不会被覆盖。'));
    renderRecords(target.migration, false);
  }
  async function save() {
    if (!candidate || saving) return;
    setSaving(true); announce('正在保存并核验新项目…');
    try {
      const message = await commit(candidate);
      candidate = null; announce(message);
      label.hidden = true; content.replaceChildren();
      renderRecords(records(), true);
    } catch (error) { announce(`更新失败：${error.message}。原有项目未被覆盖。`); }
    finally { setSaving(false); }
  }
  function pendingRow(record, editable) {
    const row = el('section', 'migration-record');
    row.append(el('strong', '', `${record.details.name || record.nodeUid || '资料级记录'} · ${reasons[record.reason] || record.reason}`));
    row.append(el('p', '', record.status === 'discarded' ? '已明确放弃' : '待复核'));
    const data = record.details;
    if (data.annotation) {
      const note = data.annotation;
      row.append(details('旧作者标注', `分组：${note.groups?.join('、') || '无'}\n阶段：${note.stage || '无'}\n状态：${note.status || '无'}\n备注：${note.note || '无'}`));
    }
    if (data.repair) {
      const repair = data.repair, target = repair.target;
      row.append(el('p', '', `UID ${target.nodeUid} · CUT 源序号 ${target.cutSourceIndex} · 文本框源序号 ${target.textBoxSourceIndex} · 布局 ${target.layoutIndex}`));
      row.append(details('旧原文', repair.before), details('目标文本', repair.after));
      if (data.currentBefore !== undefined) row.append(details('新资料原文', data.currentBefore));
    }
    if (data.nodePatch) {
      const patch = data.nodePatch;
      const connections = Array.isArray(patch.connections) ? patch.connections.length : 0;
      const layoutNodes = Array.isArray(patch.layout?.nodes) ? patch.layout.nodes.length : 0;
      const canvas = patch.layout?.canvas;
      row.append(el('p', '', `旧节点补丁：连接 ${connections} 项 · 布局节点 ${layoutNodes} 项${canvas ? ` · 画布 ${canvas.width}×${canvas.height}` : ''}`));
      row.append(details('原来源', `bundleId：${patch.source?.bundleId ?? '未知'}\nmanifestSha256：${patch.source?.manifestSha256 ?? '未知'}`));
      row.append(el('p', 'muted', '新资料不会自动套用旧补丁；请在打开新资料后于“节点补丁”入口重新创建。'));
      row.append(details('旧节点补丁 JSON', patch));
      row.append(button('下载旧节点补丁', () => download(patch, 'ec-scene-node-patch.old.json')));
    }
    if (data.ecLayout) {
      const layout = data.ecLayout, canvas = layout.canvas;
      row.append(el('p', '', `旧 EC 布局：${Object.keys(layout.positions || {}).length} 个节点${canvas ? ` · 画布 ${canvas.width}×${canvas.height}` : ''}`));
      row.append(el('p', 'muted', '目标资料没有完整 EC 几何，未把旧 EC 坐标套到当前布局；旧布局、画布和来源保留在记录与历史快照中。'));
      row.append(details('旧 EC 布局 JSON', layout));
      row.append(button('下载旧 EC 布局', () => download(layout, 'author-layout.ec.old.json')));
    }
    row.append(details('原始内容与来源', record));
    if (editable && record.status !== 'discarded') {
      row.append(button('明确放弃此记录', async () => {
        if (reviewing || saving || !window.confirm('放弃此记录？原始内容仍保留在历史中，处理状态会保存到当前项目。')) return;
        reviewing = true; setSaving(true);
        try {
          await discard(record.id); content.replaceChildren(); renderRecords(records(), true);
          announce('处理状态已保存。');
        } catch (error) { announce(`保存失败：${error.message}`); }
        finally { reviewing = false; setSaving(false); }
      }));
    }
    return row;
  }
  function renderRecords(value, editable) {
    if (!value) { content.append(el('p', '', '没有迁移记录。')); return; }
    content.append(el('h3', '', `待处理记录（${value.pending.filter(item => item.status !== 'discarded').length}）`));
    for (const record of value.pending) content.append(pendingRow(record, editable));
    content.append(el('h3', '', '历史报告'));
    for (const history of value.history) {
      const row = el('section', 'migration-record');
      row.append(el('strong', '', history.name || '旧项目'), details('来源', history.source));
      for (const field of ['report', 'previous']) if (history[field]) {
        row.append(details(field === 'report' ? '旧报告全文' : '旧前版报告全文', history[field]));
        row.append(button(field === 'report' ? '下载旧报告' : '下载旧前版报告',
          () => download(history[field], `history-${field}.json`)));
      }
      content.append(row);
    }
  }
  return {
    open() {
      reset(); title.textContent = '更新资料包'; label.hidden = false; confirm.hidden = false;
      announce(''); dialog.showModal(); picker.focus();
    },
    openRecords() {
      reset(); title.textContent = '迁移记录与历史报告'; label.hidden = true; confirm.hidden = true;
      announce('历史报告不代表当前资料的有效结论。'); renderRecords(records(), true); dialog.showModal(); close.focus();
    }
  };
}
