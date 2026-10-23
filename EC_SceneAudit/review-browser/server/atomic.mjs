import { open, mkdir, rename, rm, stat, readFile } from 'node:fs/promises';
import { dirname, isAbsolute } from 'node:path';

// 只接受绝对路径:相对路径会被解析到进程工作目录,把数据写到意料之外的位置。
function absoluteTarget(target) {
  if (!isAbsolute(target)) throw new Error(`写入目标必须是绝对路径:${target}`);
  return target;
}

// 临时名放在同目录，保证 rename 与目标同卷、在 NTFS 上是原子替换。
export function tempName(target) {
  return `${target}.tmp-${process.pid}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export async function atomicWrite(target, bytes) {
  const file = absoluteTarget(target);
  const temp = tempName(file);
  await mkdir(dirname(file), { recursive: true });
  const handle = await open(temp, 'w');
  try {
    await handle.writeFile(bytes);
    await handle.sync();
  } finally { await handle.close(); }
  await rename(temp, file);
}

export async function pathExists(target) {
  try { await stat(target); return true; } catch { return false; }
}

export async function removePath(target) { await rm(target, { recursive: true, force: true }); }

// 多文件事务：先写全部临时文件，再逐个 rename；rename 失败时用备份回滚已替换的目标。
export async function commitFiles(entries) {
  const planned = entries.map(entry => ({ ...entry, target: absoluteTarget(entry.target) }));
  const staged = [];
  const backups = [];
  try {
    for (const entry of planned) {
      const temp = tempName(entry.target);
      await mkdir(dirname(entry.target), { recursive: true });
      const handle = await open(temp, 'w');
      try {
        await handle.writeFile(entry.bytes ?? Buffer.from(entry.text ?? '', 'utf8'));
        await handle.sync();
      } finally { await handle.close(); }
      staged.push({ temp, target: entry.target });
    }
  } catch (error) {
    await Promise.all(staged.map(item => rm(item.temp, { force: true })));
    throw error;
  }
  try {
    for (const item of staged) {
      const existed = await pathExists(item.target);
      if (existed) {
        const backup = tempName(item.target);
        await rename(item.target, backup);
        backups.push({ backup, target: item.target });
      } else backups.push({ backup: null, target: item.target });
      await rename(item.temp, item.target);
    }
  } catch (error) {
    for (const item of [...backups].reverse()) {
      await rm(item.target, { force: true });
      if (item.backup) await rename(item.backup, item.target);
    }
    await Promise.all(staged.map(item => rm(item.temp, { force: true })));
    throw new Error(`写入未完成，已恢复原内容：${error.message}`);
  } finally {
    for (const item of backups) if (item.backup) await rm(item.backup, { force: true });
  }
}

export async function readJsonFile(target) {
  try { return JSON.parse(await readFile(target, 'utf8')); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}

export function message(error) {
  if (!error) return '未知错误';
  if (error.code === 'ENOSPC') return '磁盘空间不足';
  if (error.code === 'EACCES' || error.code === 'EPERM') return '没有写入权限';
  return error.message || String(error);
}
