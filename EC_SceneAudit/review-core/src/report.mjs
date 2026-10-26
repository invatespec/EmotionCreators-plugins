import { validateShape } from './schema.mjs';
import { matchesEvidenceQuote, resolveEvidence } from './evidence.mjs';

// 撤销项可以只写 id/status/revisionReason，其余字段从前版同名条目继承。
const INHERITABLE = ['severity', 'certainty', 'title', 'locations', 'trigger', 'evidence', 'impact', 'recommendation'];

export function validateReport(report, bundle, { previousReport, allowUnresolvedInherits = false } = {}) {
  const errors = validateShape('report', report), warnings = [];
  const fail = (code, path, message) => errors.push({ code, path, message });
  if (!bundle?.verified) fail('unverified-bundle', '/source', '资料尚未通过来源核验。');
  if (errors.length) return { ok: false, errors, warnings };
  // 展开必须先于引用检查：精简撤销项没有位置与证据，先查引用只会报一串缺字段的错。
  const { value: current, changed } = expandInherited(report, previousReport, fail, warnings, allowUnresolvedInherits);
  if (errors.length) return { ok: false, errors, warnings };
  if (current.source.bundleId !== bundle.manifest.bundleId || current.source.manifestSha256 !== bundle.manifestSha256) {
    fail('report-source', '/source', '报告与资料批次或正文清单不匹配。');
  }
  checkReferences(current, bundle, fail);
  checkFindings(current, fail);
  checkRevision(current, previousReport, bundle, fail, warnings);
  const result = { ok: errors.length === 0, errors, warnings };
  if (changed) result.expanded = current;
  return result;
}

// 只补缺字段的条目；写了什么就覆盖什么，其余保留前版值。
// allowUnresolvedInherits 用于校验前版自身（可能也是精简的）：不展开、不报错，交给后续检查按现实情况判断。
function expandInherited(report, previous, fail, warnings, allowUnresolvedInherits) {
  // 前版来自用户选择的文件，可能结构损坏；这里只取可用条目，坏前版由 checkRevision 统一判失败。
  const old = new Map((Array.isArray(previous?.findings) ? previous.findings : [])
    .filter(finding => finding && typeof finding === 'object')
    .map(finding => [finding.id, finding]));
  let changed = false;
  const findings = report.findings.map((finding, index) => {
    if (!INHERITABLE.some(key => finding[key] === undefined))
      return finding;
    if (allowUnresolvedInherits)
      return finding;
    if (!previous) {
      fail('inherits-without-previous', '/findings/' + index, '撤销项缺少字段，且未提供前版报告可供继承。');
      return finding;
    }
    const base = old.get(finding.id);
    if (!base) {
      fail('inherits-unknown-id', '/findings/' + index, '撤销项引用的 ID 不在前版报告中：' + finding.id);
      return finding;
    }
    // 只补结论字段；replacesFindingIds 属于与前版的关系，不能跟着抄过来。
    const merged = { ...finding };
    for (const key of INHERITABLE) if (merged[key] === undefined) merged[key] = base[key];
    const missing = INHERITABLE.filter(key => merged[key] === undefined);
    // 前版本身也是精简项（连续两轮撤销同一问题）时补不全，降级为提示：撤销项不参与定位，缺展示字段不改变结论。
    if (missing.length) {
      warnings.push({ code: 'inherits-incomplete', path: '/findings/' + index,
        message: '前版条目同样缺少字段，已按原文保留：' + missing.join('、') });
      changed = true;
      return merged;
    }
    changed = true;
    return merged;
  });
  return { value: { ...report, findings }, changed };
}

// 跨批次前版只核对结构与问题 ID：旧引用属于旧资料包，不能解析到当前证据上。
export function validatePortableReport(report) {
  const errors = validateShape('report', report), warnings = [], fail = (code, path, message) => errors.push({ code, path, message });
  if (errors.length === 0) checkFindings(report, fail);
  return { ok: errors.length === 0, errors, warnings };
}

function checkReferences(report, bundle, fail) {
  const check = (reference, path, quote) => {
    try {
      const value = resolveEvidence(bundle, reference);
      if (quote !== undefined && !matchesEvidenceQuote(value, quote)) throw new Error('引用片段未出现在指定证据内。');
    } catch (error) { fail('evidence-reference', path, error.message); }
  };
  report.coverage.reviewed.forEach((ref, i) => check(ref, '/coverage/reviewed/' + i));
  report.coverage.excluded.forEach((item, i) => {
    if (item.reference) check(item.reference, '/coverage/excluded/' + i);
  });
  report.findings.forEach((finding, i) => {
    // 补不全的撤销项可能没有位置与证据；写了的字段仍要逐条解析，不能整条跳过。
    (finding.locations ?? []).forEach((ref, j) => check(ref, '/findings/' + i + '/locations/' + j));
    (finding.evidence ?? []).forEach((item, j) => check(item.reference, '/findings/' + i + '/evidence/' + j, item.quote));
  });
}

