import { $, el, button } from './dom.js';
import { listProjects, saveProject, renameProject, deleteProject } from '../projects.mjs';
import { captureDirectory } from '../files.mjs';
import { listRegistry, registerBundle, deleteRegistry } from '../api.mjs';
import './projects.css';

export function mountProjects({ current, capture, openProject, openRegistryBundle, renamed, updateBundle, migrationRecords, hasMigration = () => false, storage = () => null, saved = () => {} }) {
  const dialog = el('dialog', 'project-dialog'); dialog.id = 'project-dialog'; dialog.setAttribute('aria-labelledby', 'project-title');
  dialog.innerHTML = `<header><h2 id="project-title">本地审查项目</h2><button id="project-close" type="button" aria-label="关闭项目列表">×</button></header>
    <div class="project-scroll">
      <p class="project-note">保存资料包、报告、作者标注、修复草稿和布局,下次直接打开。项目保存在本机数据目录,换浏览器或清空浏览器数据都不受影响。</p>
      <label>当前项目名称<input id="project-name" maxlength="120" placeholder="给这次审查起个名字"></label>
      <div class="project-toolbar"><button id="project-save" type="button">保存当前审查</button><div class="project-entry-actions"><button id="project-update" type="button">更新资料包</button><button id="project-register" type="button">登记资料包</button><button id="project-migration" type="button">迁移记录与历史报告</button></div></div>
      <p id="project-status" role="status" aria-live="polite"></p>
      <p id="project-storage" class="muted"></p>
      <div id="project-list"></div>
      <section class="project-registry"><h3>已登记资料包</h3><p class="muted">登记后整包复制进数据目录,下次启动可直接打开,不必再选目录。</p><div id="registry-list"></div></section>
    </div>`;
  document.body.append(dialog);
  const update = $('project-update'), records = $('project-migration'), register = $('project-register');
  const picker = el('input'); picker.type = 'file'; picker.multiple = true; picker.setAttribute('webkitdirectory', ''); picker.hidden = true; picker.id = 'registry-picker';
  dialog.append(picker);
  const opener = button('项目', open); opener.id = 'project-open'; document.querySelector('.top-actions').prepend(opener);
  const shortcut = button('打开已保存项目', open); shortcut.id = 'project-open-import'; $('import-dialog').querySelector('.import-actions').prepend(shortcut);
  let busy = false, listing = 0;
  function announce(message) { $('project-status').textContent = message; }
  function refreshControls() {
    for (const element of dialog.querySelectorAll('button,input')) if (element.id !== 'project-close') element.disabled = busy;
    $('project-save').disabled = busy || !current();
    update.disabled = busy || !current() || !updateBundle;
    records.disabled = busy || !current() || !hasMigration();
  }
  function showStorage() {
    const state = storage();
    $('project-storage').textContent = state?.ok === false ? `存储不可用:${state.reason}。当前修改不会保存,请导出备份。`
      : state?.root ? `数据目录:${state.root}` : '';
  }
  async function run(action) {
    if (busy) return;
    busy = true; refreshControls();
    try { await action(); } catch (error) { announce(`操作失败:${error.message}。原有项目未被清除。`); }
    finally { busy = false; refreshControls(); }
  }
  // 同一 Scene 的连续更新共享 lineage;最新一个作为当前条目,其余收进历史子项。
  function groupByLineage(projects) {
    const groups = new Map();
    for (const project of projects) {
      const key = project.lineage ?? project.id;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(project);
    }
    return [...groups.values()].map(versions => versions.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)));
  }
  function projectRow(project, { current: isCurrent = true, extra = [] } = {}) {
    const row = el('article', `project-entry${isCurrent ? '' : ' is-history'}`); row.dataset.project = project.id;
    row.append(el('strong', '', project.name), el('span', 'muted', `${new Date(project.updatedAt).toLocaleString()} · ${(project.bytes / 1048576).toFixed(1)} MB`));
    const actions = el('div', 'project-entry-actions');
    const load = button(isCurrent ? '打开' : '打开此版本', () => run(async () => {
      announce('正在核验并打开项目...');
      if (await openProject(project.id)) { $('project-name').value = project.name; dialog.close(); }
      else announce('已有更新的资料选择,本次打开已取消');
    })); load.dataset.action = 'open';
    const rename = button('改名', () => run(async () => {
      const name = window.prompt('项目名称', project.name); if (name === null) return;
      await renameProject(project.id, name); renamed(project.id, name.trim()); await renderList();
      if (current()?.id === project.id) $('project-name').value = name.trim();
      announce('项目已改名');
    })); rename.dataset.action = 'rename';
    const remove = button('删除', () => run(async () => {
      if (!window.confirm(`删除数据目录里的项目「${project.name}」?原始资料文件不受影响。`)) return;
      await deleteProject(project.id); await renderList(); announce('项目已从数据目录删除');
    })); remove.dataset.action = 'delete';
    actions.append(load, rename, remove, ...extra); row.append(actions); return row;
  }
  async function renderList() {
    const generation = ++listing, projects = await listProjects();
    if (generation !== listing) return;
    const list = $('project-list');
    if (!projects.length) { list.replaceChildren(el('p', 'muted', '尚无项目。加载资料后,可保存当前审查。')); refreshControls(); return; }
    list.replaceChildren(...groupByLineage(projects).map(versions => {
      const [head, ...history] = versions;
      const card = el('section', 'project-group');
      card.append(projectRow(head));
      if (history.length) {
        const details = el('details', 'project-history');
        details.append(el('summary', '', `历史版本 ${history.length} 个`));
        const body = el('div', 'project-history-body');
        body.append(...history.map(version => projectRow(version, { current: false })));
        details.append(body); card.append(details);
      }
      return card;
    }));
    refreshControls();
  }
  // 登记项字节在数据根里,打开直接读副本,不再让玩家选目录。
  function registryRow(bundle) {
    const row = el('article', 'project-entry'); row.dataset.bundle = bundle.id;
    row.append(el('strong', '', bundle.name || bundle.bundleId), el('span', 'muted', `资料包 · ${new Date(bundle.registeredAt).toLocaleString()} · ${(bundle.bytes / 1048576).toFixed(1)} MB`));
    const actions = el('div', 'project-entry-actions');
    const open = button('打开', () => run(async () => {
      announce('正在核验并打开资料包...');
      if (await openRegistryBundle?.(bundle.id)) dialog.close();
      else announce('已有更新的资料选择,本次打开已取消');
    })); open.dataset.action = 'open-registry';
    actions.append(open);
    const remove = button('删除登记', () => run(async () => {
      if (!window.confirm(`从数据目录删除已登记资料包「${bundle.name || bundle.bundleId}」?原始导出目录不受影响。`)) return;
      await deleteRegistry(bundle.id); await renderRegistry(); announce('资料包已从数据目录移除');
    })); remove.dataset.action = 'delete-registry';
    actions.append(remove); row.append(actions); return row;
  }
  async function renderRegistry() {
    const list = $('registry-list'), bundles = await listRegistry();
    list.replaceChildren(...(bundles.length ? bundles.map(registryRow) : [el('p', 'muted', '尚无登记资料包。')]));
  }
  async function open() {
    $('import-dialog').close(); if (!dialog.open) dialog.showModal();
    const info = current(); $('project-name').value = info?.name || info?.title || '';
    refreshControls(); showStorage();
    await run(async () => { await Promise.all([renderList(), renderRegistry()]); announce('选择项目打开,或保存当前审查。'); });
  }
  $('project-close').onclick = () => dialog.close();
  $('project-update').onclick = () => run(async () => { dialog.close(); updateBundle(); });
  $('project-migration').onclick = () => run(async () => { dialog.close(); migrationRecords(); });
  // 登记只读取整包字节交给服务端核验,校验失败时不落盘。
  $('project-register').onclick = () => { announce('请选择要登记的资料包目录...'); picker.click(); };
  picker.onchange = () => run(async () => {
    const files = [...picker.files]; picker.value = '';
    if (!files.length) return;
    announce('正在核验并登记资料包...');
    const bundle = await registerBundle(await captureDirectory(files));
    await renderRegistry(); announce(`已登记资料包:${bundle.name || bundle.bundleId}`);
  });
  $('project-save').onclick = () => run(async () => {
    announce('正在保存完整资料和当前编辑...');
    // 只有落盘成功且发起保存的视图仍有效,才把当前视图转为项目态。
    const snapshot = await capture($('project-name').value);
    await saveProject(snapshot.project);
    if (snapshot.valid()) saved(snapshot.project, snapshot.epoch);
    await renderList(); announce('项目已保存,下次可从此列表直接打开。');
  });
  // 画布保存按钮在项目态复用对话框保存路径;对话框没开过时项目名缺省为当前项目,避免拿空名或旧输入保存。
  function saveCurrent() {
    const info = current(); if (!info) return;
    $('project-name').value = info.name || info.title || '未命名项目';
    $('project-save').click();
  }
  return { open, saveCurrent };
}