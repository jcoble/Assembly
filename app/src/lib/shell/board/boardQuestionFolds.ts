// Which Build Board questions the owner folded, kept across closing the board and app restarts.

export type Folds = Record<string, boolean>;
export const FOLDS_KEY = 'assembly.board.questionFolds';

/** A question the owner never folded starts expanded while open and collapsed once answered. */
export const isFoldOpen = (folds: Folds, id: string, answered: boolean): boolean => folds[id] ?? !answered;

export function readFolds(storage: Storage): Folds {
  try {
    const value: unknown = JSON.parse(storage.getItem(FOLDS_KEY) ?? '{}');
    return value && typeof value === 'object' && !Array.isArray(value) ? value as Folds : {};
  } catch {
    return {};
  }
}

/** The board lists only some of each machine's questions (and none from an unreachable one), so a
 *  missing id isn't gone. Instead of pruning, keep the newest MAX_FOLDS entries. */
export const MAX_FOLDS = 500;

export function writeFolds(storage: Storage, folds: Folds): void {
  storage.setItem(FOLDS_KEY, JSON.stringify(Object.fromEntries(Object.entries(folds).slice(-MAX_FOLDS))));
}
