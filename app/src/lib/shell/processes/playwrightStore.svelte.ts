/**
 * playwrightStore.svelte.ts — state for the /next "Playwright processes" card.
 *
 * WHAT THIS CARD IS FOR
 * Playwright leaves processes behind. When a test run or an agent's browser
 * session is interrupted, its Chrome/Chromium windows and its background
 * helpers keep running, and in Activity Monitor they look exactly like the
 * user's own Chrome — same name, same icon. This card lists ONLY the processes
 * the desktop app has positively identified as Playwright's (a Playwright
 * profile folder, or a Playwright script path), grouped the way the operating
 * system groups them, so stopping them is two clicks and cannot touch the
 * user's real browser.
 *
 * Two rules this module exists to enforce (same shape as `contextStore`):
 *
 * 1. **No backend, ever.** Nothing here calls Tauri, fetches, or touches
 *    browser storage. Every backend call lives in `playwrightService.ts` and lands
 *    here as a plain mutation.
 * 2. **No `$effect`.** `$effect` is illegal in a `.svelte.ts` module. Loading is
 *    driven imperatively by the service.
 *
 * Loads carry a monotonic ticket, so a slow first read can never overwrite a
 * fast second one. The import is type-only on purpose: the module stays runnable
 * on its own, which is what `scripts/playwrightStore.test.ts` compiles.
 */
import type {
  PlaywrightCleanupResult,
  PlaywrightProcessInfo,
  PlaywrightSessionInfo
} from '../../tauriSource.ts';

/**
 * The four things the desktop app can recognise, plus a catch-all.
 *
 *  - `browser` — a Chrome or Chromium started by Playwright, using one of its
 *    throwaway profile folders. This is the one that looks like the user's own
 *    browser and is never it.
 *  - `daemon`  — the long-lived helper `playwright test` keeps around.
 *  - `server`  — `playwright run-cli-server`, the command-line server.
 *  - `mcp`     — the Playwright MCP server an agent drives a browser through.
 */
export type PlaywrightKind = 'browser' | 'daemon' | 'server' | 'mcp' | 'other';

/** One process inside a group, ready to render. */
export interface PlaywrightProcessRow {
  pid: number;
  /** The short program name, e.g. `Google Chrome`, `playwright-mcp`. */
  name: string;
  kind: PlaywrightKind;
  /** Plain-English name for `kind`. */
  kindLabel: string;
  /** Seconds since it started, or `null` when the system did not say. */
  ageSeconds: number | null;
  /** `ageSeconds` as a phrase: `2 hours`, `unknown age`. */
  ageLabel: string;
  /** The full command line, for the title attribute. */
  commandLine: string;
}

/** One session: everything the operating system runs in a single process group. */
export interface PlaywrightGroup {
  /** Process group id — the handle the stop command takes. */
  pgid: number;
  kind: PlaywrightKind;
  /** Plain-English name for `kind`, e.g. "Playwright's own browser". */
  kindLabel: string;
  /** One sentence saying what this kind of session actually is. */
  explanation: string;
  /** Every process id in the group, lowest first. */
  pids: number[];
  processCount: number;
  /** Age of the OLDEST process in the group, in seconds; `null` when unknown. */
  ageSeconds: number | null;
  ageLabel: string;
  processes: PlaywrightProcessRow[];
}

// ── Reading `ps` output ───────────────────────────────────────────────────────

/**
 * Seconds from the elapsed time `ps` prints: `SS`, `MM:SS`, `HH:MM:SS` or
 * `DD-HH:MM:SS`. Anything else returns `null` — an unreadable age is shown as
 * unknown, never guessed.
 */
export function parseProcessElapsed(elapsed: string | null | undefined): number | null {
  const text = (elapsed ?? '').trim();
  if (!text) return null;

  const [days, clock] = text.includes('-') ? text.split('-', 2) : ['0', text];
  const dayCount = Number(days);
  if (!Number.isFinite(dayCount) || dayCount < 0) return null;

  const parts = clock.split(':');
  if (parts.length > 3) return null;
  let seconds = 0;
  for (const part of parts) {
    if (!/^\d+$/.test(part)) return null;
    seconds = seconds * 60 + Number(part);
  }
  return dayCount * 86400 + seconds;
}

