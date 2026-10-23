import { loadBundle, portablePath } from '../../review-core/src/bundle.mjs';
import { validateReport } from '../../review-core/src/report.mjs';

// 目录成员按小写路径去重，保留原始大小写供清单读取。
export function directoryMembers(files) {
  const members = new Map();
  for (const file of files) {
    const relative = file.webkitRelativePath || file.name;
    const path = file.webkitRelativePath ? relative.slice(relative.indexOf('/') + 1) : relative;
    if (!portablePath(path) || members.has(path.toLowerCase())) throw new Error('目录包含无效或重复路径。');
    members.set(path.toLowerCase(), { path, file });
  }
  if (!members.has('bundle.json')) throw new Error('请选择直接包含 bundle.json 的单个 F8 资料包目录。');
  return members;
}

export function selectedDirectory(files) {
  const members = directoryMembers(files);
  return async path => {
    const member = members.get(path.toLowerCase());
    if (!member) throw new Error(`缺少文件：${path}`);
    return new Uint8Array(await member.file.arrayBuffer());
  };
}

// 保存项目与登记资料包都需要整包字节,读一次后交给 /api/。
export async function captureDirectory(files) {
  const entries = [];
  for (const { path, file } of directoryMembers(files).values()) entries.push({ path, bytes: new Uint8Array(await file.arrayBuffer()) });
  return entries;
}

export function storedDirectory(entries) {
  if (!Array.isArray(entries) || !entries.length) throw new Error('项目缺少资料文件。');
  const members = new Map();
  for (const entry of entries) {
    if (!entry || !portablePath(entry.path) || members.has(entry.path.toLowerCase())) throw new Error('项目资料路径无效或重复。');
    const bytes = entry.bytes instanceof Uint8Array ? entry.bytes : entry.bytes instanceof ArrayBuffer ? new Uint8Array(entry.bytes) : null;
    if (!bytes) throw new Error(`项目文件内容无效：${entry.path}`);
    members.set(entry.path.toLowerCase(), bytes);
  }
  if (!members.has('bundle.json') || !members.has('bundle.sha256')) throw new Error('项目缺少 bundle.json 或 bundle.sha256。');
  return async path => {
    const bytes = members.get(path.toLowerCase());
    if (!bytes) throw new Error(`缺少文件：${path}`);
    return bytes;
  };
}

export async function openStoredDirectory(entries) {
  return loadBundle({ readFile: storedDirectory(entries) });
}

export async function openDirectory(files) {
  return loadBundle({ readFile: selectedDirectory(files) });
}

export async function readReport(file) {
  if (!file) return null;
  try { return JSON.parse(await file.text()); }
  catch { throw new Error(`${file.name} 不是可读取的 JSON 报告。`); }
}

export function checkReport(report, bundle, previousReport) {
  if (!report) return { ok: true, errors: [], warnings: [], mode: 'scene' };
  const result = validateReport(report, bundle, { previousReport });
  return { ...result, mode: result.ok ? 'report' : 'mismatch' };
}

// 资料/报告读取完成顺序不能覆盖用户最后一次选择。
export function generationGate() {
  let current = 0;
  return { next: () => ++current, isCurrent: token => token === current };
}
