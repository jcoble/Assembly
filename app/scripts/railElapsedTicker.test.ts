/** Run: node --experimental-strip-types scripts/railElapsedTicker.test.ts */
import assert from 'node:assert/strict';

import { formatRailElapsed } from '../src/lib/shell/components/railElapsedTicker.ts';

assert.equal(formatRailElapsed(0), '0s');
assert.equal(formatRailElapsed(-5_000), '0s', 'a clock skew never prints a negative age');
assert.equal(formatRailElapsed(38_000), '38s');
assert.equal(formatRailElapsed(59_999), '59s');
assert.equal(formatRailElapsed(60_000), '1m', 'from one minute the seconds are dropped');
assert.equal(formatRailElapsed(134_000), '2m');
assert.equal(formatRailElapsed(701_000), '11m');
assert.equal(formatRailElapsed(3_599_000), '59m');
assert.equal(formatRailElapsed(3_600_000), '1h', 'from one hour the minutes are dropped');
assert.equal(formatRailElapsed(7_800_000), '2h');
assert.equal(formatRailElapsed(86_399_999), '23h');
assert.equal(formatRailElapsed(86_400_000), '1d', 'from 24 hours the age is shown in days');
assert.equal(formatRailElapsed(180_000_000), '2d');

// The rail refreshes nowMs every 15 s, so one session's age reads 0s, 15s, 30s, 1m, 2m as the clock advances.
assert.deepEqual([0, 15_000, 30_000, 60_000, 120_000].map(formatRailElapsed), ['0s', '15s', '30s', '1m', '2m']);

console.log('railElapsedTicker: ok');
