// The stage close-up: each reviewer of a review group per round with its verdict and
// findings, the cap decisions, and a proof step's evidence. Logic only.
import assert from 'node:assert/strict';
import { capDecisions, closeupKind, parseFinding, proofEvidence, proofImagePaths, reviewGroup, reviewRounds } from '../src/lib/shell/workflows/workflowCloseup.ts';
import type { WorkflowRunRecord } from '../src/lib/shell/workflows/workflowTypes.ts';

const RUN = 'workflow-run-1';

function run(nodes: { id: string; step: Record<string, unknown>; state?: string; attempt?: number; startedAtMs?: number | null; failure?: unknown; output?: unknown }[], snapshot: unknown = {}): WorkflowRunRecord {
  return {
    id: RUN,
    inputSnapshot: snapshot,
    definition: { nodes: nodes.map((node) => ({ id: node.id, title: `${node.id} title`, dependsOn: [], step: node.step })) },
    nodes: nodes.map((node) => ({ nodeId: node.id, state: node.state ?? 'completed', attempt: node.attempt ?? 1,
      startedAtMs: node.startedAtMs === undefined ? 1 : node.startedAtMs, failure: node.failure ?? null, structuredOutput: node.output ?? null }))
  } as unknown as WorkflowRunRecord;
}

const GROUP = run([
  { id: 'spec', step: { agent: {} } },
  { id: 'spec-review', step: { review: { of: 'spec' } }, attempt: 3, state: 'waiting-input',
    failure: { code: 'loop-stopped', message: 'Stopped after 2 rounds with 1 still found' } },
  { id: 'spec-adversarial', step: { review: { of: 'spec' } }, attempt: 3, state: 'running' },
  { id: 'code-review', step: { review: { of: 'build' } } },
  { id: 'proof', step: { agent: {} }, output: { summary: 'Shown in the app', files: ['proof/a.png', '/abs/b.txt'] } }
], {
  cwd: '/repo',
  answers: [
    { step: 'spec-review', decision: 'another-round', reply: null, by: 'orchestrator', why: 'The finding is in scope.' },
    { step: 'code-review', decision: 'accept', reply: null, by: 'owner', why: null },
    { step: 'spec-review', decision: 'accept', reply: 'Fine', by: 'owner' }
  ]
});

{ // closeup_kind_is_review_for_review_steps_and_proof_for_the_proof_step
  assert.equal(closeupKind(GROUP, 'spec-review'), 'review');
  assert.equal(closeupKind(GROUP, 'proof'), 'proof');
  assert.equal(closeupKind(GROUP, 'spec'), null);
  assert.equal(closeupKind(GROUP, 'missing'), null);
}

{ // review_group_is_every_review_step_sending_back_to_the_same_step
  assert.deepEqual(reviewGroup(GROUP, 'spec-adversarial'), ['spec-review', 'spec-adversarial']);
  assert.deepEqual(reviewGroup(GROUP, 'code-review'), ['code-review']);
  assert.deepEqual(reviewGroup(GROUP, 'spec'), []);
}

{ // findings_split_into_severity_tag_location_and_text
  assert.deepEqual(parseFinding('[P1] src/routes/+page.svelte (Lane B effect): Query the count on switch.'),
    { severity: 'P1', location: 'src/routes/+page.svelte (Lane B effect)', text: 'Query the count on switch.' });
  assert.deepEqual(parseFinding('src/a.ts:12: wrong name'), { severity: null, location: 'src/a.ts:12', text: 'wrong name' });
  assert.deepEqual(parseFinding('core/src/broker.rs:99-108 — REWORK: scope'), { severity: null, location: 'core/src/broker.rs:99-108', text: 'REWORK: scope' });
  assert.deepEqual(parseFinding('The plan misses the phone screen.'), { severity: null, location: null, text: 'The plan misses the phone screen.' });
  // A report the engine stored that is not a string still shows, as its JSON.
  assert.deepEqual(parseFinding({ file: 'a.ts' }), { severity: null, location: null, text: '{"file":"a.ts"}' });
}

