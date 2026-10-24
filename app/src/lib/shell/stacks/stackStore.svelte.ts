/**
 * stackStore.svelte.ts — Svelte 5 runes state for the /next run configurations.
 *
 * A RUN CONFIGURATION is one saved way to start something: `pnpm dev`,
 * `docker compose up`, `dotnet watch`. On screen that is the only word used.
 *
 * WHY THE CODE STILL SAYS "STACK". The exported functions and `script` field
 * were written when the feature was called a stack. The vocabulary changed
 * where a person can see it and stayed where only the code can: `stack` in this
 * module means "run configuration".
 *
 * The store holds
 *
 *  - the stacks you have saved, per project folder;
 *  - which terminal session each one was last started in — the tag that makes a
 *    session in the rail recognisable as a stack rather than an agent;
 *  - the processes that are listening on a port right now, as last read.
 *
 * THREE RULES this module exists to enforce (same shape as `browserStore` and
 * `contextStore`):
 *
 * 1. **No backend, ever.** SQLite IO is injected by `stackService.ts`. Every
 *    Tauri call still lives there and lands here as a plain value.
 * 2. **No `$effect`.** It is illegal in a `.svelte.ts` module and against the
 *    shell rules, so saving is an explicit `persist…()` call at the end of every
 *    mutator that changes something worth keeping.
 * 3. **Nothing loads at launch.** The panel is mounted up front and parked
 *    off-screen; `hydrateStacks()` (storage only) and the service's `activate`
 *    are what wake it up when the user first opens the view.
 *
 * WHY THE SESSION TAG LIVES HERE. `OwnedSession` is shared with the whole shell
 * and this lane may not edit it, so "this session is stack X" is kept in this
 * module, keyed by `ownedId`, and saved under this module's own storage key. If
 * the shell later grows a `kind: 'agent' | 'stack'` field, `stackIdForOwnedId`
 * is the one lookup that has to move.
 *
 * WHY PROCESSES ARE MATCHED BY FOLDER. The backend can only see processes that
 * are LISTENING on a port, and it reports the folder each one was started in —
 * never which terminal spawned it. A stack's shell spawns children with their
 * own pids, so a pid comparison would find nothing. So a port counts as this
 * stack's when it was opened in the stack's folder (or a folder underneath it)
 * AND this stack has a terminal that is still running. Two stacks in the SAME
 * folder will therefore both show that folder's ports; the row says "in this
 * folder", not "definitely yours".
 *
 * The module deliberately has no imports, which is what
 * lets `scripts/stackStore.test.mjs` compile and exercise it in plain node.
 */

// ── Stored shapes ─────────────────────────────────────────────────────────────

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
 * EVERY field after `cwd` was added after this shape first shipped, and every
 * one of them is optional for the same reason: a configuration that does not
 * use one is written exactly as it was before that field existed, so a saved
 * value never grows a key nothing reads, and anything saved by an older build
 * loads unchanged. See `parseStackDefinitions`.
 */
export interface StackDefinition {
  /** Minted here; the key everything else points at. */
  id: string;
  /** What the user calls it — "Web", "API", "Database". */
  name: string;
  /** The command line, exactly as it would be typed into a terminal. */
  script: string;
  /** The project folder the command runs in. */
  cwd: string;
  /** Environment variables to set for the run; absent when there are none. */
  env?: StackEnvVar[];
  /** The key combination that runs this action, as captured, e.g. "Cmd+Shift+R". Absent when none is set. */
  keybinding?: string;
  /** A page to open when this action runs. Absent when the action serves no page. */
  previewUrl?: string;
  /** Run this action automatically whenever a worktree is created. */
  runOnWorktreeCreation?: boolean;
  /** Open `previewUrl` in the Browser panel when this action runs. */
  openPreviewOnRun?: boolean;
}

