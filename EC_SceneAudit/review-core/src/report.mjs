import { validateShape } from './schema.mjs';
import { matchesEvidenceQuote, resolveEvidence } from './evidence.mjs';

export function validateReport(report, bundle, { previousReport } = {}) {
  const errors = validateShape('report', report), warnings = [];
  const fail = (code, path, message) => errors.push({ code, path, message });
  if (!bundle?.verified) fail('unverified-bundle', '/source', '资料尚未通过来源核验。');
  if (errors.length) return { ok: false, errors, warnings };
  if (report.source.bundleId !== bundle.manifest.bundleId || report.source.manifestSha256 !== bundle.manifestSha256) {
    fail('report-source', '/source', '报告与资料批次或正文清单不匹配。');
  }
  checkReferences(report, bundle, fail);
  checkFindings(report, fail);
  checkRevision(report, previousReport, bundle, fail, warnings);
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
    finding.locations.forEach((ref, j) => check(ref, '/findings/' + i + '/locations/' + j));
    finding.evidence.forEach((item, j) => check(item.reference, '/findings/' + i + '/evidence/' + j, item.quote));
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
  if (!validateReport(previous, bundle).ok || report.supersedesReportId !== previous.reportId) {
    fail('revision-parent', '/supersedesReportId', '前版未通过校验或修订关系不符。');
    return;
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
    if (old.has(finding.id) && content(old.get(finding.id)) !== content(finding) && !finding.revisionReason?.trim()) {
      fail('revision-reason', '/findings/' + i, '已有问题改变时需要修订理由。');
    }
  }
}

function content(finding) {
  const { revisionReason, ...value } = finding;
  return JSON.stringify(canonical(value));
}

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  }
  return value;
}
