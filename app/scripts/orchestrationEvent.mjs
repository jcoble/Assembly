import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';

export const orchestrationSchemaVersion = 1;
export const orchestrationEventStoreEnv = 'MAC_COMMAND_BAR_ORCHESTRATION_DB';

const execFileAsync = promisify(execFile);

const explicitKeyMap = new Map([
  ['run-id', 'runId'],
  ['project-id', 'projectID'],
  ['task-id', 'taskID'],
  ['issue-id', 'issueID']
]);

export function defaultOrchestrationEventStorePath(homeDirectory = os.homedir()) {
  return path.join(
    homeDirectory,
    'Library',
    'Application Support',
    'MacCommandBar',
    'sessions.db'
  );
}

export function orchestrationEventStorePath(env = process.env) {
  const configuredPath = env[orchestrationEventStoreEnv]?.trim();
  return configuredPath || defaultOrchestrationEventStorePath();
}

export function normalizeOrchestrationEventInput(input) {
  const project =
    objectValue(input.project) ??
    objectValue(input.repo) ??
    objectValue(input.repository) ??
    objectValue(input.workspace);
  const run = objectValue(input.run);
  const task = objectValue(input.task);
  const root =
    objectValue(input.root) ??
    objectValue(input.checkout) ??
    objectValue(input.worktree) ??
    objectValue(input.workspace);
  const agent =
    objectValue(input.agent) ??
    objectValue(input.activeAgent) ??
    objectValue(input.active_agent) ??
    objectValue(input.worker) ??
    objectValue(input.delegate);
  const step =
    objectValue(input.step) ??
    objectValue(input.stage) ??
    objectValue(input.batch) ??
    objectValue(input.fixBatch) ??
    objectValue(input.fix_batch);
  const scenario =
    objectValue(input.scenario) ??
    objectValue(input.testScenario) ??
    objectValue(input.test_scenario) ??
    objectValue(input.workflow);
  const issue =
    objectValue(input.issue) ??
    objectValue(input.finding) ??
    objectValue(input.bug) ??
    objectValue(input.defect);
  const artifact =
    objectValue(input.artifact) ??
    objectValue(input.handoff) ??
    firstObjectValue(input.artifacts) ??
    firstObjectValue(input.evidence) ??
    firstObjectValue(input.files) ??
    firstObjectValue(input.outputs);
  const link =
    objectValue(input.link) ??
    firstObjectValue(input.links) ??
    firstObjectValue(input.references);
  const counts =
    objectValue(input.counts) ??
    objectValue(input.metrics) ??
    objectValue(input.tally) ??
    objectValue(input.tallies) ??
    objectValue(input.loop) ??
    objectValue(input.autoResolve) ??
    objectValue(input.auto_resolve);
  const approval = objectValue(input.approval) ?? objectValue(input.signoff) ?? objectValue(input.signOff);
  const blocker = objectValue(input.blocker) ?? objectValue(input.blocked);
  const decision =
    objectValue(input.decision) ??
    objectValue(input.waitingDecision) ??
    objectValue(input.waiting_decision) ??
    objectValue(input.userDecision) ??
    objectValue(input.user_decision);
  const eventAlias = firstOptionalString(
    input.event,
    input.type,
    input.eventType,
    input.event_type,
    input.kind,
    input.action,
    input.name
  );

  return {
    ...input,
    preset: firstOptionalString(input.preset, presetFromEventAlias(eventAlias)),
    runId: firstOptionalString(
      input.runId,
      input.runID,
      input.run_id,
      input.run,
      run?.id,
      run?.runId,
      run?.runID,
      run?.run_id
    ),
    projectID: firstOptionalString(
      input.projectID,
      input.projectId,
      input.project_id,
      project?.id,
      project?.projectID,
      project?.projectId,
      project?.slug
    ),
    projectName: firstOptionalString(input.projectName, input.project_name, project?.name, project?.title),
    projectPath: firstOptionalString(
      input.projectPath,
      input.project_path,
      input.workspacePath,
      input.workspace_path,
      project?.path,
      project?.root,
      project?.workspacePath,
      project?.workspace_path
    ),
    rootLabel: firstOptionalString(input.rootLabel, input.root_label, root?.label, root?.name, root?.slug),
    taskID: firstOptionalString(
      input.taskID,
      input.taskId,
      input.task_id,
      input.task,
      task?.id,
      task?.key,
      task?.taskID,
      task?.taskId
    ),
    agentId: firstOptionalString(
      input.agentId,
      input.agentID,
      input.agent_id,
      input.activeAgentId,
      input.active_agent_id,
      agent?.id,
      agent?.name,
      agent?.label
    ),
    agentProvider: firstOptionalString(input.agentProvider, input.agent_provider, agent?.provider, agent?.model),
    agentRole: firstOptionalString(
      input.agentRole,
      input.agent_role,
      input.agentType,
      input.agent_type,
      agent?.role,
      agent?.type,
      agent?.agentType,
      agent?.agent_type
    ),
    stepId: firstOptionalString(input.stepId, input.stepID, input.step_id, step?.id, step?.key),
    stepKind: firstOptionalString(
      input.stepKind,
      input.step_kind,
      input.stage,
      input.phase,
      step?.kind,
      step?.type,
      step?.stage,
      step?.phase
    ),
    artifactId: firstOptionalString(input.artifactId, input.artifactID, input.artifact_id, artifact?.id, artifact?.key),
    artifactKind: firstOptionalString(input.artifactKind, input.artifact_kind, artifact?.kind, artifact?.type),
    artifactPath: firstOptionalString(input.artifactPath, input.artifact_path, artifact?.path, artifact?.file, artifact?.filePath),
    artifactUrl: firstOptionalString(
      input.artifactUrl,
      input.artifactURL,
      input.artifact_url,
      artifact?.url,
      artifact?.href,
      artifact?.uri
    ),
    linkKind: firstOptionalString(input.linkKind, input.link_kind, link?.kind, link?.type),
    linkLabel: firstOptionalString(input.linkLabel, input.link_label, link?.label, link?.title, link?.name),
    linkUrl: firstOptionalString(input.linkUrl, input.linkURL, input.link_url, link?.url, link?.href, link?.uri),
    scenario: firstOptionalString(
      input.scenario,
      input.scenarioName,
      input.scenario_name,
      input.workflow,
      scenario?.name,
      scenario?.title,
      scenario?.id
    ),
    issueID: firstOptionalString(
      input.issueID,
      input.issueId,
      input.issue_id,
      input.findingID,
      input.findingId,
      input.finding_id,
      issue?.id,
      issue?.key,
      issue?.issueID,
      issue?.issueId
    ),
    retryAttempt: firstDefined(input.retryAttempt, input.retry_attempt, input.attempt, input.retry, input.retestAttempt),
    approvalSubject: firstOptionalString(
      input.approvalSubject,
      input.approval_subject,
      input.signoffSubject,
      input.signOffSubject,
      input.sign_off_subject,
      approval?.subject,
      approval?.title,
      approval?.message,
      decision?.subject,
      decision?.title
    ),
    blockerReason: firstOptionalString(
      input.blockerReason,
      input.blocker_reason,
      optionalAliasString(input.blocker),
      optionalAliasString(input.blocked),
      blocker?.reason,
      blocker?.message,
      blocker?.title
    ),
    decisionPrompt: firstOptionalString(
      input.decisionPrompt,
      input.decision_prompt,
      optionalAliasString(input.decision),
      optionalAliasString(input.waitingDecision),
      optionalAliasString(input.waiting_decision),
      decision?.prompt,
      decision?.question,
      decision?.message
    ),
    scenarioCount: firstDefined(
      input.scenarioCount,
      input.scenario_count,
      counts?.scenarioCount,
      counts?.scenario,
      counts?.scenarios,
      counts?.created,
      counts?.createdCount
    ),
    issueCount: firstDefined(
      input.issueCount,
      input.issue_count,
      input.foundCount,
      input.found_count,
      counts?.issueCount,
      counts?.issue,
      counts?.issues,
      counts?.found,
      counts?.foundCount,
      counts?.findings,
      counts?.discovered
    ),
    testCount: firstDefined(input.testCount, input.test_count, counts?.testCount, counts?.test, counts?.tests),
    retestCount: firstDefined(
      input.retestCount,
      input.retest_count,
      input.retestedCount,
      input.retested_count,
      counts?.retestCount,
      counts?.retest,
      counts?.retests,
      counts?.retested,
      counts?.retestedCount
    ),
    fixCount: firstDefined(
      input.fixCount,
      input.fix_count,
      input.fixBatchCount,
      input.fix_batch_count,
      counts?.fixCount,
      counts?.fix,
      counts?.fixes,
      counts?.fixBatches,
      counts?.fix_batches,
      counts?.batches
    ),
    resolvedCount: firstDefined(
      input.resolvedCount,
      input.resolved_count,
      input.fixedCount,
      input.fixed_count,
      counts?.resolvedCount,
      counts?.resolved,
      counts?.fixed,
      counts?.fixedCount
    ),
    verifiedCount: firstDefined(
      input.verifiedCount,
      input.verified_count,
      input.uiVerifiedCount,
      input.ui_verified_count,
      counts?.verifiedCount,
      counts?.verified,
      counts?.uiVerified,
      counts?.ui_verified,
      counts?.passed,
      counts?.passedCount
    ),
    delegatedCount: firstDefined(
      input.delegatedCount,
      input.delegated_count,
      input.delegationCount,
      input.delegation_count,
      counts?.delegatedCount,
      counts?.delegated,
      counts?.delegations,
      counts?.assigned
    ),
    decisionCount: firstDefined(
      input.decisionCount,
      input.decision_count,
      input.needsDecisionCount,
      input.needs_decision_count,
      counts?.decisionCount,
      counts?.decision,
      counts?.decisions,
      counts?.needsDecision,
      counts?.needs_decision,
      counts?.['needs-decision'],
      counts?.pendingDecision,
      counts?.pending_decision
    ),
    approvalCount: firstDefined(
      input.approvalCount,
      input.approval_count,
      input.signoffCount,
      input.signOffCount,
      input.sign_off_count,
      counts?.approvalCount,
      counts?.approval,
      counts?.approvals,
      counts?.signoff,
      counts?.signoffs,
      counts?.signOffs,
      counts?.sign_offs
    ),
    failedCount: firstDefined(
      input.failedCount,
      input.failed_count,
      counts?.failedCount,
      counts?.failed,
      counts?.failures,
      counts?.blocked,
      counts?.blockers
    )
  };
}

