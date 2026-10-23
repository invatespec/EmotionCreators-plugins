export function resolvePointer(document, pointer) {
  if (pointer === '') return document;
  if (!pointer.startsWith('/') || /~(?:[^01]|$)/u.test(pointer)) {
    throw new Error('需要标准 JSON Pointer，不能包含 rawRef 动作后缀。');
  }
  return pointer.slice(1).split('/').reduce((value, token) => {
    const key = token.replaceAll('~1', '/').replaceAll('~0', '~');
    if (Array.isArray(value) && !/^(0|[1-9][0-9]*)$/u.test(key)) throw new Error('数组下标无效。');
    if (value === null || typeof value !== 'object' || !Object.hasOwn(value, key)) {
      throw new Error('引用路径不存在：' + pointer);
    }
    return value[key];
  }, document);
}

export function resolveEvidence(bundle, reference) {
  const member = reference.fileId === 'manifest'
    ? { json: bundle.manifest, meta: { role: 'manifest' } }
    : bundle.files.get(reference.fileId);
  if (!member) throw new Error('不存在的 fileId：' + reference.fileId);
  if (reference.kind === 'json-pointer') {
    if (!member.json) throw new Error('此文件没有可解析的 JSON。');
    return resolvePointer(member.json, reference.pointer);
  }
  if (reference.kind !== 'text-lines' || typeof member.text !== 'string') {
    throw new Error('此文件没有已确认编码的文本。');
  }
  const lines = member.text.split(/\r\n|\r|\n/u);
  if (!Number.isInteger(reference.startLine) || !Number.isInteger(reference.endLine)
    || reference.startLine < 1 || reference.endLine < reference.startLine || reference.endLine > lines.length) {
    throw new Error('文本行号越界或倒置。');
  }
  return lines.slice(reference.startLine - 1, reference.endLine).join('\n');
}

export function legacyReference(rawRef, fileId = 'scene') {
  const match = rawRef.match(/\/(?:command:[^/]+|reference)@[0-9]+$/u);
  const pointer = match ? rawRef.slice(0, -match[0].length) : rawRef;
  if (!pointer.startsWith('#')) throw new Error('旧 rawRef 缺少 #。');
  return {
    reference: { fileId, kind: 'json-pointer', pointer: decodeURIComponent(pointer.slice(1)) },
    action: match ? match[0].slice(1) : null,
  };
}

export function evidenceText(value) {
  if (typeof value === 'string') return value;
  return value?.raw ?? value?.rawText ?? JSON.stringify(value);
}

export function matchesEvidenceQuote(value, quote) {
  const text = evidenceText(value);
  if (typeof text !== 'string' || typeof quote !== 'string') return false;
  if (typeof value === 'string' || typeof value?.raw === 'string' || typeof value?.rawText === 'string') {
    const normalize = input => input.replace(/\r\n|\r/gu, '\n');
    return normalize(text).includes(normalize(quote));
  }
  const fragment = jsonTokens(quote);
  if (!fragment?.length) return text.includes(quote);
  const source = jsonTokens(text);
  // 按完整词法单元比较，不能把数值 1 匹配到 10，也不能删除字符串内部的空格。
  return source !== null && source.some((_, start) =>
    fragment.every((token, offset) => token === source[start + offset]));
}

function jsonTokens(text) {
  const token = /[ \t\r\n]*("(?:[^"\\\u0000-\u001f]|\\(?:["\\/bfnrt]|u[\da-fA-F]{4}))*"|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null|[{}\[\]:,])/gy;
  const result = [];
  let offset = 0;
  while (offset < text.length) {
    if (/^[ \t\r\n]*$/u.test(text.slice(offset))) break;
    const match = token.exec(text);
    if (!match) return null;
    result.push(match[1]);
    offset = token.lastIndex;
  }
  return result;
}
