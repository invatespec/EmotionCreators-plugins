import { validateShape } from './schema.mjs';
import { resolveEvidence } from './evidence.mjs';
import { validateGeometry } from './geometry.mjs';

export async function sha256(bytes) {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
}

export function portablePath(path) {
  return typeof path === 'string' && path.length > 0 && !/[:\\\u0000-\u001f]/u.test(path)
    && path.split('/').every(part => part !== '' && part !== '.' && part !== '..' && !/[. ]$/u.test(part));
}

const decode = (bytes, encoding = 'utf-8') => new TextDecoder(encoding, { fatal: true }).decode(bytes);

export async function loadBundle({ readFile, hash = sha256 }) {
  const errors = [], warnings = [];
  const result = { manifest: null, manifestSha256: null, files: new Map(), verified: false };
  const fail = (code, path, message) => errors.push({ code, path, message });
  try {
    const bytes = await readFile('bundle.json');
    result.manifestSha256 = await hash(bytes);
    const expected = decode(await readFile('bundle.sha256')).trim();
    if (expected !== result.manifestSha256) fail('manifest-hash', 'bundle.sha256', '清单校验值不匹配。');
    result.manifest = JSON.parse(decode(bytes));
    errors.push(...validateShape('bundle', result.manifest));
    if (errors.length) return { ok: false, errors, warnings, bundle: result };
      await loadMembers(result, readFile, hash, fail, warnings);
      checkManifest(result, fail, warnings);
      const geometry = validateGeometry(result.files.get('scene')?.json);
      result.geometry = geometry;
      if (!geometry.available && result.files.get('scene')?.json?.geometry !== undefined) {
        warnings.push({ code: 'geometry-invalid', path: '/geometry', message: geometry.reason });
      }
  } catch (error) {
    fail('bundle-read', '', error.message);
  }
  result.verified = errors.length === 0;
  return { ok: result.verified, errors, warnings, bundle: result };
}

async function loadMembers(bundle, readFile, hash, fail, warnings) {
  const paths = new Set();
  for (const meta of bundle.manifest.files) {
    const key = meta.path.toLowerCase();
    if (!portablePath(meta.path) || paths.has(key) || ['bundle.json', 'bundle.sha256'].includes(key)) {
      fail('member-path', meta.path, '成员路径不安全、重复或占用清单路径。');
      continue;
    }
    paths.add(key);
    if (meta.fileId === 'manifest' || bundle.files.has(meta.fileId)) {
      fail('file-id', meta.path, 'fileId 重复或使用保留值。');
      continue;
    }
    const member = { meta };
    bundle.files.set(meta.fileId, member);
    try {
      member.bytes = await readFile(meta.path);
      if (member.bytes.byteLength !== meta.byteLength || await hash(member.bytes) !== meta.sha256) {
        fail('member-hash', meta.path, '成员正文与清单不一致。');
        continue;
      }
      decodeMember(member, warnings);
    } catch (error) {
      fail('member-read', meta.path, error.message);
    }
  }
}

function decodeMember(member, warnings) {
  const { meta } = member;
  if (meta.role !== 'dependency') {
    member.text = decode(member.bytes);
    member.json = JSON.parse(member.text);
    return;
  }
  if (!portablePath(meta.sourcePath)) throw new Error('依赖来源必须为 UserData 内相对路径。');
  if (meta.encoding === null) {
    warnings.push({ code: 'encoding-unknown', path: meta.path, message: '正文已保存，编码未确认。' });
    return;
  }
  try {
    member.text = decode(member.bytes, meta.encoding);
  } catch (error) {
    warnings.push({ code: 'encoding-unsupported', path: meta.path, message: error.message });
  }
}

