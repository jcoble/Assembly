import './svelteRuneTestSetup.ts';
import assert from 'node:assert/strict';
import type { OwnedSession } from '../src/lib/shell/ownedSessions.ts';
import { hydrateOwned, rail, replaceConnectedRemoteRows } from '../src/lib/shell/stores/sessionRailStore.svelte.ts';

// Regression 03a10064b: after a machine connects in Settings -> Connections, a
// row left from a removed or recreated profile with the same owned id must not
// stay as a duplicate. replaceConnectedRemoteRows is that rule moved out of
// RemoteConnections.svelte unchanged so it can be tested.
const row = (ownedId: string, remoteProfileId: string | null = null) =>
  ({ ownedId, title: ownedId, executionEnvironment: remoteProfileId ? 'remote' : 'local', remoteProfileId }) as unknown as OwnedSession;
const shown = () => rail.owned.map((s) => `${s.ownedId}@${s.remoteProfileId ?? 'local'}`);

hydrateOwned([row('local-1'), row('other-row', 'other'), row('moved', 'removed-machine'), row('gone', 'machine'), row('legacy', 'agent-workbox')]);
replaceConnectedRemoteRows(['machine', 'agent-workbox'], [row('moved', 'machine'), row('new', 'machine')]);
assert.deepEqual(shown(), ['local-1@local', 'other-row@other', 'moved@machine', 'new@machine'],
  'local rows and other machines stay; the connected and replaced profiles and any incoming owned id are replaced');

hydrateOwned([row('local-1'), row('gone', 'machine')]);
replaceConnectedRemoteRows(['machine', null], []);
assert.deepEqual(shown(), ['local-1@local'], 'no replaced profile leaves local rows alone');
console.log('remoteRowReplacement: all tests passed');
