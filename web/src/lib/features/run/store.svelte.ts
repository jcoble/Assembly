/**
 * Reactive Svelte 5 store and actions for the Run panel (saved runnable commands).
 *
 * Persists definitions and run records to `.NET` SQLite via `preferencesStore`
 * and drives live command processes through `api.ts` (`terminal/*` + `/socket/terminal`).
 */

import { preferencesStore } from '$lib/application/preferences.svelte';
import { isBackendAbort } from '$lib/infrastructure/backend';

import {
  closeRunTerminal,
  listRunTerminals,
  readSavedDefinitionsValue,
  readSavedRunsValue,
  startRunTerminal,
  writeSavedDefinitions,
  writeSavedRuns
} from './api';
import { matchKeybinding } from './keybindingCapture';
import {
  STORAGE_WRITE_FAILED_MESSAGE,
  buildStackDefinition,
  buildStackRows,
  commandWithEnv,
  describeStackProblem,
  folderName,
  mintStackId,
  normalizeDefinitionsForStorage,
  normalizeRunsForStorage,
  parseStackDefinitions,
  parseStackRuns
} from './model';
import type {
  StackDefinition,
  StackDraft,
  StackProcess,
  StackRow,
  StackRunRecord,
  TerminalInfo
} from './types';

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Single reactive state container for saved actions, run records, and live terminals. */
export const stacks = $state<{
  hydrated: boolean;
  activeRoot: string | null;
  projectName: string;
  definitions: StackDefinition[];
  runs: Record<string, StackRunRecord>;
  terminals: Record<string, TerminalInfo>;
  processes: StackProcess[];
  loading: boolean;
  error: string | null;
  unavailableReason: string | null;
  requestId: number;
  loadedAt: number | null;
  notice: string | null;
  busyByStackId: Record<string, boolean>;
}>({
  hydrated: false,
  activeRoot: null,
  projectName: '',
  definitions: [],
  runs: {},
  terminals: {},
  processes: [],
  loading: false,
  error: null,
  unavailableReason: null,
  requestId: 0,
  loadedAt: null,
  notice: null,
  busyByStackId: {}
});

const startsInFlight = new Set<string>();
const stopsInFlight = new Set<string>();
let lastDefsSnapshot = '';
let lastRunsSnapshot = '';
let refreshController: AbortController | null = null;

function syncBusyFlag(stackId: string): void {
  const busy = startsInFlight.has(stackId) || stopsInFlight.has(stackId);
  if (busy) {
    stacks.busyByStackId = { ...stacks.busyByStackId, [stackId]: true };
  } else if (stackId in stacks.busyByStackId) {
    const next = { ...stacks.busyByStackId };
    delete next[stackId];
    stacks.busyByStackId = next;
  }
}

function stableJson(value: unknown): string {
  if (value === undefined) return '__undefined__';
  try {
    return JSON.stringify(value) ?? '';
  } catch {
    return '';
  }
}

/**
 * Synchronize saved definitions and run records from `preferencesStore`.
 * Safe to call inside a reactive `$effect`: only updates when serialized values change.
 */
export function syncRunPreferences(): void {
  if (!preferencesStore.loaded) return;
  const rawDefs = readSavedDefinitionsValue();
  const rawRuns = readSavedRunsValue();
  const defsSig = stableJson(rawDefs);
  const runsSig = stableJson(rawRuns);

  if (stacks.hydrated && defsSig === lastDefsSnapshot && runsSig === lastRunsSnapshot) {
    return;
  }

  lastDefsSnapshot = defsSig;
  lastRunsSnapshot = runsSig;

  const definitions = parseStackDefinitions(rawDefs);
  const known = new Set(definitions.map((definition) => definition.id));
  const runs: Record<string, StackRunRecord> = {};
  for (const run of parseStackRuns(rawRuns)) {
    if (!known.has(run.stackId)) continue;
    runs[run.ownedId] = run;
  }
  stacks.definitions = definitions;
  stacks.runs = runs;
  stacks.hydrated = true;
}

async function persistDefinitions(): Promise<boolean> {
  try {
    lastDefsSnapshot = stableJson(normalizeDefinitionsForStorage(stacks.definitions));
    await writeSavedDefinitions(stacks.definitions);
    return true;
  } catch {
    stacks.notice = STORAGE_WRITE_FAILED_MESSAGE;
    return false;
  }
}