/** `90` → `1 minute`; `null` → `unknown age`. Always the right singular. */
export function describeAge(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) {
    return 'unknown age';
  }
  if (seconds < 60) return 'less than a minute';
  if (seconds < 3600) return plural(Math.floor(seconds / 60), 'minute');
  if (seconds < 86400) return plural(Math.floor(seconds / 3600), 'hour');
  return plural(Math.floor(seconds / 86400), 'day');
}

function plural(value: number, singular: string, many = `${singular}s`): string {
  return `${value} ${value === 1 ? singular : many}`;
}

// ── What each process is ─────────────────────────────────────────────────────

/**
 * Which kind a process is, read from the label the desktop app already worked
 * out (`main.rs playwright_process_label`). Anything unrecognised is `other`;
 * this never guesses from a command line the backend did not vouch for.
 */
export function playwrightKindOf(process: { label?: string | null }): PlaywrightKind {
  const label = (process?.label ?? '').toLowerCase();
  if (label.includes('browser')) return 'browser';
  if (label.includes('daemon')) return 'daemon';
  if (label.includes('mcp')) return 'mcp';
  if (label.includes('server')) return 'server';
  return 'other';
}

/** The name a person reads on the row. */
export function describePlaywrightKind(kind: PlaywrightKind): string {
  if (kind === 'browser') return "Playwright's own browser";
  if (kind === 'daemon') return "Playwright's test-runner daemon";
  if (kind === 'server') return "Playwright's command-line server";
  if (kind === 'mcp') return "Playwright's browser-control server for agents";
  return 'A Playwright helper process';
}

/** One sentence saying what the thing is, shown under the group's name. */
export function explainPlaywrightKind(kind: PlaywrightKind): string {
  if (kind === 'browser') {
    return 'A Chrome or Chromium window Playwright started with its own throwaway profile. It is not the Chrome you browse in.';
  }
  if (kind === 'daemon') {
    return 'The background helper the Playwright test runner keeps alive between runs.';
  }
  if (kind === 'server') {
    return 'The Playwright command-line server a test run or a script connected to.';
  }
  if (kind === 'mcp') {
    return 'The server an agent uses to drive a browser through Playwright.';
  }
  return 'A process the desktop app recognised as belonging to Playwright.';
}

/**
 * The kind that best names a whole group: the thing that STARTED the session
 * beats the browsers it opened, so a test run reads as a test run and not as
 * three browser windows. Mirrors how the desktop app picks a group label.
 */
function groupKind(kinds: PlaywrightKind[]): PlaywrightKind {
  for (const candidate of ['daemon', 'server', 'mcp', 'other'] as PlaywrightKind[]) {
    if (kinds.includes(candidate)) return candidate;
  }
  return kinds.includes('browser') ? 'browser' : 'other';
}

// ── Grouping ─────────────────────────────────────────────────────────────────

function toProcessRow(process: PlaywrightProcessInfo): PlaywrightProcessRow {
  const kind = playwrightKindOf(process);
  const ageSeconds = parseProcessElapsed(process.elapsed);
  return {
    pid: process.pid,
    name: process.name || 'process',
    kind,
    kindLabel: describePlaywrightKind(kind),
    ageSeconds,
    ageLabel: describeAge(ageSeconds),
    commandLine: process.args || process.command || ''
  };
}

function makeGroup(
  pgid: number,
  processes: PlaywrightProcessRow[],
  fallbackKind: PlaywrightKind,
  fallbackPids: number[]
): PlaywrightGroup {
  const pids = (processes.length > 0 ? processes.map((row) => row.pid) : [...fallbackPids]).sort(
    (left, right) => left - right
  );
  const kind = processes.length > 0 ? groupKind(processes.map((row) => row.kind)) : fallbackKind;
  const ages = processes
    .map((row) => row.ageSeconds)
    .filter((value): value is number => value !== null);
  const ageSeconds = ages.length > 0 ? Math.max(...ages) : null;

  return {
    pgid,
    kind,
    kindLabel: describePlaywrightKind(kind),
    explanation: explainPlaywrightKind(kind),
    pids,
    processCount: pids.length,
    ageSeconds,
    ageLabel: describeAge(ageSeconds),
    processes: [...processes].sort((left, right) => left.pid - right.pid)
  };
}

