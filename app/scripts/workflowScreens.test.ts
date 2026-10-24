// The Workflows screen's two pure functions: which Board column a run sits in, and where
// each step's box goes on the Map. Logic only.
import assert from 'node:assert/strict';
import { boardColumn, costText, stepGlyph } from '../src/lib/shell/workflows/workflowBoard.ts';
import { layoutWorkflowMap } from '../src/lib/shell/workflows/workflowMapLayout.ts';
import type { WorkflowNodeState, WorkflowRunRecord, WorkflowRunState } from '../src/lib/shell/workflows/workflowTypes.ts';

const STEPS = ['spec', 'spec-review', 'build', 'checks', 'code-review', 'proof', 'pr', 'pr-feedback', 'merge'];

function run(state: WorkflowRunState, states: Partial<Record<string, WorkflowNodeState>>): WorkflowRunRecord {
  return {
    state,
    definition: { nodes: STEPS.map((id, index) => ({ id, title: id, dependsOn: index ? [STEPS[index - 1]] : [] })) },
    nodes: STEPS.map((nodeId) => ({ nodeId, state: states[nodeId] ?? 'blocked', startedAtMs: ['blocked', 'ready', 'queued'].includes(states[nodeId] ?? 'blocked') ? null : 1 }))
  } as unknown as WorkflowRunRecord;
}

{ // board_columns_follow_the_runs_current_step
  assert.equal(boardColumn(run('draft', {})), 'queued');
  assert.equal(boardColumn(run('queued', {})), 'queued');
  assert.equal(boardColumn(run('running', { spec: 'running' })), 'spec');
  assert.equal(boardColumn(run('running', { spec: 'completed', 'spec-review': 'running' })), 'spec');
  assert.equal(boardColumn(run('running', { spec: 'completed', 'spec-review': 'completed', build: 'starting' })), 'build');
  assert.equal(boardColumn(run('running', { build: 'completed', checks: 'running' })), 'check');
  assert.equal(boardColumn(run('running', { checks: 'completed', 'code-review': 'running' })), 'review');
  assert.equal(boardColumn(run('running', { 'code-review': 'completed', proof: 'running' })), 'proof');
  assert.equal(boardColumn(run('running', { proof: 'completed', pr: 'running' })), 'pr');
  assert.equal(boardColumn(run('running', { pr: 'completed', 'pr-feedback': 'running' })), 'pr');
  assert.equal(boardColumn(run('running', { 'pr-feedback': 'completed', merge: 'running' })), 'merge');
  // Between steps, the run sits where its last started step is.
  assert.equal(boardColumn(run('running', { build: 'completed', checks: 'ready' })), 'build');
}

{ // waiting_runs_go_to_needs_you_and_ended_runs_to_done_or_where_they_stopped
  assert.equal(boardColumn(run('waiting-input', { checks: 'waiting-input' })), 'needs-you');
  assert.equal(boardColumn(run('running', { 'code-review': 'waiting-approval' })), 'needs-you');
  assert.equal(boardColumn(run('completed', { merge: 'completed' })), 'done');
  assert.equal(boardColumn(run('cancelled', { build: 'cancelled' })), 'done');
  // A failed run stays in the column of the step that failed, so it reads as stuck there.
  assert.equal(boardColumn(run('failed', { build: 'completed', checks: 'failed' })), 'check');
  // A paused run stays where it is.
  assert.equal(boardColumn(run('paused', { build: 'completed', checks: 'completed', 'code-review': 'ready' })), 'check');
}

{ // the_map_lays_steps_out_by_needs_depth_in_file_order
  const layout = layoutWorkflowMap([
    { id: 'build', dependsOn: [] },
    { id: 'checks', dependsOn: ['build'] },
    { id: 'review-a', dependsOn: ['checks'] },
    { id: 'review-b', dependsOn: ['checks'] },
    { id: 'pr', dependsOn: ['review-a', 'review-b'] },
    { id: 'notes', dependsOn: [] }
  ]);
  assert.deepEqual(layout.boxes, [
    { id: 'build', column: 0, row: 0 },
    { id: 'checks', column: 1, row: 0 },
    { id: 'review-a', column: 2, row: 0 },
    { id: 'review-b', column: 2, row: 1 },
    { id: 'pr', column: 3, row: 0 },
    { id: 'notes', column: 0, row: 1 }
  ]);
  assert.deepEqual([layout.columns, layout.rows], [4, 2]);
  // A need on a step that is not in the file, or a cycle, does not hang or throw.
  assert.deepEqual(layoutWorkflowMap([{ id: 'a', dependsOn: ['b'] }, { id: 'b', dependsOn: ['a'] }]).boxes.length, 2);
}

{ // the_layout_is_computed_once_per_workflow_file
  const nodes = [{ id: 'a', dependsOn: [] }, { id: 'b', dependsOn: ['a'] }];
  assert.equal(layoutWorkflowMap(nodes), layoutWorkflowMap(structuredClone(nodes)));
  assert.notEqual(layoutWorkflowMap(nodes), layoutWorkflowMap([{ id: 'a', dependsOn: [] }]));
}


{ // step_glyphs_name_each_state_and_a_step_waiting_again_was_sent_back
  const glyph = (state: WorkflowNodeState, attempt = 1) => stepGlyph({ state, attempt });
  assert.deepEqual(
    [glyph('completed'), glyph('running'), glyph('failed'), glyph('waiting-input'), glyph('cancelled'), glyph('skipped'), glyph('blocked'), glyph('ready', 2)],
    ['passed', 'running', 'failed', 'needs-you', 'stopped', 'skipped', 'waiting', 'sent-back']
  );
}


{ // costs_read_reported_first_and_label_estimates
  const row = (reportedCostMicros: number | null, estimatedCostMicros: number | null) => ({ reportedCostMicros, estimatedCostMicros });
  assert.equal(costText([row(1_070_000, null)]), '$1.07');
  assert.equal(costText([row(1_070_000, 200_000)]), '$1.07 + $0.20 est.');
  assert.equal(costText([row(1_070_000, null), row(null, 740_000)]), '$1.07 + $0.74 est.');
  assert.equal(costText([row(null, 740_000)]), '$0.74 est.');
  assert.equal(costText([row(null, null)]), '');
  assert.equal(costText([]), '');
}

console.log('workflow screens tests passed');