export function normalizeOrchestrationEvent(input) {
  const expandedInput = applyOrchestrationEventPreset(normalizeOrchestrationEventInput(input));
  const runId = String(expandedInput.runId ?? '').trim();
  if (!runId) {
    throw new Error('runId is required');
  }

  return {
    schemaVersion: Number(expandedInput.schemaVersion ?? orchestrationSchemaVersion),
    id: String(expandedInput.id ?? `evt-${Date.now()}-${randomUUID().slice(0, 8)}`),
    runId,
    timestamp: String(expandedInput.timestamp ?? new Date().toISOString()),
    kind: String(expandedInput.kind ?? 'run.updated'),
    status: String(expandedInput.status ?? 'running'),
    title: optionalString(expandedInput.title),
    message: optionalString(expandedInput.message),
    projectID: optionalString(expandedInput.projectID),
    projectName: optionalString(expandedInput.projectName),
    projectPath: optionalString(expandedInput.projectPath),
    rootLabel: optionalString(expandedInput.rootLabel),
    taskID: optionalString(expandedInput.taskID),
    agentId: optionalString(expandedInput.agentId),
    agentProvider: optionalString(expandedInput.agentProvider),
    agentRole: optionalString(expandedInput.agentRole),
    stepId: optionalString(expandedInput.stepId),
    stepKind: optionalString(expandedInput.stepKind),
    artifactId: optionalString(expandedInput.artifactId),
    artifactKind: optionalString(expandedInput.artifactKind),
    artifactPath: optionalString(expandedInput.artifactPath),
    artifactUrl: optionalString(expandedInput.artifactUrl),
    linkKind: optionalString(expandedInput.linkKind),
    linkLabel: optionalString(expandedInput.linkLabel),
    linkUrl: optionalString(expandedInput.linkUrl),
    scenario: optionalString(expandedInput.scenario),
    issueID: optionalString(expandedInput.issueID) ?? optionalString(expandedInput.issueId),
    retryAttempt: optionalCount(expandedInput.retryAttempt, 'retryAttempt'),
    approvalSubject:
      optionalString(expandedInput.approvalSubject) ??
      approvalSubjectFromExpandedEvent(expandedInput),
    blockerReason: optionalString(expandedInput.blockerReason),
    decisionPrompt: optionalString(expandedInput.decisionPrompt),
    scenarioCount: optionalCount(expandedInput.scenarioCount, 'scenarioCount'),
    issueCount: optionalCount(expandedInput.issueCount, 'issueCount'),
    testCount: optionalCount(expandedInput.testCount, 'testCount'),
    retestCount: optionalCount(expandedInput.retestCount, 'retestCount'),
    fixCount: optionalCount(expandedInput.fixCount, 'fixCount'),
    resolvedCount: optionalCount(expandedInput.resolvedCount, 'resolvedCount'),
    verifiedCount: optionalCount(expandedInput.verifiedCount, 'verifiedCount'),
    delegatedCount: optionalCount(expandedInput.delegatedCount, 'delegatedCount'),
    decisionCount: optionalCount(expandedInput.decisionCount, 'decisionCount'),
    approvalCount: optionalCount(expandedInput.approvalCount, 'approvalCount'),
    failedCount: optionalCount(expandedInput.failedCount, 'failedCount')
  };
}

