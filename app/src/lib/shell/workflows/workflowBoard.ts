/**
 * Which column of the Workflows board a run sits in, read from the engine's run
 * record: a waiting step puts it in Needs you, an ended run in Done, and
 * otherwise its current step's name says where in the pipeline it is.
 */
import type { WorkflowNodeRunRecord, WorkflowRunRecord } from './workflowTypes.ts';

export const BOARD_COLUMNS = [
  { id: 'needs-you', label: 'Needs you' },
  { id: 'queued', label: 'Queued' },
  { id: 'spec', label: 'Spec' },
  { id: 'build', label: 'Build' },
  { id: 'check', label: 'Check' },
  { id: 'review', label: 'Review' },
  { id: 'proof', label: 'Proof' },
  { id: 'pr', label: 'PR and bot' },
  { id: 'merge', label: 'Merge' },
  { id: 'done', label: 'Done' }
] as const;

export type BoardColumnId = (typeof BOARD_COLUMNS)[number]['id'];

const WAITING = ['waiting-input', 'waiting-approval'];
const ACTIVE = ['starting', 'running', 'failed', ...WAITING];

/** The step a run is at: the first one active, else the last one that started, else the first. */
export function currentStep(run: WorkflowRunRecord): WorkflowNodeRunRecord | null {
  const ordered = run.definition.nodes
    .map((definition) => run.nodes.find((node) => node.nodeId === definition.id))
    .filter((node): node is WorkflowNodeRunRecord => Boolean(node));
  return ordered.find((node) => ACTIVE.includes(node.state))
    ?? ordered.findLast((node) => node.startedAtMs !== null)
    ?? ordered[0] ?? null;
}

export function boardColumn(run: WorkflowRunRecord): BoardColumnId {
  if (WAITING.includes(run.state) || run.nodes.some((node) => WAITING.includes(node.state))) return 'needs-you';
  if (run.state === 'completed' || run.state === 'cancelled') return 'done';
  const step = currentStep(run);
  if (run.state === 'draft' || run.state === 'queued' || !step?.startedAtMs) return 'queued';
  const id = step.nodeId.toLowerCase();
  if (/spec|recon|plan/.test(id)) return 'spec';
  if (/merge|gate/.test(id)) return 'merge';
  if (/(^|-)pr($|-)|bot/.test(id)) return 'pr';
  if (/proof|judge/.test(id)) return 'proof';
  if (/review/.test(id)) return 'review';
  if (/check|test|verif/.test(id)) return 'check';
  return 'build';
}

export type StepGlyph = 'passed' | 'running' | 'sent-back' | 'failed' | 'stopped' | 'needs-you' | 'waiting' | 'skipped';

/** The glyph a step shows on the Board and the Map. A step waiting again after it ran was sent back. */
export function stepGlyph(node: Pick<WorkflowNodeRunRecord, 'state' | 'attempt'>): StepGlyph {
  switch (node.state) {
    case 'completed': return 'passed';
    case 'starting':
    case 'running': return 'running';
    case 'failed': return 'failed';
    case 'waiting-input':
    case 'waiting-approval': return 'needs-you';
    case 'skipped': return 'skipped';
    case 'cancelled': return 'stopped';
  }
  return node.attempt > 1 ? 'sent-back' : 'waiting';
}

/** A duration as the board shows it: "45s", "12m", "2h 05m", "3 days". */
export function shortDuration(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours}h ${String(minutes % 60).padStart(2, '0')}m`;
  return `${Math.floor(hours / 24)} days`;
}

/** The run's task, as its snapshot holds it. */
export function runTask(run: WorkflowRunRecord): { key: string | null; title: string } {
  const task = (run.inputSnapshot as { task?: { key?: unknown; title?: unknown } } | null)?.task;
  return {
    key: typeof task?.key === 'string' && task.key ? task.key : null,
    title: typeof task?.title === 'string' && task.title ? task.title : run.definition.name
  };
}

const dollars = (micros: number): string => `$${(micros / 1_000_000).toFixed(2)}`;

/**
 * Usage rows' cost: "$1.07" when all of it was reported, else "$1.07 + $0.74 est.", or '' when none is priced.
 * A row's estimate only counts usage no reported cost covers, so the two add up.
 */
export function costText(rows: readonly { reportedCostMicros: number | null; estimatedCostMicros: number | null }[]): string {
  const reported = rows.reduce((sum, row) => sum + (row.reportedCostMicros ?? 0), 0);
  const estimated = rows.reduce((sum, row) => sum + (row.estimatedCostMicros ?? 0), 0);
  const priced = rows.some((row) => row.reportedCostMicros !== null);
  if (!priced && !estimated) return '';
  return [priced ? dollars(reported) : '', estimated ? `${dollars(estimated)} est.` : ''].filter(Boolean).join(' + ');
}
