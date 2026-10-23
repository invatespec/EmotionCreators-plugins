import { resolve, sep } from 'node:path';
import { readdir, mkdir, stat, readFile } from 'node:fs/promises';
import { commitFiles, atomicWrite, readJsonFile, removePath } from './atomic.mjs';
import { projectDirectory, registryDirectory, projectId } from './paths.mjs';
import { verifyBundle } from './verify.mjs';

const NAME_LIMIT = 120;
const MAX_PACKAGE_BYTES = 16 * 1024 * 1024;

export function projectName(value) {
  if (typeof value !== 'string' || !value.trim() || value.trim().length > NAME_LIMIT) throw new Error('项目名须为 1–120 字');
  return value.trim();
}

function memberEntries(files) {
  if (!Array.isArray(files) || !files.length) throw new Error('项目缺少资料文件。');
  const entries = [];
  for (const file of files) {
    const bytes = file?.bytes instanceof Uint8Array ? file.bytes : null;
    if (!bytes || typeof file?.path !== 'string') throw new Error(`项目文件内容无效：${file?.path ?? ''}`);
    entries.push({ path: file.path, bytes });
  }
  const total = entries.reduce((sum, entry) => sum + entry.bytes.byteLength, 0);
  if (total > MAX_PACKAGE_BYTES) throw new Error('资料包超过 16 MiB 上限');
  return entries;
}

// 整包字节只在写入前复核一次；失败即拒绝落盘，磁盘上的旧内容不动。
export async function verifyPackage(files) {
  const entries = memberEntries(files);
  const result = await verifyBundle(entries);
  if (!result.ok) throw new Error(`资料未通过核验：${result.errors.map(error => error.message).join('；')}`);
  return { entries, manifest: result.bundle.manifest, manifestSha256: result.bundle.manifestSha256 };
}

const readMeta = directory => readJsonFile(resolve(directory, 'project.json'));

// 摘要只带占用大小;project.json 里 files 只有 path,大小由 listProjects 量盘后传入。
function summaryOf(project, bytes) {
  // 旧版保存会丢掉 lineage；迁移记录里的来源仍可还原分组，避免旧数据继续显示为同名项目。
  const lineage = project.lineage ?? project.migration?.source?.projectId ?? project.id;
  return { id: project.id, name: project.name, updatedAt: project.updatedAt, source: project.source,
    lineage, previousId: project.previousId ?? null,
    bytes: bytes ?? 0, fileCount: Array.isArray(project.files) ? project.files.length : 0 };
}

function projectEntries(project, entries) {
  // project.json 排在最后：中断时旧元数据仍在，可拦住半份 files/。
  const meta = { ...project, files: entries.map(entry => ({ path: entry.path })) };
  return [...entries.map(entry => ({ target: `files/${entry.path}`, bytes: entry.bytes })),
    { target: 'project.json', text: JSON.stringify(meta, null, 2) }];
}

// 项目目录先写进临时目录再整体替换，避免残留上一次的成员文件。
async function stageProject(directory, project, entries, extras = []) {
  return commitFiles([...projectEntries(project, entries), ...extras].map(entry => ({ ...entry, target: resolve(directory, entry.target) })));
}

export async function listProjects(root) {
  const base = resolve(root, 'projects');
  let names;
  try { names = await readdir(base); } catch { return []; }
  const projects = [];
  for (const name of names) {
    const directory = resolve(base, name);
    try {
      const meta = await readMeta(directory);
      if (!meta || typeof meta.id !== 'string') continue;
      projects.push(summaryOf(meta, await directoryBytes(directory)));
    } catch { /* 单个项目损坏时不拖垮整个列表。 */ }
  }
  return projects.sort((a, b) => String(b.updatedAt).localeCompare(String(a.updatedAt)));
}

// 列表只报占用大小,不读整包正文:project.json 不带字节,直接量 files/ 的实际大小。
async function directoryBytes(directory) {
  let total = 0;
  try {
    for (const name of await readdir(resolve(directory, 'files'))) total += (await stat(resolve(directory, 'files', name))).size;
  } catch { /* 缺 files/ 时按 0 计。 */ }
  return total;
}

// project.json 只存路径,读回时按清单从 files/ 补字节;缺文件即视为项目损坏。
export async function readProject(root, id) {
  const directory = projectDirectory(root, id);
  const meta = await readMeta(directory);
  if (!meta || meta.id !== id) throw new Error('找不到该项目,可能已被删除');
  const files = [];
  for (const file of Array.isArray(meta.files) ? meta.files : []) files.push({ path: file.path, bytes: await readFile(resolve(directory, 'files', file.path)) });
  return { ...meta, files };
}

export async function saveProject(root, project) {
  if (!project || project.version !== 1 || typeof project.id !== 'string') throw new Error('项目资料格式无效');
  const name = projectName(project.name);
  // 落盘前必须复核字节:坏包一旦写入,后续只能靠 bundle.sha256 在读取侧报错。
  const { entries } = await verifyPackage(project.files);
  const directory = projectDirectory(root, project.id);
  const saved = { ...project, name };
  await stageProject(directory, saved, entries);
  return summaryOf(saved, entries.reduce((total, entry) => total + entry.bytes.byteLength, 0));
}

export async function renameProject(root, id, name) {
  const checked = projectName(name), directory = projectDirectory(root, id);
  const meta = await readMeta(directory);
  if (!meta || meta.id !== id) throw new Error('找不到该项目，可能已被删除');
  const updated = { ...meta, name: checked };
  await atomicWrite(resolve(directory, 'project.json'), JSON.stringify(updated, null, 2));
  return summaryOf(updated, await directoryBytes(directory));
}

