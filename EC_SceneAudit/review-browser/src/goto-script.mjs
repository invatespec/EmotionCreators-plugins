import { DOMParser } from '@xmldom/xmldom';

// 静态 goto 解析：对齐 Variable Mod v0.6 解释器。
// 只回答“原文里声明了哪些跳转及候选目标”，不执行脚本、不评估变量、不替代运行时。

const SCRIPT_OPEN = '<script>';
const SCRIPT_CLOSE = '</script>';
const COMMAND_NAMES = new Set(['goto-cut', 'goto-part']);
// 解释器只对 if/loop 立即递归；event-* 与子脚本把子元素存成文本，触发/调用时才分析。
const CONDITION_CONTAINERS = new Set(['if', 'loop']);
const DEFERRED_CONTAINERS = new Set(['event-next', 'event-select', 'event-update']);
// event-part 注册子脚本到目标 Part，不是即时跳转，不能画成即时边。
const REGISTRATION_CONTAINERS = new Set(['event-part']);
// load-script 在这些容器内仍会被解释器处理（条件成立/事件触发/子脚本调用），
// 收集调用签名时必须覆盖；未知包装内不会执行，不递归。
const LOAD_CONTAINERS = new Set(['if', 'loop', 'event-next', 'event-select', 'event-update', 'event-part', 'sub-script']);

// .NET Convert.ToInt32 的取值域；越界抛 OverflowException → 解析失败，不做兼容落点。
const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;

const isDynamicText = value => typeof value === 'string' && value.length >= 2
  && value.startsWith('#') && value.endsWith('#');

function childElements(element) {
  const result = [];
  for (let node = element.firstChild; node; node = node.nextSibling)
    if (node.nodeType === 1) result.push(node);
  return result;
}

function nameOf(element) { return element.localName || element.nodeName; }

function attributeOf(element, name) {
  return element.hasAttribute && element.hasAttribute(name) ? element.getAttribute(name) : null;
}

// 严格 XML：解析告警、错误、无根、命名空间或根名不符都判无效。
function parseBlock(blockText) {
  let problem = null;
  const parser = new DOMParser({
    errorHandler: {
      warning: message => { problem = problem || String(message); },
      error: message => { problem = problem || String(message); },
      fatalError: message => { problem = problem || String(message); },
    },
  });
  let document = null;
  try { document = parser.parseFromString(blockText, 'application/xml'); }
  catch (error) { problem = problem || (error && error.message) || String(error); }
  const root = document && document.documentElement;
  if (problem || !root) return { document: null, root: null, error: problem || '脚本 XML 无根元素。' };
  // XDocument.Parse 成功但 Element("script") 返回 null 时解释器抛异常、整块不执行；
  // 根名或命名空间不符走同一结果；doctype 不加额外限制。
  if (nameOf(root) !== 'script' || root.prefix || (root.namespaceURI && root.namespaceURI !== ''))
    return { document: null, root: null, error: '脚本根元素必须是大小写精确的 <script>。' };
  return { document, root, error: null };
}

// 收集解释器会处理的 load-script 调用签名（含 file 路径与嵌套位置）；
// 只沿解释器支持的容器递归，未知包装内的 load 不会执行，不计入。
function collectLoadScripts(element, path = []) {
  const result = [];
  for (const child of childElements(element)) {
    const name = nameOf(child);
    if (name === 'load-script') {
      result.push({ file: attributeOf(child, 'file'), path: [...path] });
      continue;
    }
    if (LOAD_CONTAINERS.has(name))
      result.push(...collectLoadScripts(child, [...path, name]));
  }
  return result;
}

// 只按运行时字面搜索取第一段；<SCRIPT>、<script > 都不算可执行块。
export function extractScriptBlock(rawText) {
  const text = typeof rawText === 'string' ? rawText : '';
  const start = text.indexOf(SCRIPT_OPEN);
  if (start < 0)
    return { found: false, valid: false, start: -1, end: null, blockText: null, error: null, loadScripts: [] };
  const close = text.indexOf(SCRIPT_CLOSE);
  if (close < 0 || start >= close)
    return { found: true, valid: false, start, end: null, blockText: null, error: '首个 <script> 没有在 </script> 处闭合。', loadScripts: [] };
  const end = close + SCRIPT_CLOSE.length;
  const blockText = text.slice(start, end);
  const parsed = parseBlock(blockText);
  const loadScripts = parsed.root ? collectLoadScripts(parsed.root) : [];
  return { found: true, valid: parsed.error === null, start, end, blockText, error: parsed.error, loadScripts };
}