/**
 * Group raw `ps` rows by process group id — one group per session, lowest group
 * id first. Used directly by the tests and as the engine under
 * `buildPlaywrightGroups`.
 */
export function groupPlaywrightProcesses(processes: PlaywrightProcessInfo[]): PlaywrightGroup[] {
  const byPgid = new Map<number, PlaywrightProcessRow[]>();
  for (const process of processes ?? []) {
    const rows = byPgid.get(process.pgid) ?? [];
    rows.push(toProcessRow(process));
    byPgid.set(process.pgid, rows);
  }

  return [...byPgid.entries()]
    .sort(([left], [right]) => left - right)
    .map(([pgid, rows]) => makeGroup(pgid, rows, 'other', []));
}

/**
 * Turn what `list_playwright_sessions` returned into the card's groups. The
 * desktop app has already grouped by process group id; this re-derives the kind
 * and the age from the processes so the wording is ours. A session that came
 * back without per-process detail still gets its pids and its kind from the
 * session's own label.
 */
export function buildPlaywrightGroups(sessions: PlaywrightSessionInfo[]): PlaywrightGroup[] {
  return (sessions ?? [])
    .map((session) =>
      makeGroup(
        session.pgid,
        (session.processes ?? []).map(toProcessRow),
        playwrightKindOf(session),
        session.pids ?? []
      )
    )
    .sort((left, right) => left.pgid - right.pgid);
}

/** "2 sessions, 4 processes, oldest 2 hours" — empty string when there is none. */
export function summarizePlaywrightGroups(groups: PlaywrightGroup[]): string {
  if (!groups || groups.length === 0) return '';
  const processCount = groups.reduce((total, group) => total + group.processCount, 0);
  const ages = groups
    .map((group) => group.ageSeconds)
    .filter((value): value is number => value !== null);
  const parts = [plural(groups.length, 'session'), plural(processCount, 'process', 'processes')];
  if (ages.length > 0) parts.push(`oldest ${describeAge(Math.max(...ages))}`);
  return parts.join(', ');
}

/** What a stop actually did, as one sentence the user can act on. */
export function describeCleanupOutcome(result: PlaywrightCleanupResult): string {
  const stoppedGroups = result?.terminatedPgids?.length ?? 0;
  const stoppedProcesses = result?.terminatedPids?.length ?? 0;
  const failures = result?.failedPgids ?? [];

  if (stoppedGroups === 0 && failures.length === 0) {
    return 'Nothing was running, so nothing was stopped.';
  }

  const sentences: string[] = [];
  if (stoppedGroups > 0) {
    sentences.push(
      `Stopped ${plural(stoppedGroups, 'session')} (${plural(
        stoppedProcesses,
        'process',
        'processes'
      )}).`
    );
  }
  if (failures.length > 0) {
    const reasons = [...new Set(failures.map((failure) => failure.message).filter(Boolean))];
    sentences.push(
      `${plural(failures.length, 'process', 'processes')} could not be stopped: ${reasons.join(
        '; '
      )}`
    );
  }
  return sentences.join(' ');
}

// ── State ────────────────────────────────────────────────────────────────────

/**
 * Whether this desktop build can stop ONE session on its own.
 *  - `unknown` — not asked yet;
 *  - `yes`     — the command answered;
 *  - `no`      — this build predates it, so only "Stop all Playwright" works.
 */
export type PerSessionStopSupport = 'unknown' | 'yes' | 'no';

export const playwrightState = $state<{
  /** The card has been shown at least once and its first read has started. */
  activated: boolean;
  groups: PlaywrightGroup[];
  loading: boolean;
  error: string | null;
  unavailableReason: string | null;
  requestId: number;
  loadedAt: number | null;
  /** The group being stopped right now, or `null`. */
  stoppingPgid: number | null;
  /** A stop-everything is running. */
  stoppingAll: boolean;
  /** Any stop is running — buttons read this. */
  busy: boolean;
  /** What the last stop did, in plain words. Cleared when a new one starts. */
  lastResult: string | null;
  perSessionStopSupported: PerSessionStopSupport;
}>({
  activated: false,
  groups: [],
  loading: false,
  error: null,
  unavailableReason: null,
  requestId: 0,
  loadedAt: null,
  stoppingPgid: null,
  stoppingAll: false,
  busy: false,
  lastResult: null,
  perSessionStopSupported: 'unknown'
});

