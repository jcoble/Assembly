/**
 * discardConfirm.ts — the exact question asked before any change is thrown away.
 *
 * Discarding is the one thing this panel does that git cannot undo: there is no
 * commit to go back to and no reflog entry, the work is simply gone. So the
 * wording is not decided inside a component where it can only be read by
 * clicking through the app. It is worked out here, as a pure function of what
 * was picked, and the test below reads every sentence.
 *
 * Two rules the shapes enforce:
 *
 * 1. **The question names the files.** A dialog that says "discard changes?"
 *    with no list is a dialog people learn to click through. Up to five names
 *    are shown, and the rest are counted.
 * 2. **Deleting untracked files is a separate sentence.** `git restore` puts a
 *    tracked file back; an untracked file has nowhere to go back to, so it is
 *    deleted outright. Those are different consequences and they are said
 *    differently.
 */

export interface DiscardTarget {
  relativePath: string;
  /** True when git has never tracked this file, so discarding deletes it. */
  untracked: boolean;
}

export interface DiscardRequest {
  /** One file's row, or the whole working copy. */
  scope: 'file' | 'all';
  targets: DiscardTarget[];
  /** For the "all" scope: how many untracked files are in the working copy. */
  untrackedCount?: number;
}

export interface DiscardQuestion {
  title: string;
  intro: string;
  lines: string[];
  confirmLabel: string;
  cancelLabel: string;
  /** Always true — a discard has no gentle variant. Kept so the dialog can read it. */
  destructive: true;
  /** True when going through with it deletes files off disk. */
  deletesFiles: boolean;
}

/** How many file names the question spells out before it starts counting. */
export const DISCARD_NAMES_SHOWN = 5;

function count(value: number, singular: string): string {
  return value === 1 ? `1 ${singular}` : `${value} ${singular}s`;
}

/** The file names the question lists, with the overflow counted rather than cut. */
export function describeDiscardTargets(targets: DiscardTarget[]): string[] {
  const shown = targets.slice(0, DISCARD_NAMES_SHOWN).map((target) => target.relativePath);
  const hidden = targets.length - shown.length;
  if (hidden > 0) shown.push(`…and ${count(hidden, 'more file')}`);
  return shown;
}

/**
 * The whole question. `scope: 'all'` never lists names — the answer is "every
 * changed file", and a list of forty paths in a dialog is not read by anyone.
 */
export function describeDiscardQuestion(request: DiscardRequest): DiscardQuestion {
  const untracked = request.targets.filter((target) => target.untracked);

  if (request.scope === 'all') {
    const untrackedCount = request.untrackedCount ?? untracked.length;
    const lines = [
      'Every tracked file goes back to how the last commit has it, staged or not.'
    ];
    if (untrackedCount > 0) {
      lines.push(
        `${count(untrackedCount, 'new file')} git has never tracked will be deleted from disk.`
      );
    }
    lines.push('Git keeps no copy of this. It cannot be undone.');
    return {
      title: 'Discard every change?',
      intro: 'This throws away the whole working copy, not just what is on screen.',
      lines,
      confirmLabel: untrackedCount > 0 ? 'Discard and delete' : 'Discard everything',
      cancelLabel: 'Keep my changes',
      destructive: true,
      deletesFiles: untrackedCount > 0
    };
  }

  const one = request.targets.length === 1;
  const lines: string[] = [];
  if (untracked.length > 0) {
    lines.push(
      `${count(untracked.length, 'file')} git has never tracked will be deleted from disk.`
    );
  }
  const tracked = request.targets.length - untracked.length;
  if (tracked > 0) {
    lines.push(
      `${count(tracked, 'file')} goes back to how the last commit has it, staged or not.`
    );
  }
  lines.push(...describeDiscardTargets(request.targets));
  lines.push('Git keeps no copy of this. It cannot be undone.');

  return {
    title: one ? 'Discard changes to this file?' : `Discard changes to ${count(request.targets.length, 'file')}?`,
    intro: 'The work in it is not committed anywhere, so it goes for good.',
    lines,
    confirmLabel: untracked.length > 0 && tracked === 0 ? 'Delete the file' : 'Discard changes',
    cancelLabel: 'Keep my changes',
    destructive: true,
    deletesFiles: untracked.length > 0
  };
}
