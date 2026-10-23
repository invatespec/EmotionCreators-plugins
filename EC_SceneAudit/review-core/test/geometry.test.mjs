import test from 'node:test';
import assert from 'node:assert/strict';
import { validateGeometry } from '../src/geometry.mjs';

const scene = () => ({ nodes: [{ uid: 'a', position: { x: 20, y: -30 }, outputs: [{ slot: 0 }, { slot: 1 }] }], geometry: {
  version: 1, coordinateSpace: 'grid-ui-top-left-y-down', canvas: { width: 400, height: 300 }, nodes: [{ uid: 'a', status: 'exact',
    frame: { x: 25, y: 38, width: 184, height: 66 }, positionOffset: { x: 5, y: 8 }, input: { enabled: true, x: 20, y: 36 },
    outputs: [{ slot: 0, enabled: true, x: 214, y: 40 }, { slot: 1, enabled: false, x: 214, y: 112 }] }] } });

test('validates optional geometry while allowing ports outside the node frame', () => {
  const result = validateGeometry(scene());
  assert.equal(result.available, true);
  assert.equal(result.value.nodes[0].outputs[1].y, 112);
});

test('accepts geometry output ports as a subset of existing Scene slots', () => {
  const value = scene();
  value.geometry.nodes[0].outputs.pop();
  assert.equal(validateGeometry(value).available, true);
  value.geometry.nodes[0].outputs[0].slot = 9;
  assert.equal(validateGeometry(value).available, false);
});

test('keeps old bundles readable without geometry', () => {
  assert.equal(validateGeometry({ nodes: [] }).available, false);
});

test('rejects duplicate slots, missing nodes, non-finite values, and inconsistent coordinates', () => {
  const duplicate = scene(); duplicate.geometry.nodes[0].outputs[1].slot = 0;
  assert.equal(validateGeometry(duplicate).available, false);
  const missing = scene(); missing.geometry.nodes = [];
  assert.equal(validateGeometry(missing).available, false);
  const nonFinite = scene(); nonFinite.geometry.nodes[0].input.x = Infinity;
  assert.equal(validateGeometry(nonFinite).available, false);
  const inconsistent = scene(); inconsistent.geometry.nodes[0].frame.y += 1;
  assert.equal(validateGeometry(inconsistent).available, false);
});
