/**
 * Pure domain and wire types for the Run panel feature.
 * No Svelte runes, no WebSockets, no DOM dependencies.
 */

/** One environment variable set for a run configuration. */
export interface RunEnvVar {
  /** Variable name as the shell sees it, e.g. `NODE_ENV`. */
  key: string;
  /** Unquoted value; POSIX single-quoting happens when building the command line. */
  value: string;
}

/**
 * A saved run configuration: what to run, in which folder, under what name.
 * Optional fields stay omitted when unset so stored JSON stays identical to mac-command-bar.
 */
export interface RunDefinition {
  id: string;
  name: string;
  script: string;
  cwd: string;
  env?: RunEnvVar[];
  keybinding?: string;
  previewUrl?: string;
  runOnWorktreeCreation?: boolean;
  openPreviewOnRun?: boolean;
}

/** Editor draft handed over when adding or editing a run configuration. */
export interface RunDraft {
  name: string;
  script: string;
  cwd: string;
  env?: RunEnvVar[];
  keybinding?: string;
  previewUrl?: string;
  runOnWorktreeCreation?: boolean;
  openPreviewOnRun?: boolean;
}

/**
 * The terminal a run configuration was last started in, and how it ended.
 * Field names (`ownedId`, `stackId`) match the persisted mac-command-bar schema 1:1.
 */
export interface RunRecord {
  /** `.NET` terminal id (`term-...`) backing this run. */
  ownedId: string;
  /** Saved `RunDefinition.id` this run belongs to. */
  stackId: string;
  /** `Date.now()` when started, or `0` if unreadable. */
  startedAt: number;
  exited: boolean;
  exitCode: number | null;
  signal: string | null;
}

/** One listening process reported for a run configuration's folder. */
export interface RunProcess {
  pid: number;
  port: number;
  command: string;
  cwd: string;
}

export type RunState = 'stopped' | 'starting' | 'running' | 'failed';

export interface RunStatus {
  state: RunState;
  ports: number[];
  processes: RunProcess[];
  pid: number | null;
  exitCode: number | null;
  signal: string | null;
}

/** A saved run configuration paired with its live status for rendering. */
export interface RunRow extends RunStatus {
  definition: RunDefinition;
  /** Active or last terminal id, or `null` if never started. */
  ownedId: string | null;
  statusLabel: string;
}

/** `.NET` `TerminalInfo` DTO returned by `terminal/start` and `terminal/list`. */
export interface TerminalInfo {
  id: string;
  cwd: string;
  shell: string;
  command?: string | null;
  cols: number;
  rows: number;
  pid: number;
  owner?: string | null;
  startedAt: string;
  exitCode?: number | null;
}

/** `.NET` `OutputPiece` DTO returned by `terminal/read` (`data` is base64 bytes). */
export interface TerminalOutputPiece {
  offset: number;
  end: number;
  data: string;
}

export type TerminalUnavailableReason = 'connection_lost' | 'terminal_missing' | 'source_changed';

/** `.NET` `OutputRow` published on `/socket/terminal` topic `terminal`. */
export interface TerminalOutputRow {
  seq?: number | null;
  prevSeq?: number | null;
  data?: string | null;
  exit?: number | null;
  unavailable?: TerminalUnavailableReason | null;
}
