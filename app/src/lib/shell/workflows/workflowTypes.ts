/**
 * Typed frontend contracts for the WorkflowEngine snapshot.
 *
 * `WorkflowRunRecord` and the related records mirror the serde/camelCase
 * shapes in `src-tauri/src/workflow/mod.rs`.  The `*View` types add only stable
 * presentation identity and provenance; they never replace the engine's
 * state/phase with a client-side guess.
 */

import type { ConversationUsage } from '../conversation/conversationTypes.ts';

export type WorkflowRunState =
  | 'draft'
  | 'queued'
  | 'running'
  | 'waiting-approval'
  | 'waiting-input'
  | 'paused'
  | 'completed'
  | 'failed'
  | 'cancelled';

export type WorkflowNodeState =
  | 'blocked'
  | 'ready'
  | 'queued'
  | 'starting'
  | 'running'
  | 'waiting-approval'
  | 'waiting-input'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'skipped';

/** This is the engine's typed phase.  Never derive it from assistant prose. */
export type WorkflowLoopPhase =
  | 'draft'
  | 'scheduling'
  | 'planning'
  | 'implementing'
  | 'reviewing'
  | 'verifying'
  | 'approval'
  | 'completed'
  | 'failed'
  | 'cancelled';

export type WorkflowOutputContract =
  | 'ImplementationReceipt'
  | 'PullRequestReceipt'
  | 'PullRequestMergeReceipt'
  | 'ReviewReceipt'
  | 'SpecReceipt'
  | 'SpecComplianceReceipt'
  | 'VerificationReceipt'
  | 'PlanReceipt';

export type WorkflowProvenance = 'provider-native' | 'workflow';

export const WORKFLOW_PROVENANCE_LABELS: Record<WorkflowProvenance, string> = {
  'provider-native': 'Provider-native',
  workflow: 'Workflow'
};

export function provenanceLabel(provenance: WorkflowProvenance): string {
  return WORKFLOW_PROVENANCE_LABELS[provenance];
}

export interface WorkflowDefinitionV1 {
  version: number;
  id: string;
  name: string;
  description: string;
  trigger: unknown;
  /** The file's `limits`, such as `max_rounds`. */
  limits?: Record<string, unknown>;
  inputs: unknown[];
  roles: AgentRoleDefinition[];
  nodes: WorkflowNodeDefinition[];
  edges: WorkflowEdge[];
  concurrency: WorkflowConcurrencyPolicy;
  budgets: WorkflowBudgetPolicy;
  completion: WorkflowCompletionPolicy;
}

export interface AgentRoleDefinition {
  id: string;
  name: string;
  purpose: string;
  providerPolicy: ProviderSelectionPolicy;
  modelPolicy: ConfigSelectionPolicy;
  effortPolicy: ConfigSelectionPolicy;
  permissionPolicy: ConfigSelectionPolicy;
  promptTemplateId: string;
  outputContract: WorkflowOutputContract;
  workspacePolicy: WorkflowWorkspacePolicy;
  retryPolicy: WorkflowRetryPolicy;
}

export interface WorkflowNodeDefinition {
  id: string;
  title: string;
  roleId: string;
  /** The step as written in the workflow file: `review`, `on_fail`, `run`, `agent`. */
  step?: Record<string, unknown>;
  dependsOn: string[];
  condition: unknown | null;
  fanOut: unknown | null;
  approvalGate: WorkflowApprovalGate | null;
  timeoutSeconds: number;
  maxAttempts: number;
}

export interface WorkflowEdge {
  from: string;
  to: string;
}

export interface WorkflowApprovalGate {
  id: string;
  prompt: string;
}

export interface ProviderSelectionPolicy {
  provider: string;
  allowedProviders: string[];
}

export interface ConfigSelectionPolicy {
  value: string | null;
  overrides: Record<string, string>;
}

export type WorkflowWorkspacePolicy =
  | { kind: 'read-only-current' }
  | { kind: 'shared-current'; fileAllowList: string[] }
  | { kind: 'dedicated-existing'; worktreeId: string }
  | { kind: 'dedicated-new'; branchTemplate: string }
  | { kind: 'none' };

export interface WorkflowRetryPolicy {
  maxAttempts: number;
  retryableCodes: string[];
}

export interface WorkflowConcurrencyPolicy {
  global: number;
  workflow: number;
  providers: Record<string, number>;
}

