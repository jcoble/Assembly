/**
 * Ephemeral UI state for the Agent Control Center.
 *
 * The store caches complete engine snapshots and remembers only selection and
 * list filters.  It never calls Tauri, reads a ledger, reduces event history,
 * or invents a workflow phase.
 */
import type { WorkflowFilter, WorkflowRunRecord } from './workflowTypes';

export interface WorkflowStoreState {
  runs: WorkflowRunRecord[];
  selectedWorkflowRunId: string | null;
  selectedNodeId: string | null;
  query: string;
  filter: WorkflowFilter;
  loading: boolean;
  error: string | null;
  unavailableReason: string | null;
  activated: boolean;
}

export const workflowState = $state<WorkflowStoreState>({
  runs: [],
  selectedWorkflowRunId: null,
  selectedNodeId: null,
  query: '',
  filter: 'all',
  loading: false,
  error: null,
  unavailableReason: null,
  activated: false
});

export function resetWorkflowStore(): void {
  workflowState.runs = [];
  workflowState.selectedWorkflowRunId = null;
  workflowState.selectedNodeId = null;
  workflowState.query = '';
  workflowState.filter = 'all';
  workflowState.loading = false;
  workflowState.error = null;
  workflowState.unavailableReason = null;
  workflowState.activated = false;
}

// Runs are re-read on every change, so a read clears only a failure of its own;
// an action's error stays until the next action.
let loadError: string | null = null;

export function beginWorkflowLoad(): void {
  workflowState.loading = true;
  workflowState.unavailableReason = null;
  workflowState.activated = true;
}

export function applyWorkflowSnapshots(runs: WorkflowRunRecord[] | null): void {
  const current = new Map(workflowState.runs.map((run) => [run.id, run]));
  workflowState.runs = runs?.map((run) => {
    const newer = current.get(run.id);
    return newer && newer.lastSequence > run.lastSequence ? newer : run;
  }) ?? [];
  workflowState.loading = false;
  if (workflowState.error === loadError) workflowState.error = null;
  workflowState.unavailableReason = runs === null ? 'Workflow runs are available in the desktop app.' : null;
  workflowState.activated = true;
  if (
    workflowState.selectedWorkflowRunId &&
    !workflowState.runs.some((run) => run.id === workflowState.selectedWorkflowRunId)
  ) {
    workflowState.selectedWorkflowRunId = workflowState.runs[0]?.id ?? null;
    workflowState.selectedNodeId = null;
  }
}

export function failWorkflowLoad(message: string): void {
  workflowState.loading = false;
  workflowState.error = loadError = message;
  workflowState.unavailableReason = null;
  workflowState.activated = true;
}

export function upsertWorkflowSnapshot(run: WorkflowRunRecord): void {
  const index = workflowState.runs.findIndex((candidate) => candidate.id === run.id);
  if (index < 0) workflowState.runs = [...workflowState.runs, run];
  else if (run.lastSequence >= workflowState.runs[index].lastSequence) workflowState.runs[index] = run;
  workflowState.loading = false;
  workflowState.unavailableReason = null;
  workflowState.activated = true;
  if (!workflowState.selectedWorkflowRunId) workflowState.selectedWorkflowRunId = run.id;
}

export function selectWorkflowRun(workflowRunId: string | null): void {
  workflowState.selectedWorkflowRunId = workflowRunId;
  workflowState.selectedNodeId = null;
}

export function selectWorkflowNode(nodeId: string | null): void {
  workflowState.selectedNodeId = nodeId;
}

export function setWorkflowQuery(query: string): void {
  workflowState.query = query;
}

export function setWorkflowFilter(filter: WorkflowFilter): void {
  workflowState.filter = filter;
}

export function selectedWorkflowRun(): WorkflowRunRecord | null {
  const id = workflowState.selectedWorkflowRunId;
  return id ? workflowState.runs.find((run) => run.id === id) ?? null : null;
}

export function selectedWorkflowNode(): WorkflowRunRecord['nodes'][number] | null {
  const run = selectedWorkflowRun();
  const nodeId = workflowState.selectedNodeId;
  return run && nodeId ? run.nodes.find((node) => node.nodeId === nodeId) ?? null : null;
}

/** Filtering is presentation state, not a second workflow projection. */
export function filteredWorkflowRuns(): WorkflowRunRecord[] {
  const query = workflowState.query.trim().toLowerCase();
  const filter = workflowState.filter;
  return workflowState.runs.filter((run) => {
    const matchesQuery =
      !query ||
      run.definition.name.toLowerCase().includes(query) ||
      run.workflowId.toLowerCase().includes(query) ||
      run.id.toLowerCase().includes(query);
    if (!matchesQuery) return false;
    if (filter === 'active') return ['queued', 'running', 'paused'].includes(run.state);
    if (filter === 'waiting') return ['waiting-approval', 'waiting-input'].includes(run.state);
    if (filter === 'failed') return run.state === 'failed';
    if (filter === 'completed') return ['completed', 'cancelled'].includes(run.state);
    return true;
  });
}

export function workflowFilterOptions(): readonly { value: WorkflowFilter; label: string }[] {
  return [
    { value: 'all', label: 'All runs' },
    { value: 'active', label: 'Active' },
    { value: 'waiting', label: 'Waiting' },
    { value: 'failed', label: 'Failed' },
    { value: 'completed', label: 'Completed' }
  ];
}
