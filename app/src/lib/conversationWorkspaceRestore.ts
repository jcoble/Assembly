/**
 * Pure conversation restore planner for "P3 — conversations as live workspaces".
 *
 * Computes a restore plan over plain strings/paths only: no app state, no DOM,
 * and no git execution. It honors the strict disk/worktree safety rules by NEVER
 * emitting a git-mutating command — restoring the working directory is done by
 * pointing the terminal's own `cwd` at the resolved path. A worktree that is no
 * longer registered is surfaced to the UI (via `missingWorktree`) as a copy-plan,
 * never auto-repaired here.
 *
 * Mirrors the style of `workspaceSnapshot.ts`. Its path-normalization helpers are
 * not exported, so equivalent minimal helpers are defined locally below.
 */

export type ConversationRestoreSession = {
  projectPath?: string | null;
  branchHint?: string | null;
};

export type ConversationRestoreSnapshot = {
  worktreePath?: string | null;
  branch?: string | null;
  cwd?: string | null;
};

export type ConversationRestoreInput = {
  session: ConversationRestoreSession;
  snapshot: ConversationRestoreSnapshot;
  knownWorktreePaths: string[];
  resumeCommand: string;
};

export type ConversationRestorePlan = {
  cwd: string;
  branch: string | null;
  worktreePath: string | null;
  missingWorktree: boolean;
  preCommands: string[];
  resumeCommand: string;
};

export function planConversationRestore(input: ConversationRestoreInput): ConversationRestorePlan {
  const session = input.session ?? {};
  const snapshot = input.snapshot ?? {};

  const worktreePath = normalizeOptionalPath(snapshot.worktreePath);
  const projectPath = normalizeOptionalPath(session.projectPath);
  const snapshotCwd = normalizeOptionalPath(snapshot.cwd);

  // A worktree is already checked out to its branch, so it wins and no switch is
  // needed; otherwise fall back to the session project path, then the snapshot cwd.
  const cwd = worktreePath ?? projectPath ?? snapshotCwd ?? '';

  // Branch is informational only — it never drives a command.
  const branch = normalizeOptionalString(session.branchHint) ?? normalizeOptionalString(snapshot.branch) ?? null;

  const knownWorktreePaths = normalizePathList(input.knownWorktreePaths);
  const missingWorktree = worktreePath !== null && !knownWorktreePaths.includes(worktreePath);

  return {
    cwd,
    branch,
    worktreePath,
    missingWorktree,
    // ALWAYS empty: no `git switch`, no `git checkout`, no `git worktree add`.
    // Restoring the cwd via the terminal is enough; a missing worktree is a
    // copy-plan surfaced separately to the UI, not auto-fixed here.
    preCommands: [],
    resumeCommand: input.resumeCommand
  };
}

function normalizePath(path: string): string {
  return path.trim().replace(/\/+$/, '');
}

function normalizeOptionalPath(path: string | null | undefined): string | null {
  if (typeof path !== 'string') return null;
  const normalizedPath = normalizePath(path);
  return normalizedPath.length === 0 ? null : normalizedPath;
}

function normalizeOptionalString(value: string | null | undefined): string | null {
  if (typeof value !== 'string') return null;
  const normalizedValue = value.trim();
  return normalizedValue.length === 0 ? null : normalizedValue;
}

function normalizePathList(paths: string[] | null | undefined): string[] {
  const seen = new Set<string>();
  return (paths ?? [])
    .map((path) => normalizeOptionalPath(path))
    .filter((path): path is string => path !== null)
    .filter((path) => {
      if (seen.has(path)) return false;
      seen.add(path);
      return true;
    });
}