export interface WorkflowBudgetPolicy {
  maximumActiveAgents: number;
  maximumChildDepth: number;
  maximumAttemptsPerNode: number;
  maximumWallTimeSeconds: number;
  maximumTokens: number | null;
  maximumToolTerminals: number;
  maximumWorktrees: number;
}

export interface WorkflowCompletionPolicy {
  cancelDescendantsOnFailure: boolean;
}

/** Exact serialized `WorkflowArtifactRef`. */
export interface WorkflowArtifactRef {
  id: string;
  kind: string;
  path: string | null;
  url: string | null;
  digest: string | null;
}

/** Exact serialized `WorkflowGateRecord`. */
export interface WorkflowGateRecord {
  id: string;
  nodeRunId: string;
  approved: boolean | null;
  structuredInput: unknown | null;
}

/** Exact serialized `WorkflowFailure`. */
export interface WorkflowFailure {
  code: string;
  message: string;
  budget: string | null;
}

/** Exact serialized `WorkflowNodeRunRecord`. */
export interface WorkflowNodeRunRecord {
  id: string;
  nodeId: string;
  roleId: string;
  state: WorkflowNodeState;
  attempt: number;
  depth: number;
  ownedId: string;
  provider: string;
  providerInstanceId: string | null;
  startedAtMs: number | null;
  finishedAtMs: number | null;
  outputContract: WorkflowOutputContract;
  structuredOutput: unknown | null;
  artifacts: WorkflowArtifactRef[];
  gate: WorkflowGateRecord | null;
  leaseId: string | null;
  failure: WorkflowFailure | null;
  usage: ConversationUsage | null;
}

/** One ledger row of a run (`workflow_history`): every attempt of every step, oldest first. */
export interface WorkflowHistoryRow {
  sequence: number;
  timestampMs: number;
  kind: string;
  nodeId: string | null;
  attempt: number | null;
  ownedId: string | null;
  /** The state of the step after this change. */
  status: string | null;
  commands: { command: string; exitCode: number | null }[];
  findingCount: number | null;
  verdict: string | null;
  files: string[];
  /** Only on `workflow.node.owner-answered`; `by` and `why` say who answered and, for the main orchestrator, why. */
  answer: { step: string; decision: string; reply: string | null; by?: 'owner' | 'orchestrator'; why?: string | null } | null;
  gateApproved: boolean | null;
}

/** Exact serialized `WorkflowRunRecord`; this is what Tauri returns. */
export interface WorkflowRunRecord {
  id: string;
  workflowId: string;
  definition: WorkflowDefinitionV1;
  state: WorkflowRunState;
  phase: WorkflowLoopPhase;
  inputSnapshot: unknown;
  inputHash: string;
  createdAtMs: number;
  updatedAtMs: number;
  nodes: WorkflowNodeRunRecord[];
  tokensUsed: number;
  toolTerminalsUsed: number;
  worktreesAllocated: number;
  lastSequence: number;
}

/** The five structured output contracts accepted by the engine. */
export interface ImplementationReceipt {
  changedFiles: string[];
  summary: string;
  tests: unknown[];
  knownRisks: string[];
  commit: string | null;
  branch: string | null;
  worktree: string | null;
  artifacts: WorkflowArtifactRef[];
}

export interface ReviewReceipt {
  severity: string;
  file: string;
  line: number;
  evidence: string;
  recommendation: string;
  confidence: number;
  blocking: boolean;
}

export interface SpecComplianceReceipt {
  requirementId: string;
  status: string;
  evidence: unknown;
  gap: string | null;
  recommendedAction: string | null;
}

export interface VerificationReceipt {
  command: string;
  exit: number;
  duration: number;
  evidenceArtifacts: WorkflowArtifactRef[];
  cleanupReceipt: unknown;
}

export interface PlanReceipt {
  orderedSteps: unknown[];
  dependencies: unknown[];
  risk: unknown;
  estimatedParallelLanes: number;
  ownerMerge?: boolean;
}

export interface PullRequestReceipt {
  url: string;
  branch: string;
  artifacts: WorkflowArtifactRef[];
}

export interface PullRequestMergeReceipt {
  url: string;
  mergeCommitSha: string;
}

/* -------------------------------------------------------------------------- */
/* Presentation views                                                         */
/* -------------------------------------------------------------------------- */