export function applyOrchestrationEventPreset(input) {
  const preset = optionalString(input.preset)?.toLowerCase();
  if (!preset) {
    return input;
  }

  const scenario = optionalString(input.scenario);
  const issueId = optionalString(input.issueID) ?? optionalString(input.issueId);
  const agentSubject = optionalString(input.agentRole) ?? optionalString(input.agentId) ?? optionalString(input.agentProvider);
  const subject = issueId ?? scenario ?? optionalString(input.title);
  const counts = orchestrationPresetCountMessage(input);
  const presets = {
    'run-started': {
      kind: 'run.started',
      status: 'running',
      title: 'Run started'
    },
    'run-completed': {
      kind: 'run.completed',
      status: 'succeeded',
      title: 'Run completed'
    },
    'scenario-started': {
      kind: 'scenario.started',
      status: 'running',
      stepKind: 'scenario',
      title: formatPresetTitle('Scenario started', scenario),
      message: scenario
    },
    'scenario-passed': {
      kind: 'scenario.passed',
      status: 'succeeded',
      stepKind: 'scenario',
      title: formatPresetTitle('Scenario passed', scenario),
      message: scenario
    },
    'test-started': {
      kind: 'test.started',
      status: 'running',
      stepKind: 'test',
      title: formatPresetTitle('Test started', subject),
      message: scenario
    },
    'test-failed': {
      kind: 'test.failed',
      status: 'failed',
      stepKind: 'test',
      title: formatPresetTitle('Test failed', subject),
      message: counts ?? scenario
    },
    'issue-found': {
      kind: 'issue.found',
      status: 'needs-fix',
      stepKind: 'issue',
      title: formatPresetTitle('Issue found', subject),
      message: counts ?? scenario
    },
    'agent-started': {
      kind: 'agent.started',
      status: 'running',
      title: formatPresetTitle('Agent started', agentSubject)
    },
    'agent-completed': {
      kind: 'agent.completed',
      status: 'succeeded',
      title: formatPresetTitle('Agent completed', agentSubject)
    },
    'agent-blocked': {
      kind: 'agent.blocked',
      status: 'waiting-for-approval',
      title: formatPresetTitle('Agent blocked', agentSubject)
    },
    'agent-delegated': {
      kind: 'agent.delegated',
      status: 'running',
      title: formatPresetTitle('Agent delegated', agentSubject ?? subject)
    },
    'batch-delegated': {
      kind: 'batch.delegated',
      status: 'running',
      stepKind: 'fix',
      title: formatPresetTitle('Fix batch delegated', subject),
      message: counts ?? scenario
    },
    'fix-started': {
      kind: 'fix.started',
      status: 'running',
      stepKind: 'fix',
      title: formatPresetTitle('Fix started', subject),
      message: scenario
    },
    'fix-resolved': {
      kind: 'fix.resolved',
      status: 'succeeded',
      stepKind: 'fix',
      title: formatPresetTitle('Fix resolved', subject),
      message: counts ?? scenario
    },
    'retest-started': {
      kind: 'retest.started',
      status: 'running',
      stepKind: 'retest',
      title: formatPresetTitle('Retest started', subject),
      message: scenario
    },
    'retry-started': {
      kind: 'retry.started',
      status: 'running',
      stepKind: 'retest',
      title: formatPresetTitle('Retry started', subject),
      message: scenario
    },
    'retest-passed': {
      kind: 'retest.passed',
      status: 'succeeded',
      stepKind: 'retest',
      title: formatPresetTitle('Retest passed', subject),
      message: counts ?? scenario
    },
    'retest-failed': {
      kind: 'retest.failed',
      status: 'failed',
      stepKind: 'retest',
      title: formatPresetTitle('Retest failed', subject),
      message: counts ?? scenario
    },
    'ui-verified': {
      kind: 'ui.verified',
      status: 'succeeded',
      stepKind: 'retest',
      title: formatPresetTitle('UI verified', subject),
      message: counts ?? scenario
    },
    'ui-failed': {
      kind: 'ui.failed',
      status: 'failed',
      stepKind: 'test',
      title: formatPresetTitle('UI failed', subject),
      message: counts ?? scenario
    },
    'approval-required': {
      kind: 'approval.required',
      status: 'waiting-for-approval',
      stepKind: 'approval',
      title: 'Approval required'
    },
    'approval-granted': {
      kind: 'approval.granted',
      status: 'succeeded',
      stepKind: 'approval',
      title: 'Approval granted'
    },
    'blocker-reported': {
      kind: 'blocker.reported',
      status: 'blocked',
      stepKind: 'blocker',
      title: 'Blocker reported'
    },
    'artifact-added': {
      kind: 'artifact.available',
      status: 'succeeded',
      stepKind: 'artifact',
      artifactKind: optionalString(input.artifactKind) ?? 'artifact',
      title: formatPresetTitle('Artifact available', optionalString(input.artifactKind))
    },
    handoff: {
      kind: 'handoff.available',
      status: 'succeeded',
      stepKind: 'handoff',
      artifactKind: 'handoff',
      title: 'Handoff available'
    }
  };
  const defaults = presets[preset];
  if (!defaults) {
    throw new Error(`Unknown orchestration event preset: ${input.preset}`);
  }

  return {
    ...defaults,
    ...input,
    kind: shouldUsePresetKind(input.kind, preset) ? defaults.kind : input.kind,
    status: input.status ?? defaults.status,
    title: input.title ?? defaults.title,
    message: input.message ?? defaults.message,
    stepKind: input.stepKind ?? defaults.stepKind,
    artifactKind: input.artifactKind ?? defaults.artifactKind
  };
}