function checkManifest(bundle, fail, warnings) {
  const { manifest, files } = bundle;
  const scene = files.get('scene'), summary = files.get('summary');
  if (scene?.meta.role !== 'scene' || summary?.meta.role !== 'summary'
    || manifest.files.filter(f => f.role === 'scene').length !== 1
    || manifest.files.filter(f => f.role === 'summary').length !== 1) {
    fail('required-member', '/files', '需要唯一的 scene 与 summary 成员。');
    return;
  }
  if (scene.json?.schemaVersion !== '1.0' || !Array.isArray(scene.json?.nodes)
    || summary.json?.schemaVersion !== '1.0-ai' || !Array.isArray(summary.json?.parts)
    || !Array.isArray(summary.json?.scripts)) {
    fail('snapshot-shape', '/files', '不支持或不完整的快照结构。');
    return;
  }
  for (const data of [scene.json, summary.json]) {
    if (data.scene?.title !== manifest.scene.title || data.scene?.exportedAt !== manifest.scene.exportedAt) {
      fail('snapshot-source', '/scene', '清单、摘要与完整证据的 Scene 元数据不一致。');
    }
  }
  if (summary.json.sourceEvidenceFile !== scene.meta.path) {
    fail('summary-source', '/files/summary', '摘要未引用包内的完整证据。');
  }
  checkDependencies(bundle, fail, warnings);
  checkDependencyReachability(bundle, fail);
  checkSummaryReferences(bundle, fail);
}

function checkDependencies(bundle, fail, warnings) {
  for (const [i, dependency] of bundle.manifest.dependencies.entries()) {
    try { resolveEvidence(bundle, dependency.from); }
    catch (error) { fail('dependency-reference', '/dependencies/' + i, error.message); }
    if (dependency.status === 'included') {
      if (bundle.files.get(dependency.targetFileId)?.meta.role !== 'dependency') {
        fail('dependency-target', '/dependencies/' + i, '已收录依赖没有对应正文。');
      }
    } else {
      if (dependency.targetFileId !== null || !dependency.reason?.trim()) {
        fail('dependency-status', '/dependencies/' + i, '未收录依赖必须保留原因且没有正文 ID。');
      }
      warnings.push({ code: dependency.status, path: '/dependencies/' + i, message: dependency.reason });
    }
  }
  for (const [i, gap] of bundle.manifest.limitations.entries()) {
    if (gap.source.fileId !== 'manifest' && !bundle.files.has(gap.source.fileId)) {
      fail('limitation-reference', '/limitations/' + i, '资料缺口引用了不存在的文件。');
    }
    const member = bundle.files.get(gap.source.fileId);
    const unreadable = gap.source.kind === 'text-lines' && member && member.text === undefined;
    if (!unreadable) {
      try { resolveEvidence(bundle, gap.source); }
      catch (error) { fail('limitation-reference', '/limitations/' + i, error.message); }
    }
    warnings.push({ code: gap.code, path: '/limitations/' + i, message: gap.message });
  }
}

function checkDependencyReachability(bundle, fail) {
  const reached = new Set(['scene']);
  const queue = ['scene'];
  while (queue.length) {
    const source = queue.shift();
    for (const edge of bundle.manifest.dependencies) {
      if (edge.from.fileId !== source || edge.status !== 'included' || reached.has(edge.targetFileId)) continue;
      reached.add(edge.targetFileId);
      queue.push(edge.targetFileId);
    }
  }
  for (const file of bundle.manifest.files.filter(f => f.role === 'dependency')) {
    if (!reached.has(file.fileId)) fail('unreferenced-dependency', '/files', '依赖没有从当前 Scene 开始的引用链：' + file.fileId);
  }
}

function checkSummaryReferences(bundle, fail) {
  const summary = bundle.files.get('summary').json;
  for (const [name, entries] of [['parts', summary.parts], ['scripts', summary.scripts]]) {
    for (const [i, entry] of entries.entries()) {
      try {
        if (typeof entry.rawRef !== 'string' || !entry.rawRef.startsWith('#/')) throw new Error('缺少来源 rawRef。');
        resolveEvidence(bundle, { fileId: 'scene', kind: 'json-pointer', pointer: entry.rawRef.slice(1) });
      } catch (error) { fail('summary-reference', '/' + name + '/' + i, error.message); }
    }
  }
}