async function persistRuns(): Promise<boolean> {
  try {
    const list = Object.values(stacks.runs);
    lastRunsSnapshot = stableJson(normalizeRunsForStorage(list));
    await writeSavedRuns(list);
    return true;
  } catch {
    stacks.notice = STORAGE_WRITE_FAILED_MESSAGE;
    return false;
  }
}

// ── Derived queries ──────────────────────────────────────────────────────────

export function allStackRows(): StackRow[] {
  return buildStackRows(stacks.definitions, stacks.runs, stacks.processes, stacks.terminals);
}

export function isStackBusy(stackId: string): boolean {
  return stacks.busyByStackId[stackId] === true;
}

export function ownedIdForStack(stackId: string): string | null {
  let latest: StackRunRecord | null = null;
  for (const run of Object.values(stacks.runs)) {
    if (run.stackId !== stackId) continue;
    if (!latest || run.startedAt >= latest.startedAt) latest = run;
  }
  return latest?.ownedId ?? null;
}

// ── Input & notice mutations ─────────────────────────────────────────────────

export function setStackInput(input: { activeRoot: string | null; projectName?: string }): void {
  stacks.activeRoot = input.activeRoot;
  stacks.projectName = (input.projectName ?? '').trim() || folderName(input.activeRoot);
}

export function clearStackNotice(): void {
  stacks.notice = null;
}

// ── Definition CRUD ──────────────────────────────────────────────────────────

export async function addStack(draft: StackDraft): Promise<StackDefinition | null> {
  const problem = describeStackProblem(draft);
  if (problem) {
    stacks.notice = problem;
    return null;
  }
  syncRunPreferences();
  const definition = buildStackDefinition(mintStackId(), draft);
  stacks.definitions = [...stacks.definitions, definition];
  stacks.notice = null;
  if (!(await persistDefinitions())) {
    stacks.definitions = stacks.definitions.filter((existing) => existing.id !== definition.id);
    return null;
  }
  return definition;
}

