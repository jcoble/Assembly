import assert from 'node:assert/strict';
import { mockIPC, clearMocks } from '@tauri-apps/api/mocks';
import type { CliStatus, CliUpdateState } from '../src/lib/shell/providers/providerCliUpdateService.svelte.ts';
globalThis.window = {} as Window & typeof globalThis;
Object.defineProperty(window, 'crypto', { value: globalThis.crypto });
const { checkProviderClis, updateProviderCli } = await import('../src/lib/shell/providers/providerCliUpdateService.svelte.ts');
const mac: CliUpdateState = { checking: false, updating: null, statuses: [], message: '' };
const remote: CliUpdateState = { checking: false, updating: null, statuses: [], message: '' };
const versions: CliStatus[] = [
  { provider: 'codex', version: 'codex-cli 0.161.0', error: null },
  { provider: 'claude', version: '2.1.293 (Claude Code)', error: null }
];
let calls = 0;
let resolveCheck: (value: CliStatus[]) => void = () => { throw new Error('No check'); };
mockIPC((command, args) => {
  assert.equal(command, 'check_provider_clis'); assert.deepEqual(args, { profileId: 'workbox' });
  calls++; return new Promise<CliStatus[]>(resolve => { resolveCheck = resolve; });
});
const aborted = new AbortController();
const pendingCheck = checkProviderClis(remote, aborted.signal, 'workbox');
await checkProviderClis(remote, aborted.signal, 'workbox');
assert.equal(calls, 1);
aborted.abort(); resolveCheck(versions); await pendingCheck;
assert.deepEqual(remote.statuses, [], 'closed owner ignores a late check');
assert.equal(remote.checking, false);
mockIPC(() => versions);
const owner = new AbortController();
await checkProviderClis(remote, owner.signal, 'workbox');
await checkProviderClis(mac, owner.signal);
let resolveUpdate: (value: CliStatus) => void = () => { throw new Error('No update'); };
mockIPC((command, args) => {
  assert.equal(command, 'update_provider_cli');
  assert.deepEqual(args, { profileId: 'workbox', provider: 'codex' });
  return new Promise<CliStatus>(resolve => { resolveUpdate = resolve; });
});
const update = updateProviderCli('codex', remote, owner.signal, 'workbox');
assert.equal(remote.updating, 'codex'); assert.equal(mac.updating, null);
await updateProviderCli('claude', remote, owner.signal, 'workbox');
resolveUpdate({ ...versions[0], version: 'codex-cli 0.162.0' }); await update;
assert.equal(remote.statuses[0].version, 'codex-cli 0.162.0');
assert.equal(remote.statuses[1].version, versions[1].version);
assert.equal(mac.statuses[0].version, versions[0].version);
assert.equal(remote.updating, null);
assert.match(remote.message, /installed codex-cli 0.162.0/);
mockIPC(() => { throw new Error('SSH permission denied'); });
await updateProviderCli('claude', remote, owner.signal, 'workbox');
assert.match(remote.message, /failed.*SSH permission denied/);
assert.equal(remote.statuses[1].version, versions[1].version);
assert.equal(remote.updating, null);
clearMocks();
console.log('CLI service: host isolation, provider isolation, single flight, late checks and errors passed');
