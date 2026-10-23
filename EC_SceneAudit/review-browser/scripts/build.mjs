import { build } from 'esbuild';
import { mkdir, copyFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
await mkdir(resolve(root, 'dist'), { recursive: true });
await build({ entryPoints: [resolve(root, 'src/app.js')], outfile: resolve(root, 'dist/app.js'), bundle: true,
  format: 'esm', platform: 'browser', target: 'es2022', nodePaths: [resolve(root, 'node_modules')], logLevel: 'warning' });
await copyFile(resolve(root, 'index.html'), resolve(root, 'dist/index.html'));
if (process.argv.includes('--prototype')) {
  const prototype = resolve(root, 'prototype/node-pool');
  await build({ entryPoints: [resolve(prototype, 'app.js')], outfile: resolve(prototype, 'demo-bundle.js'), bundle: true, format: 'esm', target: 'es2022' });
  await copyFile(resolve(root, 'src/ui/styles.css'), resolve(prototype, 'styles.css'));
}
console.log('静态网页已构建到 review-browser/dist');

// 打包单文件可执行程序:dist/ 三个资源经 assets.mjs 的 import 内嵌进 exe,发布目录只要这一个文件。
if (process.argv.includes('--exe')) {
  const outfile = resolve(root, 'release/scene-review.exe');
  await mkdir(dirname(outfile), { recursive: true });
  await compile(outfile);
  console.log(`可执行文件已生成:${outfile}`);
}

function compile(outfile) {
  return new Promise((accept, reject) => {
    const child = spawn('bun', ['build', '--compile', '--outfile', outfile, resolve(root, 'server/main.mjs')], { stdio: 'inherit' });
    child.on('error', error => reject(error.code === 'ENOENT' ? new Error('未找到 bun 命令,打包需要 Bun 1.3 或更新版本') : error));
    child.on('exit', code => code === 0 ? accept() : reject(new Error(`bun build --compile 退出码 ${code}`)));
  });
}