export async function writeOrchestrationEventsToSqlite(storePath, events) {
  const rows = events.map((event) => orchestrationEventSqlValues(event));
  const inserts = rows
    .map(
      (row) =>
        `INSERT INTO orchestration_events (id, run_id, kind, timestamp, sequence, workflow_id, idempotency_key, payload_json) VALUES (${row.join(', ')});`
    )
    .join('\n');
  const sql = `BEGIN IMMEDIATE;
${orchestrationEventTableSql()}
${inserts}
COMMIT;`;
  try {
    await execFileAsync('sqlite3', [storePath, sql]);
  } catch (error) {
    throw new Error(`Could not write orchestration events to ${storePath}: ${error.message}`);
  }
}

function orchestrationEventTableSql() {
  return `CREATE TABLE IF NOT EXISTS orchestration_events (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    sequence INTEGER,
    workflow_id TEXT,
    idempotency_key TEXT,
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json))
);
CREATE INDEX IF NOT EXISTS orchestration_events_run_idx ON orchestration_events(run_id);
CREATE INDEX IF NOT EXISTS orchestration_events_workflow_idx ON orchestration_events(workflow_id, kind);
CREATE UNIQUE INDEX IF NOT EXISTS orchestration_events_idempotency_idx
    ON orchestration_events(run_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;`;
}

function orchestrationEventSqlValues(event) {
  return [
    sqlString(event.id),
    sqlString(event.runId),
    sqlString(event.kind),
    sqlString(event.timestamp),
    event.sequence === undefined || event.sequence === null ? 'NULL' : String(Number(event.sequence)),
    sqlString(event.workflowId),
    sqlString(event.idempotencyKey),
    sqlString(JSON.stringify(event))
  ];
}

function sqlString(value) {
  if (value === undefined || value === null) {
    return 'NULL';
  }
  return `'${String(value).replaceAll("'", "''")}'`;
}

export async function appendOrchestrationEvent(input, options = {}) {
  const event = normalizeOrchestrationEvent(input);
  const storePath = options.storePath ?? orchestrationEventStorePath(options.env);
  await fs.mkdir(path.dirname(storePath), { recursive: true });
  await writeOrchestrationEventsToSqlite(storePath, [event]);
  return { event, storePath };
}

export function orchestrationEventInputsFromPayload(payload) {
  const payloads = Array.isArray(payload) ? payload : [payload];
  return payloads.flatMap((entry) => {
    if (!isObjectLike(entry)) {
      throw new Error('Orchestration event payload must be a JSON object or array of objects');
    }

    const loopEvents = orchestrationLoopEnvelopeEventInputs(entry);
    if (loopEvents) {
      return loopEvents.map((event) => normalizeOrchestrationEvent(event));
    }

    const children = Array.isArray(entry.events)
      ? entry.events
      : Array.isArray(entry.items)
        ? entry.items
        : null;

    if (!children) {
      return [normalizeOrchestrationEvent(entry)];
    }

    const base = { ...entry };
    delete base.events;
    delete base.items;
    return children.map((child) => normalizeOrchestrationEvent({ ...base, ...child }));
  });
}

function orchestrationLoopEnvelopeEventInputs(entry) {
  const batches = Array.isArray(entry.scenario_batches)
    ? entry.scenario_batches
    : Array.isArray(entry.scenarioBatches)
      ? entry.scenarioBatches
      : null;
  if (!batches) return null;

  const base = orchestrationEnvelopeBase(entry);
  const tallyCounts = orchestrationEnvelopeTallyCounts(entry);
  const events = batches.flatMap((batch) => orchestrationScenarioBatchEventInputs(base, batch));
  if (events.length === 0) return [];

  return events.map((event, index) => (index === 0 ? { ...event, ...tallyCounts } : event));
}

function orchestrationEnvelopeBase(entry) {
  const base = { ...entry };
  delete base.events;
  delete base.items;
  delete base.scenario_batches;
  delete base.scenarioBatches;
  delete base.tally;
  delete base.tallies;
  delete base.counts;
  delete base.metrics;
  return base;
}

function orchestrationScenarioBatchEventInputs(base, batchValue) {
  const batch = objectValue(batchValue);
  if (!batch) return [];

  const events = [
    ...arrayValue(batch.scenarios).flatMap((scenario) => orchestrationScenarioEventInputs(base, batch, scenario)),
    ...arrayValue(batch.issues).flatMap((issue) => orchestrationIssueEventInputs(base, batch, issue)),
    ...arrayValue(batch.fix_tasks).flatMap((task) => orchestrationFixTaskEventInputs(base, batch, task)),
    ...arrayValue(batch.fixTasks).flatMap((task) => orchestrationFixTaskEventInputs(base, batch, task)),
    ...arrayValue(batch.retests).flatMap((retest) => orchestrationRetestEventInputs(base, batch, retest)),
    ...arrayValue(batch.ui_proof_artifacts).flatMap((artifact) =>
      orchestrationUiProofArtifactEventInputs(base, batch, artifact)
    ),
    ...arrayValue(batch.uiProofArtifacts).flatMap((artifact) =>
      orchestrationUiProofArtifactEventInputs(base, batch, artifact)
    )
  ];

  const decisionEvent = orchestrationDecisionEventInput(base, batch);
  if (decisionEvent) {
    events.push(decisionEvent);
  } else {
    const handoffEvent = orchestrationHandoffEventInput(base, batch);
    if (handoffEvent) events.push(handoffEvent);
  }

  return events;
}