function diagnostic(code, message, origin) {
  return { code, message, severity: 'warning', origin: origin ? { ...origin } : null };
}

// .NET Convert.ToInt32(text, 10) 允许首尾空白与前导符号，但超出 Int32 会抛错。
function integerLiteral(raw) {
  if (typeof raw !== 'string') return null;
  const text = raw.trim();
  if (!/^[+-]?\d+$/u.test(text)) return null;
  const value = Number(text);
  return Number.isInteger(value) && value >= INT32_MIN && value <= INT32_MAX ? value : null;
}

function findNodesByName(nodes, name) {
  return (nodes || []).filter(node => node && !node.system
    && typeof node.name === 'string' && node.name === name);
}

function isHPart(node) {
  return !!node && String(node.kind || '').toUpperCase() === 'H';
}

// CUT 定位：脚本 index 指游戏 CUT 数组位置，导出为 sourceIndex；next/prev 也按数组位置前/后。
// 源索引缺失或重复（ordered=false 或不可验证）时不可定位；非连续时按 sourceIndex 唯一映射。
function orderedCuts(source) {
  const cuts = source && Array.isArray(source.cuts) ? source.cuts : null;
  if (!cuts || source.ordered === false) return null;
  const indices = cuts.map(cut => cut?.sourceIndex);
  const ok = indices.every(index => Number.isInteger(index) && index >= 0)
    && new Set(indices).size === indices.length;
  return ok ? cuts : null;
}

const cutIndexBySource = (cuts, sourceIndex) => cuts.findIndex(cut => cut?.sourceIndex === sourceIndex);

// goto-cut 只作用于当前 Part；返回目标 CUT 的模型数组位置。
function resolveGotoCut(element, context, path, origin, nodes) {
  const raw = attributeOf(element, 'index');
  const base = { command: 'goto-cut', context, path, name: null, index: raw,
    dynamic: false, status: 'resolved', to: null, toCut: null, targetName: origin?.nodeName || '', hint: null, reason: null };
  // 空 index 走 TagGotoCutProcess 的 text.Length > 0 假分支 → 报错。
  if (raw === null || raw.length === 0)
    return { parseError: true, command: { ...base, status: 'invalid-index', reason: 'goto-cut 缺少非空 index。' } };
  if (isDynamicText(raw))
    return { parseError: false, command: { ...base, dynamic: true, status: 'dynamic-target', to: origin?.nodeId || null, reason: 'goto-cut 目标 CUT 依赖运行时变量，停止展开。' } };
  const source = (nodes || []).find(node => node && node.id === origin?.nodeId) || null;
  if (!source)
    return { parseError: false, command: { ...base, status: 'missing-source', reason: '找不到当前 Part，无法定位 goto-cut。' } };
  base.to = source.id;
  base.targetName = source.name;
  const cuts = orderedCuts(source);
  if (!cuts)
    return { parseError: false, command: { ...base, status: 'unordered-cuts', reason: '当前 Part 的 CUT 源索引缺失或重复，无法定位落点。' } };
  let keyword = raw === 'next' ? 'next' : raw === 'prev' ? 'prev' : raw === 'end' ? 'end' : null;
  let numeric = null;
  if (!keyword) {
    numeric = integerLiteral(raw);
    if (numeric === null)
      return { parseError: true, command: { ...base, status: 'invalid-index', reason: 'goto-cut 的 index 必须是整数、变量或 next/prev/end。' } };
    if (numeric === -1) keyword = 'next';
    else if (numeric === -2) keyword = 'prev';
    else if (numeric === -3) keyword = 'end';
    else if (numeric < 0)
      return { parseError: false, command: { ...base, status: 'unsupported-negative', reason: '未映射负数索引没有确定的运行时落点。' } };
  }
  // end 只依赖 CUT 总数（原生 _maxCut - 1），不需要当前 CUT 位置。
  if (keyword === 'end')
    return { parseError: false, command: { ...base, toCut: cuts.length - 1 } };
  const current = origin?.cutIndex;
  if (!Number.isInteger(current) || current < 0 || current >= cuts.length)
    return { parseError: false, command: { ...base, status: 'missing-source', reason: '当前 CUT 位置不可定位。' } };
  if (keyword === 'next') {
    const next = current + 1;
    if (next >= cuts.length)
      return { parseError: false, command: { ...base, status: 'next-at-last', hint: 'next-at-last', reason: '已是最后一个 CUT，运行时可能转正常下一 Part。' } };
    return cuts[next].sourceIndex === cuts[current].sourceIndex + 1
      ? { parseError: false, command: { ...base, toCut: next } }
      : { parseError: false, command: { ...base, status: 'cut-gap', reason: '相邻 CUT 源索引不连续，next 不跨缺口猜测。' } };
  }
  if (keyword === 'prev') {
    const previous = current - 1;
    if (previous < 0)
      return { parseError: false, command: { ...base, status: 'prev-at-first', reason: '首个 CUT 没有 prev 目标。' } };
    return cuts[current].sourceIndex === cuts[previous].sourceIndex + 1
      ? { parseError: false, command: { ...base, toCut: previous } }
      : { parseError: false, command: { ...base, status: 'cut-gap', reason: '相邻 CUT 源索引不连续，prev 不跨缺口猜测。' } };
  }
  const index = cutIndexBySource(cuts, numeric);
  return index >= 0
    ? { parseError: false, command: { ...base, toCut: index } }
    : { parseError: false, command: { ...base, status: 'missing-cut', reason: '目标 CUT 超出当前 Part 范围。' } };
}