export interface WorkflowArtifactView extends WorkflowArtifactRef {
  workflowRunId: string;
  nodeRunId: string;
  provenance: WorkflowProvenance;
}

export interface WorkflowGateView extends WorkflowGateRecord {
  workflowRunId: string;
  provenance: WorkflowProvenance;
}

export interface WorkflowAgentView {
  workflowRunId: string | null;
  nodeRunId: string | null;
  /** Provider/session identity. It must never be confused with workflowRunId. */
  ownedId: string;
  roleId: string | null;
  provider: string;
  providerInstanceId: string | null;
  state: WorkflowNodeState | 'provider-native';
  attempt: number | null;
  depth: number | null;
  outputContract: WorkflowOutputContract | null;
  leaseId: string | null;
  failure: WorkflowFailure | null;
  provenance: WorkflowProvenance;
}

export interface WorkflowNodeView extends Omit<WorkflowNodeRunRecord, 'artifacts' | 'gate'> {
  workflowRunId: string;
  artifacts: WorkflowArtifactView[];
  gate: WorkflowGateView | null;
  agent: WorkflowAgentView;
  provenance: WorkflowProvenance;
}

export interface WorkflowRunView extends Omit<WorkflowRunRecord, 'nodes'> {
  /** Stable workflow identity, deliberately distinct from every node ownedId. */
  workflowRunId: string;
  nodes: WorkflowNodeView[];
  provenance: WorkflowProvenance;
}

export function toWorkflowArtifactView(
  workflowRunId: string,
  nodeRunId: string,
  artifact: WorkflowArtifactRef
): WorkflowArtifactView {
  return {
    ...artifact,
    workflowRunId,
    nodeRunId,
    provenance: 'workflow'
  };
}

export function toWorkflowGateView(
  workflowRunId: string,
  gate: WorkflowGateRecord
): WorkflowGateView {
  return { ...gate, workflowRunId, provenance: 'workflow' };
}

export function toWorkflowAgentView(
  workflowRunId: string,
  node: WorkflowNodeRunRecord
): WorkflowAgentView {
  return {
    workflowRunId,
    nodeRunId: node.id,
    ownedId: node.ownedId,
    roleId: node.roleId,
    provider: node.provider,
    providerInstanceId: node.providerInstanceId,
    state: node.state,
    attempt: node.attempt,
    depth: node.depth,
    outputContract: node.outputContract,
    leaseId: node.leaseId,
    failure: node.failure,
    provenance: 'workflow'
  };
}

export function toWorkflowNodeView(
  workflowRunId: string,
  node: WorkflowNodeRunRecord
): WorkflowNodeView {
  return {
    ...node,
    workflowRunId,
    artifacts: node.artifacts.map((artifact) =>
      toWorkflowArtifactView(workflowRunId, node.id, artifact)
    ),
    gate: node.gate ? toWorkflowGateView(workflowRunId, node.gate) : null,
    agent: toWorkflowAgentView(workflowRunId, node),
    provenance: 'workflow'
  };
}

export function toWorkflowRunView(record: WorkflowRunRecord): WorkflowRunView {
  return {
    ...record,
    workflowRunId: record.id,
    nodes: record.nodes.map((node) => toWorkflowNodeView(record.id, node)),
    provenance: 'workflow'
  };
}

/** A provider-native child can be shown beside workflow agents without being
 * assigned a fake workflow identity. */
export function providerNativeAgentView(input: {
  ownedId: string;
  provider: string;
  providerInstanceId?: string | null;
  state?: WorkflowAgentView['state'];
  roleId?: string | null;
}): WorkflowAgentView {
  return {
    workflowRunId: null,
    nodeRunId: null,
    ownedId: input.ownedId,
    roleId: input.roleId ?? null,
    provider: input.provider,
    providerInstanceId: input.providerInstanceId ?? null,
    state: input.state ?? 'provider-native',
    attempt: null,
    depth: null,
    outputContract: null,
    leaseId: null,
    failure: null,
    provenance: 'provider-native'
  };
}

/* -------------------------------------------------------------------------- */
/* Tauri command DTOs                                                          */
/* -------------------------------------------------------------------------- */

/** The Notion task a run works on, snapshotted when the run is created. */
export interface WorkflowTaskSnapshot {
  key: string;
  title: string;
  body: string;
}

