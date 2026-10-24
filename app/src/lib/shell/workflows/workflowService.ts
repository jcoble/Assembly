/**
 * The only workflow module allowed to cross the Tauri boundary.
 *
 * This file deliberately contains no reducer, JSONL reader, phase inference,
 * or optimistic state.  Every command returns the engine's complete typed
 * snapshot; callers land that snapshot in `workflowStore.svelte.ts`.
 */
import { listen, type UnlistenFn } from '@tauri-apps/api/event';

import {
  answerWorkflowPauseFromTauri,
  approveWorkflowGateFromTauri,
  cancelWorkflowRunFromTauri,
  createWorkflowRunFromTauri,
  isNativeTauriRuntime,
  listWorkflowFilesFromTauri,
  listWorkflowRunHistoryFromTauri,
  listWorkflowRunsFromTauri,
  pauseWorkflowRunFromTauri,
  remoteWorkflowFromTauri,
  resumeWorkflowRunFromTauri,
  retryWorkflowNodeFromTauri,
  startWorkflowRunFromTauri
} from '$lib/tauriSource';
import {
  trackTauriListener,
  trackTauriSubscriber
} from '$lib/shell/resourceDiagnostics.svelte';
import type {
  AnswerWorkflowPauseDto,
  ApproveWorkflowGateDto,
  CancelWorkflowRunDto,
  CreateWorkflowRunDto,
  PauseWorkflowRunDto,
  ResumeWorkflowRunDto,
  RetryWorkflowNodeDto,
  StartWorkflowRunDto,
  WorkflowHistoryRow,
  WorkflowRunRecord,
  WorkflowUsageRow
} from './workflowTypes';
import type { WorkflowReviewReport } from './workflowCloseup.ts';

export const workflowSnapshotEvent = 'workflow-run-updated';

// `machine` is a remote profile id: that machine's own engine runs the command.
// Null means this Mac.

export async function listWorkflowRuns(machine: string | null = null): Promise<WorkflowRunRecord[] | null> {
  return machine ? remoteWorkflowFromTauri(machine, 'list', {}) : listWorkflowRunsFromTauri();
}

/** Every ledger row of one run, oldest first, for the run timeline. */
export async function listWorkflowRunHistory(
  machine: string | null,
  runId: string,
  signal: AbortSignal
): Promise<WorkflowHistoryRow[] | null> {
  const rows = machine
    ? await remoteWorkflowFromTauri<WorkflowHistoryRow[]>(machine, 'history', { runId }, signal)
    : await listWorkflowRunHistoryFromTauri(runId);
  return signal.aborted ? null : rows;
}

export async function createWorkflowRun(
  input: CreateWorkflowRunDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'create', input);
  return createWorkflowRunFromTauri(input.repoRoot, input.workflowPath, input.task, input.input, input.idempotencyKey);
}

export async function listWorkflowFiles(repoRoot: string, machine: string | null = null): Promise<string[] | null> {
  return machine ? remoteWorkflowFromTauri(machine, 'listFiles', { repoRoot }) : listWorkflowFilesFromTauri(repoRoot);
}

export async function answerWorkflowPause(
  input: AnswerWorkflowPauseDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'answer', input);
  return answerWorkflowPauseFromTauri(input.runId, input.nodeId, input.decision, input.reply, input.idempotencyKey);
}

export async function startWorkflowRun(
  input: StartWorkflowRunDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'start', input);
  return startWorkflowRunFromTauri(input.runId, input.idempotencyKey);
}

export async function pauseWorkflowRun(
  input: PauseWorkflowRunDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'pause', input);
  return pauseWorkflowRunFromTauri(input.runId, input.idempotencyKey);
}

export async function resumeWorkflowRun(
  input: ResumeWorkflowRunDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'resume', input);
  return resumeWorkflowRunFromTauri(input.runId, input.idempotencyKey);
}

export async function cancelWorkflowRun(
  input: CancelWorkflowRunDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'cancel', input);
  return cancelWorkflowRunFromTauri(input.runId, input.idempotencyKey);
}

export async function retryWorkflowNode(
  input: RetryWorkflowNodeDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'retry', input);
  return retryWorkflowNodeFromTauri(input.runId, input.nodeId, input.idempotencyKey);
}

