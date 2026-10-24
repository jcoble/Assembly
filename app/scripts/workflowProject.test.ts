// The Project tab and the Board's project card: a project run's slices are the runs whose
// parent_task is its task; each slice shows its latest run. Logic only.
import assert from 'node:assert/strict';
import { projectColumn, projectIndex, projectProgress as progressOf, runGlyph, sliceCount, type ProjectProgress } from '../src/lib/shell/workflows/workflowProject.ts';
import type { WorkflowNodeState, WorkflowRunRecord, WorkflowRunState } from '../src/lib/shell/workflows/workflowTypes.ts';

const STEPS = ['build', 'checks', 'pr'];

/** The cards' path: one index of the run list, then each card's progress read from it. */
const projectProgress = (project: WorkflowRunRecord, runs: WorkflowRunRecord[]): ProjectProgress | null =>
  progressOf(project, projectIndex(runs));

function run(id: string, key: string, state: WorkflowRunState, createdAtMs: number,
  inputs: Record<string, unknown> = {}, states: Partial<Record<string, WorkflowNodeState>> = {}): WorkflowRunRecord {
  return {
    id, state, createdAtMs, updatedAtMs: createdAtMs,
    inputSnapshot: { task: { key, title: `Title of ${key}` }, inputs },
    definition: { name: 'Task', nodes: STEPS.map((nodeId, index) => ({ id: nodeId, title: nodeId, dependsOn: index ? [STEPS[index - 1]] : [] })) },
    nodes: STEPS.map((nodeId) => ({ nodeId, state: states[nodeId] ?? 'blocked', attempt: 1,
      startedAtMs: ['blocked', 'ready', 'queued'].includes(states[nodeId] ?? 'blocked') ? null : 1 }))
  } as unknown as WorkflowRunRecord;
}

const project = run('p', 'TSK-1582', 'completed', 1, {}, { build: 'completed', checks: 'completed', pr: 'completed' });
const merged = run('s1', 'TSK-1601', 'completed', 2, { parent_task: 'TSK-1582', slice: '1.0' }, { build: 'completed', checks: 'completed', pr: 'completed' });
const failed = run('s2', 'TSK-1602', 'failed', 3, { parent_task: 'TSK-1582', slice: '1.1' }, { build: 'completed', checks: 'failed' });
const queued = run('s3', 'TSK-1603', 'queued', 4, { parent_task: 'TSK-1582', slice: '1.2' });
const other = run('o', 'TSK-9', 'running', 5, { parent_task: 'TSK-1' }, { build: 'running' });

{ // a_run_with_no_child_runs_is_not_a_project
  assert.equal(projectProgress(project, [project, other]), null);
  // An empty parent_task (the workflow's default) is no parent.
  assert.equal(projectProgress(project, [project, run('e', 'TSK-5', 'running', 6, { parent_task: '' })]), null);
  // A run without a task key has no children.
  assert.equal(projectProgress(run('x', '', 'completed', 1), [run('y', 'TSK-6', 'running', 2, { parent_task: '' })]), null);
  assert.equal(projectColumn(project, projectIndex([project, other])), 'done');
}

{ // only_the_latest_run_of_the_project_task_holds_its_slices
  const earlier = run('p0', 'TSK-1582', 'failed', 0, {}, { build: 'failed' });
  assert.equal(projectProgress(earlier, [earlier, project, merged]), null);
  assert.equal(projectProgress(project, [earlier, project, merged])?.total, 1);
}

{ // child_runs_group_into_slices_in_start_order_with_merged_counted
  const progress = projectProgress(project, [queued, other, failed, project, merged]);
  assert.ok(progress);
  assert.deepEqual(progress.slices.map((slice) => [slice.key, slice.number, slice.run.id, slice.glyph]), [
    ['TSK-1601', '1.0', 's1', 'passed'],
    ['TSK-1602', '1.1', 's2', 'failed'],
    ['TSK-1603', '1.2', 's3', 'waiting']
  ]);
  assert.deepEqual([progress.done, progress.total], [1, 3]);
  assert.equal(sliceCount(progress), '1 / 3 slices');
  // The current slice is the first not merged: here the failed one, so the project card sits where it failed.
  assert.equal(progress.current?.key, 'TSK-1602');
  assert.equal(projectColumn(project, projectIndex([project, merged, failed, queued])), 'check');
}

