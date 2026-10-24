import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import {
  appendOrchestrationEvent,
  appendOrchestrationEventPayload,
  appendOrchestrationSample,
  defaultOrchestrationEventStorePath,
  normalizeOrchestrationEvent,
  orchestrationEventInputsFromPayload,
  orchestrationSampleEvents,
  orchestrationEventStorePath,
  parseOrchestrationEventArgs,
  readOrchestrationEventPayloadFile
} from './orchestrationEvent.mjs';

const execFileAsync = promisify(execFile);

async function readStoredEvents(storePath) {
  const { stdout } = await execFileAsync('sqlite3', [
    storePath,
    'SELECT payload_json FROM orchestration_events ORDER BY rowid ASC'
  ]);
  return stdout.trim() ? stdout.trim().split('\n').map((line) => JSON.parse(line)) : [];
}

const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'mcb-orch-event-test-'));
try {
  assert.equal(
    defaultOrchestrationEventStorePath('/tmp/home'),
    '/tmp/home/Library/Application Support/MacCommandBar/sessions.db'
  );
  assert.equal(
    orchestrationEventStorePath({
      MAC_COMMAND_BAR_ORCHESTRATION_DB: '/tmp/custom-sessions.db'
    }),
    '/tmp/custom-sessions.db'
  );

  const parsed = parseOrchestrationEventArgs([
    '--',
    '--run-id',
    'run-tsk-127',
    '--project-id',
    'mac-command-bar',
    '--task-id',
    'TSK-127',
    '--agent-provider',
    'codex',
    '--step-kind',
    'test',
    '--resolved-count',
    '3',
    '--issue-id',
    'AUTH-7',
    '--retry-attempt',
    '2',
    '--approval-subject',
    'Delete dirty worktree?',
    '--blocker-reason',
    'Needs manual review',
    '--decision-prompt',
    'Proceed with cleanup?'
  ]);
  assert.equal(parsed.runId, 'run-tsk-127');
  assert.equal(parsed.projectID, 'mac-command-bar');
  assert.equal(parsed.taskID, 'TSK-127');
  assert.equal(parsed.agentProvider, 'codex');
  assert.equal(parsed.stepKind, 'test');
  assert.equal(parsed.resolvedCount, '3');
  assert.equal(parsed.issueID, 'AUTH-7');
  assert.equal(parsed.retryAttempt, '2');
  assert.equal(parsed.approvalSubject, 'Delete dirty worktree?');
  assert.equal(parsed.blockerReason, 'Needs manual review');
  assert.equal(parsed.decisionPrompt, 'Proceed with cleanup?');

  const jsonFileParsed = parseOrchestrationEventArgs(['--json-file', '/tmp/orch-events.jsonl']);
  assert.equal(jsonFileParsed.jsonFile, '/tmp/orch-events.jsonl');

  const presetParsed = parseOrchestrationEventArgs([
    '--preset',
    'scenario-started',
    '--run-id',
    'run-tsk-127',
    '--scenario',
    'Trading partner setup',
    '--agent-provider',
    'codex'
  ]);
  assert.equal(presetParsed.preset, 'scenario-started');
  assert.equal(presetParsed.scenario, 'Trading partner setup');
  assert.equal(presetParsed.agentProvider, 'codex');

  const scenarioEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'scenario-started',
    scenario: 'Trading partner setup',
    taskID: 'TSK-127'
  });
  assert.equal(scenarioEvent.kind, 'scenario.started');
  assert.equal(scenarioEvent.status, 'running');
  assert.equal(scenarioEvent.stepKind, 'scenario');
  assert.equal(scenarioEvent.title, 'Scenario started: Trading partner setup');
  assert.equal(scenarioEvent.message, 'Trading partner setup');
  assert.equal(scenarioEvent.scenario, 'Trading partner setup');

  const resolvedEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'fix-resolved',
    issueId: 'AUTH-7',
    resolvedCount: '3',
    failedCount: '1'
  });
  assert.equal(resolvedEvent.kind, 'fix.resolved');
  assert.equal(resolvedEvent.status, 'succeeded');
  assert.equal(resolvedEvent.stepKind, 'fix');
  assert.equal(resolvedEvent.title, 'Fix resolved: AUTH-7');
  assert.equal(resolvedEvent.resolvedCount, 3);
  assert.equal(resolvedEvent.failedCount, 1);
  assert.match(resolvedEvent.message, /3 resolved/);
  assert.match(resolvedEvent.message, /1 failed/);
  assert.throws(
    () =>
      normalizeOrchestrationEvent({
        runId: 'run-tsk-127',
        preset: 'fix-resolved',
        resolvedCount: 'many'
      }),
    /resolvedCount must be a non-negative integer/
  );

  const issueEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'issue-found',
    issueId: 'AUTH-7',
    scenario: 'Google auth callback'
  });
  assert.equal(issueEvent.kind, 'issue.found');
  assert.equal(issueEvent.status, 'needs-fix');
  assert.equal(issueEvent.stepKind, 'issue');
  assert.equal(issueEvent.title, 'Issue found: AUTH-7');
  assert.equal(issueEvent.issueID, 'AUTH-7');
  assert.equal(issueEvent.scenario, 'Google auth callback');

  const retryEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'retry-started',
    issueId: 'AUTH-7',
    scenario: 'Google auth callback',
    retryAttempt: '2'
  });
  assert.equal(retryEvent.kind, 'retry.started');
  assert.equal(retryEvent.status, 'running');
  assert.equal(retryEvent.stepKind, 'retest');
  assert.equal(retryEvent.retryAttempt, 2);
  assert.equal(retryEvent.issueID, 'AUTH-7');

  const blockerEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'blocker-reported',
    blockerReason: 'Cannot infer destructive cleanup',
    decisionPrompt: 'Delete the dirty worktree?'
  });
  assert.equal(blockerEvent.kind, 'blocker.reported');
  assert.equal(blockerEvent.status, 'blocked');
  assert.equal(blockerEvent.stepKind, 'blocker');
  assert.equal(blockerEvent.blockerReason, 'Cannot infer destructive cleanup');
  assert.equal(blockerEvent.decisionPrompt, 'Delete the dirty worktree?');

  const delegatedEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'batch-delegated',
    resolvedCount: '3',
    agentProvider: 'codex',
    agentRole: 'fix-agent'
  });
  assert.equal(delegatedEvent.kind, 'batch.delegated');
  assert.equal(delegatedEvent.status, 'running');
  assert.equal(delegatedEvent.stepKind, 'fix');
  assert.equal(delegatedEvent.title, 'Fix batch delegated');
  assert.equal(delegatedEvent.resolvedCount, 3);
  assert.match(delegatedEvent.message, /3 resolved/);

  const agentEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'agent-started',
    agentProvider: 'claude',
    agentRole: 'ui-tester'
  });
  assert.equal(agentEvent.kind, 'agent.started');
  assert.equal(agentEvent.status, 'running');
  assert.equal(agentEvent.title, 'Agent started: ui-tester');

  const verifiedEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'ui-verified',
    scenario: 'Trading partner setup'
  });
  assert.equal(verifiedEvent.kind, 'ui.verified');
  assert.equal(verifiedEvent.status, 'succeeded');
  assert.equal(verifiedEvent.stepKind, 'retest');
  assert.equal(verifiedEvent.title, 'UI verified: Trading partner setup');

  const approvalEvent = normalizeOrchestrationEvent({
    runId: 'run-tsk-127',
    preset: 'approval-required',
    message: 'Delete dirty worktree?'
  });
  assert.equal(approvalEvent.kind, 'approval.required');
  assert.equal(approvalEvent.status, 'waiting-for-approval');
  assert.equal(approvalEvent.stepKind, 'approval');
  assert.equal(approvalEvent.title, 'Approval required');
  assert.equal(approvalEvent.message, 'Delete dirty worktree?');
  assert.equal(approvalEvent.approvalSubject, 'Delete dirty worktree?');

  const importedIssueEvent = normalizeOrchestrationEvent({
    event: 'issue_found',
    run_id: 'run-tsk-127',
    project: {
      id: 'mac-command-bar',
      name: 'MacCommandBar',
      path: '/repo'
    },
    root: {
      label: 'task worktree'
    },
    task: {
      id: 'TSK-127'
    },
    agent: {
      id: 'ui-tester-1',
      provider: 'claude',
      role: 'ui-tester'
    },
    step: {
      id: 'scenario-auth',
      kind: 'test'
    },
    scenario: {
      name: 'Google auth callback'
    },
    issue: {
      id: 'AUTH-7'
    },
    counts: {
      issues: 2,
      failed: 1
    },
    artifact: {
      id: 'screenshot-auth',
      type: 'screenshot',
      path: '/repo/.codex-artifacts/auth.png'
    },
    link: {
      type: 'trace',
      label: 'Trace',
      url: 'http://localhost:9323/trace'
    }
  });
  assert.equal(importedIssueEvent.kind, 'issue.found');
  assert.equal(importedIssueEvent.status, 'needs-fix');
  assert.equal(importedIssueEvent.runId, 'run-tsk-127');
  assert.equal(importedIssueEvent.projectID, 'mac-command-bar');
  assert.equal(importedIssueEvent.projectName, 'MacCommandBar');
  assert.equal(importedIssueEvent.projectPath, '/repo');
  assert.equal(importedIssueEvent.rootLabel, 'task worktree');
  assert.equal(importedIssueEvent.taskID, 'TSK-127');
  assert.equal(importedIssueEvent.agentId, 'ui-tester-1');
  assert.equal(importedIssueEvent.agentProvider, 'claude');
  assert.equal(importedIssueEvent.agentRole, 'ui-tester');
  assert.equal(importedIssueEvent.stepId, 'scenario-auth');
  assert.equal(importedIssueEvent.stepKind, 'test');
  assert.equal(importedIssueEvent.scenario, 'Google auth callback');
  assert.equal(importedIssueEvent.issueID, 'AUTH-7');
  assert.equal(importedIssueEvent.issueCount, 2);
  assert.equal(importedIssueEvent.failedCount, 1);
  assert.equal(importedIssueEvent.artifactId, 'screenshot-auth');
  assert.equal(importedIssueEvent.artifactKind, 'screenshot');
  assert.equal(importedIssueEvent.artifactPath, '/repo/.codex-artifacts/auth.png');
  assert.equal(importedIssueEvent.linkKind, 'trace');
  assert.equal(importedIssueEvent.linkLabel, 'Trace');
  assert.equal(importedIssueEvent.linkUrl, 'http://localhost:9323/trace');

  const aliasHeavyLoopEvent = normalizeOrchestrationEvent({
    kind: 'ui_retest_passed',
    run: 'run-tsk-127',
    workspace: {
      id: 'mac-command-bar',
      name: 'MacCommandBar',
      root: '/repo'
    },
    task: 'TSK-127',
    active_agent: {
      name: 'tester-1',
      provider: 'claude',
      agent_type: 'ui-tester'
    },
    stage: 'retest',
    workflow: 'Google auth callback',
    finding: {
      key: 'AUTH-7'
    },
    retry: '2',
    tallies: {
      found: 3,
      fixed: 2,
      retested: 1,
      ui_verified: 2,
      'needs-decision': 1,
      sign_offs: 1
    },
    evidence: [
      {
        type: 'trace',
        filePath: '/repo/.codex-artifacts/auth-trace.zip'
      }
    ],
    references: [
      {
        type: 'trace',
        label: 'Trace',
        href: 'http://localhost:9323/trace'
      }
    ]
  });
  assert.equal(aliasHeavyLoopEvent.kind, 'retest.passed');
  assert.equal(aliasHeavyLoopEvent.status, 'succeeded');
  assert.equal(aliasHeavyLoopEvent.runId, 'run-tsk-127');
  assert.equal(aliasHeavyLoopEvent.projectID, 'mac-command-bar');
  assert.equal(aliasHeavyLoopEvent.projectName, 'MacCommandBar');
  assert.equal(aliasHeavyLoopEvent.projectPath, '/repo');
  assert.equal(aliasHeavyLoopEvent.taskID, 'TSK-127');
  assert.equal(aliasHeavyLoopEvent.agentId, 'tester-1');
  assert.equal(aliasHeavyLoopEvent.agentProvider, 'claude');
  assert.equal(aliasHeavyLoopEvent.agentRole, 'ui-tester');
  assert.equal(aliasHeavyLoopEvent.stepKind, 'retest');
  assert.equal(aliasHeavyLoopEvent.scenario, 'Google auth callback');
  assert.equal(aliasHeavyLoopEvent.issueID, 'AUTH-7');
  assert.equal(aliasHeavyLoopEvent.retryAttempt, 2);
  assert.equal(aliasHeavyLoopEvent.issueCount, 3);
  assert.equal(aliasHeavyLoopEvent.resolvedCount, 2);
  assert.equal(aliasHeavyLoopEvent.retestCount, 1);
  assert.equal(aliasHeavyLoopEvent.verifiedCount, 2);
  assert.equal(aliasHeavyLoopEvent.decisionCount, 1);
  assert.equal(aliasHeavyLoopEvent.approvalCount, 1);
  assert.equal(aliasHeavyLoopEvent.artifactKind, 'trace');
  assert.equal(aliasHeavyLoopEvent.artifactPath, '/repo/.codex-artifacts/auth-trace.zip');
  assert.equal(aliasHeavyLoopEvent.linkKind, 'trace');
  assert.equal(aliasHeavyLoopEvent.linkUrl, 'http://localhost:9323/trace');

  const aliasDecisionEvent = normalizeOrchestrationEvent({
    event: 'needs_decision',
    run_id: 'run-tsk-127',
    signOffSubject: 'Merge delegated fixes?',
    blocker: 'Manual approval required before merge',
    waitingDecision: 'Approve merge and handoff?'
  });
  assert.equal(aliasDecisionEvent.kind, 'approval.required');
  assert.equal(aliasDecisionEvent.status, 'waiting-for-approval');
  assert.equal(aliasDecisionEvent.approvalSubject, 'Merge delegated fixes?');
  assert.equal(aliasDecisionEvent.blockerReason, 'Manual approval required before merge');
  assert.equal(aliasDecisionEvent.decisionPrompt, 'Approve merge and handoff?');

  const importedDecisionEvent = normalizeOrchestrationEvent({
    type: 'approval_required',
    runID: 'run-tsk-127',
    task_id: 'TSK-127',
    signoff: {
      subject: 'Review dirty worktree backup'
    },
    blocker: {
      reason: 'Uncommitted work needs backup before cleanup'
    },
    decision: {
      prompt: 'Back up and remove this worktree?'
    },
    counts: {
      approvals: 1,
      decisions: 1
    }
  });
  assert.equal(importedDecisionEvent.kind, 'approval.required');
  assert.equal(importedDecisionEvent.status, 'waiting-for-approval');
  assert.equal(importedDecisionEvent.runId, 'run-tsk-127');
  assert.equal(importedDecisionEvent.taskID, 'TSK-127');
  assert.equal(importedDecisionEvent.approvalSubject, 'Review dirty worktree backup');
  assert.equal(importedDecisionEvent.blockerReason, 'Uncommitted work needs backup before cleanup');
  assert.equal(importedDecisionEvent.decisionPrompt, 'Back up and remove this worktree?');
  assert.equal(importedDecisionEvent.approvalCount, 1);
  assert.equal(importedDecisionEvent.decisionCount, 1);

  const payloadEvents = orchestrationEventInputsFromPayload({
    run_id: 'run-tsk-127',
    project: {
      id: 'mac-command-bar',
      name: 'MacCommandBar',
      path: '/repo'
    },
    task: {
      id: 'TSK-127'
    },
    events: [
      {
        event: 'scenario_started',
        scenario: 'Trading partner auth callback',
        counts: {
          scenarios: 1
        }
      },
      {
        event: 'ui_verified',
        scenario: 'Trading partner auth callback',
        counts: {
          resolved: 1,
          verified: 1
        },
        artifact: {
          kind: 'trace',
          url: 'http://localhost:9323/trace'
        }
      }
    ]
  });
  assert.equal(payloadEvents.length, 2);
  assert.equal(payloadEvents[0].runId, 'run-tsk-127');
  assert.equal(payloadEvents[0].taskID, 'TSK-127');
  assert.equal(payloadEvents[0].kind, 'scenario.started');
  assert.equal(payloadEvents[0].scenarioCount, 1);
  assert.equal(payloadEvents[1].kind, 'ui.verified');
  assert.equal(payloadEvents[1].verifiedCount, 1);
  assert.equal(payloadEvents[1].artifactKind, 'trace');
  assert.equal(payloadEvents[1].artifactUrl, 'http://localhost:9323/trace');

  const loopEnvelopeEvents = orchestrationEventInputsFromPayload({
    run: {
      id: 'run-e2e-42'
    },
    project: {
      id: 'mac-command-bar',
      name: 'MacCommandBar',
      path: '/repo'
    },
    task: {
      id: 'TSK-142'
    },
    activeAgent: {
      id: 'orchestrator',
      provider: 'codex',
      role: 'orchestrator'
    },
    tally: {
      scenarios: 2,
      issues: 3,
      fixes: 2,
      retested: 2,
      ui_verified: 1,
      needs_decision: 1,
      sign_offs: 1
    },
    scenario_batches: [
      {
        id: 'batch-auth',
        title: 'Auth and settings smoke',
        scenarios: [
          {
            id: 'scenario-auth-callback',
            name: 'Google auth callback',
            status: 'succeeded'
          },
          {
            id: 'scenario-settings-save',
            name: 'Settings save',
            status: 'failed',
            issue: {
              id: 'SETTINGS-2'
            }
          }
        ],
        issues: [
          {
            id: 'AUTH-7',
            scenario: 'Google auth callback',
            title: 'Callback redirect loop',
            status: 'needs-fix'
          },
          {
            id: 'SETTINGS-2',
            scenario: 'Settings save',
            title: 'Save button never enables',
            status: 'needs-fix'
          }
        ],
        fix_tasks: [
          {
            id: 'fix-auth',
            issue_id: 'AUTH-7',
            title: 'Patch callback route',
            agent: {
              id: 'fixer-auth',
              provider: 'claude',
              role: 'fix-agent'
            },
            status: 'delegated'
          },
          {
            id: 'fix-settings',
            issue_id: 'SETTINGS-2',
            title: 'Repair dirty form state',
            agent: {
              id: 'fixer-settings',
              provider: 'codex',
              role: 'fix-agent'
            },
            status: 'completed'
          }
        ],
        retests: [
          {
            id: 'retest-auth',
            issue_id: 'AUTH-7',
            scenario: 'Google auth callback',
            attempt: 2,
            status: 'passed',
            resolved: 1,
            verified: 1,
            artifact: {
              type: 'trace',
              path: '/repo/.codex-artifacts/auth-trace.zip'
            }
          },
          {
            id: 'retest-settings',
            issue_id: 'SETTINGS-2',
            scenario: 'Settings save',
            attempt: 1,
            status: 'failed',
            failed: 1,
            artifact: {
              type: 'screenshot',
              path: '/repo/.codex-artifacts/settings-failed.png'
            }
          }
        ],
        ui_proof_artifacts: [
          {
            id: 'proof-auth',
            type: 'trace',
            path: '/repo/.codex-artifacts/auth-trace.zip',
            scenario: 'Google auth callback',
            issue_id: 'AUTH-7'
          }
        ],
        handoff: {
          id: 'handoff-auth',
          path: '/repo/.codex-artifacts/run-e2e-handoff.md'
        },
        decision: {
          subject: 'Merge delegated auth/settings fixes?',
          prompt: 'Approve merge after one passing retest and one known failed retest?',
          reason: 'Human sign-off required before merging partial fix batch'
        }
      }
    ]
  });
  assert.equal(loopEnvelopeEvents.length, 10);
  assert.deepEqual(
    loopEnvelopeEvents.map((event) => [
      event.kind,
      event.stepKind,
      event.scenario,
      event.issueID,
      event.agentId,
      event.artifactKind,
      event.artifactPath
    ]),
    [
      ['scenario.passed', 'scenario', 'Google auth callback', null, 'orchestrator', null, null],
      ['test.failed', 'test', 'Settings save', 'SETTINGS-2', 'orchestrator', null, null],
      ['issue.found', 'issue', 'Google auth callback', 'AUTH-7', 'orchestrator', null, null],
      ['issue.found', 'issue', 'Settings save', 'SETTINGS-2', 'orchestrator', null, null],
      ['batch.delegated', 'fix', null, 'AUTH-7', 'fixer-auth', null, null],
      ['batch.delegated', 'fix', null, 'SETTINGS-2', 'fixer-settings', null, null],
      ['ui.verified', 'retest', 'Google auth callback', 'AUTH-7', 'orchestrator', 'trace', '/repo/.codex-artifacts/auth-trace.zip'],
      ['retest.failed', 'retest', 'Settings save', 'SETTINGS-2', 'orchestrator', 'screenshot', '/repo/.codex-artifacts/settings-failed.png'],
      ['ui.verified', 'retest', 'Google auth callback', 'AUTH-7', 'orchestrator', 'trace', '/repo/.codex-artifacts/auth-trace.zip'],
      ['approval.required', 'approval', null, null, 'orchestrator', 'handoff', '/repo/.codex-artifacts/run-e2e-handoff.md']
    ]
  );
  assert.equal(loopEnvelopeEvents[0].runId, 'run-e2e-42');
  assert.equal(loopEnvelopeEvents[0].projectID, 'mac-command-bar');
  assert.equal(loopEnvelopeEvents[0].taskID, 'TSK-142');
  assert.equal(loopEnvelopeEvents[0].scenarioCount, 2);
  assert.equal(loopEnvelopeEvents[0].issueCount, 3);
  assert.equal(loopEnvelopeEvents[0].fixCount, 2);
  assert.equal(loopEnvelopeEvents[0].retestCount, 2);
  assert.equal(loopEnvelopeEvents[0].verifiedCount, 1);
  assert.equal(loopEnvelopeEvents[0].decisionCount, 1);
  assert.equal(loopEnvelopeEvents[0].approvalCount, 1);
  assert.equal(loopEnvelopeEvents[4].status, 'delegated');
  assert.equal(loopEnvelopeEvents[5].status, 'completed');
  assert.equal(loopEnvelopeEvents[6].retryAttempt, 2);
  assert.equal(loopEnvelopeEvents[6].resolvedCount, 1);
  assert.equal(loopEnvelopeEvents[6].verifiedCount, 1);
  assert.equal(loopEnvelopeEvents[7].failedCount, 1);
  assert.equal(loopEnvelopeEvents[9].approvalSubject, 'Merge delegated auth/settings fixes?');
  assert.equal(loopEnvelopeEvents[9].blockerReason, 'Human sign-off required before merging partial fix batch');
  assert.equal(loopEnvelopeEvents[9].decisionPrompt, 'Approve merge after one passing retest and one known failed retest?');

  const sampleEvents = orchestrationSampleEvents('run-e2e-loop', {
    runId: 'run-tsk-127',
    projectID: 'mac-command-bar',
    projectName: 'MacCommandBar',
    projectPath: '/repo',
    rootLabel: 'main checkout',
    taskID: 'TSK-127',
    timestamp: '2026-06-10T12:00:00.000Z'
  });
  assert.ok(sampleEvents.length >= 9);
  assert.equal(sampleEvents[0].kind, 'run.started');
  assert.equal(sampleEvents[1].scenario, 'Trading partner Google auth callback');
  assert.equal(sampleEvents.some((event) => event.issueID === 'AUTH-7'), true);
  assert.equal(sampleEvents.some((event) => event.retryAttempt === 1), true);
  assert.equal(sampleEvents.some((event) => event.approvalSubject === 'Review batched fixes'), true);
  assert.equal(sampleEvents.some((event) => event.blockerReason === 'Manual sign-off required before merge'), true);
  assert.equal(sampleEvents.some((event) => event.artifactKind === 'handoff'), true);

  const storePath = path.join(tempRoot, 'events.db');
  const { event } = await appendOrchestrationEvent(
    {
      runId: 'run-tsk-127',
      preset: 'retest-passed',
      scenario: 'Trading partner setup',
      taskID: 'TSK-127'
    },
    { storePath }
  );
  const storedEvents = await readStoredEvents(storePath);
  assert.equal(storedEvents.length, 1);
  assert.deepEqual(storedEvents[0], event);
  assert.equal(event.schemaVersion, 1);
  assert.equal(event.runId, 'run-tsk-127');
  assert.equal(event.taskID, 'TSK-127');
  assert.equal(event.kind, 'retest.passed');
  assert.equal(event.status, 'succeeded');
  assert.equal(event.stepKind, 'retest');
  assert.equal(event.artifactPath, null);

  const sampleStorePath = path.join(tempRoot, 'sample-events.db');
  const sampleResult = await appendOrchestrationSample(
    'run-e2e-loop',
    {
      runId: 'run-tsk-127',
      projectID: 'mac-command-bar',
      projectName: 'MacCommandBar',
      projectPath: '/repo',
      taskID: 'TSK-127',
      timestamp: '2026-06-10T12:00:00.000Z'
    },
    { storePath: sampleStorePath }
  );
  const storedSampleEvents = await readStoredEvents(sampleStorePath);
  assert.equal(storedSampleEvents.length, sampleResult.events.length);
  assert.equal(storedSampleEvents[0].kind, 'run.started');
  assert.equal(storedSampleEvents.at(-1).kind, 'handoff.available');

  const payloadStorePath = path.join(tempRoot, 'payload-events.db');
  const payloadResult = await appendOrchestrationEventPayload(
    {
      run_id: 'run-tsk-127',
      project: {
        id: 'mac-command-bar',
        name: 'MacCommandBar'
      },
      events: [
        {
          event: 'agent_started',
          agent: {
            provider: 'codex',
            role: 'fix-agent'
          }
        },
        {
          event: 'handoff',
          artifact: {
            kind: 'handoff',
            path: '/repo/.codex-artifacts/handoff.md'
          }
        }
      ]
    },
    { storePath: payloadStorePath }
  );
  const storedPayloadEvents = await readStoredEvents(payloadStorePath);
  assert.equal(payloadResult.events.length, 2);
  assert.equal(storedPayloadEvents.length, 2);
  assert.equal(storedPayloadEvents[0].kind, 'agent.started');
  assert.equal(storedPayloadEvents[1].kind, 'handoff.available');

  const payloadFilePath = path.join(tempRoot, 'import-events.jsonl');
  await fs.writeFile(
    payloadFilePath,
    [
      JSON.stringify({
        event: 'test_failed',
        run_id: 'run-tsk-127',
        issue_id: 'AUTH-7',
        counts: {
          tests: 1,
          failed: 1
        }
      }),
      JSON.stringify({
        event: 'batch_delegated',
        run_id: 'run-tsk-127',
        counts: {
          fixes: 1,
          delegated: 1
        }
      })
    ].join('\n')
  );
  const importedPayloadEvents = await readOrchestrationEventPayloadFile(payloadFilePath);
  assert.equal(importedPayloadEvents.length, 2);
  assert.equal(importedPayloadEvents[0].kind, 'test.failed');
  assert.equal(importedPayloadEvents[0].testCount, 1);
  assert.equal(importedPayloadEvents[1].kind, 'batch.delegated');
  assert.equal(importedPayloadEvents[1].delegatedCount, 1);
} finally {
  await fs.rm(tempRoot, { recursive: true, force: true });
}