/** Record that the card has been shown and its first read has begun. */
export function markPlaywrightActivated(): void {
  playwrightState.activated = true;
}

/** Take a ticket for a new read. Hand it back to whichever finisher lands. */
export function beginPlaywrightLoad(): number {
  playwrightState.requestId += 1;
  playwrightState.loading = true;
  playwrightState.error = null;
  playwrightState.unavailableReason = null;
  return playwrightState.requestId;
}

/** `true` while `requestId` is still the newest ticket handed out. */
export function isCurrentPlaywrightRequest(requestId: number): boolean {
  return playwrightState.requestId === requestId;
}

/** Land a successful read. Returns `false` when a newer read superseded it. */
export function applyPlaywrightGroups(requestId: number, groups: PlaywrightGroup[]): boolean {
  if (playwrightState.requestId !== requestId) return false;
  playwrightState.groups = groups;
  playwrightState.loading = false;
  playwrightState.error = null;
  playwrightState.unavailableReason = null;
  playwrightState.loadedAt = Date.now();
  return true;
}

/** Land a failed read. Returns `false` when a newer read superseded it. */
export function failPlaywrightLoad(requestId: number, message: string): boolean {
  if (playwrightState.requestId !== requestId) return false;
  playwrightState.loading = false;
  playwrightState.error = message;
  return true;
}

/**
 * Land a read that could not run at all — the process list only exists inside
 * the desktop app. Returns `false` when a newer read superseded it.
 */
export function markPlaywrightUnavailable(requestId: number, reason: string): boolean {
  if (playwrightState.requestId !== requestId) return false;
  playwrightState.groups = [];
  playwrightState.loading = false;
  playwrightState.error = null;
  playwrightState.unavailableReason = reason;
  return true;
}

/** A single session is being stopped. */
export function beginStopSession(pgid: number): void {
  playwrightState.stoppingPgid = pgid;
  playwrightState.stoppingAll = false;
  playwrightState.busy = true;
  playwrightState.lastResult = null;
}

/** Every session is being stopped. */
export function beginStopAll(): void {
  playwrightState.stoppingPgid = null;
  playwrightState.stoppingAll = true;
  playwrightState.busy = true;
  playwrightState.lastResult = null;
}

/** A stop finished — `message` is what it did, or why it did not. */
export function finishStop(message: string | null): void {
  playwrightState.stoppingPgid = null;
  playwrightState.stoppingAll = false;
  playwrightState.busy = false;
  playwrightState.lastResult = message;
}

/** This desktop build cannot stop one session at a time. */
export function markPerSessionStopUnsupported(): void {
  playwrightState.perSessionStopSupported = 'no';
}

/** This desktop build can stop one session at a time. */
export function markPerSessionStopSupported(): void {
  playwrightState.perSessionStopSupported = 'yes';
}

/**
 * Drop everything back to launch state. Used by tests and by a full reset.
 *
 * `requestId` is the one field that does NOT go back to its launch value. It is
 * the stale-read guard, and a read issued before the reset is still in flight
 * after it: rewinding to 0 would hand the next read the same ticket number, so
 * the pre-reset read would compare equal and overwrite the newer rows. Bumping
 * it instead invalidates everything still in flight, which is what a reset
 * means.
 */
export function resetPlaywright(): void {
  playwrightState.activated = false;
  playwrightState.groups = [];
  playwrightState.loading = false;
  playwrightState.error = null;
  playwrightState.unavailableReason = null;
  playwrightState.requestId += 1;
  playwrightState.loadedAt = null;
  playwrightState.stoppingPgid = null;
  playwrightState.stoppingAll = false;
  playwrightState.busy = false;
  playwrightState.lastResult = null;
  playwrightState.perSessionStopSupported = 'unknown';
}
