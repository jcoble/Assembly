/**
 * Pure domain logic for the Run panel: parsing, serialization, environment
 * variable parsing/quoting, validation, state derivation, and base64 UTF-8
 * decoding. Zero Svelte runes, zero backend calls, zero side effects.
 */

import type {
  StackDefinition,
  StackDraft,
  StackEnvVar,
  StackProcess,
  StackRow,
  StackRunRecord,
  StackState,
  StackStatus,
  TerminalInfo
} from './types';

// ── Storage keys & constants ─────────────────────────────────────────────────

/** Preference key storing the list of saved run configurations. */
export const STACK_DEFINITIONS_SETTING_KEY = 'run-configurations.definitions';

/** Preference key storing active/recent run records so state survives reload. */
export const STACK_RUNS_SETTING_KEY = 'run-configurations.runs';

/** Shown when writing a configuration to SQLite preferences fails. */
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

/** Accepts either a JSON array string or an already-parsed array from preferences. */
function coerceArray(raw: unknown): unknown[] {
  if (Array.isArray(raw)) return raw;
  if (typeof raw !== 'string' || raw.trim() === '') return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function parseEnvList(value: unknown): StackEnvVar[] {
  if (!Array.isArray(value)) return [];
  const seen = new Set<string>();
  const variables: StackEnvVar[] = [];
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

// ── Parsing & serialization ──────────────────────────────────────────────────

/**
 * Tolerantly parse saved run configurations from either a JSON string or a
 * parsed preference array. Invalid rows and duplicate ids are dropped so
 * corrupt storage can never prevent the panel from opening.
 */
export function parseStackDefinitions(raw: unknown): StackDefinition[] {
  const seen = new Set<string>();
  const definitions: StackDefinition[] = [];
  for (const entry of coerceArray(raw)) {
    if (typeof entry !== 'object' || entry === null) continue;
    const row = entry as Record<string, unknown>;
    const id = trimmedString(row.id);
    const name = trimmedString(row.name);
    const script = trimmedString(row.script);
    const cwd = trimmedString(row.cwd);
    if (!id || !name || !script || !cwd) continue;
    if (seen.has(id)) continue;
    seen.add(id);
    const definition: StackDefinition = { id, name, script, cwd };
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

export function normalizeDefinitionsForStorage(
  definitions: readonly StackDefinition[]
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

export function serializeStackDefinitions(definitions: readonly StackDefinition[]): string {
  return JSON.stringify(normalizeDefinitionsForStorage(definitions));
}

export function parseStackRuns(raw: unknown): StackRunRecord[] {
  const seen = new Set<string>();
  const runs: StackRunRecord[] = [];
  for (const entry of coerceArray(raw)) {
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
      exitCode: typeof row.exitCode === 'number' && Number.isFinite(row.exitCode) ? row.exitCode : null,
      signal: trimmedString(row.signal) || null
    });
  }
  return runs;
}

export function normalizeRunsForStorage(runs: readonly StackRunRecord[]): StackRunRecord[] {
  return runs.map((run) => ({
    ownedId: run.ownedId,
    stackId: run.stackId,
    startedAt: run.startedAt,
    exited: run.exited,
    exitCode: run.exitCode,
    signal: run.signal
  }));
}

export function serializeStackRuns(runs: readonly StackRunRecord[]): string {
  return JSON.stringify(normalizeRunsForStorage(runs));
}

// ── Environment variables ────────────────────────────────────────────────────

function unquote(value: string): string {
  if (value.length < 2) return value;
  const first = value[0];
  const last = value[value.length - 1];
  if ((first === '"' || first === "'") && first === last) return value.slice(1, -1);
  return value;
}

/**
 * Parse `NAME=value` lines from the environment textarea.
 * Skips blank lines and `#` comments, strips optional leading `export `,
 * removes surrounding matching quotes, and reports unreadable lines separately.
 */
export function readEnvLines(text: string | null | undefined): {
  variables: StackEnvVar[];
  unreadable: string[];
} {
  const variables: StackEnvVar[] = [];
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

export function formatEnvLines(env: readonly StackEnvVar[] | null | undefined): string {
  return (env ?? []).map((entry) => `${entry.key}=${entry.value}`).join('\n');
}

function shellQuote(value: string): string {
  return `'${value.split("'").join("'\\''")}'`;
}

/** Build the shell command line with single-quoted environment variable assignments in front. */
export function commandWithEnv(
  script: string,
  env: readonly StackEnvVar[] | null | undefined
): string {
  const command = script.trim();
  const prefix = (env ?? [])
    .filter((entry) => isEnvName(entry.key))
    .map((entry) => `${entry.key}=${shellQuote(entry.value)}`)
    .join(' ');
  return prefix ? `${prefix} ${command}` : command;
}

// ── Draft validation & helpers ───────────────────────────────────────────────

export function describeStackProblem(draft: StackDraft): string | null {
  if (!draft.name.trim()) return 'Give this run configuration a name first.';
  if (!draft.script.trim()) return 'Type the command it should run.';
  if (!draft.cwd.trim()) return 'Say which folder it should run in.';
  return null;
}

export function buildStackDefinition(id: string, draft: StackDraft): StackDefinition {
  const cwd = draft.cwd.trim().replace(/(.)\/+$/, '$1');
  const definition: StackDefinition = {
    id,
    name: draft.name.trim(),
    script: draft.script.trim(),
    cwd
  };
  const env = (draft.env ?? [])
    .filter((entry) => isEnvName(entry.key.trim()))
    .map((entry) => ({ key: entry.key.trim(), value: entry.value }));
  if (env.length > 0) definition.env = env;
  const keybinding = (draft.keybinding ?? '').trim();
  if (keybinding) definition.keybinding = keybinding;
  const previewUrl = (draft.previewUrl ?? '').trim();
  if (previewUrl) definition.previewUrl = previewUrl;
  if (draft.runOnWorktreeCreation === true) definition.runOnWorktreeCreation = true;
  if (draft.openPreviewOnRun === true) definition.openPreviewOnRun = true;
  return definition;
}

export function mintStackId(): string {
  const random = globalThis.crypto?.randomUUID?.();
  return random ? `stack-${random}` : `stack-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

export function folderName(path: string | null | undefined): string {
  if (!path) return '';
  const parts = path.split('/').filter(Boolean);
  return parts[parts.length - 1] ?? '';
}

export function isInsideRoot(cwd: string, root: string): boolean {
  const folder = (cwd ?? '').replace(/\/+$/, '');
  const base = (root ?? '').replace(/\/+$/, '');
  if (!base) return true;
  return folder === base || folder.startsWith(`${base}/`);
}

// ── State derivation ─────────────────────────────────────────────────────────

export function stackProcessesFor(
  definition: { cwd: string },
  processes: readonly StackProcess[]
): StackProcess[] {
  const root = definition.cwd.replace(/\/+$/, '');
  if (!root) return [];
  return processes.filter((process) => {
    const cwd = (process.cwd ?? '').replace(/\/+$/, '');
    return cwd === root || cwd.startsWith(`${root}/`);
  });
}

/**
 * Derive one action's status from its latest run record, optional listening
 * processes, and live `.NET` `TerminalInfo`.
 */
export function deriveStackState(
  definition: { cwd: string; script?: string },
  run: StackRunRecord | null | undefined,
  processes: readonly StackProcess[],
  terminal?: TerminalInfo | null
): StackStatus {
  if (!run) {
    return { state: 'stopped', ports: [], processes: [], exitCode: null, signal: null };
  }
  if (run.exited) {
    const failed = (run.exitCode !== null && run.exitCode !== 0) || Boolean(run.signal);
    return {
      state: failed ? 'failed' : 'stopped',
      ports: [],
      processes: [],
      exitCode: run.exitCode,
      signal: run.signal
    };
  }
  if (terminal && terminal.exitCode !== null) {
    const failed = terminal.exitCode !== 0;
    return {
      state: failed ? 'failed' : 'stopped',
      ports: [],
      processes: [],
      exitCode: terminal.exitCode,
      signal: null
    };
  }
  const matched = stackProcessesFor(definition, processes);
  const ports = [...new Set(matched.map((process) => process.port).filter((port) => port > 0))].sort(
    (left, right) => left - right
  );
  if (terminal && terminal.exitCode === null) {
    const liveProcesses =
      matched.length > 0
        ? matched
        : [
            {
              pid: terminal.pid,
              port: 0,
              command: terminal.command ?? definition.script ?? '',
              cwd: terminal.cwd
            }
          ];
    return {
      state: 'running',
      ports,
      processes: liveProcesses,
      exitCode: null,
      signal: null
    };
  }
  return {
    state: ports.length > 0 ? 'running' : 'starting',
    ports,
    processes: matched,
    exitCode: null,
    signal: null
  };
}

function joinWords(words: string[]): string {
  if (words.length === 0) return '';
  if (words.length === 1) return words[0];
  return `${words.slice(0, -1).join(', ')} and ${words[words.length - 1]}`;
}

export function describeStackState(status: {
  state: StackState;
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

export function buildStackRows(
  definitions: readonly StackDefinition[],
  runsByOwnedId: Record<string, StackRunRecord>,
  processes: readonly StackProcess[],
  terminalsById: Record<string, TerminalInfo> = {}
): StackRow[] {
  const runByStack = new Map<string, StackRunRecord>();
  for (const run of Object.values(runsByOwnedId)) {
    const existing = runByStack.get(run.stackId);
    if (!existing || run.startedAt >= existing.startedAt) {
      runByStack.set(run.stackId, run);
    }
  }
  return definitions.map((definition) => {
    const run = runByStack.get(definition.id) ?? null;
    const terminal = run ? (terminalsById[run.ownedId] ?? null) : null;
    const status = deriveStackState(definition, run, processes, terminal);
    return {
      ...status,
      definition,
      ownedId: run ? run.ownedId : null,
      statusLabel: describeStackState(status)
    };
  });
}

// ── Wire decoding ────────────────────────────────────────────────────────────

const utf8Decoder = new TextDecoder('utf-8');

/** Decode a base64-encoded UTF-8 `byte[]` payload from `.NET` `System.Text.Json`. */
export function decodeBase64Utf8(base64: string | null | undefined): string {
  if (!base64) return '';
  try {
    const binary = globalThis.atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i += 1) {
      bytes[i] = binary.charCodeAt(i);
    }
    return utf8Decoder.decode(bytes);
  } catch {
    return '';
  }
}
