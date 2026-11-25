/**
 * Pure persistence parsing/serialization, env-var quoting, validation, and status derivation
 * for the Run panel. Matches `mac-command-bar/tauri-svelte-preview/src/lib/shell/stacks/stackStore.svelte.ts` 1:1.
 *
 * Pure module (type-only imports): runnable directly in Node for focused unit tests.
 */

import type {
  RunDefinition,
  RunDraft,
  RunEnvVar,
  RunProcess,
  RunRecord,
  RunRow,
  RunState,
  RunStatus,
  TerminalInfo
} from './types';

/** Preference key where saved run configurations live (preserved from mac-command-bar). */
export const RUN_DEFINITIONS_PREF_KEY = 'run-configurations.definitions';

/** Preference key where last run records live (preserved from mac-command-bar). */
export const RUN_RUNS_PREF_KEY = 'run-configurations.runs';

/** Shown when writing a run configuration to the Assembly database fails. */
export const STORAGE_WRITE_FAILED_MESSAGE =
  'This run configuration could not be saved to the Assembly database.';

const ENV_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

function trimmedString(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function finiteNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function isEnvName(value: string): boolean {
  return ENV_NAME.test(value);
}

function parseArray(raw: unknown): unknown[] {
  if (Array.isArray(raw)) return raw;
  if (typeof raw !== 'string' || raw.trim() === '') return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function parseEnvList(value: unknown): RunEnvVar[] {
  if (!Array.isArray(value)) return [];
  const seen = new Set<string>();
  const variables: RunEnvVar[] = [];
  for (const entry of value) {
    if (typeof entry !== 'object' || entry === null) continue;
    const row = entry as Record<string, unknown>;
    const key = trimmedString(row.key);
    if (!key || !isEnvName(key) || seen.has(key)) continue;
    seen.add(key);
    variables.push({ key, value: typeof row.value === 'string' ? row.value : '' });
  }
  return variables;
}

/**
 * Tolerantly read saved run configurations from either a parsed JSON array or JSON string.
 * Corrupt entries are skipped and unset optional fields remain omitted for round-trip stability.
 */
export function parseRunDefinitions(raw: unknown): RunDefinition[] {
  const seen = new Set<string>();
  const definitions: RunDefinition[] = [];
  for (const entry of parseArray(raw)) {
    if (typeof entry !== 'object' || entry === null) continue;
    const row = entry as Record<string, unknown>;
    const id = trimmedString(row.id);
    const name = trimmedString(row.name);
    const script = trimmedString(row.script);
    const cwd = trimmedString(row.cwd);
    if (!id || !name || !script || !cwd) continue;
    if (seen.has(id)) continue;
    seen.add(id);
    const definition: RunDefinition = { id, name, script, cwd };
    const env = parseEnvList(row.env);
    if (env.length > 0) definition.env = env;
    const keybinding = trimmedString(row.keybinding);
    if (keybinding) definition.keybinding = keybinding;
    const previewUrl = trimmedString(row.previewUrl);
    if (previewUrl) definition.previewUrl = previewUrl;
    if (row.runOnWorktreeCreation === true) definition.runOnWorktreeCreation = true;
    if (row.openPreviewOnRun === true) definition.openPreviewOnRun = true;
    definitions.push(definition);
  }
  return definitions;
}

/** Build plain JSON-ready objects for persistence, omitting unset optional fields. */
export function serializeRunDefinitions(
  definitions: readonly RunDefinition[]
): Record<string, unknown>[] {
  return definitions.map((definition) => {
    const env = definition.env ?? [];
    const row: Record<string, unknown> = {
      id: definition.id,
      name: definition.name,
      script: definition.script,
      cwd: definition.cwd
    };
    if (env.length > 0) row.env = env.map((entry) => ({ key: entry.key, value: entry.value }));
    const keybinding = (definition.keybinding ?? '').trim();
    if (keybinding) row.keybinding = keybinding;
    const previewUrl = (definition.previewUrl ?? '').trim();
    if (previewUrl) row.previewUrl = previewUrl;
    if (definition.runOnWorktreeCreation === true) row.runOnWorktreeCreation = true;
    if (definition.openPreviewOnRun === true) row.openPreviewOnRun = true;
    return row;
  });
}

/** Tolerantly read saved run records from either a parsed JSON array or JSON string. */
export function parseRunRuns(raw: unknown): RunRecord[] {
  const seen = new Set<string>();
  const runs: RunRecord[] = [];
  for (const entry of parseArray(raw)) {
    if (typeof entry !== 'object' || entry === null) continue;
    const row = entry as Record<string, unknown>;
    const ownedId = trimmedString(row.ownedId);
    const stackId = trimmedString(row.stackId);
    if (!ownedId || !stackId || seen.has(ownedId)) continue;
    seen.add(ownedId);
    runs.push({
      ownedId,
      stackId,
      startedAt: finiteNumber(row.startedAt),
      exited: row.exited === true,
      exitCode:
        typeof row.exitCode === 'number' && Number.isFinite(row.exitCode) ? row.exitCode : null,
      signal: trimmedString(row.signal) || null
    });
  }
  return runs;
}

/** Build plain JSON-ready run record objects for persistence. */
export function serializeRunRuns(runs: readonly RunRecord[]): Record<string, unknown>[] {
  return runs.map((run) => ({
    ownedId: run.ownedId,
    stackId: run.stackId,
    startedAt: run.startedAt,
    exited: run.exited,
    exitCode: run.exitCode,
    signal: run.signal
  }));
}

function unquote(value: string): string {
  if (value.length < 2) return value;
  const first = value[0];
  const last = value[value.length - 1];
  if ((first === '"' || first === "'") && first === last) return value.slice(1, -1);
  return value;
}

/**
 * Parse one `KEY=value` per line from the environment textarea.
 * Skips blank lines and `#` comments, allows a leading `export `, strips one pair of matching
 * quotes, reports unreadable lines, and lets the last duplicate key win.
 */
export function readEnvLines(text: string | null | undefined): {
  variables: RunEnvVar[];
  unreadable: string[];
} {
  const variables: RunEnvVar[] = [];
  const unreadable: string[] = [];
  const positionOf = new Map<string, number>();
  for (const rawLine of String(text ?? '').split('\n')) {
    const line = rawLine.trim();
    if (line === '' || line.startsWith('#')) continue;
    const body = line.startsWith('export ') ? line.slice('export '.length).trim() : line;
    const split = body.indexOf('=');
    if (split <= 0) {
      unreadable.push(line);
      continue;
    }
    const key = body.slice(0, split).trim();
    if (!isEnvName(key)) {
      unreadable.push(line);
      continue;
    }
    const value = unquote(body.slice(split + 1).trim());
    const existing = positionOf.get(key);
    if (existing === undefined) {
      positionOf.set(key, variables.length);
      variables.push({ key, value });
    } else {
      variables[existing] = { key, value };
    }
  }
  return { variables, unreadable };
}

/** Format saved variables as one `KEY=value` per line for the editor textarea. */
export function formatEnvLines(env: readonly RunEnvVar[] | null | undefined): string {
  return (env ?? []).map((entry) => `${entry.key}=${entry.value}`).join('\n');
}

function shellQuote(value: string): string {
  return `'${value.split("'").join("'\\''")}'`;
}

/** Prefix a command with shell-quoted `KEY='value'` assignments, matching mac-command-bar 1:1. */
export function commandWithEnv(
  script: string,
  env: readonly RunEnvVar[] | null | undefined
): string {
  const command = script.trim();
  const prefix = (env ?? [])
    .filter((entry) => isEnvName(entry.key))
    .map((entry) => `${entry.key}=${shellQuote(entry.value)}`)
    .join(' ');
  return prefix ? `${prefix} ${command}` : command;
}

/** First validation error for a draft, or `null` when ready to save. */
export function describeRunProblem(draft: RunDraft): string | null {
  if (!draft.name.trim()) return 'Give this run configuration a name first.';
  if (!draft.script.trim()) return 'Type the command it should run.';
  if (!draft.cwd.trim()) return 'Say which folder it should run in.';
  return null;
}

/** Normalize a validated draft into a `RunDefinition` with the given id. */
export function buildRunDefinition(id: string, draft: RunDraft): RunDefinition {
  const cwd = draft.cwd.trim().replace(/(.)\/+$/, '$1');
  const env = (draft.env ?? [])
    .filter((entry) => isEnvName(entry.key.trim()))
    .map((entry) => ({ key: entry.key.trim(), value: entry.value }));
  const keybinding = (draft.keybinding ?? '').trim();
  const previewUrl = (draft.previewUrl ?? '').trim();

  const definition: RunDefinition = {
    id,
    name: draft.name.trim(),
    script: draft.script.trim(),
    cwd
  };
  if (env.length > 0) definition.env = env;
  if (keybinding) definition.keybinding = keybinding;
  if (previewUrl) definition.previewUrl = previewUrl;
  if (draft.runOnWorktreeCreation === true) definition.runOnWorktreeCreation = true;
  if (draft.openPreviewOnRun === true) definition.openPreviewOnRun = true;
  return definition;
}

/** Last path segment of a folder path, e.g. `/a/b/my-app` -> `my-app`. */
export function folderName(path: string | null | undefined): string {
  if (!path) return '';
  const parts = path.split('/').filter(Boolean);
  return parts[parts.length - 1] ?? '';
}

/** True when `cwd` is `root` itself or inside it (boundary-safe for `/a/ap` vs `/a/app`). */
export function isInsideRoot(cwd: string, root: string): boolean {
  const folder = (cwd ?? '').replace(/\/+$/, '');
  const base = (root ?? '').replace(/\/+$/, '');
  if (!base) return true;
  return folder === base || folder.startsWith(`${base}/`);
}

/** Listening processes belonging to a run configuration's folder or any subfolder. */
export function runProcessesFor(
  definition: { cwd: string },
  processes: readonly RunProcess[]
): RunProcess[] {
  const root = definition.cwd.replace(/\/+$/, '');
  if (!root) return [];
  return processes.filter((process) => {
    const cwd = (process.cwd ?? '').replace(/\/+$/, '');
    return cwd === root || cwd.startsWith(`${root}/`);
  });
}

function joinWords(words: string[]): string {
  if (words.length === 0) return '';
  if (words.length === 1) return words[0];
  return `${words.slice(0, -1).join(', ')} and ${words[words.length - 1]}`;
}

/** Plain-English status label for a run configuration row chip. */
export function describeRunState(status: {
  state: RunState;
  ports: number[];
  exitCode: number | null;
  signal: string | null;
}): string {
  if (status.state === 'running') {
    if (status.ports.length > 0) {
      const ports = status.ports.map((port) => String(port));
      return `running on ${ports.length === 1 ? 'port' : 'ports'} ${joinWords(ports)}`;
    }
    return 'running';
  }
  if (status.state === 'starting') return 'starting…';
  if (status.state === 'failed') {
    if (status.signal) return `stopped by ${status.signal}`;
    return `stopped with error code ${status.exitCode ?? 'unknown'}`;
  }
  return 'not running';
}

/**
 * Derive current run state from its run record, live terminal info, and optional listening ports.
 */
export function deriveRunState(
  definition: { cwd: string },
  run: RunRecord | null | undefined,
  options: {
    starting?: boolean;
    terminal?: TerminalInfo | null;
    processes?: readonly RunProcess[];
  } = {}
): RunStatus {
  const { starting = false, terminal = null, processes = [] } = options;
  if (starting && (!run || run.exited)) {
    return { state: 'starting', ports: [], processes: [], pid: null, exitCode: null, signal: null };
  }
  if (!run) {
    return { state: 'stopped', ports: [], processes: [], pid: null, exitCode: null, signal: null };
  }
  if (run.exited) {
    const failed = (run.exitCode !== null && run.exitCode !== 0) || Boolean(run.signal);
    return {
      state: failed ? 'failed' : 'stopped',
      ports: [],
      processes: [],
      pid: null,
      exitCode: run.exitCode,
      signal: run.signal
    };
  }
  const matched = runProcessesFor(definition, processes);
  const ports = [
    ...new Set(matched.map((process) => process.port).filter((port) => port > 0))
  ].sort((left, right) => left - right);
  return {
    state: 'running',
    ports,
    processes: matched,
    pid: terminal?.pid ?? null,
    exitCode: null,
    signal: null
  };
}

/** Combine every saved run configuration with its live status in saved order. */
export function buildRunRows(
  definitions: readonly RunDefinition[],
  runsByOwnedId: Readonly<Record<string, RunRecord>>,
  terminalsById: Readonly<Record<string, TerminalInfo>> = {},
  startingIds: ReadonlySet<string> = new Set(),
  processes: readonly RunProcess[] = []
): RunRow[] {
  const runByStack = new Map<string, RunRecord>();
  for (const run of Object.values(runsByOwnedId)) {
    runByStack.set(run.stackId, run);
  }
  return definitions.map((definition) => {
    const run = runByStack.get(definition.id) ?? null;
    const terminal = run ? (terminalsById[run.ownedId] ?? null) : null;
    const status = deriveRunState(definition, run, {
      starting: startingIds.has(definition.id),
      terminal,
      processes
    });
    return {
      ...status,
      definition,
      ownedId: run ? run.ownedId : null,
      statusLabel: describeRunState(status)
    };
  });
}

/** Validate and normalize a preview URL, allowing only `http:` and `https:` schemes. */
export function normalizePreviewUrl(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') return null;
  const trimmed = raw.trim();
  if (!trimmed) return null;
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
    return parsed.href;
  } catch {
    return null;
  }
}

/** Extract a concise tab title for a configured preview URL. */
export function formatPreviewTabTitle(url: string): string {
  const trimmed = url.trim();
  try {
    const parsed = new URL(trimmed);
    return parsed.host || trimmed;
  } catch {
    return trimmed || 'Preview';
  }
}

/**
 * Order saved definitions so actions inside `activeRoot` take shortcut precedence over
 * other projects when two actions share the same keybinding.
 */
export function orderShortcutsForRoot(
  definitions: readonly RunDefinition[],
  activeRoot: string | null | undefined
): RunDefinition[] {
  const root = (activeRoot ?? '').trim();
  if (!root) return [...definitions];
  const inProject: RunDefinition[] = [];
  const other: RunDefinition[] = [];
  for (const definition of definitions) {
    if (isInsideRoot(definition.cwd, root)) {
      inProject.push(definition);
    } else {
      other.push(definition);
    }
  }
  return [...inProject, ...other];
}

/**
 * Split `runs` into terminal IDs superseded by `stackId` (both exited and active) and
 * the remaining records belonging to other actions.
 */
export function partitionSupersededRuns(
  runs: Readonly<Record<string, RunRecord>>,
  stackId: string
): { supersededIds: string[]; remaining: Record<string, RunRecord> } {
  const supersededIds: string[] = [];
  const remaining: Record<string, RunRecord> = {};
  for (const run of Object.values(runs)) {
    if (run.stackId === stackId) {
      supersededIds.push(run.ownedId);
    } else {
      remaining[run.ownedId] = run;
    }
  }
  return { supersededIds, remaining };
}

/** Return a copy of `terminals` with all given IDs removed. */
export function omitTerminalIds(
  terminals: Readonly<Record<string, TerminalInfo>>,
  ids: readonly string[]
): Record<string, TerminalInfo> {
  if (ids.length === 0) return { ...terminals };
  const drop = new Set(ids);
  const next: Record<string, TerminalInfo> = {};
  for (const [id, info] of Object.entries(terminals)) {
    if (!drop.has(id)) next[id] = info;
  }
  return next;
}

/**
 * Close every backend terminal associated with `stackId` (ignoring already-closed errors)
 * so `TerminalHost` frees retained output rings before their run records are discarded.
 */
export async function releaseSupersededTerminals(
  runs: Readonly<Record<string, RunRecord>>,
  terminals: Readonly<Record<string, TerminalInfo>>,
  stackId: string,
  closeTerminal: (terminalId: string) => Promise<void>
): Promise<{
  closedIds: string[];
  remainingRuns: Record<string, RunRecord>;
  remainingTerminals: Record<string, TerminalInfo>;
}> {
  const { supersededIds, remaining } = partitionSupersededRuns(runs, stackId);
  await Promise.allSettled(supersededIds.map((terminalId) => closeTerminal(terminalId)));
  return {
    closedIds: supersededIds,
    remainingRuns: remaining,
    remainingTerminals: omitTerminalIds(terminals, supersededIds)
  };
}
