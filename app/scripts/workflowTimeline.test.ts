// The run timeline merges a run's ledger rows, the messages sent during it and
// what its snapshot holds (the PR link, proof files) into one list in time order.
// Logic only.
import assert from 'node:assert/strict';
import { buildWorkflowTimeline } from '../src/lib/shell/workflows/workflowTimeline.ts';
import type { WorkflowHistoryRow, WorkflowRunRecord } from '../src/lib/shell/workflows/workflowTypes.ts';
import type { AgentMessage } from '../src/lib/tauriSource.ts';

const RUN = 'workflow-run-1';

function row(sequence: number, timestampMs: number, kind: string, nodeId: string | null, extra: Partial<WorkflowHistoryRow> = {}): WorkflowHistoryRow {
  return {
    sequence, timestampMs, kind, nodeId, attempt: nodeId ? 1 : null, ownedId: nodeId ? `agent-${nodeId}` : null,
    status: null, commands: [], findingCount: null, verdict: null, files: [], answer: null, gateApproved: null, ...extra
  };
}

function message(id: string, createdAtMs: number, extra: Partial<AgentMessage>): AgentMessage {
  return {
    id, createdAtMs, senderName: 'sender', fromOwnedId: 'someone', toOwnedId: 'someone-else', receiver: 'Main',
    body: '', receipt: 'delivered', runId: RUN, kind: 'message', machine: 'mac', ...extra
  };
}

function node(nodeId: string, structuredOutput: unknown, finishedAtMs: number) {
  return { id: `${RUN}:${nodeId}`, nodeId, roleId: nodeId, state: 'completed', attempt: 1, depth: 0, ownedId: `agent-${nodeId}`,
    provider: '', providerInstanceId: null, startedAtMs: finishedAtMs - 1, finishedAtMs, outputContract: 'VerificationReceipt',
    structuredOutput, artifacts: [], gate: null, leaseId: null, failure: null, usage: null };
}

{ // builds_timeline_in_time_order_from_history_messages_and_snapshot
  const history: WorkflowHistoryRow[] = [
    row(1, 1, 'workflow.run.created', null),
    row(2, 1, 'workflow.run.started', null),
    row(3, 2, 'workflow.node.starting', 'spec'),
    row(4, 2, 'workflow.node.running', 'spec'),
    row(5, 3, 'workflow.scheduler.settled', null),
    row(6, 4, 'workflow.node.agent-finished', 'spec'),
    // Bookkeeping rows are left out, but they still tell whose session is whose.
    row(16, 4.5, 'workflow.node.starting', 'build'),
    row(15, 5, 'workflow.node.redo', 'spec-review', { attempt: 2 }),
    row(7, 6, 'workflow.node.owner-answered', 'spec-review', {
      answer: { step: 'spec-review', decision: 'another-round', reply: 'Try once more' }
    }),
    row(8, 8, 'workflow.node.agent-finished', 'code-review', { verdict: 'pass', findingCount: 0 }),
    row(9, 10, 'workflow.node.running', 'pr'),
    row(10, 10.5, 'workflow.node.command-recorded', 'pr', { commands: [{ command: 'git push', exitCode: 0 }] }),
    row(11, 11, 'workflow.node.commands-finished', 'pr', { status: 'failed', findingCount: 1, commands: [
      { command: 'git push', exitCode: 0 }, { command: 'gh pr create', exitCode: 1 }
    ] }),
    row(12, 20, 'workflow.node.retried', 'pr', { attempt: 2 }),
    row(13, 21, 'workflow.node.running', 'pr', { attempt: 2 }),
    row(14, 22, 'workflow.node.commands-finished', 'pr', { attempt: 2, status: 'completed', findingCount: 0, commands: [
      { command: 'git push', exitCode: 0 }, { command: 'gh pr create', exitCode: 0 }
    ] })
  ];
  // Messages arrive newest first, as the message list returns them.
  const messages: AgentMessage[] = [
    message('m3', 12, { fromOwnedId: `workflow:${RUN}`, senderName: 'workflow · Dev pipeline', body: '[TSK-1 failed] pr: One or more commands failed' }),
    message('m1', 3, { fromOwnedId: 'agent-spec', toOwnedId: 'agent-build', receiver: 'build session', body: 'Spec is ready' })
  ];
  const run = {
    id: RUN,
    definition: { nodes: ['spec', 'build', 'spec-review', 'code-review', 'proof', 'pr'].map((id) => ({ id, title: id })) },
    // A review round that found something keeps its findings only here.
    inputSnapshot: { cwd: '/repo', rounds: { 'spec-review': [['a', 'b', 'c']] } },
    nodes: [
      node('proof', { summary: 'done', files: ['proof/a.png'] }, 9),
      node('pr', { commands: [
        { command: 'git push', exitCode: 0, output: 'visit https://github.com/o/r/pull/new/branch to open one' },
        { command: 'gh pr create', exitCode: 0, output: 'https://github.com/o/r/pull/42\n' }
      ] }, 22)
    ]
  } as unknown as WorkflowRunRecord;

  const items = buildWorkflowTimeline(history, messages, run);
  assert.deepEqual(items.map((item) => item.title), [
    'Run started',
    'spec started',
    'spec → build',
    'spec finished',
    'spec-review round 1: fail',
    'You answered spec-review: another round',
    'code-review round 1: pass',
    'Proof: 1 file',
    'pr started',
    'pr: 1 of 2 failed',
    'Workflow → Main',
    'You retried pr',
    'pr started',
    'pr: 2 of 2 passed',
    'PR opened'
  ]);

  const times = items.map((item) => item.atMs);
  assert.deepEqual(times, [...times].sort((a, b) => a - b), 'items are in time order');

  const review = items.find((item) => item.title === 'spec-review round 1: fail');
  assert.equal(review?.nodeId, 'spec-review');
  assert.equal(review?.detail, '3 findings');
  assert.equal(items.find((item) => item.kind === 'owner')?.detail, '“Try once more”');

  const sent = items.findLast((item) => item.kind === 'message');
  assert.equal(sent?.detail, 'pr: One or more commands failed');
  assert.equal(sent?.receipt, 'delivered');

  // A retried step shows both attempts, each with its own result.
  const prResults = items.filter((item) => item.nodeId === 'pr' && item.kind === 'checks');
  assert.deepEqual(prResults.map((item) => [item.attempt, item.verdict, item.tone]), [[1, 'fail', 'bad'], [2, 'pass', 'good']]);

  // The PR link is the first real pull request URL in the pr step's command output.
  const pr = items.at(-1);
  assert.equal(pr?.url, 'https://github.com/o/r/pull/42');
  assert.equal(pr?.detail, 'o/r #42');

  // Proof files come from the proof step, resolved against the run's checkout.
  assert.deepEqual(items.find((item) => item.kind === 'proof')?.files, ['/repo/proof/a.png']);
}

