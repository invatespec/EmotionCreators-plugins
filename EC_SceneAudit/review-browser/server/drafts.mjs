import { resolve } from 'node:path';
import { mkdir } from 'node:fs/promises';
import { readJsonFile, atomicWrite, removePath } from './atomic.mjs';
import { draftFile, DRAFT_KINDS } from './paths.mjs';

export { DRAFT_KINDS };

const JSON_KINDS = new Set(['annotations', 'repairs', 'layout', 'node-patch']);

// 草稿与项目不同：不做来源核验，核验仍由前端负责，这里只保证读写一致。
export async function readDraft(root, kind, id) {
  check(kind);
  const value = await readJsonFile(draftFile(root, kind, id));
  return value === null ? null : value;
}

export async function writeDraft(root, kind, id, value) {
  check(kind);
  if (value === undefined) throw new Error('草稿内容不能为空');
  const target = draftFile(root, kind, id);
  await mkdir(resolve(target, '..'), { recursive: true });
  await atomicWrite(target, JSON.stringify(value, null, 2));
  return true;
}

export async function deleteDraft(root, kind, id) {
  check(kind);
  await removePath(draftFile(root, kind, id));
  return true;
}

function check(kind) {
  if (!JSON_KINDS.has(kind)) throw new Error('不支持的草稿类型');
}
