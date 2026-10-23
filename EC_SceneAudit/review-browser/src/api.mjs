// 前端唯一的服务端入口:所有持久化与登记都走 /api/。
const BASE = '/api';
const JSON_HEADERS = { 'Content-Type': 'application/json' };

async function request(path, { method = 'GET', body } = {}) {
  let response;
  try {
    response = await fetch(BASE + path, { method, headers: body === undefined ? undefined : JSON_HEADERS, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new Error('无法连接本机服务,请确认审查程序仍在运行');
  }
  const text = await response.text();
  let payload = null;
  try { payload = text ? JSON.parse(text) : null; } catch { payload = null; }
  if (!response.ok) throw new Error(payload?.error?.message || `服务返回错误(${response.status})`);
  return payload ?? {};
}

// 草稿与项目共用 bundleId + manifestSha256 隔离,键形态与磁盘目录映射解耦。
export function identityKey(source) { return JSON.stringify([source.bundleId, source.manifestSha256]); }

function bytesToBase64(value) {
  const bytes = toBytes(value);
  if (!(bytes instanceof Uint8Array)) throw new Error('文件内容无效');
  let text = '';
  // 一次转整个大数组会超出参数上限,按块拼。
  for (let offset = 0; offset < bytes.length; offset += 0x8000) text += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return btoa(text);
}

function encodeFiles(files) {
  // 服务端只解码名为 bytes 的字段,编码侧必须同名。
  return files.map(file => ({ ...file, bytes: bytesToBase64(file.bytes) }));
}

// 服务端把 Uint8Array 直接 JSON 序列化,读回时是 {0:..,1:..} 的索引对象;
// 这里统一还原成 Uint8Array,否则 storedDirectory 的 instanceof 判断会失败。
function decodeFiles(files) {
  if (!Array.isArray(files)) return files;
  return files.map(file => ({ ...file, bytes: toBytes(file?.bytes) }));
}

function toBytes(value) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  // Bun 把 Node Buffer 序列化为 {type:'Buffer',data:[...]};普通 Uint8Array 则是索引键对象。
  if (value && typeof value === 'object') {
    if (value.type === 'Buffer' && Array.isArray(value.data)) return Uint8Array.from(value.data);
    const entries = Object.values(value);
    if (entries.every(entry => Number.isInteger(entry))) return Uint8Array.from(entries);
  }
  return value;
}

export function session() { return request('/session'); }

export async function listProjects() {
  const payload = await request('/projects');
  return payload.projects ?? [];
}

export async function readProject(id) {
  const payload = await request(`/projects/${encodeURIComponent(id)}`);
  return payload.project ? { ...payload.project, files: decodeFiles(payload.project.files) } : null;
}

export function saveProject(project) {
  return request(`/projects/${encodeURIComponent(project.id)}`, { method: 'PUT', body: { project: { ...project, files: encodeFiles(project.files) } } });
}

export function renameProject(id, name) {
  return request(`/projects/${encodeURIComponent(id)}`, { method: 'PATCH', body: { name } });
}

export function deleteProject(id) {
  return request(`/projects/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

export function saveMigratedProject(project, sourceProject) {
  return request('/projects/migrate', { method: 'POST', body: {
    project: { ...project, files: encodeFiles(project.files) },
    source: { ...sourceProject, files: encodeFiles(sourceProject.files) } } });
}

export async function listRegistry() {
  const payload = await request('/registry');
  return payload.bundles ?? [];
}

export async function readRegistry(id) {
  const payload = await request(`/registry/${encodeURIComponent(id)}`);
  return payload.bundle ?? null;
}

export function registerBundle(files) {
  return request('/registry', { method: 'POST', body: { files: encodeFiles(files) } });
}

export function deleteRegistry(id) {
  return request(`/registry/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

// 登记项字节与项目同形,调用方直接交给 storedDirectory 核验。
export async function readRegistryFiles(id) {
  const payload = await request(`/registry/${encodeURIComponent(id)}/files`);
  return decodeFiles(payload.files ?? []);
}

export async function readDraft(kind, id) {
  const payload = await request(`/drafts/${kind}/${encodeURIComponent(id)}`);
  return payload.value ?? null;
}

export function writeDraft(kind, id, value) {
  return request(`/drafts/${kind}/${encodeURIComponent(id)}`, { method: 'PUT', body: { value } });
}

export function deleteDraft(kind, id) {
  return request(`/drafts/${kind}/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

// 草稿读取全部前置到 mountView 之前,避免视图挂载后再异步补数据。
// 读取失败(文件损坏等)单独记下 kind,调用方据此给警告,不能当成「没有草稿」而覆盖原文件。
export async function readDrafts(id, kinds) {
  const values = {}, failed = [];
  await Promise.all(kinds.map(async kind => {
    try { values[kind] = await readDraft(kind, id); }
    catch { values[kind] = null; failed.push(kind); }
  }));
  return { values, failed };
}

// 保存失败只影响当前草稿,不该阻断阅读,因此批量写用 allSettled 收集结果。
export function writeDrafts(id, values) {
  return Promise.allSettled(Object.entries(values).map(([kind, value]) => writeDraft(kind, id, value)));
}
