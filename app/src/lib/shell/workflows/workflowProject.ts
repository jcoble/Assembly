/**
 * A project's progress, read from run records: its slices are the runs whose
 * `parent_task` input is the project's task key. A slice run again shows its
 * latest run; slices are in the order they started, which is plan order.
 */
import { boardColumn, runTask, type BoardColumnId, type StepGlyph } from './workflowBoard.ts';
import type { WorkflowRunRecord } from './workflowTypes.ts';

export interface ProjectSlice {
  key: string;
  title: string;
  /** The slice's number in the approved plan ("1.2"), when the run was given one. */
  number: string | null;
  run: WorkflowRunRecord;
  glyph: StepGlyph;
}

export interface ProjectProgress {
  slices: ProjectSlice[];
  /** Slices whose run completed. */
  done: number;
  total: number;
  /** The first slice not completed, or null when all are. */
  current: ProjectSlice | null;
}

const input = (run: WorkflowRunRecord, name: string): string | null => {
  const value = (run.inputSnapshot as { inputs?: Record<string, unknown> } | null)?.inputs?.[name];
  return typeof value === 'string' && value ? value : null;
};

export const parentTask = (run: WorkflowRunRecord): string | null => input(run, 'parent_task');

/** A whole run's state as a step glyph. */
export function runGlyph(run: WorkflowRunRecord): StepGlyph {
  if (run.state.startsWith('waiting') || run.nodes.some((node) => node.state.startsWith('waiting'))) return 'needs-you';
  switch (run.state) {
    case 'completed': return 'passed';
    case 'running': return 'running';
    case 'failed': return 'failed';
    case 'cancelled': return 'stopped';
  }
  return 'waiting';
}

/** The run list read once: each parent task's child runs, and when each task's newest run was created. */
export interface ProjectIndex {
  children: Map<string, WorkflowRunRecord[]>;
  newest: Map<string, number>;
}

/** Built once per change of the run list; every card then reads its project from it. */
export function projectIndex(runs: readonly WorkflowRunRecord[]): ProjectIndex {
  const children = new Map<string, WorkflowRunRecord[]>();
  const newest = new Map<string, number>();
  for (const run of runs) {
    const key = runTask(run).key;
    if (!key) continue;
    newest.set(key, Math.max(newest.get(key) ?? run.createdAtMs, run.createdAtMs));
    const parent = parentTask(run);
    if (!parent) continue;
    const siblings = children.get(parent);
    if (siblings) siblings.push(run);
    else children.set(parent, [run]);
  }
  return { children, newest };
}

/** The project's slices, or null when no run names it as its parent. */
export function projectProgress(project: WorkflowRunRecord, index: ProjectIndex): ProjectProgress | null {
  const key = runTask(project).key;
  // A project run again (say after its plan was sent back) leaves the slices to its latest run.
  if (!key || (index.newest.get(key) ?? 0) > project.createdAtMs) return null;
  const latest = new Map<string, { first: number; run: WorkflowRunRecord }>();
  for (const run of index.children.get(key) ?? []) {
    const task = runTask(run).key;
    if (run.id === project.id || !task) continue;
    const seen = latest.get(task);
    latest.set(task, {
      first: Math.min(seen?.first ?? run.createdAtMs, run.createdAtMs),
      run: seen && seen.run.createdAtMs > run.createdAtMs ? seen.run : run
    });
  }
  if (!latest.size) return null;
  const slices = [...latest.entries()].sort((a, b) => a[1].first - b[1].first).map(([task, { run }]) => ({
    key: task, title: runTask(run).title, number: input(run, 'slice'), run, glyph: runGlyph(run)
  }));
  const done = slices.filter((slice) => slice.run.state === 'completed').length;
  return { slices, done, total: slices.length, current: slices.find((slice) => slice.run.state !== 'completed') ?? null };
}

export const sliceCount = (progress: ProjectProgress): string => `${progress.done} / ${progress.total} slices`;

/** A project's Board column is its current slice's; any other run's is its own. */
export function projectColumn(run: WorkflowRunRecord, index: ProjectIndex): BoardColumnId {
  const progress = projectProgress(run, index);
  if (!progress) return boardColumn(run);
  return progress.current ? boardColumn(progress.current.run) : 'done';
}
