// 构建性能入口：只产出 prototype/node-pool/perf-bundle.*，不触碰 dist。
import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
await build({
  entryPoints: [resolve(root, 'prototype/node-pool/perf-app.js')],
  outfile: resolve(root, 'prototype/node-pool/perf-bundle.js'),
  bundle: true, format: 'esm', platform: 'browser', target: 'es2022',
  nodePaths: [resolve(root, 'node_modules')], logLevel: 'warning'
});
console.log('性能夹具已构建：prototype/node-pool/perf-bundle.js / perf-bundle.css');