{ // command step redo after on_fail shows failed commands, not a review round
  // Rows shaped like a real run of a command step with `on_fail`: a `command-recorded` row after each
  // command (each carries the commands so far), then `workflow.node.redo` or `workflow.node.findings`.
  // No `commands-finished` row is written for a failed attempt.
  const lint = { command: 'pnpm lint', exitCode: 0 };
  const test = { command: 'pnpm test', exitCode: 1 };
  const history: WorkflowHistoryRow[] = [
    row(1, 1, 'workflow.node.running', 'checks'),
    row(2, 2, 'workflow.node.command-recorded', 'checks', { commands: [lint] }),
    row(3, 3, 'workflow.node.command-recorded', 'checks', { commands: [lint, test] }),
    row(4, 4, 'workflow.node.redo', 'checks', { attempt: 2 }),
    row(5, 5, 'workflow.node.running', 'checks', { attempt: 2 }),
    row(6, 6, 'workflow.node.command-recorded', 'checks', { attempt: 2, commands: [lint] }),
    row(7, 7, 'workflow.node.command-recorded', 'checks', { attempt: 2, commands: [lint, test] }),
    row(8, 8, 'workflow.node.findings', 'checks', { attempt: 2, findingCount: 1, commands: [lint, test] })
  ];
  const run = {
    id: RUN,
    definition: { nodes: [{ id: 'checks', title: 'checks', step: { run: ['pnpm lint', 'pnpm test'], on_fail: 'build' } }] },
    inputSnapshot: { rounds: { checks: [['pnpm test'], ['pnpm test']] } },
    nodes: []
  } as unknown as WorkflowRunRecord;

  const items = buildWorkflowTimeline(history, [], run);
  assert.deepEqual(items.map((item) => item.title), [
    'checks started', 'checks: 1 of 2 failed', 'checks started', 'checks: 1 of 2 failed'
  ]);
  assert.ok(items.every((item) => item.kind !== 'review'), 'a command step has no review round');
  const failed = items.filter((item) => item.kind === 'checks');
  assert.deepEqual(failed.map((item) => [item.attempt, item.verdict, item.tone, item.detail]),
    [[1, 'fail', 'bad', 'pnpm test'], [2, 'fail', 'bad', 'pnpm test']]);
}

{ // an answer by the main orchestrator says so and shows its why
  const history: WorkflowHistoryRow[] = [
    row(1, 1, 'workflow.node.owner-answered', 'review', {
      answer: { step: 'review', decision: 'accept', reply: null, by: 'orchestrator', why: 'Both findings are wording nits the spec allows.' }
    })
  ];
  const run = { id: RUN, definition: { nodes: [{ id: 'review', title: 'review' }] }, inputSnapshot: {}, nodes: [] } as unknown as WorkflowRunRecord;
  const [item] = buildWorkflowTimeline(history, [], run);
  assert.deepEqual([item.title, item.detail], ['Orchestrator answered review: accept', 'Both findings are wording nits the spec allows.']);
  assert.equal(item.kind, 'orchestrator', 'it does not read as the owner');
}

console.log('workflowTimeline tests passed');