function orchestrationScenarioEventInputs(base, batch, scenarioValue) {
  const scenario = objectValue(scenarioValue);
  if (!scenario) return [];

  const status = optionalString(scenario.status);
  const issue = objectValue(scenario.issue) ?? objectValue(scenario.finding);
  const failed = isFailureStatus(status);
  return [
    {
      ...base,
      preset: failed ? 'test-failed' : isSuccessStatus(status) ? 'scenario-passed' : 'scenario-started',
      status: failed ? 'failed' : undefined,
      stepId: firstOptionalString(scenario.stepId, scenario.step_id, scenario.id, scenario.key),
      scenario: firstOptionalString(scenario.name, scenario.title, scenario.id, scenario.key),
      issueID: firstOptionalString(scenario.issueID, scenario.issueId, scenario.issue_id, issue?.id, issue?.key),
      title: optionalString(scenario.title),
      message: optionalString(scenario.summary) ?? optionalString(scenario.message) ?? optionalString(batch.title)
    }
  ];
}

function orchestrationIssueEventInputs(base, batch, issueValue) {
  const issue = objectValue(issueValue);
  if (!issue) return [];

  return [
    {
      ...base,
      preset: 'issue-found',
      status: optionalString(issue.status),
      stepId: firstOptionalString(issue.stepId, issue.step_id, issue.id, issue.key),
      scenario: firstOptionalString(issue.scenario, issue.scenarioName, issue.scenario_name, batch.title),
      issueID: firstOptionalString(issue.issueID, issue.issueId, issue.issue_id, issue.id, issue.key),
      title: optionalString(issue.title),
      message: optionalString(issue.summary) ?? optionalString(issue.message) ?? optionalString(issue.description)
    }
  ];
}

function orchestrationFixTaskEventInputs(base, batch, taskValue) {
  const task = objectValue(taskValue);
  if (!task) return [];

  return [
    {
      ...base,
      preset: 'batch-delegated',
      status: optionalString(task.status),
      stepId: firstOptionalString(task.stepId, task.step_id, task.id, task.key),
      scenario: firstOptionalString(task.scenario, task.scenarioName, task.scenario_name),
      issueID: firstOptionalString(task.issueID, task.issueId, task.issue_id, task.issue, task.finding),
      title: optionalString(task.title),
      message: optionalString(task.summary) ?? optionalString(task.message),
      agent: objectValue(task.agent) ?? objectValue(task.delegate),
      delegatedCount: firstDefined(task.delegatedCount, task.delegated_count),
      fixCount: firstDefined(task.fixCount, task.fix_count)
    }
  ];
}

function orchestrationRetestEventInputs(base, batch, retestValue) {
  const retest = objectValue(retestValue);
  if (!retest) return [];

  const status = optionalString(retest.status);
  const preset = isFailureStatus(status)
    ? 'retest-failed'
    : isSuccessStatus(status)
      ? 'ui-verified'
      : 'retest-started';
  return [
    {
      ...base,
      preset,
      status: isFailureStatus(status) ? 'failed' : undefined,
      stepId: firstOptionalString(retest.stepId, retest.step_id, retest.id, retest.key),
      scenario: firstOptionalString(retest.scenario, retest.scenarioName, retest.scenario_name, batch.title),
      issueID: firstOptionalString(retest.issueID, retest.issueId, retest.issue_id, retest.issue, retest.finding),
      retryAttempt: firstDefined(retest.retryAttempt, retest.retry_attempt, retest.attempt, retest.retry),
      title: optionalString(retest.title),
      message: optionalString(retest.summary) ?? optionalString(retest.message),
      artifact: objectValue(retest.artifact) ?? firstObjectValue(retest.artifacts),
      retestCount: firstDefined(retest.retestCount, retest.retest_count),
      resolvedCount: firstDefined(retest.resolvedCount, retest.resolved_count, retest.resolved, retest.fixed),
      verifiedCount: firstDefined(retest.verifiedCount, retest.verified_count, retest.verified, retest.ui_verified),
      failedCount: firstDefined(retest.failedCount, retest.failed_count, retest.failed)
    }
  ];
}

function orchestrationUiProofArtifactEventInputs(base, batch, artifactValue) {
  const artifact = objectValue(artifactValue);
  if (!artifact) return [];

  return [
    {
      ...base,
      preset: 'ui-verified',
      stepId: firstOptionalString(artifact.stepId, artifact.step_id, artifact.id, artifact.key),
      scenario: firstOptionalString(artifact.scenario, artifact.scenarioName, artifact.scenario_name, batch.title),
      issueID: firstOptionalString(artifact.issueID, artifact.issueId, artifact.issue_id, artifact.issue, artifact.finding),
      title: optionalString(artifact.title),
      message: optionalString(artifact.summary) ?? optionalString(artifact.message) ?? 'UI proof artifact available',
      artifact
    }
  ];
}

function orchestrationDecisionEventInput(base, batch) {
  const decision =
    objectValue(batch.decision) ??
    objectValue(batch.signoff) ??
    objectValue(batch.signOff) ??
    objectValue(batch.approval);
  if (!decision) return null;

  return {
    ...base,
    preset: 'approval-required',
    scenario: firstOptionalString(decision.scenario, decision.scenarioName, decision.scenario_name),
    issueID: firstOptionalString(decision.issueID, decision.issueId, decision.issue_id, decision.issue, decision.finding),
    approvalSubject: firstOptionalString(decision.subject, decision.title, decision.message),
    blockerReason: firstOptionalString(decision.reason, decision.blockerReason, decision.blocker_reason),
    decisionPrompt: firstOptionalString(decision.prompt, decision.question, decision.message),
    artifact: objectValue(batch.handoff),
    artifactKind: objectValue(batch.handoff) ? 'handoff' : undefined,
    approvalCount: firstDefined(decision.approvalCount, decision.approval_count, decision.signoffCount, decision.signoff_count),
    decisionCount: firstDefined(decision.decisionCount, decision.decision_count)
  };
}