export async function updateStack(
  stackId: string,
  draft: StackDraft
): Promise<StackDefinition | null> {
  syncRunPreferences();
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
  const updated = buildStackDefinition(existing.id, draft);
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

export async function removeStack(stackId: string): Promise<void> {
  syncRunPreferences();
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

// ── Process lifecycle & reconciliation ───────────────────────────────────────

export async function refreshStacks(): Promise<void> {
  refreshController?.abort();
  const controller = new AbortController();
  refreshController = controller;

  stacks.requestId += 1;
  const ticket = stacks.requestId;
  stacks.loading = true;
  stacks.error = null;
  stacks.unavailableReason = null;

  try {
    const listed = await listRunTerminals(controller.signal);
    if (stacks.requestId !== ticket) return;

    const byId: Record<string, TerminalInfo> = {};
    for (const term of listed) {
      byId[term.id] = term;
    }
    stacks.terminals = byId;

    let runsChanged = false;
    const nextRuns: Record<string, StackRunRecord> = { ...stacks.runs };
    for (const [terminalId, run] of Object.entries(nextRuns)) {
      if (run.exited) continue;
      if (startsInFlight.has(run.stackId) || stopsInFlight.has(run.stackId)) continue;
      const live = byId[terminalId];
      if (!live) {
        nextRuns[terminalId] = { ...run, exited: true, exitCode: 0, signal: null };
        runsChanged = true;
      } else if (live.exitCode !== null) {
        nextRuns[terminalId] = { ...run, exited: true, exitCode: live.exitCode, signal: null };
        runsChanged = true;
      }
    }

    if (runsChanged) {
      stacks.runs = nextRuns;
      await persistRuns();
    }

    stacks.loading = false;
    stacks.loadedAt = Date.now();
  } catch (error) {
    if (stacks.requestId !== ticket || isBackendAbort(error)) return;
    stacks.loading = false;
    stacks.error = `Could not read running processes: ${describeError(error)}`;
  } finally {
    if (refreshController === controller) {
      refreshController = null;
    }
  }
}

export function openPreviewUrl(rawUrl: string): void {
  const url = rawUrl.trim();
  if (!url) return;
  if (typeof window === 'undefined') return;
  const opened = window.open(url, '_blank', 'noopener');
  if (!opened) {
    stacks.notice = `Preview URL ready at ${url} (the Browser panel is not mounted yet).`;
  }
}

export async function startStack(stackId: string): Promise<void> {
  if (startsInFlight.has(stackId)) return;
  const definition = stacks.definitions.find((entry) => entry.id === stackId);
  if (!definition) return;
  const cwd = definition.cwd.trim();
  if (!cwd) {
    stacks.error = `Could not start "${definition.name}": no folder is configured.`;
    return;
  }

  startsInFlight.add(stackId);
  syncBusyFlag(stackId);
  stacks.error = null;
  let shouldRefresh = false;
  try {
    const command = commandWithEnv(definition.script, definition.env);
    const info = await startRunTerminal(cwd, command);
    stacks.terminals = { ...stacks.terminals, [info.id]: info };

    const nextRuns: Record<string, StackRunRecord> = {};
    for (const run of Object.values(stacks.runs)) {
      if (run.stackId === definition.id) continue;
      nextRuns[run.ownedId] = run;
    }
    const exited = info.exitCode !== null;
    nextRuns[info.id] = {
      ownedId: info.id,
      stackId: definition.id,
      startedAt: Date.now(),
      exited,
      exitCode: info.exitCode,
      signal: null
    };
    stacks.runs = nextRuns;
    await persistRuns();

    const preview = (definition.previewUrl ?? '').trim();
    if (definition.openPreviewOnRun === true && preview) {
      openPreviewUrl(preview);
    }

    shouldRefresh = !exited;
  } catch (error) {
    stacks.error = `Could not start "${definition.name}": ${describeError(error)}`;
  } finally {
    startsInFlight.delete(stackId);
    syncBusyFlag(stackId);
  }
  if (shouldRefresh) {
    void refreshStacks();
  }
}

export async function stopStack(stackId: string): Promise<void> {
  if (stopsInFlight.has(stackId)) return;
  const definition = stacks.definitions.find((entry) => entry.id === stackId);
  const ownedId = ownedIdForStack(stackId);
  if (!ownedId) return;

  stopsInFlight.add(stackId);
  syncBusyFlag(stackId);
  stacks.error = null;
  try {
    await closeRunTerminal(ownedId);
    const nextTerminals = { ...stacks.terminals };
    delete nextTerminals[ownedId];
    stacks.terminals = nextTerminals;

    const existing = stacks.runs[ownedId];
    if (existing) {
      stacks.runs = {
        ...stacks.runs,
        [ownedId]: { ...existing, exited: true, exitCode: 0, signal: null }
      };
      await persistRuns();
    }
  } catch (error) {
    stacks.error = `Could not stop "${definition?.name ?? 'this run configuration'}": ${describeError(error)}`;
  } finally {
    stopsInFlight.delete(stackId);
    syncBusyFlag(stackId);
  }
}

export async function restartStack(stackId: string): Promise<void> {
  await stopStack(stackId);
  await startStack(stackId);
}

/** Immediately reflect a terminal exit pushed over `/socket/terminal`. */
export function noteRunTerminalExit(terminalId: string, exitCode: number): void {
  const existing = stacks.runs[terminalId];
  if (!existing || existing.exited || stopsInFlight.has(existing.stackId)) return;

  const currentTerminal = stacks.terminals[terminalId];
  if (currentTerminal) {
    stacks.terminals = {
      ...stacks.terminals,
      [terminalId]: { ...currentTerminal, exitCode }
    };
  }
  stacks.runs = {
    ...stacks.runs,
    [terminalId]: { ...existing, exited: true, exitCode, signal: null }
  };
  void persistRuns();
}

// ── Global keybinding dispatch ───────────────────────────────────────────────

export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || tag === 'BUTTON';
}

export function handleRunKeydown(event: KeyboardEvent): void {
  if (event.defaultPrevented || isTypingTarget(event.target)) return;
  const matched = matchKeybinding(event, stacks.definitions);
  if (!matched || matched.cwd.trim().length === 0) return;
  event.preventDefault();
  void startStack(matched.id);
}