{ // a_slice_run_again_shows_its_latest_run
  const again = run('s2b', 'TSK-1602', 'running', 7, { parent_task: 'TSK-1582', slice: '1.1' }, { build: 'running' });
  const progress = projectProgress(project, [project, merged, failed, queued, again]);
  assert.deepEqual(progress?.slices.map((slice) => slice.run.id), ['s1', 's2b', 's3']);
  assert.equal(projectColumn(project, projectIndex([project, merged, failed, queued, again])), 'build');
}

{ // every_slice_merged_puts_the_project_in_done
  const progress = projectProgress(project, [project, merged]);
  assert.equal(sliceCount(progress!), '1 / 1 slices');
  assert.equal(progress?.current, null);
  assert.equal(projectColumn(project, projectIndex([project, merged])), 'done');
  // A slice not started yet puts the project card in Queued.
  assert.equal(projectColumn(project, projectIndex([project, merged, queued])), 'queued');
}

{ // run_glyphs_follow_the_run_state
  assert.equal(runGlyph(run('a', 'T', 'running', 1, {}, { build: 'running' })), 'running');
  assert.equal(runGlyph(run('a', 'T', 'running', 1, {}, { build: 'waiting-input' })), 'needs-you');
  assert.equal(runGlyph(run('a', 'T', 'cancelled', 1)), 'stopped');
  assert.equal(runGlyph(run('a', 'T', 'draft', 1)), 'waiting');
  assert.equal(runGlyph(run('a', 'T', 'paused', 1)), 'waiting');
}

{ // the_index_is_built_in_one_pass_and_every_card_reads_only_from_it
  // A few thousand runs: 200 projects of 10 slice runs each (one slice run twice), plus 800 plain runs.
  const many: WorkflowRunRecord[] = [];
  for (let p = 0; p < 200; p++) {
    many.push(run(`p${p}`, `TSK-P${p}`, 'completed', p * 100));
    for (let s = 0; s < 10; s++) {
      many.push(run(`p${p}s${s}`, `TSK-P${p}-${s}`, s < 4 ? 'completed' : s === 4 ? 'failed' : 'queued', p * 100 + s + 1, { parent_task: `TSK-P${p}` }));
    }
    many.push(run(`p${p}again`, `TSK-P${p}-4`, 'running', p * 100 + 50, { parent_task: `TSK-P${p}` }, { build: 'running' }));
  }
  for (let o = 0; o < 800; o++) many.push(run(`o${o}`, `TSK-O${o}`, 'running', o, {}, { build: 'running' }));
  // Count every pass over the run list: building the index is the only one.
  let passes = 0;
  const counted = new Proxy(many, { get(target, name, receiver) {
    if (name === Symbol.iterator) passes++;
    return Reflect.get(target, name, receiver);
  } });
  const index = projectIndex(counted);
  const progress = many.map((candidate) => progressOf(candidate, index));
  const columns = many.map((candidate) => projectColumn(candidate, index));
  assert.equal(passes, 1);
  assert.equal(many.length, 3200);
  // The same answers as a fresh index per card.
  for (const [at, candidate] of [many[0], many[12], many[2399], many[3199]].entries()) {
    assert.deepEqual(progressOf(candidate, projectIndex(many)), progress[many.indexOf(candidate)], `card ${at}`);
  }
  const first = progress[0];
  assert.deepEqual([first?.done, first?.total, first?.current?.key, first?.current?.run.id], [4, 10, 'TSK-P0-4', 'p0again']);
  assert.equal(columns[0], 'build');
  assert.equal(progress.filter(Boolean).length, 200);
}

console.log('workflowProject tests passed');