function orchestrationHandoffEventInput(base, batch) {
  const handoff = objectValue(batch.handoff);
  if (!handoff) return null;

  return {
    ...base,
    preset: 'handoff',
    message: optionalString(handoff.summary) ?? optionalString(handoff.message),
    artifact: handoff,
    artifactKind: 'handoff'
  };
}

function orchestrationEnvelopeTallyCounts(entry) {
  const counts = objectValue(entry.tally) ?? objectValue(entry.tallies) ?? objectValue(entry.counts) ?? objectValue(entry.metrics);
  if (!counts) return {};

  return {
    scenarioCount: firstDefined(counts.scenarioCount, counts.scenario_count, counts.scenario, counts.scenarios),
    issueCount: firstDefined(counts.issueCount, counts.issue_count, counts.issue, counts.issues, counts.found),
    testCount: firstDefined(counts.testCount, counts.test_count, counts.test, counts.tests),
    retestCount: firstDefined(counts.retestCount, counts.retest_count, counts.retest, counts.retests, counts.retested),
    fixCount: firstDefined(counts.fixCount, counts.fix_count, counts.fix, counts.fixes, counts.fixBatches, counts.fix_batches),
    resolvedCount: firstDefined(counts.resolvedCount, counts.resolved_count, counts.resolved, counts.fixed),
    verifiedCount: firstDefined(counts.verifiedCount, counts.verified_count, counts.verified, counts.uiVerified, counts.ui_verified),
    delegatedCount: firstDefined(counts.delegatedCount, counts.delegated_count, counts.delegated, counts.delegations),
    decisionCount: firstDefined(counts.decisionCount, counts.decision_count, counts.decision, counts.decisions, counts.needsDecision, counts.needs_decision),
    approvalCount: firstDefined(counts.approvalCount, counts.approval_count, counts.approval, counts.approvals, counts.signoff, counts.signoffs, counts.signOffs, counts.sign_offs),
    failedCount: firstDefined(counts.failedCount, counts.failed_count, counts.failed, counts.failures)
  };
}

export async function readOrchestrationEventPayloadFile(filePath) {
  const contents = await fs.readFile(filePath, 'utf8');
  const trimmed = contents.trim();
  if (!trimmed) {
    return [];
  }

  const payload = parseOrchestrationPayloadFileContents(trimmed);
  return orchestrationEventInputsFromPayload(payload);
}

export async function appendOrchestrationEventPayload(payload, options = {}) {
  const events = orchestrationEventInputsFromPayload(payload);
  const storePath = options.storePath ?? orchestrationEventStorePath(options.env);
  await fs.mkdir(path.dirname(storePath), { recursive: true });
  await writeOrchestrationEventsToSqlite(storePath, events);
  return { events, storePath };
}

export function orchestrationSampleEvents(sampleName, input = {}) {
  const sample = optionalString(sampleName)?.toLowerCase();
  if (sample !== 'run-e2e-loop') {
    throw new Error(`Unknown orchestration sample: ${sampleName}`);
  }

  const base = {
    runId: input.runId,
    projectID: input.projectID,
    projectName: input.projectName,
    projectPath: input.projectPath,
    rootLabel: input.rootLabel,
    taskID: input.taskID
  };
  const scenario = optionalString(input.scenario) ?? 'Trading partner Google auth callback';
  const issueID = optionalString(input.issueID) ?? optionalString(input.issueId) ?? 'AUTH-7';
  const baseTimestamp = optionalString(input.timestamp);
  const handoffPath =
    optionalString(input.artifactPath) ??
    (optionalString(input.projectPath)
      ? path.join(String(input.projectPath), '.codex-artifacts', 'run-e2e-tests-handoff.md')
      : null);

  return [
    {
      preset: 'run-started',
      title: 'E2E auto-resolve loop started',
      message: 'Scenario-driven browser loop started'
    },
    {
      preset: 'scenario-started',
      scenario,
      scenarioCount: 1,
      agentProvider: 'codex',
      agentRole: 'orchestrator',
      agentId: 'orchestrator'
    },
    {
      preset: 'test-failed',
      scenario,
      issueID,
      message: 'Browser scenario found a Google auth callback regression',
      issueCount: 1,
      failedCount: 1,
      agentProvider: 'claude',
      agentRole: 'ui-tester',
      agentId: 'ui-tester'
    },
    {
      preset: 'issue-found',
      scenario,
      issueID,
      message: 'Redirect mismatch after external provider callback',
      issueCount: 1,
      agentProvider: 'claude',
      agentRole: 'ui-tester',
      agentId: 'ui-tester'
    },
    {
      preset: 'batch-delegated',
      scenario,
      issueID,
      message: 'Fix batch delegated to implementation agent',
      fixCount: 1,
      delegatedCount: 1,
      agentProvider: 'codex',
      agentRole: 'fix-agent',
      agentId: 'fixer-1'
    },
    {
      preset: 'fix-resolved',
      scenario,
      issueID,
      message: 'Auth callback route fixed and ready for retest',
      resolvedCount: 1,
      agentProvider: 'codex',
      agentRole: 'fix-agent',
      agentId: 'fixer-1'
    },
    {
      preset: 'retry-started',
      scenario,
      issueID,
      retryAttempt: 1,
      retestCount: 1,
      agentProvider: 'claude',
      agentRole: 'ui-tester',
      agentId: 'ui-tester'
    },
    {
      preset: 'ui-verified',
      scenario,
      issueID,
      retryAttempt: 1,
      resolvedCount: 1,
      verifiedCount: 1,
      agentProvider: 'claude',
      agentRole: 'ui-tester',
      agentId: 'ui-tester'
    },
    {
      preset: 'approval-required',
      message: 'Review batched fixes',
      approvalSubject: 'Review batched fixes',
      blockerReason: 'Manual sign-off required before merge',
      decisionPrompt: 'Approve merge after UI retest?',
      approvalCount: 1,
      decisionCount: 1,
      agentProvider: 'codex',
      agentRole: 'orchestrator',
      agentId: 'orchestrator'
    },
    {
      preset: 'handoff',
      artifactPath: handoffPath,
      artifactKind: 'handoff',
      message: 'Scenario loop handoff ready',
      agentProvider: 'codex',
      agentRole: 'orchestrator',
      agentId: 'orchestrator'
    }
  ].map((event, index) =>
    normalizeOrchestrationEvent({
      ...base,
      timestamp: orchestrationSampleTimestamp(baseTimestamp, index),
      ...event
    })
  );
}

