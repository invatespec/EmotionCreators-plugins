import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, resolve, isAbsolute } from 'node:path';

// 数据根与默认 config.json 的落点。打包后进程就是 exe,必须用 execPath 定位(从快捷方式启动时工作目录会变);开发模式退回启动目录,避免把 config.json / data/ 写进 server/。
const PACKAGED = Boolean(process.execPath) && !/(?:^|[\\/])(?:node|bun)\.exe$/iu.test(process.execPath);
export const ENTRY_DIR = PACKAGED ? dirname(process.execPath) : process.cwd();
export const DEFAULT_PORT = 18762;
export const CONFIG_FILE = 'config.json';
export const CONFIG_README = 'config.README.txt';
export const DRAFT_KINDS = ['annotations', 'repairs', 'layout', 'node-patch'];
const README_TEXT = `Scene 审查本机服务配置说明
================================

config.json 字段（JSON 不支持注释，改动前请先备份）：

  dataRoot  数据根目录。相对路径以本可执行文件所在目录为基准；
            默认 "data"。首次运行会自动创建，删掉后重启会重新生成。
  port      HTTP 监听端口，只绑定 127.0.0.1。默认 ${DEFAULT_PORT}。

优先级：命令行参数（--port / --data-root / --config）> 本文件 > 内置默认值。
改完保存后重启程序生效，无需重新编译。
`;

function number(value, label) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > 65535) throw new Error(`${label}须为 1–65535 的整数`);
  return parsed;
}

function text(value, label) {
  if (typeof value !== 'string' || !value.trim()) throw new Error(`${label}不能为空`);
  return value.trim();
}

// 配置文件损坏时只提示并退回默认值，不静默使用半份配置。
export async function loadConfig(configPath) {
  const file = resolve(configPath);
  const warnings = [];
  let raw = null;
  try { raw = await readFile(file, 'utf8'); } catch { warnings.push(`未找到配置文件，已按默认值运行，并会在 ${file} 生成默认配置。`); }
  const config = { dataRoot: 'data', port: DEFAULT_PORT, file, created: false };
  if (raw !== null) {
    try {
      const parsed = JSON.parse(raw);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('配置须为对象');
      if (parsed.dataRoot !== undefined) config.dataRoot = text(parsed.dataRoot, 'dataRoot');
      if (parsed.port !== undefined) config.port = number(parsed.port, 'port');
    } catch (error) { warnings.push(`配置文件无法读取（${error.message}），已按默认值运行。`); }
  }
  return { config, warnings };
}

export async function ensureConfigFiles(config) {
  await mkdir(dirname(config.file), { recursive: true });
  await writeFile(config.file, JSON.stringify({ dataRoot: config.dataRoot, port: config.port }, null, 2) + '\n', 'utf8');
  await writeFile(resolve(dirname(config.file), CONFIG_README), README_TEXT, 'utf8');
  config.created = true;
}

// 相对数据根以可执行文件所在目录为基准,而不是进程工作目录:
// 从快捷方式、任务栏或其它 cwd 启动时,数据仍应留在 exe 同级,与双击行为一致。
export function resolveDataRoot(config) {
  const target = isAbsolute(config.dataRoot) ? config.dataRoot : resolve(ENTRY_DIR, config.dataRoot);
  return resolve(target);
}

export function projectId(source) { return JSON.stringify([source.bundleId, source.manifestSha256]); }

// 项目 id 含 [ " : 等字符，不能直接当目录名；用稳定哈希做一层映射。
export function directoryName(bundleId, manifestSha256) {
  return createHash('sha256').update(`${bundleId}\n${manifestSha256}`, 'utf8').digest('hex').slice(0, 32);
}

export function directoryNameForId(id) {
  let source;
  try { source = JSON.parse(id); } catch { throw new Error('项目标识无效'); }
  if (!Array.isArray(source) || source.length !== 2 || typeof source[0] !== 'string' || typeof source[1] !== 'string')
    throw new Error('项目标识无效');
  return directoryName(source[0], source[1]);
}

export function projectDirectory(root, id) { return resolve(root, 'projects', directoryNameForId(id)); }
export function registryDirectory(root, id) { return resolve(root, 'registry', directoryNameForId(id)); }
export function draftFile(root, kind, id) {
  if (!DRAFT_KINDS.includes(kind)) throw new Error('不支持的草稿类型');
  return resolve(root, 'drafts', kind, `${directoryNameForId(id)}.json`);
}

export async function ensureDataRoot(root) {
  await mkdir(resolve(root, 'projects'), { recursive: true });
  await mkdir(resolve(root, 'registry'), { recursive: true });
  for (const kind of DRAFT_KINDS) await mkdir(resolve(root, 'drafts', kind), { recursive: true });
}
