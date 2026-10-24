/**
 * resourceStopModel.ts — the words and the request behind the stop button.
 *
 * Stopping something from this panel ends real work, so the dialog has to say
 * exactly what will be signalled — the process ids, not "this session". That
 * wording is decided here rather than inside the component so it can be read in
 * a test, and so the request sent to the backend is built from the same numbers
 * the person just read.
 *
 * Nothing in this file stops anything on its own. There is no automatic path:
 * the only caller is a button, and the only thing that follows the button is a
 * dialog.
 */

export type ResourceStopScope = 'session' | 'process';

export type ResourceStopTarget = {
  scope: ResourceStopScope;
  /** What the row is called in the panel. */
  label: string;
  /** The session the row belongs to, when the panel knows it. */
  ownedId?: string;
  /** The process the signal starts from. */
  rootPid: number;
  /** Every process id the signal will reach, root included. */
  pids: number[];
};

export type ResourceStopQuestion = {
  title: string;
  intro: string;
  lines: string[];
  confirmLabel: string;
  cancelLabel: string;
};

export type ResourceStopRequestBody = {
  rootPid: number;
  ownedId?: string;
  expectedPids: number[];
};

/** Process ids, in order, with no repeats — the list the dialog reads out. */
export function stopTargetPids(target: ResourceStopTarget): number[] {
  const seen = new Set<number>();
  const ordered: number[] = [];
  for (const pid of [target.rootPid, ...target.pids]) {
    if (!Number.isInteger(pid) || pid <= 0 || seen.has(pid)) continue;
    seen.add(pid);
    ordered.push(pid);
  }
  return ordered;
}

function joinPids(pids: number[]): string {
  if (pids.length === 0) return 'none';
  if (pids.length === 1) return `PID ${pids[0]}`;
  return `PIDs ${pids.slice(0, -1).join(', ')} and ${pids[pids.length - 1]}`;
}

/** What the confirmation dialog says before anything is signalled. */
export function describeStopQuestion(target: ResourceStopTarget): ResourceStopQuestion {
  const pids = stopTargetPids(target);
  const isSession = target.scope === 'session';
  return {
    title: isSession ? `Stop ${target.label}?` : `Stop ${target.label}?`,
    intro: isSession
      ? 'This ends the session and everything running under it.'
      : 'This ends this process and anything it started.',
    lines: [
      `${pids.length} ${pids.length === 1 ? 'process' : 'processes'} will be asked to stop: ${joinPids(pids)}.`,
      'Anything still running five seconds later is forced to quit.',
      'Unsaved work in those processes is lost.'
    ],
    confirmLabel: pids.length === 1 ? 'Stop process' : 'Stop processes',
    cancelLabel: 'Leave it running'
  };
}

/** The exact request the backend receives, built from the numbers just shown. */
export function buildStopRequest(target: ResourceStopTarget): ResourceStopRequestBody {
  const request: ResourceStopRequestBody = {
    rootPid: target.rootPid,
    expectedPids: stopTargetPids(target)
  };
  if (target.ownedId) request.ownedId = target.ownedId;
  return request;
}