function checkFindings(report, fail) {
  const ids = new Map();
  for (const [i, finding] of report.findings.entries()) {
    if (ids.has(finding.id)) fail('finding-id', '/findings/' + i, '问题 ID 重复。');
    ids.set(finding.id, finding);
    if ((finding.status === 'withdrawn' || finding.replacesFindingIds?.length) && !finding.revisionReason?.trim()) {
      fail('revision-reason', '/findings/' + i, '撤销或替换需要明确理由。');
    }
    if (!report.supersedesReportId && (finding.status === 'withdrawn' || finding.replacesFindingIds?.length)) {
      fail('revision-parent', '/findings/' + i, '撤销或替换需要前版报告 ID。');
    }
  }
  for (const [i, finding] of report.findings.entries()) {
    for (const replaced of finding.replacesFindingIds ?? []) {
      if (replaced === finding.id || ids.get(replaced)?.status === 'active') {
        fail('replacement-conflict', '/findings/' + i, '被替换问题不能同时作为有效问题。');
      }
    }
  }
}

function checkRevision(report, previous, bundle, fail, warnings) {
  if (report.supersedesReportId === report.reportId) {
    fail('revision-self', '/supersedesReportId', '报告不能替换自己。');
    return;
  }
  if (!previous) {
    if (report.supersedesReportId) warnings.push({ code: 'history-unavailable', path: '/supersedesReportId', message: '未提供前版，当前结论可读，修订关系尚未核验。' });
    return;
  }
  const sameBundle = previous.source?.bundleId === bundle.manifest.bundleId
    && previous.source?.manifestSha256 === bundle.manifestSha256;
  // 前版自身可能也是精简撤销项；它的更早前版没有传入，无法展开，此处跳过缺字段。
  const checked = sameBundle
    ? validateReport(previous, bundle, { allowUnresolvedInherits: true })
    : validatePortableReport(previous);
  if (!checked.ok || report.supersedesReportId !== previous.reportId) {
    fail('revision-parent', '/supersedesReportId', '前版未通过校验或修订关系不符。');
    return;
  }
  // 修完重导出后沿用旧报告是正常流程，但旧引用未在新证据上复核，必须明确提示。
  // 报告里没有 Scene 标题，无法自动确认是否同一 Scene，只能提示用户自行核对。
  if (!sameBundle) {
    warnings.push({ code: 'previous-other-bundle', path: '/source',
      message: '前版来自另一批资料，已按问题 ID 核对修订关系；前版引用未在当前资料上复核，请确认是同一 Scene 的新导出。' });
  }
  const old = new Map(previous.findings.map(f => [f.id, f]));
  const current = new Map(report.findings.map(f => [f.id, f]));
  const replaced = new Set(report.findings.flatMap(f => f.replacesFindingIds ?? []));
  for (const finding of previous.findings.filter(f => f.status === 'active')) {
    if (!current.has(finding.id) && !replaced.has(finding.id)) {
      fail('revision-omission', '/findings', '旧有效问题消失，需明确保留、撤销或替换：' + finding.id);
    }
  }
  for (const [i, finding] of report.findings.entries()) {
    if (finding.status === 'withdrawn' && !old.has(finding.id)) {
      fail('withdrawal-missing', '/findings/' + i, '被撤销 ID 不在前版报告中。');
    }
    if ((finding.replacesFindingIds ?? []).some(id => !old.has(id))) {
      fail('replacement-missing', '/findings/' + i, '被替换 ID 不在前版报告中。');
    }
    if (old.has(finding.id) && content(old.get(finding.id), sameBundle) !== content(finding, sameBundle)
        && !finding.revisionReason?.trim()) {
      fail('revision-reason', '/findings/' + i, '已有问题改变时需要修订理由。');
    }
  }
}

// 跨批次时证据与位置必然改写，只比对结论字段；同批次仍逐字比对全部内容。
function content(finding, sameBundle) {
  if (sameBundle) {
    const { revisionReason, ...value } = finding;
    return JSON.stringify(canonical(value));
  }
  const { revisionReason, evidence, locations, ...value } = finding;
  return JSON.stringify(canonical(value));
}

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  }
  return value;
}
