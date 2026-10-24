import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import test from 'node:test';

const script = new URL('../../scripts/measure.sh', import.meta.url).pathname;
const measure = (...args: string[]) => spawnSync('bash', [script, ...args], { encoding: 'utf8' });

test('a check passes when every repeat returns within 15 MB of the start', () => {
  assert.equal(measure('check', '500', '510', '512', '505').status, 0);
  assert.equal(measure('check', '500', '515', '515', '515').status, 0);
  assert.equal(measure('check', '500', '480', '470', '480').status, 0, 'memory going down is not a leak');
});

test('a check fails when a repeat ends more than 15 MB above the start or the repeat before', () => {
  const high = measure('check', '500', '510', '516', '505');
  assert.equal(high.status, 1);
  assert.match(high.stdout, /repeat 2: 516 MB is more than 15 MB above the start \(500 MB\)/);
  assert.equal(measure('check', '500', '470', '490', '500').status, 1, 'climbing 20 MB on one repeat fails');
  assert.equal(measure('check', '500').status, 2, 'a check needs at least one repeat');
  assert.equal(measure('check', '500', 'lots').status, 2);
});

test('a sample sums PSS and private dirty over the process and its children', { skip: process.platform !== 'linux' }, async () => {
  const child = spawn('sleep', ['30']);
  await once(child, 'spawn');
  try {
    const result = measure('sample', String(process.pid));
    assert.equal(result.status, 0, result.stderr);
    const rows = result.stdout.trim().split('\n');
    assert.ok(rows.some((row) => row.startsWith(`${child.pid} `)), result.stdout);
    const [label, pss, privateDirty] = rows.at(-1)!.split(/\s+/);
    assert.equal(label, 'total');
    assert.ok(Number(pss) > 0 && Number(privateDirty) > 0, result.stdout);
  } finally {
    child.kill();
  }
});
