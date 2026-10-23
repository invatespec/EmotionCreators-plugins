import test from 'node:test';
import assert from 'node:assert/strict';
import { matchesEvidenceQuote } from '../src/evidence.mjs';
import { validateReport } from '../src/report.mjs';
import { fixture, report } from './fixtures.mjs';

test('text quotes accept line-ending differences and preserve all other whitespace', () => {
  for (const ending of ['\r\n', '\r', '\n']) {
    const value = ['<script>', '<set name="a b" value="1"/>', '</script>'].join(ending);
    assert.equal(matchesEvidenceQuote(value, '<script>\n<set name="a b" value="1"/>'), true);
    assert.equal(matchesEvidenceQuote(value, '<set name="ab" value="1"/>'), false);
    assert.equal(matchesEvidenceQuote(value, '<set  name="a b" value="1"/>'), false);
    assert.equal(matchesEvidenceQuote(value, '<set name="a b" value="2"/>'), false);
  }
});

test('raw and rawText objects retain text quote semantics', () => {
  for (const key of ['raw', 'rawText']) {
    const value = { [key]: 'first\r\n  second', metadata: 'not text' };
    assert.equal(matchesEvidenceQuote(value, 'first\n  second'), true);
    assert.equal(matchesEvidenceQuote(value, 'first\nsecond'), false);
    assert.equal(matchesEvidenceQuote(value, 'not text'), false);
  }
});

test('structured JSON quotes ignore only whitespace outside string tokens', () => {
  const value = { name: 'a b', outputs: [{ slot: 0, targetUid: 'p1' }], escaped: 'a "quote" \\ path' };
  for (const indent of [2, 4, '\t']) assert.equal(matchesEvidenceQuote(value, JSON.stringify(value, null, indent)), true);
  assert.equal(matchesEvidenceQuote(value, '"slot" : 0,\n "targetUid" : "p1"'), true);
  assert.equal(matchesEvidenceQuote(value, '"name" : "ab"'), false);
  assert.equal(matchesEvidenceQuote(value, '"name" : "a  b"'), false);
  assert.equal(matchesEvidenceQuote(value, '"targetUid" : "other"'), false);
  assert.equal(matchesEvidenceQuote(value, '"slot" : 0 "targetUid" : "p1"'), false);
});

test('JSON quote matching preserves numeric, boolean, escape and token boundaries', () => {
  const value = { count: 10, truth: true, text: 'false', escaped: 'a\\n b' };
  assert.equal(matchesEvidenceQuote(value, '"count": 1'), false);
  assert.equal(matchesEvidenceQuote(value, '"truth": false'), false);
  assert.equal(matchesEvidenceQuote(value, 'false'), false);
  assert.equal(matchesEvidenceQuote(value, '"escaped": "an b"'), false);
  assert.equal(matchesEvidenceQuote(value, '"count":\u00a010'), false);
  assert.equal(matchesEvidenceQuote(value, '"count": 10'), true);
});

test('unquoted prose excerpts in structured evidence remain exact matches', () => {
  const value = [{ message: '目标 Part 不存在' }];
  assert.equal(matchesEvidenceQuote(value, '目标 Part 不存在'), true);
  assert.equal(matchesEvidenceQuote(value, '目标Part不存在'), false);
});

test('report integration accepts formatting differences without weakening identity or location checks', async () => {
  const { loaded } = await fixture();
  const current = report(loaded.bundle);
  const evidence = current.findings[0].evidence[0];
  evidence.reference = { fileId: 'scene', kind: 'json-pointer', pointer: '/nodes/0/outputs' };
  evidence.quote = '"slot": 0, "targetUid": "a"';
  assert.equal(validateReport(current, loaded.bundle).ok, true);
  for (const modify of [
    r => { r.source.bundleId = 'different'; },
    r => { r.findings[0].evidence[0].reference.pointer = '/nodes/0/part'; },
    r => { r.findings[0].evidence[0].quote = '"slot": 0, "targetUid": "b"'; },
  ]) {
    const wrong = structuredClone(current);
    modify(wrong);
    assert.equal(validateReport(wrong, loaded.bundle).ok, false);
  }
});
