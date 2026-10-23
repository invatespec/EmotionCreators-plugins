import { sha256, loadBundle } from '../src/bundle.mjs';

export const scriptPointer = '/nodes/0/part/cuts/0/textBoxes/1/layouts/0/scriptBlocks/0/raw';
export const ref = { fileId: 'scene', kind: 'json-pointer', pointer: scriptPointer };

export async function fixture() {
  const sceneInfo = { title: '测试 Scene', exportedAt: '2026-10-04T00:00:00Z' };
  const script = '<script><load-script file="a.xml"/></script>';
  const box = (sourceIndex, option, rawText) => ({
    sourceIndex, sortOrder: 10 - sourceIndex, option,
    layouts: [{ layoutIndex: 0, rawText, scriptBlocks: option ? [] : [{ raw: script }] }],
  });
  const scene = {
    schemaVersion: '1.0', scene: sceneInfo,
    nodes: [{
      uid: 'p1', name: '起点', outputs: [{ slot: 0, targetUid: 'a' }, { slot: 1, targetUid: 'b' }],
      part: { name: '起点', type: 'ADV', cuts: [{
        sourceIndex: 0, endCut: true, optionCount: 2,
        textBoxes: [box(2, true, '第二个选项'), box(0, false, script), box(1, true, '第一个选项')],
      }] },
    }],
  };
  const summary = {
    schemaVersion: '1.0-ai', sourceEvidenceFile: 'scene.json', scene: sceneInfo,
    parts: [{ rawRef: '#/nodes/0/part', name: '起点' }],
    scripts: [{ rawRef: '#' + scriptPointer.slice(0, -4) }],
  };
  const data = new Map([
    ['scene.json', encode(JSON.stringify(scene))],
    ['ai-summary.json', encode(JSON.stringify(summary))],
    ['dependencies/dep-0001.xml', encode(['<script>', '<set name="x" value="1"/>', '</script>'].join(String.fromCharCode(10)))],
  ]);
  const files = [];
  for (const [path, bytes] of data) {
    const role = path === 'scene.json' ? 'scene' : path === 'ai-summary.json' ? 'summary' : 'dependency';
    const fileId = role === 'dependency' ? 'dep-0001' : role;
    files.push({ fileId, role, path, byteLength: bytes.length, sha256: await sha256(bytes),
      ...(role === 'dependency' ? { sourcePath: 'a.xml', capturedAt: sceneInfo.exportedAt, encoding: 'utf-8', parseStatus: 'valid' } : {}) });
  }
  const manifest = {
    format: 'ec-scene-review-bundle', version: 1, bundleId: 'fixture-1', scene: sceneInfo, files,
    dependencies: [{ from: ref, requestedPath: 'a.xml', targetFileId: 'dep-0001', status: 'included', reason: null, context: ['script'] }],
    limitations: [],
  };
  await seal(data, manifest);
  const readFile = async path => {
    if (!data.has(path)) throw new Error('Missing: ' + path);
    return data.get(path);
  };
  const loaded = await loadBundle({ readFile });
  return { data, manifest, scene, summary, readFile, loaded };
}

export const encode = value => new TextEncoder().encode(value);

export async function seal(data, manifest) {
  const bytes = encode(JSON.stringify(manifest));
  data.set('bundle.json', bytes);
  data.set('bundle.sha256', encode(await sha256(bytes)));
}

export function report(bundle) {
  return {
    format: 'ec-scene-review-report', version: 1, reportId: 'r1', supersedesReportId: null,
    source: { bundleId: bundle.manifest.bundleId, manifestSha256: bundle.manifestSha256 },
    summary: '合成测试报告，不代表用户场景缺陷。',
    coverage: { reviewed: [ref], excluded: [], limitations: ['没有执行游戏。'] },
    findings: [{
      id: 'f1', status: 'active', severity: 'minor', certainty: 'conditional', title: '待核对的脚本前提',
      locations: [ref], trigger: '到达本 CUT 时',
      evidence: [{ reference: ref, explanation: '脚本读取外部文件。', quote: '<load-script file="a.xml"/>' }],
      impact: '需核对依赖状态。', recommendation: '检查资料清单。', revisionReason: null,
    }],
  };
}