export async function appendOrchestrationSample(sampleName, input, options = {}) {
  const events = orchestrationSampleEvents(sampleName, input);
  const storePath = options.storePath ?? orchestrationEventStorePath(options.env);
  await fs.mkdir(path.dirname(storePath), { recursive: true });
  await writeOrchestrationEventsToSqlite(storePath, events);
  return { events, storePath };
}

export function parseOrchestrationEventArgs(args) {
  const event = {};
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--') {
      continue;
    }
    if (arg === '--help' || arg === '-h') {
      event.help = true;
      continue;
    }
    if (arg === '--json') {
      const value = args[index + 1];
      if (!value) {
        throw new Error('--json requires a JSON object value');
      }
      const payload = JSON.parse(value);
      if (Array.isArray(payload)) {
        event.events = payload;
      } else {
        Object.assign(event, payload);
      }
      index += 1;
      continue;
    }
    if (!arg.startsWith('--')) {
      throw new Error(`Unexpected argument: ${arg}`);
    }

    const key = arg.slice(2);
    const value = args[index + 1];
    if (value === undefined || value.startsWith('--')) {
      throw new Error(`${arg} requires a value`);
    }
    event[argumentKeyToEventKey(key)] = value;
    index += 1;
  }

  return event;
}

function argumentKeyToEventKey(key) {
  if (explicitKeyMap.has(key)) {
    return explicitKeyMap.get(key);
  }
  return key.replace(/-([a-z])/g, (_, character) => character.toUpperCase());
}

function parseOrchestrationPayloadFileContents(contents) {
  try {
    return JSON.parse(contents);
  } catch {
    return contents
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
      .map((line, index) => {
        try {
          return JSON.parse(line);
        } catch (error) {
          throw new Error(`Could not parse orchestration payload line ${index + 1}: ${error.message}`);
        }
      });
  }
}

function presetFromEventAlias(value) {
  const normalized = optionalString(value)
    ?.toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  if (!normalized) {
    return null;
  }

  const aliases = new Map([
    ['run-start', 'run-started'],
    ['run-started', 'run-started'],
    ['run-begin', 'run-started'],
    ['run-begun', 'run-started'],
    ['run-complete', 'run-completed'],
    ['run-completed', 'run-completed'],
    ['run-finished', 'run-completed'],
    ['run-done', 'run-completed'],
    ['scenario', 'scenario-started'],
    ['scenario-created', 'scenario-started'],
    ['scenario-create', 'scenario-started'],
    ['scenario-start', 'scenario-started'],
    ['scenario-started', 'scenario-started'],
    ['scenario-executed', 'scenario-passed'],
    ['scenario-complete', 'scenario-passed'],
    ['scenario-pass', 'scenario-passed'],
    ['scenario-passed', 'scenario-passed'],
    ['test', 'test-started'],
    ['test-start', 'test-started'],
    ['test-started', 'test-started'],
    ['test-fail', 'test-failed'],
    ['test-failed', 'test-failed'],
    ['test-failure', 'test-failed'],
    ['issue-created', 'issue-found'],
    ['issue-discovered', 'issue-found'],
    ['issue-found', 'issue-found'],
    ['finding', 'issue-found'],
    ['finding-found', 'issue-found'],
    ['finding-discovered', 'issue-found'],
    ['bug-found', 'issue-found'],
    ['issue', 'issue-found'],
    ['agent-start', 'agent-started'],
    ['agent-started', 'agent-started'],
    ['agent-complete', 'agent-completed'],
    ['agent-completed', 'agent-completed'],
    ['agent-blocked', 'agent-blocked'],
    ['agent-delegated', 'agent-delegated'],
    ['delegate-agent', 'agent-delegated'],
    ['delegated-agent', 'agent-delegated'],
    ['batch-delegated', 'batch-delegated'],
    ['batch-started', 'batch-delegated'],
    ['fix-batch-started', 'batch-delegated'],
    ['fix-batch-delegated', 'batch-delegated'],
    ['delegated-fix-batch', 'batch-delegated'],
    ['fix-batch', 'batch-delegated'],
    ['fix-start', 'fix-started'],
    ['fix-started', 'fix-started'],
    ['fix-began', 'fix-started'],
    ['fix-complete', 'fix-resolved'],
    ['fix-completed', 'fix-resolved'],
    ['fix-fixed', 'fix-resolved'],
    ['fix-resolved', 'fix-resolved'],
    ['fix-resolve', 'fix-resolved'],
    ['fixed', 'fix-resolved'],
    ['ui-retest-start', 'retest-started'],
    ['ui-retest-started', 'retest-started'],
    ['retest-start', 'retest-started'],
    ['retest-started', 'retest-started'],
    ['retry-start', 'retry-started'],
    ['retry-started', 'retry-started'],
    ['retry', 'retry-started'],
    ['ui-retest-pass', 'retest-passed'],
    ['ui-retest-passed', 'retest-passed'],
    ['retest-pass', 'retest-passed'],
    ['retest-passed', 'retest-passed'],
    ['ui-retest-fail', 'retest-failed'],
    ['ui-retest-failed', 'retest-failed'],
    ['retest-fail', 'retest-failed'],
    ['retest-failed', 'retest-failed'],
    ['ui-pass', 'ui-verified'],
    ['ui-passed', 'ui-verified'],
    ['ui-proof', 'ui-verified'],
    ['ui-verified', 'ui-verified'],
    ['ui-verification', 'ui-verified'],
    ['browser-verified', 'ui-verified'],
    ['browser-proof', 'ui-verified'],
    ['ui-failed', 'ui-failed'],
    ['browser-failed', 'ui-failed'],
    ['approval-needed', 'approval-required'],
    ['approval-requested', 'approval-required'],
    ['approval-required', 'approval-required'],
    ['needs-approval', 'approval-required'],
    ['decision-needed', 'approval-required'],
    ['decision-required', 'approval-required'],
    ['needs-decision', 'approval-required'],
    ['waiting-decision', 'approval-required'],
    ['waiting-for-decision', 'approval-required'],
    ['signoff-required', 'approval-required'],
    ['sign-off-required', 'approval-required'],
    ['approval-granted', 'approval-granted'],
    ['approved', 'approval-granted'],
    ['signoff-granted', 'approval-granted'],
    ['sign-off-granted', 'approval-granted'],
    ['blocked', 'blocker-reported'],
    ['blocker', 'blocker-reported'],
    ['blocker-reported', 'blocker-reported'],
    ['artifact-added', 'artifact-added'],
    ['artifact-available', 'artifact-added'],
    ['artifact-created', 'artifact-added'],
    ['trace-added', 'artifact-added'],
    ['screenshot-added', 'artifact-added'],
    ['report-added', 'artifact-added'],
    ['handoff-created', 'handoff'],
    ['handoff-ready', 'handoff'],
    ['handoff', 'handoff'],
    ['handoff-available', 'handoff']
  ]);

  return aliases.get(normalized) ?? null;
}

