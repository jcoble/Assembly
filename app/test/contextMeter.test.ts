import assert from 'node:assert/strict';
import { test } from 'node:test';
import { contextMeter } from '../src/lib/composer/contextMeter.ts';

test('nothing reported shows nothing, not zero', () => {
  assert.equal(contextMeter(null), null);
  assert.equal(contextMeter({ used: 10, size: 0 }), null);
  assert.equal(contextMeter({ used: 2_000, size: 1_000 }), null);
});

test('the popover and hint read the rounded shares', () => {
  const meter = contextMeter({ used: 51_285, size: 1_000_000 })!;
  assert.equal(meter.detail, '51,285 of 1,000,000 (5%)');
  assert.equal(meter.left, 95);
  assert.equal(meter.used, 5);
});

test('a touched window paints at least a tenth spent; an untouched one is full', () => {
  assert.equal(contextMeter({ used: 0, size: 1_000 })!.arc, 100);
  assert.equal(contextMeter({ used: 1, size: 1_000 })!.arc, 90);
  assert.equal(contextMeter({ used: 600, size: 1_000 })!.arc, 40);
});

test('warm at a quarter left, hot at a tenth', () => {
  const at = (used: number) => contextMeter({ used, size: 100 })!;
  assert.deepEqual([at(74).warm, at(74).hot], [false, false]);
  assert.deepEqual([at(75).warm, at(75).hot], [true, false]);
  assert.deepEqual([at(90).warm, at(90).hot], [true, true]);
});
