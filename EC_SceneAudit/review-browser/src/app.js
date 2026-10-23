import './ui/styles.css';
import './styles.css';
import { $, el, resetCanvasFrame } from './ui/dom.js';
import { mountView } from './ui/view.js';
import { openDirectory, openStoredDirectory, readReport, checkReport, generationGate } from './files.mjs';
import { createModel } from './model.mjs';
import { adaptFindings } from './locations.mjs';
import { exportAnnotations } from './annotations.mjs';
import { createProject, prepareProject, readProject, projectId, saveProject, saveMigratedProject } from './projects.mjs';
import { session, readDrafts, readRegistryFiles, identityKey } from './api.mjs';
import { prepareMigration, discardMigrationRecord } from './migration.mjs';
import { mountProjects } from './ui/projects.js';
import { mountMigration } from './ui/migration.js';

const gate = generationGate();
let currentToken = 0;
function nextGeneration() { currentToken = gate.next(); return currentToken; }
let bundleResult = null, view = null, loadedFiles = null;
let selectedFiles = null, reportsReady = false, projectTitle = '';
// 当前项目的分组身份；迁移后的项目再次保存必须原样带回。
let projectMeta = null;
let reportInput = null, previousInput = null, currentReport = null, previousReport = null;
// 最近一次发布到视图的报告内容;用于判断用户是否真的替换/清除报告。
let reportsBaseline = null;
const reportKey = value => JSON.stringify(value ?? null);
let migration = null, storage = null;
// 启动即探测一次服务端存储;资料加载前先 await,避免草稿预取与可用性判断抢跑。
const sessionReady = session()
  .then(result => { storage = result; })
  .catch(error => { storage = { storage: { ok: false, reason: error.message } }; });

// 打开资料前先探测存储并预取四类草稿,createState 因此保持同步。
// 某一类读取失败时按不可用处理,避免把坏文件当成空草稿覆盖。
async function readViewDraft(id) {
  await sessionReady;
  const { values, failed } = await readDrafts(id, ['layout', 'node-patch', 'annotations', 'repairs']);
  if (failed.length) $('status').textContent = `草稿读取失败,本次按空状态继续:${failed.join('、')}`;
  return { ...values, failed, storage: storage?.storage?.ok === true };
}
function vault() { return { root: storage?.storage?.root, ok: storage?.storage?.ok, reason: storage?.storage?.reason }; }

function reportSession() {
  if (storage?.storage?.ok === false) $('status').textContent = `存储不可用:${storage.storage.reason}。当前修改不会保存,请导出备份。`;
}

function diagnostics(summary, items = []) {
  $('load-summary').textContent = summary;
  $('status').textContent = summary;
  $('load-diagnostics').replaceChildren(...items.map(item => el('li', '', typeof item === 'string' ? item : `${item.code}：${item.message}${item.path ? ' (' + item.path + ')' : ''}`)));
}

function clearView() {
  view?.destroy(); view = null; bundleResult = null;
  reportsBaseline = null;
  resetCanvasFrame();
  migration = null; projectMeta = null;
  $('scene-title').textContent = '尚未加载资料'; $('source-summary').textContent = '';
  $('problem-list').replaceChildren(); $('detail-page').replaceChildren();
  $('edges').replaceChildren(); $('regions').replaceChildren(); $('problem-count').textContent = '';
  $('cut-panel').hidden = true; $('source-warning').hidden = true;
  $('empty-selection').hidden = false;
  $('empty-selection').textContent = '请选择完整的 F8 资料包';
  $('report-overview').hidden = true;
}

function reportOverview(report, checked) {
  $('report-overview').hidden = !report || !checked.ok;
  if (!report || !checked.ok) return;
  const revision = report.supersedesReportId ? `修订自：${report.supersedesReportId}` : '首版报告';
  $('report-overview-text').textContent = `${report.reportId}\n${revision}\n\n${report.summary}\n\n已审查引用：${report.coverage.reviewed.length}\n未覆盖：\n${report.coverage.excluded.map(item => item.reason).join('\n') || '未列出'}\n\n限制：\n${report.coverage.limitations.join('\n') || '未列出'}`;
}

