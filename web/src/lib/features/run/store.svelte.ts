/**
 * Reactive Svelte 5 runes store for project Run configurations.
 *
 * Persists definitions and last run records through `preferencesStore` (`preferences` topic +
 * `preferences/set`) using the preserved `run-configurations.*` keys, and manages process
 * lifecycle through `.NET` `/socket/terminal` RPCs (`api.ts`).
 */

import { preferencesStore } from '$lib/application/preferences.svelte';
import { isBackendAbort } from '$lib/infrastructure/backend';
import { closeRunTerminal, listRunTerminals, startRunTerminal } from './api';
import {
  RUN_DEFINITIONS_PREF_KEY,
  RUN_RUNS_PREF_KEY,
  STORAGE_WRITE_FAILED_MESSAGE,
  buildRunDefinition,
  buildRunRows,
  commandWithEnv,
  describeRunProblem,
  folderName,
  normalizePreviewUrl,
  omitTerminalIds,
  parseRunDefinitions,
  parseRunRuns,
  partitionSupersededRuns,
  releaseSupersededTerminals,
  serializeRunDefinitions,
  serializeRunRuns
} from './model';
import type {
  RunDefinition,
  RunDraft,
  RunProcess,
  RunRecord,
  RunRow,
  TerminalInfo
} from './types';

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function mintRunId(): string {
  const random = globalThis.crypto?.randomUUID?.();
  return random ? `stack-${random}` : `stack-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

class RunStore {
  hydrated = $state<boolean>(false);
  activeRoot = $state<string | null>(null);
  projectName = $state<string>('');
  definitions = $state<RunDefinition[]>([]);
  runs = $state<Record<string, RunRecord>>({});
  terminals = $state<Record<string, TerminalInfo>>({});
  processes = $state<RunProcess[]>([]);
  loading = $state<boolean>(false);
  error = $state<string | null>(null);
  notice = $state<string | null>(null);
  private busy = $state<Record<string, 'starting' | 'stopping'>>({});
  private requestId = 0;
  private pendingWrites = 0;

  get rows(): RunRow[] {
    const startingIds = new Set(
      Object.entries(this.busy)
        .filter(([, kind]) => kind === 'starting')
        .map(([id]) => id)
    );
    return buildRunRows(this.definitions, this.runs, this.terminals, startingIds, this.processes);
  }

  get running(): RunRow[] {
    return this.rows.filter((row) => row.state === 'running' || row.state === 'starting');
  }

  isBusy(id: string): boolean {
    return this.busy[id] !== undefined;
  }

  setContext(root: string | null, projectName?: string): void {
    const trimmedRoot = (root ?? '').trim();
    this.activeRoot = trimmedRoot || null;
    this.projectName = (projectName ?? '').trim() || folderName(trimmedRoot);
  }

  /**
   * Synchronize definitions and run records from `preferencesStore` once loaded (and on updates).
   * Skipped while a local preference write is in flight to avoid transient rollback flicker.
   */
  syncFromPreferences(rawDefinitions: unknown, rawRuns: unknown): void {
    if (!preferencesStore.loaded || this.pendingWrites > 0) return;
    const definitions = parseRunDefinitions(rawDefinitions);
    const known = new Set(definitions.map((definition) => definition.id));
    const runs: Record<string, RunRecord> = {};
    for (const run of parseRunRuns(rawRuns)) {
      if (!known.has(run.stackId)) continue;
      runs[run.ownedId] = run;
    }
    this.definitions = definitions;
    this.runs = runs;
    this.hydrated = true;
  }

  /** Ensure latest loaded preferences are reflected before mutating. */
  private ensureSyncedFromPreferences(): void {
    if (!this.hydrated && preferencesStore.loaded) {
      this.syncFromPreferences(
        preferencesStore.get(RUN_DEFINITIONS_PREF_KEY),
        preferencesStore.get(RUN_RUNS_PREF_KEY)
      );
    }
  }

  private async persistDefinitions(): Promise<boolean> {
    this.pendingWrites += 1;
    try {
      await preferencesStore.set(
        RUN_DEFINITIONS_PREF_KEY,
        serializeRunDefinitions(this.definitions)
      );
      return true;
    } catch {
      this.notice = STORAGE_WRITE_FAILED_MESSAGE;
      return false;
    } finally {
      this.pendingWrites = Math.max(0, this.pendingWrites - 1);
    }
  }

  private async persistRuns(): Promise<boolean> {
    this.pendingWrites += 1;
    try {
      await preferencesStore.set(RUN_RUNS_PREF_KEY, serializeRunRuns(Object.values(this.runs)));
      return true;
    } catch {
      this.notice = STORAGE_WRITE_FAILED_MESSAGE;
      return false;
    } finally {
      this.pendingWrites = Math.max(0, this.pendingWrites - 1);
    }
  }

  /**
   * Query `.NET` `terminal/list` once to reconcile live PTY status with saved run records.
   * No polling: called only when the Run tab opens, after start/stop, or on Refresh click.
   */
  async refresh(signal?: AbortSignal): Promise<void> {
    this.ensureSyncedFromPreferences();
    const ticket = ++this.requestId;
    this.loading = true;
    this.error = null;
    try {
      const list = await listRunTerminals(signal);
      if (this.requestId !== ticket) return;
      const byId: Record<string, TerminalInfo> = {};
      for (const info of list) {
        byId[info.id] = info;
      }
      this.terminals = byId;
      this.loading = false;

      let changed = false;
      const nextRuns: Record<string, RunRecord> = { ...this.runs };
      for (const [ownedId, run] of Object.entries(this.runs)) {
        if (run.exited) continue;
        const live = byId[ownedId];
        if (!live) {
          nextRuns[ownedId] = { ...run, exited: true, exitCode: 0, signal: null };
          changed = true;
        } else if (live.exitCode !== null && live.exitCode !== undefined) {
          nextRuns[ownedId] = { ...run, exited: true, exitCode: live.exitCode, signal: null };
          changed = true;
        }
      }
      if (changed) {
        this.runs = nextRuns;
        await this.persistRuns();
      }
    } catch (err) {
      if (this.requestId !== ticket) return;
      this.loading = false;
      if (!isBackendAbort(err)) {
        this.error = `Could not read running processes: ${describeError(err)}`;
      }
    }
  }

  async add(draft: RunDraft): Promise<RunDefinition | null> {
    const problem = describeRunProblem(draft);
    if (problem) {
      this.notice = problem;
      return null;
    }
    this.ensureSyncedFromPreferences();
    const definition = buildRunDefinition(mintRunId(), draft);
    const before = this.definitions;
    this.definitions = [...before, definition];
    this.notice = null;
    if (!(await this.persistDefinitions())) {
      this.definitions = before;
      return null;
    }
    return definition;
  }

  async update(id: string, draft: RunDraft): Promise<RunDefinition | null> {
    this.ensureSyncedFromPreferences();
    const existing = this.definitions.find((definition) => definition.id === id);
    if (!existing) {
      this.notice = 'That run configuration is no longer saved.';
      return null;
    }
    const problem = describeRunProblem(draft);
    if (problem) {
      this.notice = problem;
      return null;
    }
    const updated = buildRunDefinition(existing.id, draft);
    const before = this.definitions;
    this.definitions = before.map((definition) => (definition.id === id ? updated : definition));
    this.notice = null;
    if (!(await this.persistDefinitions())) {
      this.definitions = before;
      return null;
    }
    return updated;
  }

  async remove(id: string): Promise<void> {
    this.ensureSyncedFromPreferences();
    const beforeDefs = this.definitions;
    const beforeRuns = this.runs;
    const { supersededIds, remaining } = partitionSupersededRuns(beforeRuns, id);
    this.definitions = beforeDefs.filter((definition) => definition.id !== id);
    this.runs = remaining;
    const defsSaved = await this.persistDefinitions();
    if (!defsSaved) {
      this.definitions = beforeDefs;
      this.runs = beforeRuns;
      return;
    }
    if (supersededIds.length > 0) {
      await this.persistRuns();
      await Promise.allSettled(supersededIds.map((terminalId) => closeRunTerminal(terminalId)));
      this.terminals = omitTerminalIds(this.terminals, supersededIds);
    }
  }

  async start(id: string): Promise<boolean> {
    if (this.busy[id]) return false;
    this.ensureSyncedFromPreferences();
    const definition = this.definitions.find((item) => item.id === id);
    if (!definition) return false;
    this.busy = { ...this.busy, [id]: 'starting' };
    this.error = null;
    try {
      // Close any superseded (exited or prior) terminal for this action so `.NET` `TerminalHost`
      // releases its retained output ring (up to 16 MiB) before starting the replacement.
      const { closedIds } = await releaseSupersededTerminals(
        this.runs,
        this.terminals,
        id,
        closeRunTerminal
      );
      if (closedIds.length > 0) {
        this.terminals = omitTerminalIds(this.terminals, closedIds);
        const closedSet = new Set(closedIds);
        let closedActive = false;
        const cleanedRuns: Record<string, RunRecord> = { ...this.runs };
        for (const [ownedId, run] of Object.entries(cleanedRuns)) {
          if (closedSet.has(ownedId) && !run.exited) {
            cleanedRuns[ownedId] = { ...run, exited: true, exitCode: 0, signal: null };
            closedActive = true;
          }
        }
        if (closedActive) {
          this.runs = cleanedRuns;
        }
      }

      const command = commandWithEnv(definition.script, definition.env);
      const info = await startRunTerminal(definition.cwd, command);

      const { supersededIds: lateSuperseded, remaining } = partitionSupersededRuns(this.runs, id);
      const extraToClose = lateSuperseded.filter((terminalId) => !closedIds.includes(terminalId));
      if (extraToClose.length > 0) {
        await Promise.allSettled(extraToClose.map((terminalId) => closeRunTerminal(terminalId)));
      }
      this.terminals = {
        ...omitTerminalIds(this.terminals, lateSuperseded),
        [info.id]: info
      };

      const alreadyExited = info.exitCode !== null && info.exitCode !== undefined;
      this.runs = {
        ...remaining,
        [info.id]: {
          ownedId: info.id,
          stackId: id,
          startedAt: Date.now(),
          exited: alreadyExited,
          exitCode: alreadyExited ? (info.exitCode ?? 0) : null,
          signal: null
        }
      };
      await this.persistRuns();
      return true;
    } catch (err) {
      this.error = `Could not start "${definition.name}": ${describeError(err)}`;
      return false;
    } finally {
      const nextBusy = { ...this.busy };
      delete nextBusy[id];
      this.busy = nextBusy;
    }
  }

  /** Start a saved action and open its preview URL if `openPreviewOnRun` is enabled. */
  async launch(id: string, onOpenPreview?: (url: string) => void): Promise<boolean> {
    this.ensureSyncedFromPreferences();
    const definition = this.definitions.find((item) => item.id === id);
    if (!definition || !definition.cwd.trim()) return false;
    const started = await this.start(id);
    if (!started) return false;
    if (definition.openPreviewOnRun === true) {
      const url = normalizePreviewUrl(definition.previewUrl);
      if (url) onOpenPreview?.(url);
    }
    return true;
  }

  /** Stop an action if running, then launch it again with its current saved configuration. */
  async restart(id: string, onOpenPreview?: (url: string) => void): Promise<boolean> {
    await this.stop(id);
    return this.launch(id, onOpenPreview);
  }

  async stop(id: string): Promise<void> {
    if (this.busy[id]) return;
    this.ensureSyncedFromPreferences();
    const definition = this.definitions.find((item) => item.id === id);
    const activeRun = Object.values(this.runs).find((run) => run.stackId === id && !run.exited);
    if (!activeRun) return;
    this.busy = { ...this.busy, [id]: 'stopping' };
    this.error = null;
    try {
      await closeRunTerminal(activeRun.ownedId);
      const nextTerminals = { ...this.terminals };
      delete nextTerminals[activeRun.ownedId];
      this.terminals = nextTerminals;
      this.runs = {
        ...this.runs,
        [activeRun.ownedId]: { ...activeRun, exited: true, exitCode: 0, signal: null }
      };
      await this.persistRuns();
    } catch (err) {
      this.error = `Could not stop "${definition?.name ?? 'this run configuration'}": ${describeError(err)}`;
    } finally {
      const nextBusy = { ...this.busy };
      delete nextBusy[id];
      this.busy = nextBusy;
    }
  }

  async noteTerminalExit(
    terminalId: string,
    exitCode: number | null,
    signal: string | null = null
  ): Promise<void> {
    const run = this.runs[terminalId];
    if (!run || run.exited) return;
    const live = this.terminals[terminalId];
    if (live && exitCode !== null) {
      this.terminals = { ...this.terminals, [terminalId]: { ...live, exitCode } };
    }
    this.runs = {
      ...this.runs,
      [terminalId]: { ...run, exited: true, exitCode, signal }
    };
    await this.persistRuns();
  }

  async noteTerminalMissing(terminalId: string): Promise<void> {
    const run = this.runs[terminalId];
    if (!run || run.exited) return;
    const nextTerminals = { ...this.terminals };
    delete nextTerminals[terminalId];
    this.terminals = nextTerminals;
    this.runs = {
      ...this.runs,
      [terminalId]: { ...run, exited: true, exitCode: 0, signal: null }
    };
    await this.persistRuns();
  }

  clearNotice(): void {
    this.notice = null;
  }

  clearError(): void {
    this.error = null;
  }
}

export const runStore = new RunStore();