{ // rounds_come_from_the_runs_review_reports_per_reviewer_and_attempt
  const reports = [
    { attemptId: `${RUN}:spec-review:1`, atMs: 10, report: { verdict: 'fail', findings: ['src/a.ts:1: one', 'two'] } },
    { attemptId: `${RUN}:spec-adversarial:1`, atMs: 11, report: { verdict: 'pass', findings: [] } },
    { attemptId: `${RUN}:code-review:1`, atMs: 12, report: { verdict: 'pass', findings: [] } },
    { attemptId: `${RUN}:spec-review:2`, atMs: 20, report: { verdict: 'fail', findings: ['[P2] still one'] } },
    { attemptId: `${RUN}:spec-adversarial:2`, atMs: 21, report: { verdict: 'fail', findings: [] } },
    { attemptId: `${RUN}:spec-review:3`, atMs: 30, report: { verdict: 'pass', findings: [] } }
  ];
  const reviewers = reviewRounds(GROUP, ['spec-review', 'spec-adversarial'], reports);
  assert.deepEqual(reviewers.map((reviewer) => [reviewer.nodeId, reviewer.title]), [['spec-review', 'spec-review title'], ['spec-adversarial', 'spec-adversarial title']]);
  assert.deepEqual(reviewers[0].rounds.map((round) => [round.round, round.verdict, round.findings.length, round.atMs]),
    [[1, 'fail', 2, 10], [2, 'fail', 1, 20], [3, 'pass', 0, 30]]);
  assert.deepEqual(reviewers[0].rounds[0].findings[0], { severity: null, location: 'src/a.ts:1', text: 'one' });
  assert.equal(reviewers[0].rounds[1].findings[0].severity, 'P2');
  // A reviewer at work on a round it has not reported yet shows that round with no verdict.
  assert.deepEqual(reviewers[1].rounds.map((round) => [round.round, round.verdict]), [[1, 'pass'], [2, 'fail'], [3, null]]);
  // No reports yet: only the round in progress.
  assert.deepEqual(reviewRounds(GROUP, ['spec-adversarial'], [])[0].rounds.map((round) => round.round), [3]);
  // A reviewer that never started has no rounds.
  const fresh = run([{ id: 'r', step: { review: { of: 'x' } }, state: 'blocked', startedAtMs: null }]);
  assert.deepEqual(reviewRounds(fresh, ['r'], [])[0].rounds, []);
}

{ // cap_decisions_keep_who_decided_and_why_for_the_group_only
  assert.deepEqual(capDecisions(GROUP, ['spec-review', 'spec-adversarial']), [
    { step: 'spec-review', decision: 'another-round', by: 'orchestrator', why: 'The finding is in scope.', reply: null },
    { step: 'spec-review', decision: 'accept', by: 'owner', why: null, reply: 'Fine' }
  ]);
  assert.deepEqual(capDecisions(run([]), ['x']), []);
}

{ // proof_evidence_is_the_steps_summary_and_files_resolved_against_the_run_folder
  assert.deepEqual(proofEvidence(GROUP, GROUP.nodes[4]), { summary: 'Shown in the app', files: ['/repo/proof/a.png', '/abs/b.txt'] });
  assert.deepEqual(proofEvidence(GROUP, GROUP.nodes[0]), { summary: '', files: [] });
}

{ // proof_images_are_read_from_the_machine_that_ran_the_run
  const files = ['/repo/proof/a.png', '/repo/proof/notes.txt', '/repo/proof/b.JPG'];
  assert.deepEqual(proofImagePaths(files, null), ['/repo/proof/a.png', '/repo/proof/b.JPG']);
  assert.deepEqual(proofImagePaths(files, 'workbox test'), ['assembly-remote://workbox%20test/repo/proof/a.png', 'assembly-remote://workbox%20test/repo/proof/b.JPG']);
}

console.log('workflowCloseup tests passed');
