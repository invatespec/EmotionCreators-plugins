import { resolve } from 'node:path';
import { loadConfig, resolveDataRoot, ensureDataRoot, ensureConfigFiles, ENTRY_DIR, CONFIG_FILE, DEFAULT_PORT } from './paths.mjs';
import { message } from './atomic.mjs';
import { readAsset, assetResponse, assetMissingResponse, ASSETS } from './assets.mjs';

const BODY_LIMIT = 32 * 1024 * 1024;

export function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === '--port' || arg === '--data-root' || arg === '--config') {
      const value = argv[++index];
      if (value === undefined || value.startsWith('--')) throw new Error(`${arg} 需要一个值`);
      options[arg.slice(2) === 'data-root' ? 'dataRoot' : arg.slice(2)] = value;
    } else if (arg.startsWith('--')) throw new Error(`无法识别的参数：${arg}`);
  }
  if (options.port !== undefined) {
    const port = Number(options.port);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('端口须为 1–65535 的整数');
    options.port = port;
  }
  return options;
}

export async function prepare(options = {}) {
  const configPath = options.config ?? resolve(ENTRY_DIR, CONFIG_FILE);
  const { config, warnings } = await loadConfig(configPath);
  if (options.port !== undefined) config.port = options.port;
  if (options.dataRoot !== undefined) config.dataRoot = options.dataRoot;
  for (const warning of warnings) console.log(`提示：${warning}`);
  const root = resolveDataRoot(config);
  await ensureConfigFiles(config);
  await ensureDataRoot(root);
  return { config, root };
}

// 路由是「一个 pattern + 一个 method」；root 只在这里注入，路由表本身不持有它。
export function createHandler(root, routes) {
  const table = routes.map(item => ({ pattern: item.pattern, method: item.method, run: item.run }));
  return async function handle(request) {
    const url = new URL(request.url);
    if (request.method === 'GET' || request.method === 'HEAD') {
      const path = url.pathname === '/' ? '/index.html' : url.pathname;
      if (ASSETS.has(path)) return await serveAsset(path, request.method === 'HEAD');
    }
    // 只接受本机回环来源，避免同机其它页面对本服务发起跨站请求。
    if (!isLoopback(request)) return json({ error: { code: 'origin', message: '只接受来自本机的请求' } }, 403);
    return route(request, url, root, table);
  };
}

function isLoopback(request) {
  const origin = request.headers.get('origin');
  if (!origin || origin === 'null') return true;
  try {
    const host = new URL(origin).hostname;
    return host === '127.0.0.1' || host === 'localhost' || host === '[::1]';
  } catch { return false; }
}

async function route(request, url, root, table) {
  if (!url.pathname.startsWith('/api/')) return json({ error: { code: 'not-found', message: '没有这个接口' } }, 404);
  const method = request.method === 'HEAD' ? 'GET' : request.method;
  for (const item of table) {
    const match = item.pattern.exec(url.pathname);
    if (!match || item.method !== method) continue;
    let body = null;
    if (method === 'PUT' || method === 'POST' || method === 'PATCH') {
      try { body = await readBody(request); }
      catch (error) { return json({ error: { code: 'body', message: error.message } }, 400); }
    }
    try {
      const result = await item.run({ params: match.groups ?? {}, body, url, request, root });
      if (result === undefined) return new Response(null, { status: 204 });
      return json(result.payload, result.status ?? 200);
    } catch (error) {
      return json({ error: { code: error.code ?? 'failed', message: message(error) } }, error.status ?? 400);
    }
  }
  return json({ error: { code: 'not-found', message: '没有这个接口' } }, 404);
}

async function readBody(request) {
  const declared = Number(request.headers.get('content-length') ?? 0);
  if (declared > BODY_LIMIT) throw new Error('请求内容超过 32 MiB 上限');
  const text = await request.text();
  if (Buffer.byteLength(text, 'utf8') > BODY_LIMIT) throw new Error('请求内容超过 32 MiB 上限');
  if (!text) return {};
  let parsed;
  try { parsed = JSON.parse(text); } catch { throw new Error('请求内容不是可读取的 JSON'); }
  return decodeBuffers(parsed);
}

// 前端把文件正文编成 base64 字符串，服务端需要 Uint8Array。
// 只认项目/资料包载荷里的 files[].bytes，别处同名字段保持原样，避免误解码。
function decodeBuffers(value, key = '') {
  if (Array.isArray(value)) return value.map(item => decodeBuffers(item, key));
  if (!value || typeof value !== 'object') return key === 'bytes' && typeof value === 'string' ? toBytes(value) : value;
  const result = {};
  for (const [childKey, item] of Object.entries(value)) result[childKey] = decodeBuffers(item, childKey);
  return result;
}

function toBytes(value) {
  const buffer = Buffer.from(value, 'base64');
  if (!buffer.byteLength && value) throw new Error('文件内容不是有效的 base64');
  return new Uint8Array(buffer);
}

async function serveAsset(pathname, headOnly) {
  try { return assetResponse(pathname, await readAsset(pathname), headOnly); }
  catch { return assetMissingResponse(); }
}

export function json(payload, status = 200) {
  return new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' } });
}

// 端口被占用时给出可用端口，让玩家不必重新编译就能换端口。
export function portInUse(error, port) {
  const busy = error?.code === 'EADDRINUSE' || /in use|被占用/i.test(error?.message ?? '');
  if (!busy) return null;
  return new Error(`端口 ${port} 已被占用。请改用 --port ${port + 1}，或修改 config.json 的 port 后重试。`);
}

export { DEFAULT_PORT };