function shouldUsePresetKind(kind, preset) {
  const text = optionalString(kind);
  if (!text) return true;
  if (!preset) return false;
  if (text.includes('.')) return false;
  return presetFromEventAlias(text) === preset;
}

function isObjectLike(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function objectValue(value) {
  return isObjectLike(value) ? value : null;
}

function firstObjectValue(value) {
  return Array.isArray(value) ? value.find(isObjectLike) ?? null : null;
}

function arrayValue(value) {
  return Array.isArray(value) ? value : [];
}

function firstOptionalString(...values) {
  for (const value of values) {
    const text = optionalAliasString(value);
    if (text) return text;
  }
  return null;
}

function optionalAliasString(value) {
  if (typeof value === 'object' && value !== null) {
    return null;
  }
  return optionalString(value);
}

function firstDefined(...values) {
  return values.find((value) => value !== undefined && value !== null && value !== '');
}

function optionalString(value) {
  if (value === undefined || value === null) {
    return null;
  }
  const text = String(value).trim();
  return text ? text : null;
}

function isSuccessStatus(value) {
  const text = optionalString(value)?.toLowerCase();
  return Boolean(text && /^(success|succeeded|pass|passed|complete|completed|verified|done)$/.test(text));
}

function isFailureStatus(value) {
  const text = optionalString(value)?.toLowerCase();
  return Boolean(text && /^(fail|failed|failure|blocked|error|errored)$/.test(text));
}

function optionalCount(value, fieldName) {
  if (value === undefined || value === null || value === '') {
    return null;
  }

  const count = Number(value);
  if (!Number.isInteger(count) || count < 0) {
    throw new Error(`${fieldName} must be a non-negative integer`);
  }

  return count;
}

function approvalSubjectFromExpandedEvent(event) {
  const kind = optionalString(event.kind);
  if (!kind?.startsWith('approval.')) return null;
  return optionalString(event.message) ?? optionalString(event.title);
}

function orchestrationSampleTimestamp(timestamp, index) {
  const base = timestamp ? new Date(timestamp) : new Date();
  if (Number.isNaN(base.getTime())) {
    return timestamp ? `${timestamp}-${String(index + 1).padStart(3, '0')}` : new Date().toISOString();
  }

  return new Date(base.getTime() + index * 60_000).toISOString();
}

function formatPresetTitle(prefix, subject) {
  return subject ? `${prefix}: ${subject}` : prefix;
}

function orchestrationPresetCountMessage(input) {
  const parts = [
    formatPresetCount(input.scenarioCount, 'scenario'),
    formatPresetCount(input.issueCount, 'issue'),
    formatPresetCount(input.testCount, 'test'),
    formatPresetCount(input.retestCount, 'retest'),
    formatPresetCount(input.fixCount, 'fix'),
    formatPresetCount(input.resolvedCount, 'resolved'),
    formatPresetCount(input.verifiedCount, 'UI verified'),
    formatPresetCount(input.delegatedCount, 'delegated'),
    formatPresetCount(input.decisionCount, 'decision'),
    formatPresetCount(input.approvalCount, 'approval'),
    formatPresetCount(input.failedCount, 'failed')
  ].filter(Boolean);

  return parts.length ? parts.join(' · ') : null;
}

function formatPresetCount(value, label) {
  const text = optionalString(value);
  return text ? `${text} ${label}` : null;
}

function printHelp() {
  console.log(`Usage:
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --kind test.started --status running
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --preset scenario-started --scenario "Trading partner setup"
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --preset issue-found --issue-id AUTH-7 --message "Redirect loop"
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --preset batch-delegated --resolved-count 3 --agent-role fix-agent
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --preset ui-verified --scenario "Trading partner setup"
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --preset approval-required --message "Needs deletion sign-off"
  node scripts/orchestrationEvent.mjs --run-id run-tsk-127 --sample run-e2e-loop --task-id TSK-127
  node scripts/orchestrationEvent.mjs --json '{"runId":"run-tsk-127","kind":"run.created"}'
  node scripts/orchestrationEvent.mjs --json-file /tmp/orchestration-events.jsonl

Writes event records to the orchestration_events table in:
  $${orchestrationEventStoreEnv}, or
  ${defaultOrchestrationEventStorePath()}`);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    const event = parseOrchestrationEventArgs(process.argv.slice(2));
    if (event.help) {
      printHelp();
      process.exit(0);
    }
    const result = event.sample
      ? await appendOrchestrationSample(event.sample, event)
      : event.jsonFile
        ? await appendOrchestrationEventPayload(await readOrchestrationEventPayloadFile(event.jsonFile))
        : event.events
          ? await appendOrchestrationEventPayload(event)
          : await appendOrchestrationEvent(event);
    console.log(JSON.stringify(result, null, 2));
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exit(1);
  }
}
