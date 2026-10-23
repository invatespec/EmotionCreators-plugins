// 旧静态预览入口:只服务 dist/ 三份资源,没有 /api/,前端在此无法工作。
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, extname } from 'node:path';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../dist');
const port = Number(process.env.SCENE_REVIEW_PORT || 18762);
const allowed = new Set(['/index.html', '/app.js', '/app.css']);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };
const server = createServer(async (request, response) => {
  const path = new URL(request.url, 'http://127.0.0.1').pathname;
  const file = path === '/' ? '/index.html' : path;
  if (request.method !== 'GET' && request.method !== 'HEAD') { response.writeHead(405).end(); return; }
  if (!allowed.has(file)) { response.writeHead(404).end(); return; }
  try {
    const body = await readFile(resolve(root, file.slice(1)));
    response.writeHead(200, { 'Content-Type': types[extname(file)], 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    response.end(request.method === 'HEAD' ? undefined : body);
  } catch { response.writeHead(503, { 'Content-Type': 'text/plain; charset=utf-8' }).end('请先运行 npm run build'); }
});
server.on('error', error => { console.error(error.message); process.exitCode = 1; });
server.listen(port, '127.0.0.1', () => console.log(`Scene 审查：http://127.0.0.1:${port}`));