async function applyReports(token) {
  reportsReady = false;
  view.setReport([], 'scene');
  const [report, previous] = await Promise.all([readReport(reportInput), readReport(previousInput)]);
  if (!gate.isCurrent(token)) return;
  const checked = checkReport(report, bundleResult.bundle, previous);
  // 报告属于项目快照内容;只有实际替换/清除才标脏,初次加载与同内容重读不动。
  // 未通过核验的报告不进基准,也不能算用户编辑。
  const next = { report: reportKey(report), previous: reportKey(previous) };
  const changed = checked.ok && reportsBaseline !== null && (next.report !== reportsBaseline.report || next.previous !== reportsBaseline.previous);
  if (checked.ok) reportsBaseline = next;
  currentReport = report; previousReport = previous; reportsReady = checked.ok;
  if (changed && view.state.projectSave) view.actions.draftChanged();
  displayReport(report, checked);
}

function displayReport(report, checked) {
  const findings = checked.ok && report ? adaptFindings(bundleResult.bundle, view.data, report) : [];
  view.setReport(findings, checked.mode);
  reportOverview(report, checked);
  const notes = [...bundleResult.warnings, ...view.data.notices, ...checked.errors, ...checked.warnings];
  diagnostics(!checked.ok ? '报告未通过核验，当前仅浏览资料' : report ? `已加载 ${findings.filter(item => item.status === 'active').length} 条有效问题` : '资料已核验，可直接选择节点阅读', notes);
  $('source-summary').textContent = `${view.data.nodes.length} 节点 · ${view.data.nodes.reduce((sum, node) => sum + node.cuts.length, 0)} CUT${notes.length ? ' · 有资料提示' : ''}`;
}

async function loadSelection(force = false) {
  const token = nextGeneration();
  reportsReady = false;
  const selection = selectedFiles;
  if (!selection?.length) { diagnostics('请先选择资料包文件夹'); return; }
  const changed = force || loadedFiles !== selection;
  try {
    diagnostics('正在读取并核验本地文件…');
    if (changed || !bundleResult) {
      clearView();
      const result = await openDirectory(selection);
      if (!gate.isCurrent(token)) return;
      if (!result.ok) { diagnostics('资料包未通过核验，未加载内容', result.errors); return; }
      const data = createModel(result.bundle);
      const draft = await readViewDraft(identityKey(data));
      if (!gate.isCurrent(token)) return;
      bundleResult = result; loadedFiles = selection;
      view = mountView(data, 'scene', draft);
      projectTitle = '';
      $('scene-title').textContent = `${data.title} · ${data.exportedAt}`;
      $('empty-selection').textContent = '选择节点，展开 CUT 预览；或从左侧问题自动定位';
    }
    await applyReports(token);
  } catch (error) {
    if (!gate.isCurrent(token)) return;
    // 读取失败不得沿用上一次成功的报告,保存入口由 reportsReady=false 拦住。
    currentReport = previousReport = null;
    view?.setReport([], 'mismatch'); $('report-overview').hidden = true;
    diagnostics(view ? '报告读取失败，当前仅浏览资料' : '资料读取失败，未加载内容', [error.message]);
  }
}

function reportFile(value) { return value ? { name: '项目报告', text: async () => JSON.stringify(value) } : null; }

async function openProject(id) {
  await sessionReady;
  const token = nextGeneration(), project = await readProject(id);
  if (!project) throw new Error('项目已被删除');
  const prepared = await prepareProject(project);
  if (!gate.isCurrent(token)) return false;
  installProject(project, prepared);
  return true;
}