// goto-part 按精确 Part 名查当前 Scene；index 省略或空串沿用原生默认 -1（从 CUT 0 开始）。
function resolveGotoPart(element, context, path, origin, nodes) {
  const rawName = attributeOf(element, 'name');
  const rawIndex = attributeOf(element, 'index');
  const base = { command: 'goto-part', context, path, name: rawName, index: rawIndex,
    dynamic: false, status: 'resolved', to: null, toCut: null, targetName: rawName || '', hint: null, reason: null };
  if (rawName === null || rawName.length === 0)
    return { parseError: true, command: { ...base, status: 'invalid-name', reason: 'goto-part 缺少非空 name。' } };
  if (rawName === '開始' || rawName === '終了')
    return { parseError: true, command: { ...base, status: 'rejected-system-part', reason: 'goto-part 不能指向開始/終了。' } };
  if (isDynamicText(rawName))
    return { parseError: false, command: { ...base, dynamic: true, status: 'dynamic-target', reason: 'goto-part 目标 Part 依赖运行时变量，停止展开。' } };
  const matches = findNodesByName(nodes, rawName);
  if (matches.length === 0)
    return { parseError: false, command: { ...base, status: 'missing-part', reason: '目标 Part 未收录。' } };
  if (matches.length > 1)
    return { parseError: false, command: { ...base, status: 'ambiguous-part', reason: '同名 Part 不唯一，停止展开。' } };
  const target = matches[0];
  const command = { ...base, to: target.id, targetName: target.name };
  if (isHPart(target))
    return { parseError: false, command: { ...command, toCut: null, hint: 'h-part-no-cut', reason: null } };
  const cuts = orderedCuts(target);
  if (!cuts)
    return { parseError: false, command: { ...command, status: 'unordered-cuts', reason: '目标 Part 的 CUT 源索引缺失或重复，无法定位落点。' } };
  if (cuts.length === 0)
    return { parseError: false, command: { ...command, toCut: null, hint: 'no-cut', reason: null } };
  // TagGotoPartProcess 的空 index 跳过 Length > 0 分支，沿用初值 -1；原生 SetPart 从目标 CUT 0（数组首位）开始。
  if (rawIndex === null || rawIndex === '')
    return { parseError: false, command: { ...command, toCut: 0, hint: 'default-cut' } };
  if (isDynamicText(rawIndex))
    return { parseError: false, command: { ...command, dynamic: true, status: 'dynamic-index', hint: 'dynamic-index', reason: 'goto-part 落点 CUT 依赖运行时变量。' } };
  const numeric = integerLiteral(rawIndex);
  if (numeric === null)
    return { parseError: true, command: { ...command, status: 'invalid-index', reason: 'goto-part 的 index 必须是整数或变量，不接受 next/prev/end。' } };
  // 显式负索引不会被 SetPart_Pre 截获，原生从 CUT 0 开始，属未文档化兼容行为。
  if (numeric < 0)
    return { parseError: false, command: { ...command, toCut: 0, hint: 'negative-index-compat' } };
  const index = cutIndexBySource(cuts, numeric);
  return index >= 0
    ? { parseError: false, command: { ...command, toCut: index } }
    : { parseError: false, command: { ...command, toCut: null, status: 'missing-cut', reason: '目标 CUT 超出目标 Part 范围。' } };
}

