import { prepare, parseArgs, createHandler, portInUse } from './http.mjs';
import { listProjects, readProject, saveProject, renameProject, deleteProject, saveMigratedProject,
  listRegistry, registerBundle, readRegistry, readRegistryFiles, deleteRegistry } from './store.mjs';
import { readDraft, writeDraft, deleteDraft } from './drafts.mjs';

const id = String.raw`(?<id>.+)`;
const kind = String.raw`(?<kind>[^/]+)`;

const routes = [
  { method: 'GET', pattern: /^\/api\/session$/u, run: async ({ root }) => ({ payload: { storage: { root, ok: true, reason: '' }, registry: await listRegistry(root) } }) },
  { method: 'GET', pattern: /^\/api\/projects$/u, run: async ({ root }) => ({ payload: { projects: await listProjects(root) } }) },
  { method: 'GET', pattern: new RegExp(`^/api/projects/${id}$`, 'u'), run: async ({ root, params }) => ({ payload: { project: await readProject(root, decodeId(params.id)) } }) },
  { method: 'PUT', pattern: new RegExp(`^/api/projects/${id}$`, 'u'), run: async ({ root, params, body }) => ({ payload: { summary: await saveProject(root, { ...body.project, id: decodeId(params.id) }) } }) },
  { method: 'PATCH', pattern: new RegExp(`^/api/projects/${id}$`, 'u'), run: async ({ root, params, body }) => ({ payload: { summary: await renameProject(root, decodeId(params.id), body.name) } }) },
  { method: 'DELETE', pattern: new RegExp(`^/api/projects/${id}$`, 'u'), run: async ({ root, params }) => { await deleteProject(root, decodeId(params.id)); return { payload: { ok: true } }; } },
  { method: 'POST', pattern: /^\/api\/projects\/migrate$/u, run: async ({ root, body }) => ({ payload: { summary: await saveMigratedProject(root, body.project, body.source) } }) },
  { method: 'GET', pattern: /^\/api\/registry$/u, run: async ({ root }) => ({ payload: { bundles: await listRegistry(root) } }) },
  { method: 'POST', pattern: /^\/api\/registry$/u, run: async ({ root, body }) => ({ payload: { bundle: await registerBundle(root, decodeFiles(body.files)) } }) },
  // 必须排在 :id 之前,否则 .+ 会把 xxx/files 整个当成 id。
  { method: 'GET', pattern: new RegExp(`^/api/registry/${id}/files$`, 'u'), run: async ({ root, params }) => ({ payload: { files: await readRegistryFiles(root, decodeId(params.id)) } }) },
  { method: 'GET', pattern: new RegExp(`^/api/registry/${id}$`, 'u'), run: async ({ root, params }) => ({ payload: { bundle: await readRegistry(root, decodeId(params.id)) } }) },
  { method: 'DELETE', pattern: new RegExp(`^/api/registry/${id}$`, 'u'), run: async ({ root, params }) => { await deleteRegistry(root, decodeId(params.id)); return { payload: { ok: true } }; } },
  { method: 'GET', pattern: new RegExp(`^/api/drafts/${kind}/${id}$`, 'u'), run: async ({ root, params }) => ({ payload: { value: await readDraft(root, params.kind, decodeId(params.id)) } }) },
  { method: 'PUT', pattern: new RegExp(`^/api/drafts/${kind}/${id}$`, 'u'), run: async ({ root, params, body }) => { await writeDraft(root, params.kind, decodeId(params.id), body.value); return { payload: { ok: true } }; } },
  { method: 'DELETE', pattern: new RegExp(`^/api/drafts/${kind}/${id}$`, 'u'), run: async ({ root, params }) => { await deleteDraft(root, params.kind, decodeId(params.id)); return { payload: { ok: true } }; } }
];

const decodeId = value => decodeURIComponent(value);

function decodeFiles(files) {
  if (!Array.isArray(files) || !files.length) throw new Error('资料包内容为空');
  return files.map(file => {
    if (!(file?.bytes instanceof Uint8Array)) throw new Error(`文件 ${file?.path ?? ''} 内容无效`);
    return { path: file.path, bytes: file.bytes };
  });
}

let options;
try { options = parseArgs(process.argv.slice(2)); }
catch (error) { console.error(`参数错误：${error.message}`); process.exit(2); }

let ready;
try { ready = await prepare(options); }
catch (error) { console.error(`启动失败：${message(error)}`); process.exit(1); }

const handler = createHandler(ready.root, routes);
const { config } = ready;

let server;
try { server = Bun.serve({ port: config.port, hostname: '127.0.0.1', development: false, fetch: handler }); }
catch (error) {
  const busy = portInUse(error, config.port);
  if (!busy) { console.error(`启动失败：${message(error)}`); process.exit(1); }
  console.error(busy.message); process.exit(1);
}

console.log(`Scene 审查：http://127.0.0.1:${server.port}`);
console.log(`数据目录：${ready.root}`);
console.log(`配置文件：${config.file}`);
console.log('按 Ctrl+C 停止服务。');
