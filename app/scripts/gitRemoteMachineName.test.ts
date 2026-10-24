/**
 * gitRemoteMachineName.test.ts — a Git error about an unreachable remote names
 * the machine the way the user saved it, not by its internal profile id
 * (TSK-1546).
 *
 * The stores call `$state(...)` at module scope; node runs them as plain
 * TypeScript, so `$state(v)` returns `v` unchanged.
 */
import assert from 'node:assert/strict';

(globalThis as { $state?: unknown }).$state = <T>(value: T): T => value;

const { createGitService } = await import('../src/lib/shell/git/gitService.ts');
const { createGitPanelState } = await import('../src/lib/shell/git/gitPanelStore.svelte.ts');
const { rail } = await import('../src/lib/shell/stores/sessionRailStore.svelte.ts');

rail.remoteProfiles = [{ id: 'workbox-test-qa1500b', name: 'Workbox Test' } as (typeof rail.remoteProfiles)[number]];

const backend = {
  async readStatus() { throw new Error('Remote machine workbox-test-qa1500b is not connected'); }
} as unknown as Parameters<typeof createGitService>[0]['backend'];
const state = createGitPanelState();
const git = createGitService({ backend, state });
state.root = '/repo';
await git.refreshStatus();

assert.equal(state.statusError, 'Workbox Test is not connected');
console.log('gitRemoteMachineName: ok');
