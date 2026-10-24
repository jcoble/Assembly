import assert from 'node:assert/strict';
import { diskProtectionLabel, filterWorkspaceEntries, reclaimableBytes } from '../src/lib/shell/resources/workspaceSpaceViewModel.ts';

assert.equal(reclaimableBytes({ bytes: 100, reclaimableBytes: 80, protection: 'safe-candidate' }), 80);
assert.equal(reclaimableBytes({ bytes: 100, reclaimableBytes: 80, protection: 'dirty' }), 0);
assert.equal(diskProtectionLabel('safe-candidate'), 'Safe to review');
const entries = [
  { id: 'one', repositoryId: 'repo', workspaceId: 'Alpha workspace', path: '/alpha', kind: 'worktree', bytes: 1, reclaimableBytes: 1, protection: 'safe-candidate', topLevelItems: [] },
  { id: 'two', repositoryId: 'repo', workspaceId: 'Beta workspace', path: '/beta', kind: 'worktree', bytes: 1, reclaimableBytes: 1, protection: 'safe-candidate', topLevelItems: [] }
];
assert.deepEqual(filterWorkspaceEntries(entries, 'alpha').map((entry) => entry.id), ['one']);
assert.equal(filterWorkspaceEntries(entries, '  ').length, 2);
console.log('space view-model tests passed');