export async function approveWorkflowGate(
  input: ApproveWorkflowGateDto,
  machine: string | null = null
): Promise<WorkflowRunRecord | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'approveGate', input);
  return approveWorkflowGateFromTauri(
    input.runId,
    input.nodeId,
    input.approval,
    input.idempotencyKey
  );
}

type WorkflowSnapshotPayload = WorkflowRunRecord | WorkflowRunRecord[];
type WorkflowSnapshotListener = (snapshot: WorkflowSnapshotPayload) => void;

let unlisten: UnlistenFn | null = null;
let workflowSnapshotSetup: Promise<void> | null = null;
let workflowSnapshotGeneration = 0;
const listeners = new Set<WorkflowSnapshotListener>();

/**
 * Listen for an optional controller push.  A6 currently returns snapshots from
 * commands and does not require a push event, so browser builds and older
 * controllers simply receive a no-op unsubscribe.  The subscription itself is
 * kept here so components never import Tauri event APIs.
 */
export function subscribeWorkflowSnapshots(
  listener: WorkflowSnapshotListener
): () => void {
  listeners.add(listener);
  ensureWorkflowSnapshotSubscription();
  return trackTauriSubscriber(() => {
    listeners.delete(listener);
    if (listeners.size === 0) stopWorkflowSnapshotListener();
  });
}

function ensureWorkflowSnapshotSubscription(): void {
  if (!isNativeTauriRuntime() || unlisten || workflowSnapshotSetup) return;
  const generation = workflowSnapshotGeneration;
  const setup = setupWorkflowSnapshotSubscription(generation);
  workflowSnapshotSetup = setup;
  void clearWorkflowSnapshotSetup(setup);
}

async function setupWorkflowSnapshotSubscription(generation: number): Promise<void> {
  try {
    const stop = await listen<WorkflowSnapshotPayload>(workflowSnapshotEvent, ({ payload }) => {
      for (const current of listeners) current(payload);
    });
    const trackedStop = trackTauriListener(stop);
    if (generation !== workflowSnapshotGeneration || listeners.size === 0) {
      trackedStop();
      return;
    }
    unlisten = trackedStop;
  } catch {
    // An older controller has no event channel; commands remain authoritative.
    unlisten = null;
  }
}

async function clearWorkflowSnapshotSetup(setup: Promise<void>): Promise<void> {
  await setup;
  if (workflowSnapshotSetup === setup) workflowSnapshotSetup = null;
}

function stopWorkflowSnapshotListener(): void {
  workflowSnapshotGeneration += 1;
  unlisten?.();
  unlisten = null;
  workflowSnapshotSetup = null;
}

export function stopWorkflowSnapshotSubscription(): void {
  stopWorkflowSnapshotListener();
  listeners.clear();
}

/** Tells this Mac's engine what the Workflows screen shows: the board, and the run it has open. */
export async function watchWorkflowRuns(board: boolean, runId: string | null): Promise<void> {
  if (!isNativeTauriRuntime()) return;
  const { invoke } = await import('$lib/workspaceInvoke.ts');
  await invoke('watch_workflow_runs', { board, runId });
}

/** Hands each changed run the engine sends the screen to `onRun`, until `signal` ends. */
export async function followWorkflowRuns(onRun: (run: WorkflowRunRecord) => void, signal: AbortSignal): Promise<void> {
  if (!isNativeTauriRuntime() || signal.aborted) return;
  const stop = trackTauriListener(await listen<WorkflowRunRecord>('workflow-run-changed', ({ payload }) => {
    if (!signal.aborted) onRun(payload);
  }));
  if (signal.aborted) { stop(); return; }
  signal.addEventListener('abort', stop, { once: true });
}

/** A run's usage per step attempt and model, or every run's when `runId` is null, from the machine that runs it. */
export async function readWorkflowUsage(machine: string | null, runId: string | null): Promise<WorkflowUsageRow[] | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'usage', { runId });
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('$lib/workspaceInvoke.ts');
  return invoke<WorkflowUsageRow[]>('read_workflow_usage', { runId });
}

/** Every review report of a run, for the review close-up, from the machine that runs it. */
export async function readWorkflowReviews(machine: string | null, runId: string): Promise<WorkflowReviewReport[] | null> {
  if (machine) return remoteWorkflowFromTauri(machine, 'reviews', { runId });
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('$lib/workspaceInvoke.ts');
  return invoke<WorkflowReviewReport[]>('workflow_review_reports', { runId });
}