// 打开已登记资料包:字节来自数据根副本,核验与草稿预取沿用选目录那条路径。
async function openRegistryBundle(id) {
  await sessionReady;
  const token = nextGeneration();
  const files = await readRegistryFiles(id);
  const result = await openStoredDirectory(files);
  if (!gate.isCurrent(token)) return false;
  if (!result.ok) throw new Error('资料包未通过核验:' + result.errors.map(error => error.message).join(';'));
  const data = createModel(result.bundle);
  const draft = await readViewDraft(identityKey(data));
  if (!gate.isCurrent(token)) return false;
  clearView();
  bundleResult = result; loadedFiles = selectedFiles = files;
  reportInput = previousInput = null; currentReport = previousReport = null;
  view = mountView(data, 'scene', draft);
  projectTitle = '';
  $('scene-title').textContent = data.title + ' · ' + data.exportedAt;
  $('empty-selection').textContent = '选择节点,展开 CUT 预览;或从左侧问题自动定位';
  $('status').textContent = '已从登记资料包打开。如需保留报告与编辑,请另存为项目。';
  return true;
}

function installProject(project, prepared) {
  clearView();
  bundleResult = prepared.result; selectedFiles = loadedFiles = prepared.files;
  currentReport = prepared.report; previousReport = prepared.previous;
  reportsBaseline = { report: reportKey(currentReport), previous: reportKey(previousReport) };
  reportInput = reportFile(currentReport); previousInput = reportFile(previousReport);
  for (const id of ['bundle-files', 'report-file', 'previous-file']) $(id).value = '';
  view = mountView(prepared.data, 'scene', { storage: storage?.storage?.ok === true }, () => projectsUI.saveCurrent());
  // 恢复期间只装内存:各恢复入口会重复触发落盘,收尾统一写一次。
  view.setRestoring(true);
  try {
    view.restoreNodePatch(prepared.nodePatch.connections.length || prepared.nodePatch.layout ? prepared.nodePatch : null, prepared.nodePatchReview);
    view.restoreAnnotations(prepared.annotations); view.flushAnnotations(); view.actions.importLayout(prepared.annotations.layout, false);
    view.restoreRepairs(prepared.repairs);
    for (const direction of ['before', 'after']) if ([0, 1, 2, 4, 8].includes(project.defaults?.[direction])) view.state.defaults[direction] = view.state[direction] = project.defaults[direction];
    view.state.routeOffsets = prepared.routeOffsets;
  } finally { view.setRestoring(false); }
  view.actions.save(false, false);
  view.actions.render();
  projectTitle = prepared.name; reportsReady = true;
  projectMeta = { lineage: project.lineage ?? project.migration?.source?.projectId ?? null, previousId: project.previousId ?? null };
  migration = prepared.migration ?? project.migration ?? null;
  $('scene-title').textContent = `${view.data.title} · ${view.data.exportedAt}`;
  $('empty-selection').textContent = '选择节点，展开 CUT 预览；或从左侧问题自动定位';
  displayReport(currentReport, prepared.checked);
  $('status').textContent = `已打开项目：${projectTitle}`;
}

async function captureSnapshot(name) {
  if (!view || !reportsReady) throw new Error('请先等待资料与报告核验完成；失败报告不能存入项目');
  const token = currentToken, activeView = view;
  activeView.actions.clearLayoutPreview();
  activeView.flushAnnotations();
  const repairs = activeView.flushRepairs();
  // epoch 必须在 flush 后、构造快照字段前同步取,否则保存期间的新编辑会被算进本次保存。
  const epoch = activeView.editEpoch();
  const project = await createProject({ name, files: loadedFiles, report: currentReport, previous: previousReport,
    data: activeView.data, annotations: exportAnnotations(activeView.data, activeView.state), repairs,
    nodePatch: activeView.flushNodePatch(), nodePatchReview: activeView.state.nodePatchReview, defaults: activeView.state.defaults, routeOffsets: activeView.state.routeOffsets,
    lineage: projectMeta?.lineage, previousId: projectMeta?.previousId,
    ...(migration ? { migration } : {}) });
  if (!gate.isCurrent(token) || view !== activeView) throw new Error('已切换资料，取消这次保存');
  return { project, epoch, valid: () => gate.isCurrent(token) && view === activeView };
}

