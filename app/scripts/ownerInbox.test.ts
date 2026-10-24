import assert from 'node:assert/strict';
import { workflowMachine } from '../src/lib/shell/workflows/workflowTypes.ts';

{ // inbox_routes_answers_to_owning_machine
  // An answer goes to the machine that owns the run: this Mac's engine, or that remote profile.
  assert.equal(workflowMachine('mac'), null);
  assert.equal(workflowMachine('workbox-test'), 'workbox-test');
}
console.log('ownerInbox tests passed');
