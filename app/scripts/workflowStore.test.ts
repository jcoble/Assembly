// TSK-1167 regression pins for the Workflows view's run cache. Logic only.
import assert from 'node:assert/strict';
import {
  applyWorkflowSnapshots,
  beginWorkflowLoad,
  failWorkflowLoad,
  resetWorkflowStore,
  selectWorkflowRun,
  upsertWorkflowSnapshot,
  workflowState
} from '../src/lib/shell/workflows/workflowStore.svelte.ts';
import type { WorkflowRunRecord } from '../src/lib/shell/workflows/workflowTypes.ts';

const run = (id: string, lastSequence: number, state = 'running') =>
  ({ id, lastSequence, state, workflowId: 'flow', definition: { name: 'Flow' }, nodes: [] }) as unknown as WorkflowRunRecord;
const shown = () => workflowState.runs.map((each) => [each.id, each.lastSequence]);

{ // an_older_snapshot_never_replaces_a_newer_one (5f2b3d195)
  // A live update can arrive before a slower list read that was started earlier; the view keeps the newest.
  resetWorkflowStore();
  upsertWorkflowSnapshot(run('a', 5));
  upsertWorkflowSnapshot(run('a', 4));
  assert.deepEqual(shown(), [['a', 5]]);
  upsertWorkflowSnapshot(run('a', 5, 'paused'));
  assert.equal(workflowState.runs[0].state, 'paused', 'the same sequence again is taken');
  applyWorkflowSnapshots([run('a', 3), run('b', 1)]);
  assert.deepEqual(shown(), [['a', 5], ['b', 1]]);
  applyWorkflowSnapshots([run('a', 6)]);
  assert.deepEqual(shown(), [['a', 6]], 'a list read drops runs it no longer has');
}

{ // a_selected_run_that_disappears_moves_the_selection_to_the_first_run
  resetWorkflowStore();
  applyWorkflowSnapshots([run('a', 1), run('b', 1)]);
  selectWorkflowRun('b');
  workflowState.selectedNodeId = 'build';
  applyWorkflowSnapshots([run('a', 2)]);
  assert.deepEqual([workflowState.selectedWorkflowRunId, workflowState.selectedNodeId], ['a', null]);
  applyWorkflowSnapshots([]);
  assert.equal(workflowState.selectedWorkflowRunId, null);
  // Without a desktop engine the list says so instead of looking empty.
  applyWorkflowSnapshots(null);
  assert.equal(workflowState.unavailableReason, 'Workflow runs are available in the desktop app.');
}

{ // an_action_error_survives_live_updates_but_a_load_error_clears_on_the_next_read
  // Runs are re-read on every change; the owner must still be able to read why an action failed.
  resetWorkflowStore();
  workflowState.error = 'Retry: Only a failed or waiting node can be retried';
  beginWorkflowLoad();
  applyWorkflowSnapshots([run('a', 1)]);
  upsertWorkflowSnapshot(run('a', 2));
  assert.equal(workflowState.error, 'Retry: Only a failed or waiting node can be retried');
  failWorkflowLoad('Could not load workflows: offline');
  applyWorkflowSnapshots([run('a', 3)]);
  assert.equal(workflowState.error, null, 'a read that works again clears its own failure');
}

console.log('workflowStore tests passed');