export async function deleteProject(root, id) {
  const directory = projectDirectory(root, id);
  if (!(await readMeta(directory))) throw new Error('找不到该项目，可能已被删除');
  await removePath(directory);
}

// 迁移写入：新项目与来源项目必须一起生效，任一失败都不留半个结果。
export async function saveMigratedProject(root, project, sourceProject) {
  for (const item of [project, sourceProject]) {
    if (!item || item.version !== 1 || typeof item.id !== 'string') throw new Error('项目资料格式无效');
  }
  if (project.id === sourceProject.id) throw new Error('更新目标与来源相同');
  const target = { ...project, name: projectName(project.name) };
  // 迁移写入同样先复核两份整包,核验失败时磁盘上什么都不会变。
  const targetEntries = (await verifyPackage(project.files)).entries;
  const sourceEntries = (await verifyPackage(sourceProject.files)).entries;
  const sourceDirectory = projectDirectory(root, sourceProject.id);
  const targetDirectory = projectDirectory(root, target.id);
  if (await readMeta(targetDirectory)) throw new Error('目标资料已有项目，未覆盖现有内容');
  const existing = await readMeta(sourceDirectory);
  // 来源已存在时不重写它的文件，只把目标写进同一次提交。
  const writes = [
    ...projectEntries(target, targetEntries).map(entry => ({ ...entry, target: resolve(targetDirectory, entry.target) })),
    ...(existing ? [] : projectEntries(sourceProject, sourceEntries).map(entry => ({ ...entry, target: resolve(sourceDirectory, entry.target) })))
  ];
  await commitFiles(writes);
  return summaryOf(target, targetEntries.reduce((total, entry) => total + entry.bytes.byteLength, 0));
}

export async function listRegistry(root) {
  const base = resolve(root, 'registry');
  let names;
  try { names = await readdir(base); } catch { return []; }
  const bundles = [];
  for (const name of names) {
    try {
      const meta = await readJsonFile(resolve(base, name, 'registry.json'));
      if (meta && typeof meta.id === 'string') bundles.push(meta);
    } catch { /* 单个登记项损坏时不拖垮整个列表。 */ }
  }
  return bundles.sort((a, b) => String(b.registeredAt).localeCompare(String(a.registeredAt)));
}

// 登记沿用与项目相同的字节复核；失败不建立目录，也不会残留半份副本。
export async function registerBundle(root, files) {
  const entries = memberEntries(files);
  const result = await verifyBundle(entries);
  if (!result.ok) throw new Error(`资料未通过核验：${result.errors.map(error => error.message).join('；')}`);
  const bundleId = result.bundle.manifest?.bundleId;
  if (typeof bundleId !== 'string' || !bundleId) throw new Error('资料包缺少 bundleId，无法登记');
  const manifestSha256 = result.bundle.manifestSha256;
  const id = projectId({ bundleId, manifestSha256 });
  const directory = registryDirectory(root, id);
  const existing = await readJsonFile(resolve(directory, 'registry.json'));
  if (existing) return existing;
  await mkdir(directory, { recursive: true });
  // 先写 files/，registry.json 最后落盘：中断时列表看不到半份副本。
  const info = registryInfo(id, bundleId, manifestSha256, entries, sceneTitle(entries));
  await commitFiles([...entries.map(entry => ({ target: resolve(directory, 'files', entry.path), bytes: entry.bytes })),
    { target: resolve(directory, 'registry.json'), text: JSON.stringify(info, null, 2) }]);
  return info;
}

// 登记项只保存相对标识，不写机器绝对路径。
function registryInfo(id, bundleId, manifestSha256, entries, title) {
  return { id, bundleId, manifestSha256, title, name: title || bundleId,
    registeredAt: new Date().toISOString(),
    bytes: entries.reduce((total, entry) => total + entry.bytes.byteLength, 0) };
}

function sceneTitle(entries) {
  const scene = entries.find(entry => entry.path.toLowerCase() === 'scene.json');
  if (!scene) return '';
  try { return String(JSON.parse(new TextDecoder().decode(scene.bytes))?.scene?.title ?? ''); } catch { return ''; }
}

export async function readRegistry(root, id) {
  const directory = registryDirectory(root, id);
  const meta = await readJsonFile(resolve(directory, 'registry.json'));
  if (!meta || meta.id !== id) throw new Error('找不到该资料包，可能已被删除');
  return meta;
}

// 登记项读成与项目同形的字节列表。readdir recursive 会把目录本身也列出来,
// 必须先过滤掉,否则 readFile 会命中目录报 EISDIR。
export async function readRegistryFiles(root, id) {
  const directory = registryDirectory(root, id);
  const meta = await readJsonFile(resolve(directory, 'registry.json'));
  if (!meta || meta.id !== id) throw new Error('找不到该资料包,可能已被删除');
  const base = resolve(directory, 'files');
  const files = [];
  for (const item of await readdir(base, { recursive: true, withFileTypes: true })) {
    if (!item.isFile()) continue;
    const absolute = resolve(item.parentPath, item.name);
    const path = absolute.slice(base.length + 1).split(sep).join('/');
    files.push({ path, bytes: await readFile(absolute) });
  }
  return files;
}

export async function deleteRegistry(root, id) {
  const directory = registryDirectory(root, id);
  if (!(await readJsonFile(resolve(directory, 'registry.json')))) throw new Error('找不到该资料包，可能已被删除');
  await removePath(directory);
}