/** The terminal session a stack was last started in, and how it ended. */
export interface StackRunRecord {
  /** The shell's own session id — the key this record is stored under. */
  ownedId: string;
  /** Which saved stack this session was started for. */
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

/** One listening process, in the shape this store needs it. */
export interface StackProcess {
  pid: number;
  port: number;
  command: string;
  cwd: string;
}

/** Where a stack stands right now. */
export type StackState = 'stopped' | 'starting' | 'running' | 'failed';

/** What `deriveStackState` works out about one stack. */
export interface StackStatus {
  state: StackState;
  /** Ports found in this stack's folder while it is running; empty otherwise. */
  ports: number[];
  /** The processes those ports belong to. */
  processes: StackProcess[];
  exitCode: number | null;
  signal: string | null;
}

/** A stack as the panel draws it: the saved command plus where it stands. */
export interface StackRow extends StackStatus {
  definition: StackDefinition;
  /** The terminal session it is running in, or `null` when it has none. */
  ownedId: string | null;
  /** The state as a sentence: "running on port 5173". */
  statusLabel: string;
}

// ── Storage ───────────────────────────────────────────────────────────────────

/** Where the saved stacks live. */
export const STACK_DEFINITIONS_SETTING_KEY = 'run-configurations.definitions';

/** Where "this session is that stack" lives, so the tag survives a reload. */
export const STACK_RUNS_SETTING_KEY = 'run-configurations.runs';

/** What the user is told when a saved configuration could not be written down. */
export const STORAGE_WRITE_FAILED_MESSAGE =
  'This run configuration could not be saved to the Assembly database.';

function trimmedString(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function finiteNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

/** Anything that is not a plain, readable array comes back as one that is empty. */
function parseArray(raw: string | null | undefined): unknown[] {
  if (typeof raw !== 'string' || raw.trim() === '') return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

/**
 * Read a saved `env` list back. Anything that is not a list of `{key, value}`
 * pairs with a usable name comes back empty, and a list with nothing usable in
 * it comes back as `[]` — which the caller then leaves off the record entirely.
 */
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

/**
 * Read saved run configurations back. Tolerant on purpose: an unreadable value,
 * a value that is not a list, a row that is not an object, a row missing any of
 * its four required fields, and a second row re-using an id are all simply
 * dropped — a bad saved value must never be able to stop the panel opening.
 *
 * MIGRATION. Environment variables, the shortcut, the preview page and the two
 * toggles all arrived after this shape shipped, so a configuration saved by an
 * older build has none of those keys. That is not an error and needs no
 * conversion step: every one of them is optional, a missing or unusable one
 * reads as "not set", and the record is left WITHOUT the key rather than being
 * given an empty value. An old saved value therefore reads back byte-identical
 * to what was written, and a user can move between builds in either direction.
 *
 * A field that IS present but the wrong type — a number where the shortcut
 * belongs, the word "yes" where a toggle belongs — is dropped on its own. The
 * configuration itself survives, because losing a saved command over a bad
 * toggle would be a far worse trade than losing the toggle.
 */
export function parseStackDefinitions(raw: string | null | undefined): StackDefinition[] {
  const seen = new Set<string>();
  const definitions: StackDefinition[] = [];
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

export function serializeStackDefinitions(definitions: StackDefinition[]): string {
  return JSON.stringify(
    definitions.map((definition) => {
      const env = definition.env ?? [];
      const row: Record<string, unknown> = {
        id: definition.id,
        name: definition.name,
        script: definition.script,
        cwd: definition.cwd
      };
      // Each of these is only written when there is something to write — see
      // the migration note on `parseStackDefinitions`.
      if (env.length > 0) row.env = env.map((entry) => ({ key: entry.key, value: entry.value }));
      const keybinding = (definition.keybinding ?? '').trim();
      if (keybinding) row.keybinding = keybinding;
      const previewUrl = (definition.previewUrl ?? '').trim();
      if (previewUrl) row.previewUrl = previewUrl;
      if (definition.runOnWorktreeCreation === true) row.runOnWorktreeCreation = true;
      if (definition.openPreviewOnRun === true) row.openPreviewOnRun = true;
      return row;
    })
  );
}

// ── Environment variables (pure) ──────────────────────────────────────────────

/** The names a shell will accept: a letter or underscore, then word characters. */
const ENV_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

function isEnvName(value: string): boolean {
  return ENV_NAME.test(value);
}

/** Drop one matching pair of surrounding quotes, if the value has them. */
function unquote(value: string): string {
  if (value.length < 2) return value;
  const first = value[0];
  const last = value[value.length - 1];
  if ((first === '"' || first === "'") && first === last) return value.slice(1, -1);
  return value;
}

/**
 * Read the environment box: one `KEY=value` per line.
 *
 * Blank lines and lines starting with `#` are skipped, a leading `export ` is
 * allowed and ignored, and a value wrapped in quotes has them removed (the
 * quoting is put back when the command line is built, so typing them is neither
 * required nor harmful). Anything else comes back in `unreadable` so the editor
 * can say which lines it did not understand instead of dropping them silently.
 * The last line wins when a name is given twice.
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

/** The saved variables as the editor's box shows them, one per line. */
export function formatEnvLines(env: StackEnvVar[] | null | undefined): string {
  return (env ?? []).map((entry) => `${entry.key}=${entry.value}`).join('\n');
}

/** Wrap a value in single quotes so a shell takes it literally, spaces and all. */
function shellQuote(value: string): string {
  return `'${value.split("'").join("'\\''")}'`;
}

/**
 * The command line a run configuration actually runs: its script with its
 * environment variables in front of it.
 *
 * Put in FRONT rather than handed to the backend because the desktop app's
 * terminal spawn takes a command and a folder and nothing else. `KEY=value cmd`
 * is what a person would type, it works in every shell the app opens, and it is
 * visible in the terminal afterwards — so what ran is on screen rather than
 * hidden in a settings file.
 */
export function commandWithEnv(script: string, env: StackEnvVar[] | null | undefined): string {
  const command = script.trim();
  const prefix = (env ?? [])
    .filter((entry) => isEnvName(entry.key))
    .map((entry) => `${entry.key}=${shellQuote(entry.value)}`)
    .join(' ');
  return prefix ? `${prefix} ${command}` : command;
}

/**
 * Read the saved "which session is which stack" list back. Same tolerance as
 * above; a row keeping only its two ids is repaired rather than dropped, since
 * the ids are the part that matters.
 */
export function parseStackRuns(raw: string | null | undefined): StackRunRecord[] {
  const seen = new Set<string>();
  const runs: StackRunRecord[] = [];
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
      exitCode: typeof row.exitCode === 'number' && Number.isFinite(row.exitCode) ? row.exitCode : null,
      signal: trimmedString(row.signal) || null
    });
  }
  return runs;
}

export function serializeStackRuns(runs: StackRunRecord[]): string {
  return JSON.stringify(
    runs.map((run) => ({
      ownedId: run.ownedId,
      stackId: run.stackId,
      startedAt: run.startedAt,
      exited: run.exited,
      exitCode: run.exitCode,
      signal: run.signal
    }))
  );
}

// ── Derivation (pure) ─────────────────────────────────────────────────────────

/**
 * The listening processes that belong to a stack's folder — the folder itself
 * or anything underneath it. A folder whose name merely STARTS the same way
 * (`/a/ap` against `/a/app`) is a different folder, hence the separator check.
 */
export function stackProcessesFor(
  definition: { cwd: string },
  processes: StackProcess[]
): StackProcess[] {
  const root = definition.cwd.replace(/\/+$/, '');
  if (!root) return [];
  return processes.filter((process) => {
    const cwd = (process.cwd ?? '').replace(/\/+$/, '');
    return cwd === root || cwd.startsWith(`${root}/`);
  });
}

/**
 * Where one stack stands, from its last run record and the processes listening
 * right now. The four answers:
 *
 *  - **stopped** — it has never been started here, or its terminal ended and
 *    said nothing was wrong;
 *  - **failed** — its terminal ended with a non-zero code, or a signal killed it;
 *  - **running** — its terminal is still going AND something in its folder is
 *    listening on a port;
 *  - **starting** — its terminal is still going but no port has appeared yet.
 *
 * "Starting" is honest rather than optimistic: a stack that never opens a port
 * (a test watcher, a build) stays there for as long as it runs, and the sentence
 * the panel shows says exactly that.
 */
export function deriveStackState(
  definition: { cwd: string },
  run: StackRunRecord | null | undefined,
  processes: StackProcess[]
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
  const matched = stackProcessesFor(definition, processes);
  const ports = [...new Set(matched.map((process) => process.port).filter((port) => port > 0))].sort(
    (left, right) => left - right
  );
  return {
    state: ports.length > 0 ? 'running' : 'starting',
    ports,
    processes: matched,
    exitCode: null,
    signal: null
  };
}

/** English list: `a`, `a and b`, `a, b and c`. */
function joinWords(words: string[]): string {
  if (words.length === 0) return '';
  if (words.length === 1) return words[0];
  return `${words.slice(0, -1).join(', ')} and ${words[words.length - 1]}`;
}

/** One sentence for a stack's state, in words that need no explaining. */
export function describeStackState(status: {
  state: StackState;
  ports: number[];
  exitCode: number | null;
  signal: string | null;
}): string {
  if (status.state === 'running') {
    const ports = status.ports.map((port) => String(port));
    return `running on ${ports.length === 1 ? 'port' : 'ports'} ${joinWords(ports)}`;
  }
  if (status.state === 'starting') return 'started, no port yet';
  if (status.state === 'failed') {
    if (status.signal) return `stopped by ${status.signal}`;
    return `stopped with error code ${status.exitCode ?? 'unknown'}`;
  }
  return 'not running';
}

/** Every saved stack, with where it stands, in the order they were added. */
export function buildStackRows(
  definitions: StackDefinition[],
  runsByOwnedId: Record<string, StackRunRecord>,
  processes: StackProcess[]
): StackRow[] {
  const runByStack = new Map<string, StackRunRecord>();
  for (const run of Object.values(runsByOwnedId)) runByStack.set(run.stackId, run);
  return definitions.map((definition) => {
    const run = runByStack.get(definition.id) ?? null;
    const status = deriveStackState(definition, run, processes);
    return {
      ...status,
      definition,
      ownedId: run ? run.ownedId : null,
      statusLabel: describeStackState(status)
    };
  });
}

// ── Reactive state ────────────────────────────────────────────────────────────

/**
 * The single reactive stack-runner state. Read fields directly in the component
 * (`stacks.definitions`, `stacks.processes`); mutate only through the functions
 * below, so saving stays in lockstep.
 */
export const stacks = $state<{
  /** Saved stacks have been read out of storage at least once. */
  hydrated: boolean;
  /** The panel has been shown at least once and its first read has started. */
  activated: boolean;
  /** The project folder the panel is showing stacks for; `null` for all of them. */
  activeRoot: string | null;
  /** Human name of that folder, for the empty-state sentences. */
  projectName: string;
  definitions: StackDefinition[];
  /** Run records by `ownedId` — the session-to-stack tag. */
  runs: Record<string, StackRunRecord>;
  /** Processes listening on a port, as last read. */
  processes: StackProcess[];
  /** A read of the listening processes is in flight. */
  loading: boolean;
  /** Why the last read failed, in plain English; `null` when it did not. */
  error: string | null;
  /** Why there is nothing to read through nobody's fault (not the desktop app). */
  unavailableReason: string | null;
  /** Monotonic ticket of the most recent read. */
  requestId: number;
  /** `Date.now()` of the last successful read, or `null`. */
  loadedAt: number | null;
  /** A one-off message for the user (a refused add, a failed save). */
  notice: string | null;
}>({
  hydrated: false,
  activated: false,
  activeRoot: null,
  projectName: '',
  definitions: [],
  runs: {},
  processes: [],
  loading: false,
  error: null,
  unavailableReason: null,
  requestId: 0,
  loadedAt: null,
  notice: null
});

// ── Saving ────────────────────────────────────────────────────────────────────

type StackPersistence = {
  read: (key: string) => Promise<unknown>;
  write: (key: string, value: unknown) => Promise<void>;
};

let persistence: StackPersistence | null = null;
let hydrationInFlight: Promise<void> | null = null;
let hydrationToken: object | null = null;
let hydrationGeneration = 0;

/** Give this backend-free store the SQLite setting functions owned by the service. */
export function configureStackPersistence(next: StackPersistence): void {
  persistence = next;
}

function persistedJson(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  return JSON.stringify(value) ?? null;
}

/** Save the saved-stacks list. */
async function persistDefinitions(): Promise<boolean> {
  try {
    if (!persistence) throw new Error('Run configuration persistence is not configured.');
    await persistence.write(STACK_DEFINITIONS_SETTING_KEY, stacks.definitions);
    return true;
  } catch {
    stacks.notice = STORAGE_WRITE_FAILED_MESSAGE;
    return false;
  }
}

/** Save the session-to-stack tags. */
async function persistRuns(): Promise<boolean> {
  try {
    if (!persistence) throw new Error('Run configuration persistence is not configured.');
    await persistence.write(STACK_RUNS_SETTING_KEY, Object.values(stacks.runs));
    return true;
  } catch {
    stacks.notice = STORAGE_WRITE_FAILED_MESSAGE;
    return false;
  }
}

/**
 * Read the saved stacks and session tags back. Idempotent — the service calls it
 * every time the panel is opened, and only the first call does any work.
 *
 * A tag pointing at a stack that no longer exists is dropped as it is read: the
 * stack it named is gone, so the session it named is just a terminal now.
 */
export async function hydrateStacks(): Promise<void> {
  if (stacks.hydrated) return;
  if (hydrationInFlight) {
    await hydrationInFlight;
    return;
  }
  const generation = hydrationGeneration;
  const token = {};
  hydrationToken = token;
  const hydration = hydrateStacksOnce(generation, token);
  hydrationInFlight = hydration;
  await hydration;
}

async function hydrateStacksOnce(generation: number, token: object): Promise<void> {
  try {
    if (!persistence) throw new Error('Run configuration persistence is not configured.');
    const [savedDefinitions, savedRuns] = await Promise.all([
      persistence.read(STACK_DEFINITIONS_SETTING_KEY),
      persistence.read(STACK_RUNS_SETTING_KEY)
    ]);
    const definitions = parseStackDefinitions(persistedJson(savedDefinitions));
    const known = new Set(definitions.map((definition) => definition.id));
    const runs: Record<string, StackRunRecord> = {};
    for (const run of parseStackRuns(persistedJson(savedRuns))) {
      if (!known.has(run.stackId)) continue;
      runs[run.ownedId] = run;
    }
    if (generation !== hydrationGeneration) return;
    stacks.definitions = definitions;
    stacks.runs = runs;
    stacks.hydrated = true;
  } finally {
    if (hydrationToken === token) {
      hydrationInFlight = null;
      hydrationToken = null;
    }
  }
}

// ── Mutations ─────────────────────────────────────────────────────────────────

/** Record that the panel has been shown and its first read has begun. */
export function markStacksActivated(): void {
  stacks.activated = true;
}

/** Store which project the panel is showing. Pure bookkeeping — reads nothing. */
export function setStackInput(input: { activeRoot: string | null; projectName?: string }): void {
  stacks.activeRoot = input.activeRoot;
  stacks.projectName = (input.projectName ?? '').trim() || folderName(input.activeRoot);
}

/** Last folder name of a path — `/a/b/my-app` → `my-app`. */
export function folderName(path: string | null | undefined): string {
  if (!path) return '';
  const parts = path.split('/').filter(Boolean);
  return parts[parts.length - 1] ?? '';
}

/** Take a ticket for a read of the listening processes. */
export function beginStackLoad(): number {
  stacks.requestId += 1;
  stacks.loading = true;
  stacks.error = null;
  stacks.unavailableReason = null;
  return stacks.requestId;
}

/** Land a successful read. Returns `false` when a newer read superseded it. */
export function applyStackProcesses(requestId: number, processes: StackProcess[]): boolean {
  if (stacks.requestId !== requestId) return false;
  stacks.processes = processes;
  stacks.loading = false;
  stacks.error = null;
  stacks.unavailableReason = null;
  stacks.loadedAt = Date.now();
  return true;
}

/** Land a failed read. Returns `false` when a newer read superseded it. */
export function failStackLoad(requestId: number, message: string): boolean {
  if (stacks.requestId !== requestId) return false;
  stacks.loading = false;
  stacks.error = message;
  return true;
}

/** Land a read that could not run at all — desktop-app-only data, say. */
export function markStacksUnavailable(requestId: number, reason: string): boolean {
  if (stacks.requestId !== requestId) return false;
  stacks.processes = [];
  stacks.loading = false;
  stacks.error = null;
  stacks.unavailableReason = reason;
  return true;
}

function mintStackId(): string {
  const random = globalThis.crypto?.randomUUID?.();
  return random ? `stack-${random}` : `stack-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

/** What the editor hands over for a new or changed run configuration. */
export interface StackDraft {
  name: string;
  script: string;
  cwd: string;
  env?: StackEnvVar[];
  /**
   * The key combination that starts this action, already written the way
   * `keybindingCapture.ts` writes one. The capture field is the only thing that
   * produces it, so this module stores the string and never parses it.
   */
  keybinding?: string;
  previewUrl?: string;
  runOnWorktreeCreation?: boolean;
  openPreviewOnRun?: boolean;
}

/**
 * The first thing wrong with a draft, said the way it would be said to the
 * person who typed it — or `null` when there is nothing wrong. Pure, so the
 * editor can grey out its Save button without saving anything first.
 */
export function describeStackProblem(draft: StackDraft): string | null {
  if (!draft.name.trim()) return 'Give this run configuration a name first.';
  if (!draft.script.trim()) return 'Type the command it should run.';
  if (!draft.cwd.trim()) return 'Say which folder it should run in.';
  return null;
}

/** Strip a draft down to what gets saved: trimmed, with usable variables only. */
function cleanDraft(draft: StackDraft): {
  name: string;
  script: string;
  cwd: string;
  env: StackEnvVar[];
  keybinding: string;
  previewUrl: string;
  runOnWorktreeCreation: boolean;
  openPreviewOnRun: boolean;
} {
  const cwd = draft.cwd.trim();
  // A trailing slash would make the same folder look like two different ones to
  // the folder comparisons below. `/` itself keeps its slash — it is the path.
  const trimmedCwd = cwd.replace(/(.)\/+$/, '$1');
  return {
    name: draft.name.trim(),
    script: draft.script.trim(),
    cwd: trimmedCwd,
    env: (draft.env ?? [])
      .filter((entry) => isEnvName(entry.key.trim()))
      .map((entry) => ({ key: entry.key.trim(), value: entry.value })),
    keybinding: (draft.keybinding ?? '').trim(),
    previewUrl: (draft.previewUrl ?? '').trim(),
    runOnWorktreeCreation: draft.runOnWorktreeCreation === true,
    openPreviewOnRun: draft.openPreviewOnRun === true
  };
}

/** Copy the optional fields of a cleaned draft onto a record, omitting the empty ones. */
function applyOptionalFields(
  definition: StackDefinition,
  clean: ReturnType<typeof cleanDraft>
): void {
  if (clean.env.length > 0) definition.env = clean.env;
  if (clean.keybinding) definition.keybinding = clean.keybinding;
  if (clean.previewUrl) definition.previewUrl = clean.previewUrl;
  if (clean.runOnWorktreeCreation) definition.runOnWorktreeCreation = true;
  if (clean.openPreviewOnRun) definition.openPreviewOnRun = true;
}

/**
 * Save a new run configuration. Returns the saved record, or `null` when a
 * field was blank — in which case `stacks.notice` says which one, because a
 * button that silently does nothing is worse than one that explains itself.
 */
export async function addStack(draft: StackDraft): Promise<StackDefinition | null> {
  const problem = describeStackProblem(draft);
  if (problem) {
    stacks.notice = problem;
    return null;
  }
  const clean = cleanDraft(draft);
  const definition: StackDefinition = {
    id: mintStackId(),
    name: clean.name,
    script: clean.script,
    cwd: clean.cwd
  };
  applyOptionalFields(definition, clean);
  // What is on disk comes first. Adding to a list that was never read wrote
  // one action over every action ever saved, whenever the Run tab was opened
  // before a session had been picked.
  await hydrateStacks();
  stacks.definitions = [...stacks.definitions, definition];
  stacks.notice = null;
  if (!(await persistDefinitions())) {
    stacks.definitions = stacks.definitions.filter((existing) => existing.id !== definition.id);
    return null;
  }
  return definition;
}

/**
 * Change a saved run configuration in place. It keeps its id, so the terminal
 * it is running in stays tagged as its — editing the command does not orphan a
 * run that is already going, it only changes what the NEXT run does.
 *
 * Returns the saved record, or `null` when the id is unknown or a field was
 * blank; `stacks.notice` says which, same as adding one.
 */
export async function updateStack(
  stackId: string,
  draft: StackDraft
): Promise<StackDefinition | null> {
  await hydrateStacks();
  const existing = stacks.definitions.find((definition) => definition.id === stackId);
  if (!existing) {
    stacks.notice = 'That run configuration is no longer saved.';
    return null;
  }
  const problem = describeStackProblem(draft);
  if (problem) {
    stacks.notice = problem;
    return null;
  }
  const clean = cleanDraft(draft);
  const updated: StackDefinition = {
    id: existing.id,
    name: clean.name,
    script: clean.script,
    cwd: clean.cwd
  };
  applyOptionalFields(updated, clean);
  const before = stacks.definitions;
  stacks.definitions = stacks.definitions.map((definition) =>
    definition.id === stackId ? updated : definition
  );
  stacks.notice = null;
  if (!(await persistDefinitions())) {
    stacks.definitions = before;
    return null;
  }
  return updated;
}

/**
 * Forget a saved run configuration, and the tag saying which session belonged
 * to it. The SESSION survives untouched — this only removes CommandBar's saved
 * command.
 */
export async function removeStack(stackId: string): Promise<void> {
  await hydrateStacks();
  stacks.definitions = stacks.definitions.filter((definition) => definition.id !== stackId);
  const runs: Record<string, StackRunRecord> = {};
  for (const run of Object.values(stacks.runs)) {
    if (run.stackId === stackId) continue;
    runs[run.ownedId] = run;
  }
  stacks.runs = runs;
  await persistDefinitions();
  await persistRuns();
}

/**
 * Tag a terminal session as this stack's. A stack points at ONE session at a
 * time, so starting it again drops the tag on the previous session — that
 * terminal may still be on screen, it just is not this stack's any more.
 */
export async function recordStackStart(stackId: string, ownedId: string): Promise<void> {
  await hydrateStacks();
  const runs: Record<string, StackRunRecord> = {};
  for (const run of Object.values(stacks.runs)) {
    if (run.stackId === stackId) continue;
    runs[run.ownedId] = run;
  }
  runs[ownedId] = {
    ownedId,
    stackId,
    startedAt: Date.now(),
    exited: false,
    exitCode: null,
    signal: null
  };
  stacks.runs = runs;
  await persistRuns();
}

/**
 * Write down that a terminal ended. Returns `true` when that terminal was a
 * stack's — which is how the service knows whether the exit is worth a refresh —
 * and `false` for every other session in the shell.
 */
export async function recordStackExit(
  ownedId: string,
  outcome: { exitCode: number | null; signal: string | null }
): Promise<boolean> {
  await hydrateStacks();
  const run = stacks.runs[ownedId];
  if (!run) return false;
  stacks.runs = {
    ...stacks.runs,
    [ownedId]: { ...run, exited: true, exitCode: outcome.exitCode, signal: outcome.signal }
  };
  await persistRuns();
  return true;
}

/** Drop a session's tag — used when the session itself is removed. */
export async function forgetStackRun(ownedId: string): Promise<void> {
  await hydrateStacks();
  if (!stacks.runs[ownedId]) return;
  const runs = { ...stacks.runs };
  delete runs[ownedId];
  stacks.runs = runs;
  await persistRuns();
}

/** Which stack a session belongs to, or `null` when it is not a stack's. */
export function stackIdForOwnedId(ownedId: string): string | null {
  return stacks.runs[ownedId]?.stackId ?? null;
}

/** Is this session a stack's? The badge in the session rail asks this. */
export function isStackSession(ownedId: string): boolean {
  return stackIdForOwnedId(ownedId) !== null;
}

/** The session a stack is running in, or `null` when it has none. */
export function ownedIdForStack(stackId: string): string | null {
  for (const run of Object.values(stacks.runs)) {
    if (run.stackId === stackId) return run.ownedId;
  }
  return null;
}

/** The name of the stack a session belongs to, for the rail's badge tooltip. */
export function stackNameForOwnedId(ownedId: string): string | null {
  const stackId = stackIdForOwnedId(ownedId);
  if (!stackId) return null;
  return stacks.definitions.find((definition) => definition.id === stackId)?.name ?? null;
}

/** Clear the one-off message. */
export function clearStackNotice(): void {
  stacks.notice = null;
}

/**
 * Is this folder the project's own folder, or one underneath it?
 *
 * The same test `stackProcessesFor` uses, for the same reason: a configuration
 * whose folder was edited to `…/app/server` still belongs to the project at
 * `…/app`, and a folder whose name merely starts the same way does not.
 */
export function isInsideRoot(cwd: string, root: string): boolean {
  const folder = (cwd ?? '').replace(/\/+$/, '');
  const base = (root ?? '').replace(/\/+$/, '');
  if (!base) return true;
  return folder === base || folder.startsWith(`${base}/`);
}

/** The run configurations the panel should draw: this project's, or all of them. */
export function visibleStackRows(): StackRow[] {
  const root = (stacks.activeRoot ?? '').trim();
  const definitions = root
    ? stacks.definitions.filter((definition) => isInsideRoot(definition.cwd, root))
    : stacks.definitions;
  return buildStackRows(definitions, stacks.runs, stacks.processes);
}

/**
 * EVERY saved run configuration, whichever project it belongs to.
 *
 * The run button in the top bar lists all of them: it is one control for the
 * whole app, and hiding a configuration because the tool column happens to be
 * pointed elsewhere would look like the configuration had been lost.
 */
export function allStackRows(): StackRow[] {
  return buildStackRows(stacks.definitions, stacks.runs, stacks.processes);
}

/**
 * The run record for one configuration, or `null` when it has never been
 * started here. The run button reads `startedAt` off it to say when.
 */
export function runRecordForStack(stackId: string): StackRunRecord | null {
  for (const run of Object.values(stacks.runs)) {
    if (run.stackId === stackId) return run;
  }
  return null;
}

/** Drop everything back to launch state. Used by tests and by a full reset. */
export function resetStacks(): void {
  hydrationGeneration += 1;
  hydrationToken = null;
  hydrationInFlight = null;
  stacks.hydrated = false;
  stacks.activated = false;
  stacks.activeRoot = null;
  stacks.projectName = '';
  stacks.definitions = [];
  stacks.runs = {};
  stacks.processes = [];
  stacks.loading = false;
  stacks.error = null;
  stacks.unavailableReason = null;
  stacks.requestId = 0;
  stacks.loadedAt = null;
  stacks.notice = null;
}