function commandOrigin(origin, blockIndex, path, context) {
  return { ...(origin || {}), blockIndex, commandIndex: 0, path: [...path], context };
}

// 只递归解释器会处理的容器；未知包装元素不递归，避免产生伪执行边。
// conditional=true 表示处于未知条件分支：命令标 uncertain，且分支内解析失败不中断整段脚本。
function walk(elements, context, path, pass, origin, nodes, conditional) {
  for (const element of elements) {
    if (pass.halted) return;
    const name = nameOf(element);
    if (COMMAND_NAMES.has(name)) {
      const resolved = name === 'goto-cut'
        ? resolveGotoCut(element, context, path, origin, nodes)
        : resolveGotoPart(element, context, path, origin, nodes);
      const command = resolved.command;
      command.origin = commandOrigin(origin, 0, path, context);
      if (conditional) command.uncertain = true;
      pass.commands.push(command);
      if (resolved.parseError && !conditional) pass.halted = true;
      continue;
    }
    if (CONDITION_CONTAINERS.has(name)) {
      const sub = { commands: [], diagnostics: [], halted: false };
      walk(childElements(element), context === 'plain' ? name : context, [...path, name], sub, origin, nodes, true);
      pass.commands.push(...sub.commands);
      pass.diagnostics.push(...sub.diagnostics);
      continue;
    }
    if (DEFERRED_CONTAINERS.has(name)) {
      // 事件脚本触发时才分析：用子趟隔离，内部解析失败不中断当前脚本。
      const sub = { commands: [], diagnostics: [], halted: false };
      walk(childElements(element), context === 'plain' ? name : context, [...path, name], sub, origin, nodes, false);
      pass.commands.push(...sub.commands);
      pass.diagnostics.push(...sub.diagnostics);
      continue;
    }
    if (REGISTRATION_CONTAINERS.has(name)) {
      pass.diagnostics.push(diagnostic('deferred-registration',
        'event-part 注册子脚本到目标 Part，不是即时跳转，预览不生成跳转边。', origin));
      continue;
    }
    if (/(goto|go[-_]?to)/iu.test(name))
      pass.diagnostics.push(diagnostic('unsupported-command',
        `元素 ${name} 不是运行时跳转命令，预览不生成跳转边。`, origin));
  }
}

// 返回静态声明的跳转命令与候选目标；不执行脚本，不修改输入。
export function parseGotoCommands(rawText, origin, nodes) {
  const block = extractScriptBlock(rawText);
  if (!block.found) return { block, commands: [], diagnostics: [], halted: false };
  if (!block.valid)
    return { block, commands: [], diagnostics: [diagnostic('invalid-xml', block.error || '脚本 XML 无效。', origin)], halted: true };
  const parsed = parseBlock(block.blockText);
  const pass = { commands: [], diagnostics: [], halted: false };
  walk(childElements(parsed.root), 'plain', [], pass, origin, nodes, false);
  pass.commands.forEach((command, index) => { command.origin.commandIndex = index; });
  return { block, commands: pass.commands, diagnostics: pass.diagnostics, halted: pass.halted };
}
