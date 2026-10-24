/**
 * In-memory Svelte 5 projection for the /next session rail.
 *
 * SQLite owns durable session identity and metadata. The page hydrates this
 * projection from backend records and refreshes conversation state from stored
 * snapshots/events; this module performs no I/O.
 */
import type { AgentSession, RemoteAssemblyProfile } from '../../tauriSource.ts';
import type { ConversationWorkspaceState } from '../conversation/conversationStore.svelte.ts';
import type { OwnedSession } from '../ownedSessions.ts';
import type { SessionWorkspaceSnapshot } from '../sessionWorkspaces.ts';

/** SQLite setting naming the rail row the owner last had selected. */
export const ACTIVE_OWNED_SESSION_SETTING_KEY = 'shell.active-owned-session';

/**
 * What one saved remote machine's transport is doing, as the backend reports it.
 * Startup seeds the current native state, then lifecycle events keep it current.
 */
export type RemoteConnectionState = 'connected' | 'disconnected' | 'reconnecting';

/** The rail stays compact while SQLite hydrates only the selected session. */
export interface SessionProjection {
  activeOwnedId: string | null;
  rail: readonly OwnedSession[];
  activeConversation: ConversationWorkspaceState | null;
  activeWorkspace: SessionWorkspaceSnapshot | null;
}

export const rail = $state<{
  owned: OwnedSession[];
  available: AgentSession[];
  activeOwnedId: string | null;
  scanning: boolean;
  error: string | null;
  remoteConnections: Record<string, RemoteConnectionState>;
  /** The saved remote machines, so a row can name the machine it runs on. */
  remoteProfiles: RemoteAssemblyProfile[];
  /** Connected machines whose History scan has not answered yet. */
  scanningMachines: string[];
  /** History rows whose resume was refused because their own CLI or app is running them. */
  runningElsewhere: string[];
}>({
  owned: [],
  available: [],
  activeOwnedId: null,
  scanning: false,
  error: null,
  remoteConnections: {},
  remoteProfiles: [],
  scanningMachines: [],
  runningElsewhere: []
});

/** One machine's state, keyed by the profile id a remote session carries. */
export function setRemoteConnection(profileId: string, state: RemoteConnectionState): void {
  if (rail.remoteConnections[profileId] === state) return;
  rail.remoteConnections[profileId] = state;
}

export function hydrateOwned(sessions: OwnedSession[]): void {
  rail.owned = sessions;
}

/** A machine's authoritative list after it connects replaces every row under that
 * profile or the profile it replaced, and any remote row with an incoming owned id. */
export function replaceConnectedRemoteRows(profileIds: readonly (string | null)[], sessions: OwnedSession[]): void {
  const replaced = new Set(profileIds);
  const incoming = new Set(sessions.map((session) => session.ownedId));
  rail.owned = [...rail.owned.filter((session) =>
    !replaced.has(session.remoteProfileId ?? '') &&
    !(session.executionEnvironment === 'remote' && incoming.has(session.ownedId))), ...sessions];
}

export function addOwnedSession(session: OwnedSession): void {
  rail.owned = [...rail.owned, session];
}

// List items (such as background work records) are compared one level deep.
const sameItem = (left: unknown, right: unknown): boolean =>
  Object.is(left, right)
  || (typeof left === 'object' && typeof right === 'object' && left !== null && right !== null
    && Object.keys(left).length === Object.keys(right).length
    && Object.entries(left).every(([key, value]) => Object.is(value, (right as Record<string, unknown>)[key])));

const sameValue = (left: unknown, right: unknown): boolean =>
  Object.is(left, right)
  || (Array.isArray(left) && Array.isArray(right)
    && left.length === right.length && left.every((value, index) => sameItem(value, right[index])));

export function updateOwnedSession(ownedId: string, patch: Partial<OwnedSession>): void {
  const existing = rail.owned.find((session) => session.ownedId === ownedId);
  // Replacing the list re-renders every row; skip it when nothing changed.
  if (existing && Object.entries(patch).every(([key, value]) =>
    sameValue(existing[key as keyof OwnedSession], value))) return;
  rail.owned = rail.owned.map((session) =>
    session.ownedId === ownedId ? { ...session, ...patch, ownedId: session.ownedId } : session
  );
}

/** The lifecycle dates that move a session to Working, Done or Settled. */
export function ownedSessionStatusPatch(
  session: Pick<OwnedSession, 'completedAt'>,
  status: 'working' | 'done' | 'settled',
  when: Date
): Pick<OwnedSession, 'completedAt' | 'settledAt'> {
  const stamp = when.toISOString();
  return {
    completedAt: status === 'working' ? null : session.completedAt ?? stamp,
    settledAt: status === 'settled' ? stamp : null
  };
}

export function removeOwnedSession(ownedId: string): void {
  rail.owned = rail.owned.filter((session) => session.ownedId !== ownedId);
  if (rail.activeOwnedId === ownedId) rail.activeOwnedId = null;
}

export function setActiveOwned(ownedId: string | null): void {
  rail.activeOwnedId = ownedId;
}

export function setAvailable(sessions: AgentSession[]): void {
  // The rail needs resumable-session identity and row metadata, not the full
  // turns History shows after a project is expanded. Keeping those turns here
  // made closing History release nothing because the rail still owned them.
  rail.available = sessions.map((session) => ({ ...session, latestTurns: undefined }));
}
