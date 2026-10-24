// Run: node --import ./scripts/svelteRuneTestSetup.ts --experimental-strip-types src/lib/shell/providers/providerUpdateService.test.ts
import assert from 'node:assert/strict';
import test from 'node:test';
import { providerVersionLine } from './providerUpdateService.svelte.ts';

const claude = (currentVersion: string | null, availableVersion: string, updateAvailable: boolean) =>
  providerVersionLine({ provider: 'claude', currentVersion, availableVersion, updateAvailable });

test('shows an arrow only for a real update', () => {
  assert.equal(claude('0.87.0', '0.88.0', true), 'claude: 0.87.0 → 0.88.0');
  assert.equal(claude(null, '0.88.0', true), 'claude: Not installed → 0.88.0');
});

test('shows one version when installed matches the Assembly release', () => {
  assert.equal(claude('0.88.0', '0.88.0', false), 'claude: 0.88.0');
});

test('does not draw a downgrade when the installed adapter is newer', () => {
  assert.equal(claude('0.88.0', '0.87.0', false), 'claude: 0.88.0 installed · Assembly release 0.87.0');
});
