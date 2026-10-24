import assert from 'node:assert/strict';
import { diskProtectionLabel, reclaimableBytes } from '../src/lib/shell/resources/workspaceSpaceViewModel.ts';

assert.equal(diskProtectionLabel('safe-candidate'), 'Safe to review');
assert.equal(reclaimableBytes({ bytes: 100, reclaimableBytes: 60, protection: 'safe-candidate' }), 60);
assert.equal(reclaimableBytes({ bytes: 100, reclaimableBytes: 60, protection: 'dirty' }), 0);
console.log('workspace space view model tests passed');
