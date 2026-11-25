/**
 * `.NET` backend bridge for the Run panel:
 *  - SQLite preferences persistence via `preferencesStore`
 *  - PTY command lifecycle via `terminal/start`, `terminal/close`, `terminal/list`, `terminal/read`
 *  - Live stdout/stderr + exit streaming via `/socket/terminal` (`terminal` topic)
 *
 * Zero polling timers, zero raw WebSockets, zero Tauri calls.
 */

import { preferencesStore } from '$lib/application/preferences.svelte';
import { backend, isBackendAbort } from '$lib/infrastructure/backend';

import {
  STACK_DEFINITIONS_SETTING_KEY,
  STACK_RUNS_SETTING_KEY,
  decodeBase64Utf8,
  normalizeDefinitionsForStorage,
  normalizeRunsForStorage
} from './model';
import { appendOutputTail } from './runOutputTail';
import type {
  OutputPiece,
  OutputRow,
  RunOutputWatch,
  StackDefinition,
  StackRunRecord,
  TerminalInfo
} from './types';

// ── Preferences persistence ──────────────────────────────────────────────────

export function readSavedDefinitionsValue(): unknown {
  return preferencesStore.get(STACK_DEFINITIONS_SETTING_KEY);
}

export function readSavedRunsValue(): unknown {
  return preferencesStore.get(STACK_RUNS_SETTING_KEY);
}

export async function writeSavedDefinitions(definitions: readonly StackDefinition[]): Promise<void> {
  await preferencesStore.set(
    STACK_DEFINITIONS_SETTING_KEY,
    normalizeDefinitionsForStorage(definitions)
  );
}

export async function writeSavedRuns(runs: readonly StackRunRecord[]): Promise<void> {
  await preferencesStore.set(STACK_RUNS_SETTING_KEY, normalizeRunsForStorage(runs));
}

// ── Terminal RPCs ────────────────────────────────────────────────────────────

/**
 * Start a command in `cwd` using the `.NET` `terminal/start` RPC.
 *
 * `owner` is intentionally omitted: `TerminalHost.StartAsync` validates any
 * non-null `owner` against `SessionStore` via `SessionTerminalFolders`, which
 * rejects synthetic owners like `run:<id>`. Leaving `owner` unset runs the
 * command directly in `cwd` inside the user's login shell (`-l -c <command>`),
 * and the returned `TerminalInfo.id` is tracked in `run-configurations.runs`.
 */
export async function startRunTerminal(cwd: string, command: string): Promise<TerminalInfo> {
  return backend.call<TerminalInfo>('terminal', 'start', { cwd, command });
}

export async function closeRunTerminal(terminalId: string): Promise<void> {
  await backend.call('terminal', 'close', { terminal: terminalId });
}

export async function listRunTerminals(signal?: AbortSignal): Promise<TerminalInfo[]> {
  const result = await backend.call<TerminalInfo[]>('terminal', 'list', {}, { signal });
  return Array.isArray(result) ? result : [];
}

export async function readRunTerminalOutput(
  terminalId: string,
  offset = 0,
  signal?: AbortSignal
): Promise<OutputPiece> {
  return backend.call<OutputPiece>(
    'terminal',
    'read',
    { terminal: terminalId, offset },
    { signal }
  );
}

// ── Live terminal output streaming ───────────────────────────────────────────

export interface RunOutputCallbacks {
  onTail(terminalId: string, tail: string[]): void;
  onExit(terminalId: string, exitCode: number): void;
}

/**
 * Subscribe to live stdout/stderr and exit events for each running terminal on
 * the dedicated `/socket/terminal` channel, seeding buffered output once via
 * `terminal/read`. Calling `stop()` aborts in-flight seed reads and tears down
 * every `/socket/terminal` subscription immediately.
 */
export async function watchRunOutput(
  terminalIds: readonly string[],
  callbacks: RunOutputCallbacks
): Promise<RunOutputWatch> {
  let stopped = false;
  const controller = new AbortController();
  const unsubs: (() => void)[] = [];

  const watch: RunOutputWatch = {
    stop(): void {
      if (stopped) return;
      stopped = true;
      controller.abort();
      for (const unsub of unsubs) unsub();
      unsubs.length = 0;
    }
  };

  const uniqueIds = [...new Set(terminalIds.filter((id) => id.trim() !== ''))];
  if (uniqueIds.length === 0) return watch;

  const tails = new Map<string, string[]>();
  const watermarks = new Map<string, number>();
  const queued = new Map<string, OutputRow[]>();
  const hydrating = new Set<string>(uniqueIds);

  function applyRow(terminalId: string, row: OutputRow): void {
    if (stopped) return;
    const watermark = watermarks.get(terminalId) ?? 0;
    if (typeof row.data === 'string' && row.data.length > 0) {
      if (typeof row.seq !== 'number' || row.seq > watermark) {
        const text = decodeBase64Utf8(row.data);
        if (text.length > 0) {
          const next = appendOutputTail(tails.get(terminalId) ?? [], text);
          tails.set(terminalId, next);
          callbacks.onTail(terminalId, next);
        }
        if (typeof row.seq === 'number') {
          watermarks.set(terminalId, row.seq);
        }
      }
    }
    if (typeof row.exit === 'number' && Number.isFinite(row.exit)) {
      callbacks.onExit(terminalId, row.exit);
    }
  }

  for (const terminalId of uniqueIds) {
    queued.set(terminalId, []);
    const unsub = backend.subscribe<OutputRow>(
      'terminal',
      (event) => {
        if (stopped || event.type !== 'change' || !event.item) return;
        if (hydrating.has(terminalId)) {
          queued.get(terminalId)?.push(event.item);
        } else {
          applyRow(terminalId, event.item);
        }
      },
      terminalId,
      'terminal'
    );
    unsubs.push(unsub);
  }

  await Promise.all(
    uniqueIds.map(async (terminalId) => {
      try {
        const piece = await readRunTerminalOutput(terminalId, 0, controller.signal);
        if (stopped) return;
        const seeded = appendOutputTail([], decodeBase64Utf8(piece.data));
        watermarks.set(terminalId, piece.end);
        tails.set(terminalId, seeded);
        callbacks.onTail(terminalId, seeded);
      } catch (err) {
        if (stopped || isBackendAbort(err)) return;
        // Terminal may have been closed or exited before seeding; live reconciliation handles state.
      } finally {
        hydrating.delete(terminalId);
        const pending = queued.get(terminalId) ?? [];
        queued.delete(terminalId);
        for (const row of pending) applyRow(terminalId, row);
      }
    })
  );

  return watch;
}
