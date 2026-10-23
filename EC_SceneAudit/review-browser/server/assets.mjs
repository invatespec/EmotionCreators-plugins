import { extname } from 'node:path';

// 静态资源读取:开发模式与打包模式共用一份代码。
// 打包后 import.meta.url 指向内嵌虚拟路径,因此只能靠 Bun.file(import 结果) 读字节;
// fetch 对内嵌虚拟路径会报 ConnectionRefused,不能用来代替。
const SOURCES = {
  '/index.html': () => import('../dist/index.html', { with: { type: 'file' } }),
  '/app.js': () => import('../dist/app.js', { with: { type: 'file' } }),
  '/app.css': () => import('../dist/app.css', { with: { type: 'file' } })
};

export const ASSETS = new Set(Object.keys(SOURCES));

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };

// 三个资源都在 http.mjs 的抛错分支里被拦截,这里只按白名单取字节。
export async function readAsset(pathname) {
  const { default: url } = await SOURCES[pathname]();
  return Bun.file(url).arrayBuffer();
}

export function assetResponse(pathname, body, headOnly) {
  return new Response(headOnly ? null : body, {
    status: 200,
    headers: { 'Content-Type': TYPES[extname(pathname)], 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }
  });
}

export function assetMissingResponse() {
  return new Response('网页资源不可用,请重新构建程序,或在开发模式下先运行 npm run build。', { status: 503, headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
}