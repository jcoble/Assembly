import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stripSource = await readFile(
  new URL('../src/lib/shell/components/UtilityStrip.svelte', import.meta.url),
  'utf8'
);
const storeSource = await readFile(
  new URL('../src/lib/shell/resources/resourceSampleStore.svelte.ts', import.meta.url),
  'utf8'
);

assert.match(
  stripSource,
  /const refreshInterval = window\.setInterval\(\(\) => \{\s*void refreshResourceTotals\(\);\s*\}, 5_000\);/,
  'the always-visible Resources summary should refresh every five seconds'
);
assert.match(
  stripSource,
  /return \(\) => \{\s*window\.clearInterval\(refreshInterval\);/,
  'the Resources refresh interval should stop when the footer unmounts'
);
assert.match(
  storeSource,
  /if \(totalsRefreshInFlight\) return totalsRefreshInFlight;/,
  'a timer tick should reuse an active totals refresh instead of starting another'
);

console.log('utility strip resource refresh checks passed');