export interface CreateWorkflowRunDto {
  repoRoot: string;
  /** A `.assembly/workflows/*.yml` path relative to `repoRoot`. */
  workflowPath: string;
  task: WorkflowTaskSnapshot;
  input: Record<string, unknown>;
  idempotencyKey: string;
}

/** The owner's answer to a step waiting for them. */
export type WorkflowPauseDecision = 'another-round' | 'accept' | 'stop';

export interface AnswerWorkflowPauseDto {
  runId: string;
  nodeId: string;
  decision: WorkflowPauseDecision;
  reply: string | null;
  idempotencyKey: string;
}

export interface WorkflowRunIdempotencyDto {
  runId: string;
  idempotencyKey: string;
}

export interface WorkflowNodeIdempotencyDto extends WorkflowRunIdempotencyDto {
  nodeId: string;
}

export interface ApproveWorkflowGateDto extends WorkflowNodeIdempotencyDto {
  approval: { approved: boolean; [key: string]: unknown };
}

export type StartWorkflowRunDto = WorkflowRunIdempotencyDto;
export type PauseWorkflowRunDto = WorkflowRunIdempotencyDto;
export type ResumeWorkflowRunDto = WorkflowRunIdempotencyDto;
export type CancelWorkflowRunDto = WorkflowRunIdempotencyDto;
export type RetryWorkflowNodeDto = WorkflowNodeIdempotencyDto;

/** Useful for callers that want to describe one control action without IO. */
export type WorkflowCommandDto =
  | { kind: 'start'; input: StartWorkflowRunDto }
  | { kind: 'pause'; input: PauseWorkflowRunDto }
  | { kind: 'resume'; input: ResumeWorkflowRunDto }
  | { kind: 'cancel'; input: CancelWorkflowRunDto }
  | { kind: 'retry-node'; input: RetryWorkflowNodeDto }
  | { kind: 'approve-gate'; input: ApproveWorkflowGateDto };

export type WorkflowFilter = 'all' | 'active' | 'waiting' | 'failed' | 'completed';

/** What a step did, read from its structured output: what the owner sees under a step. */
export interface WorkflowStepFacts {
  commands: { command: string; exitCode: number | null; lastLines: string }[];
  verdict: string | null;
  findings: string[];
  prUrl: string | null;
}

/** A step's commands with their exit codes and last 5 output lines, its review verdict and findings, and its PR. */
export function workflowStepFacts(node: Pick<WorkflowNodeRunRecord, 'structuredOutput'>): WorkflowStepFacts {
  const output = (node.structuredOutput ?? {}) as Record<string, unknown>;
  const commands = Array.isArray(output.commands) ? output.commands as Record<string, unknown>[] : [];
  return {
    commands: commands.map((command) => ({
      command: String(command.command ?? ''),
      exitCode: typeof command.exitCode === 'number' ? command.exitCode : null,
      lastLines: String(command.output ?? '').split('\n').filter((line, index, lines) =>
        index < lines.length - 1 || line !== '').slice(-5).join('\n')
    })),
    verdict: typeof output.verdict === 'string' ? output.verdict : null,
    findings: Array.isArray(output.findings) ? output.findings.map(String) : [],
    prUrl: typeof output.url === 'string' && output.url ? output.url : null
  };
}

/** The machine a workflow command goes to: null for this Mac, else the remote profile id. */
export function workflowMachine(machine: string): string | null {
  return machine === 'mac' ? null : machine;
}

/** An engine refusal, which arrives as `{"kind":…,"detail":…}`, as its own sentence; other errors as they are. */
export function workflowErrorText(error: unknown): string {
  const text = error instanceof Error ? error.message : String(error);
  try {
    const { kind, detail } = JSON.parse(text) as { kind?: unknown; detail?: unknown };
    if (typeof kind !== 'string') return text;
    if (typeof detail === 'string') return detail;
    const message = (detail as { message?: unknown } | null)?.message;
    return typeof message === 'string' ? message : text;
  } catch {
    return text;
  }
}

/** One step attempt's usage with one model (`read_workflow_usage`); `nodeRunId` is `<run>:<step>:<attempt>`. */
export interface WorkflowUsageRow {
  nodeRunId: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  /** What the provider reported; null when it reports no cost. */
  reportedCostMicros: number | null;
  /** Tokens times the rate table; null when the model has no rate. */
  estimatedCostMicros: number | null;
}
