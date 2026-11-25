/**
 * Pure domain and wire types for the Run panel (saved runnable commands).
 * No Svelte runes, no backend calls, no DOM dependencies.
 */

/** One environment variable set for a run configuration. */
export interface StackEnvVar {
  /** The name, as the shell sees it — `NODE_ENV`. */
  key: string;
  /** The value, unquoted. Quoting happens when the command line is built. */
  value: string;
}

/**
 * A saved run configuration: what to run, in which folder, under what name.
 *
 * Every field after `cwd` is optional so older stored records load unchanged
 * without adding empty keys.
 */
export interface StackDefinition {
  /** Minted client-side; the key everything else points at. */
  id: string;
  /** What the user calls it — "Web", "API", "Database". */
  name: string;
  /** The command line, exactly as it would be typed into a terminal. */
  script: string;
  /** The project folder the command runs in. */
  cwd: string;
  /** Environment variables to set for the run; absent when there are none. */
  env?: StackEnvVar[];
  /** Captured shortcut, e.g. "Cmd+Shift+R". Absent when none is set. */
  keybinding?: string;
  /** A page to open when this action runs. Absent when the action serves no page. */
  previewUrl?: string;
  /** Run this action automatically whenever a worktree is created. */
  runOnWorktreeCreation?: boolean;
  /** Open `previewUrl` when this action runs. */
  openPreviewOnRun?: boolean;
}

/** The terminal instance a run configuration was last started in, and how it ended. */
export interface StackRunRecord {
  /** The `.NET` terminal id (`term-...`) — the key this record is stored under. */
  ownedId: string;
  /** Which saved run configuration this terminal was started for. */
  stackId: string;
  /** `Date.now()` when it was started; `0` when a saved value was unreadable. */
  startedAt: number;
  /** The terminal has ended. */
  exited: boolean;
  /** The exit code the terminal reported, when it reported one. */
  exitCode: number | null;
  /** The signal that killed it, when one did. */
  signal: string | null;
}

/** One active process associated with a running action. */
export interface StackProcess {
  pid: number;
  port: number;
  command: string;
  cwd: string;
}

/** Where a run configuration stands right now. */
export type StackState = 'stopped' | 'starting' | 'running' | 'failed';

/** Derived state for one run configuration. */
export interface StackStatus {
  state: StackState;
  /** Listening ports, when reported; empty otherwise. */
  ports: number[];
  /** Live process metadata (PID, command, folder, optional port). */
  processes: StackProcess[];
  exitCode: number | null;
  signal: string | null;
}

/** A saved action as the Run panel renders it: definition plus live state. */
export interface StackRow extends StackStatus {
  definition: StackDefinition;
  /** The `.NET` terminal id it is running (or last ran) in, or `null`. */
  ownedId: string | null;
  /** Human-readable status sentence for the row chip. */
  statusLabel: string;
}

/** Form values handed over when adding or editing a run configuration. */
export interface StackDraft {
  name: string;
  script: string;
  cwd: string;
  env?: StackEnvVar[];
  keybinding?: string;
  previewUrl?: string;
  runOnWorktreeCreation?: boolean;
  openPreviewOnRun?: boolean;
}

/** `.NET` `TerminalInfo` payload returned by `terminal/start` and `terminal/list`. */
export interface TerminalInfo {
  id: string;
  cwd: string;
  shell: string;
  command: string | null;
  cols: number;
  rows: number;
  pid: number;
  owner: string | null;
  startedAt: string;
  exitCode: number | null;
}

/** `.NET` `OutputPiece` payload returned by `terminal/read`. */
export interface OutputPiece {
  offset: number;
  end: number;
  data: string;
}

/** `.NET` `OutputRow` change payload published on the `terminal` socket topic. */
export interface OutputRow {
  seq?: number | null;
  prevSeq?: number | null;
  data?: string | null;
  exit?: number | null;
  unavailable?: string | null;
}

/** An active live output watch. Calling `stop` is idempotent. */
export interface RunOutputWatch {
  stop(): void;
}
