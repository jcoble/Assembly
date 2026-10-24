import {
  listAgentSessionsForProjectFromTauri,
  readAgentSessionDetailsFromTauri,
  readRemoteSavedSessionDetailsFromTauri,
  listAgentSessionsFromLocalBridge,
  listAgentSessionsFromTauri
} from '$lib/tauriSource.ts';
import type { AgentSession } from '$lib/tauriSource.ts';
import { rail, setAvailable } from '$lib/shell/stores/sessionRailStore.svelte.ts';
import {
  createSessionLibraryService,
  registerSessionLibraryHost
} from '$lib/shell/history/library/sessionLibraryService.ts';
import type { SessionLibraryRecord } from '$lib/shell/history/library/sessionLibraryModel.ts';

export interface SessionHistoryHostOptions {
  openOwned(ownedId: string): Promise<void>;
  deleteOwned(ownedId: string): Promise<void>;
}

export interface SessionHistoryHostHandle {
  rescan(stopSignal?: AbortSignal): Promise<void>;
  release(): void;
}

async function listLocalProviderSessions(projectPath?: string): Promise<AgentSession[]> {
  try {
    const nativeSessions = projectPath
      ? await listAgentSessionsForProjectFromTauri(projectPath)
      : await listAgentSessionsFromTauri();
    return nativeSessions ?? (await listAgentSessionsFromLocalBridge()) ?? [];
  } catch {
    return (await listAgentSessionsFromLocalBridge()) ?? [];
  }
}

/** This Mac's scan, and for the whole list each connected machine's own scan, labelled with that machine. With
 * `publish`, each machine's rows go out as soon as they are read, so a slow machine (its scan may wait 15 s) never holds
 * the others back, and `publish` ends that machine's wait; without it, the waits end once every machine has answered. */
async function listProviderSessions(projectPath?: string, publish?: (machine: string, rows: AgentSession[]) => void): Promise<AgentSession[]> {
  const machines = projectPath ? [] : Object.keys(rail.remoteConnections).filter((id) => rail.remoteConnections[id] === 'connected');
  rail.scanningMachines = [...new Set([...rail.scanningMachines, 'local', ...machines])];
  rail.runningElsewhere = [];
  const remote = Promise.all(machines.map(async (machine) => {
    let rows: AgentSession[] = [];
    try {
      rows = ((await listAgentSessionsFromTauri(machine)) ?? []).map((session) => ({ ...session, machine }));
    } catch (error) {
      console.warn(`[history] could not scan ${machine}`, error);
    }
    publish?.(machine, rows);
    return rows;
  }));
  const local = await listLocalProviderSessions(projectPath);
  publish?.('local', local);
  const rows = [...local, ...(await remote).flat()];
  if (!publish) rail.scanningMachines = rail.scanningMachines.filter((id) => id !== 'local' && !machines.includes(id));
  return rows;
}

/** Own the History panel's scanner and action bridge for the primary shell. */
export function registerSessionHistoryHost(options: SessionHistoryHostOptions): SessionHistoryHostHandle {
  let generation = 0;
  const service = createSessionLibraryService(
    {
      getOwnedSessions: () => rail.owned,
      getAvailableSessions: () => rail.available,
      listProviderSessions,
      readRemoteSavedSessionDetails: readRemoteSavedSessionDetailsFromTauri,
      readProviderSessionDetails: async (logPath, machine = 'local') => machine === 'local'
        ? (await readAgentSessionDetailsFromTauri(logPath)) ?? []
        : ((await readAgentSessionDetailsFromTauri(logPath, machine)) ?? []).map((session) => ({ ...session, machine }))
    },
    {
      onOpen: async (record: SessionLibraryRecord) => {
        if (record.ownedId) await options.openOwned(record.ownedId);
      },
      onDelete: async (record: SessionLibraryRecord) => {
        if (record.ownedId) await options.deleteOwned(record.ownedId);
      }
    }
  );
  const releaseRegistration = registerSessionLibraryHost({ service, rescan });

  async function rescan(stopSignal?: AbortSignal): Promise<void> {
    if (stopSignal?.aborted) return;
    const scanGeneration = ++generation;
    rail.scanning = true;
    try {
      const current = (): boolean => !stopSignal?.aborted && scanGeneration === generation;
      // Each machine's rows replace its own as they arrive, and only then may its rows resume: a held row learns its
      // transcript from this scan, and resuming it against the previous one would skip the checks that need it.
      const sessions = await listProviderSessions(undefined, (machine, rows) => {
        if (!current()) return;
        setAvailable([...rail.available.filter((session) => (session.machine ?? 'local') !== machine), ...rows]);
        rail.scanningMachines = rail.scanningMachines.filter((id) => id !== machine);
      });
      if (!current()) return;
      setAvailable(sessions);
    } finally {
      if (scanGeneration === generation) rail.scanningMachines = [];
      if (!stopSignal?.aborted && scanGeneration === generation) rail.scanning = false;
    }
  }

  return {
    rescan,
    release(): void {
      generation += 1;
      rail.scanning = false;
      service.release();
      releaseRegistration();
    }
  };
}