// 透传视图代次与有效性;落盘前不改项目名,失败不污染当前名称。
function captureProject(name) {
  return captureSnapshot(name);
}

const migrationUI = mountMigration({
  async preview(files) {
    const source = await captureSnapshot(projectTitle || view?.data.title || '审查项目');
    const result = await prepareMigration(source.project, files);
    if (!source.valid()) throw new Error('资料选择已改变，请重新预览');
    return { ...result, source: source.project, valid: source.valid };
  },
  async commit(candidate) {
    if (!candidate.valid()) throw new Error('资料选择已改变，请重新预览');
    const prepared = await prepareProject(candidate.project);
    if (!candidate.valid()) throw new Error('资料选择已改变，请重新预览');
    const saved = await saveMigratedProject(candidate.project, candidate.source);
    if (!candidate.valid()) return '新项目已保存；当前资料选择已改变，未切换视图。';
    try {
      installProject(saved, prepared);
      return '新项目已保存并打开，旧项目已保留。';
    } catch (error) {
      return `新项目已保存，但打开失败：${error.message}。可从项目列表重新打开。`;
    }
  },
  records: () => migration,
  async discard(id) {
    if (!migration) throw new Error('当前没有迁移记录');
    const next = discardMigrationRecord(migration, id);
    const snapshot = await captureSnapshot(projectTitle || view.data.title);
    const project = { ...snapshot.project, migration: next };
    await prepareProject(project);
    if (!snapshot.valid()) throw new Error('资料选择已改变，未更新记录');
    // 普通保存入口负责持久化，验证失败不改变当前记录。
    await saveProject(project);
    if (snapshot.valid()) migration = next;
  }
});

$('import-open').onclick = () => $('import-dialog').showModal();
$('import-close').onclick = () => $('import-dialog').close();
for (const [id, input] of [['bundle-pick', 'bundle-files'], ['report-pick', 'report-file'], ['previous-pick', 'previous-file']])
  $(id).onclick = () => $(input).click();
$('bundle-files').onchange = () => { selectedFiles = [...$('bundle-files').files]; loadSelection(); };
$('report-file').onchange = () => { reportInput = $('report-file').files[0] ?? null; loadSelection(); };
$('previous-file').onchange = () => { previousInput = $('previous-file').files[0] ?? null; loadSelection(); };
$('reload-files').onclick = () => loadSelection(true);
$('clear-previous').onclick = () => { $('previous-file').value = ''; previousInput = null; loadSelection(); };
function clearReport() { $('report-file').value = ''; $('previous-file').value = ''; reportInput = previousInput = null; loadSelection(); }
$('clear-report').onclick = clearReport;
const projectsUI = mountProjects({ current: () => view && reportsReady ? { id: projectId(view.data), title: view.data.title, name: projectTitle } : null,
  capture: captureProject, openProject, openRegistryBundle, updateBundle: () => migrationUI.open(),
  migrationRecords: () => migrationUI.openRecords(), hasMigration: () => Boolean(migration),
  storage: vault,
  renamed: (id, name) => { if (view && projectId(view.data) === id) projectTitle = name; },
  // 保存成功后只回写发起保存的视图;磁盘成功但视图已换时不改新视图的项目态与名称。
  saved: (project, epoch) => {
    if (!view || projectId(view.data) !== project.id) return;
    projectTitle = project.name;
    view.setProjectSaver(projectsUI.saveCurrent, epoch);
  } });
clearView(); $('import-dialog').showModal();
// 启动探测失败仍可只读浏览,只是不再假装能保存。
sessionReady.then(reportSession);
