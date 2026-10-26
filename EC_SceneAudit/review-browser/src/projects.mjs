import { captureDirectory, openStoredDirectory, checkReport } from './files.mjs';
import { createModel } from './model.mjs';
import { validateAnnotations } from './annotations.mjs';
import { emptyRepairs, validateRepairs } from './repairs.mjs';
import { validateMigration } from './migration-metadata.mjs';
import { recoverStoredNodePatch } from './node-patch-recovery.mjs';
import { createdEdges } from './node-patch.mjs';
import { validateRouteOffsets } from './web-layout.mjs';
import * as api from './api.mjs';

export const projectId = source => JSON.stringify([source.bundleId, source.manifestSha256]);
export function projectFiles(members) { return members.map(member => ({ name: member.path, arrayBuffer: async () => member.bytes })); }
export function projectName(value) {
  if (typeof value !== 'string' || !value.trim() || value.trim().length > 120) throw new Error('项目名须为 1–120 字');
  return value.trim();
}
export async function prepareProject(project) {
  if (project?.version !== 1) throw new Error('项目资料格式无效');
  const result = await openStoredDirectory(project.files);
  if (!result.ok) throw new Error(`项目资料未通过核验:${result.errors.map(error => error.message).join(';')}`);
  const data = createModel(result.bundle);
  if (project.id !== projectId(data) || project.source?.bundleId !== data.bundleId || project.source?.manifestSha256 !== data.manifestSha256) throw new Error('项目来源与所存资料不一致');
  const checked = checkReport(project.report, result.bundle, project.previous);
  if (!checked.ok) throw new Error(`项目报告未通过核验:${checked.errors.map(error => error.message).join(';')}`);
  if (project.previous && !project.report) throw new Error('项目缺少与前版对应的当前报告');
  const annotations = validateAnnotations(project.annotations, data);
  if (!annotations.layout) throw new Error('项目缺少节点布局');
  // 旧项目没有 repairs 字段时视为空清单;显式写入的坏字段仍要拒绝。
  const repairs = project.repairs === undefined ? emptyRepairs(data) : validateRepairs(project.repairs, data);
  const recovery = recoverStoredNodePatch(project.nodePatch, data, project.nodePatchReview);
  const migration = project.migration === undefined ? undefined : validateMigration(project.migration);
  // 空槽补丁新建的边属于当前普通边，路线校验必须放行它们。
  const routeOffsets = validateRouteOffsets(project.routeOffsets, data, createdEdges(data.baseEdges, recovery.nodePatch, data));
  if (migration?.source.projectId === project.id) throw new Error('更新记录的来源不能是当前项目自身');
  return { result, data, checked, annotations, repairs, nodePatch: recovery.nodePatch, nodePatchReview: recovery.nodePatchReview,
    migration, routeOffsets, files: projectFiles(project.files), report: project.report, previous: project.previous, name: projectName(project.name) };
}
export async function createProject({ name, files, report, previous, data, annotations, repairs, nodePatch, nodePatchReview, defaults, migration, routeOffsets, lineage, previousId }) {
  const recovery = recoverStoredNodePatch(nodePatch, data, nodePatchReview, { allowLegacyValue: false });
  const project = {
    version: 1, id: projectId(data), name: projectName(name), updatedAt: new Date().toISOString(),
    source: { bundleId: data.bundleId, manifestSha256: data.manifestSha256 },
    files: await captureDirectory(files), report: report ?? null, previous: report ? previous ?? null : null,
    annotations: structuredClone(annotations), repairs: structuredClone(repairs ?? emptyRepairs(data)), defaults: structuredClone(defaults)
  };
  // 已迁移项目再次保存时必须保留分组身份，否则会从来源项目的历史里拆成同名新项目。
  if (typeof lineage === 'string' && lineage) project.lineage = lineage;
  if (typeof previousId === 'string' && previousId) project.previousId = previousId;
  if (recovery.nodePatch.connections.length || recovery.nodePatch.layout) project.nodePatch = structuredClone(recovery.nodePatch);
  if (recovery.nodePatchReview) project.nodePatchReview = structuredClone(recovery.nodePatchReview);
  if (migration !== undefined) project.migration = validateMigration(migration);
  const routes = validateRouteOffsets(routeOffsets, data, createdEdges(data.baseEdges, recovery.nodePatch, data));
  if (Object.keys(routes).length) project.routeOffsets = routes;
  await prepareProject(project); return project;
}
// 存储统一走 /api/,核验仍留前端:prepareProject 的字节复核是落盘前的最后一道关。
export function listProjects() { return api.listProjects(); }
export function readProject(id) { return api.readProject(id); }
export function saveProject(project) { return api.saveProject(project); }
export function renameProject(id, name) { return api.renameProject(id, projectName(name)); }
export function deleteProject(id) { return api.deleteProject(id); }
export async function saveMigratedProject(project, sourceProject) {
  const target = structuredClone(project), source = structuredClone(sourceProject);
  // 同一 Scene 的连续更新共享 lineage,历史版本用 previousId 串起来。
  target.lineage = source.lineage ?? source.migration?.source?.projectId ?? source.id;
  target.previousId = source.id;
  await prepareProject(source);
  const prepared = await prepareProject(target);
  if (target.id === source.id || prepared.migration?.source.projectId !== source.id) throw new Error('更新项目来源不一致或目标资料未变化');
  await api.saveMigratedProject(target, source);
  // 返回带 lineage/previousId 的目标，调用方安装后再次保存才能沿用同一分组。
  return target;
}
