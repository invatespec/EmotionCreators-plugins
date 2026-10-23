import { createHash } from 'node:crypto';

// 与 review-core/bundle.mjs 的便携路径规则同源：只做哈希与清单一致性复核。
const UNPORTABLE = /[:\\\u0000-\u001f]/u;

export function portablePath(path) {
  return typeof path === 'string' && path.length > 0 && !UNPORTABLE.test(path)
    && path.split('/').every(part => part !== '' && part !== '.' && part !== '..' && !/[. ]$/u.test(part));
}

export function sha256(bytes) { return createHash('sha256').update(bytes).digest('hex'); }

export function decodeText(bytes) {
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
  catch { return null; }
}

// 服务端只核验「字节与清单声明一致」，JSON Schema 形状校验仍由前端 loadBundle 承担。
export async function verifyBundle(entries) {
  const errors = [], warnings = [], members = new Map();
  const fail = (code, path, message) => errors.push({ code, path, message });
  const seen = new Set();
  let manifestBytes = null, manifest = null, manifestSha256 = null, expected = null;
  for (const entry of entries) {
    const key = String(entry.path || '').toLowerCase();
    if (!portablePath(entry.path) || seen.has(key)) { fail('member-path', String(entry.path || ''), '成员路径不安全或重复。'); continue; }
    if (key === 'bundle.json') manifestBytes = entry.bytes;
    else if (key === 'bundle.sha256') expected = entry.bytes;
    seen.add(key);
    members.set(key, entry);
  }
  if (!manifestBytes || !expected) {
    fail('bundle-missing', '', '资料包缺少 bundle.json 或 bundle.sha256。');
    return { ok: false, errors, warnings, bundle: null };
  }
  manifestSha256 = sha256(manifestBytes);
  const declared = decodeText(expected);
  if (declared === null || declared.trim().toLowerCase() !== manifestSha256) fail('manifest-hash', 'bundle.sha256', '清单校验值不匹配。');
  try { manifest = JSON.parse(decodeText(manifestBytes) ?? ''); }
  catch { fail('bundle-read', 'bundle.json', 'bundle.json 不是可读取的 UTF-8 JSON。'); }
  if (manifest) {
    const files = Array.isArray(manifest.files) ? manifest.files : [];
    const declaredPaths = new Set(), fileIds = new Set();
    for (const meta of files) {
      const path = String(meta?.path || ''), pathKey = path.toLowerCase();
      const reserved = pathKey === 'bundle.json' || pathKey === 'bundle.sha256';
      if (!portablePath(path) || reserved || declaredPaths.has(pathKey)) { fail('member-path', path, '成员路径不安全、重复或占用清单路径。'); continue; }
      // fileId 是清单内部一致性约束（与 review-core 的 file-id 检查对齐），不属于 schema 形状校验。
      const fileId = meta?.fileId;
      if (fileId === 'manifest' || fileIds.has(fileId)) { fail('file-id', path, 'fileId 重复或使用保留值。'); continue; }
      fileIds.add(fileId);
      declaredPaths.add(pathKey);
      const member = members.get(pathKey);
      if (!member) { fail('member-missing', path, '缺少清单声明的成员文件。'); continue; }
      if (member.bytes.byteLength !== meta.byteLength) { fail('member-hash', path, '成员长度与清单不一致。'); continue; }
      if (sha256(member.bytes) !== meta.sha256) fail('member-hash', path, '成员正文与清单不一致。');
    }
    for (const key of members.keys()) if (key !== 'bundle.json' && key !== 'bundle.sha256' && !declaredPaths.has(key))
      warnings.push({ code: 'unlisted-member', path: key, message: '资料包包含清单未声明的文件，已忽略。' });
  }
  return { ok: errors.length === 0, errors, warnings,
    bundle: { verified: errors.length === 0, manifest, manifestSha256, files: members } };
}
