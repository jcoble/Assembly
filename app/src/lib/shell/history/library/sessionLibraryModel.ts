/**
 * Pure Session Library projection and identity rules.
 *
 * The rail/runtime stores remain the authorities. This file only projects
 * their current records into a searchable, paged list; it never persists an
 * archive and never starts a provider. Identity intentionally never includes a
 * title: two same-title sessions in different worktrees are different rows.
 */
import type { AgentSession, AgentSessionTurn } from '../../../tauriSource.ts';
import type { AgentRuntimeState, OwnedSession } from '../../ownedSessions.ts';

export type SessionLibraryState = 'working' | 'done' | 'settled' | 'resumable';
export type SessionLibrarySource = 'owned' | 'provider';
export type SessionHistoryScope = 'workspace' | 'project' | 'all';

export interface SessionLibraryTurn {
  speaker: 'user' | 'agent';
  text: string;
}

/** One agent a session spawned for itself, as a card lists it. */
export interface SessionLibrarySubagent {
  name: string;
  /** What kind of agent it was, when the transcript said. */
  kind: string | null;
  messageCount: number | null;
  /** Its own transcript file, when it has one the app can open. */
  logPath: string | null;
}

export interface SessionLibraryRecord {
  /** Stable row identity. Owned rows use ownedId; provider rows use a compound key. */
  key: string;
  source: SessionLibrarySource;
  machine: string;
  ownedId: string | null;
  provider: string;
  nativeSessionId: string | null;
  canonicalCwd: string;
  title: string;
  description: string | null;
  /**
   * The project group SQL matched the session to — `repo:<key>`, `project:<id>`
   * or `none` — and its label. History groups on these, the same as the rail.
   */
  projectGroupKey: string;
  projectGroupLabel: string;
  model: string | null;
  state: SessionLibraryState;
  runtimeState: AgentRuntimeState | null;
  lastActivity: string | null;
  /** The best existing-authority timestamp for date filtering/display. */
  updatedAt: string | null;
  /** The scanner's bounded count, when it has one. It is a floor, not a total. */
  messageCount: number | null;
  /**
   * The transcript file this session was scanned out of. Null when the scanner
   * had no single file for it — an owned session the scanner has not seen yet,
   * or a record merged from several sources. Every log action is off in that
   * case rather than guessing at a path.
   */
  logPath: string | null;
  /** These remain null/empty until the backend exposes the complete transcript. */
  startComplete?: boolean;
  firstPrompt: string | null;
  latestTurns: SessionLibraryTurn[];
  /**
   * Empty for the same reason: the scan reads a bounded window of a transcript
   * for the row's title and turn count, and never walks the agents a session
   * spawned. A card shows this block only once it has something in it.
   */
  subagents: SessionLibrarySubagent[];
  owned: OwnedSession | null;
  available: AgentSession | null;
}

export interface SessionLibraryFilters {
  query?: string;
  provider?: string;
  project?: string;
  worktree?: string;
  state?: SessionLibraryState | 'all';
  model?: string;
  dateFrom?: string | null;
  dateTo?: string | null;
}

export interface SessionHistoryFilters extends SessionLibraryFilters {
  scope?: SessionHistoryScope;
  workspacePath?: string | null;
  projectKey?: string | null;
  machine?: string;
}

export interface SessionLibraryGroup {
  state: SessionLibraryState;
  label: string;
  items: SessionLibraryRecord[];
}

/** Normalize paths for identity without guessing anything about their content. */
export function canonicalCwd(value: string | null | undefined): string {
  const raw = (value ?? '').trim().replaceAll('\\', '/').replace(/\/+/g, '/');
  if (!raw) return '';
  if (raw === '/') return raw;
  const withoutTrailingSlash = raw.replace(/\/$/, '');
  // Preserve drive-letter paths while making their separator spelling stable.
  if (/^[A-Z]:/.test(withoutTrailingSlash)) {
    return `${withoutTrailingSlash[0].toLowerCase()}${withoutTrailingSlash.slice(1)}`;
  }
  return withoutTrailingSlash;
}

export function canonicalProvider(value: string | null | undefined): string {
  return (value ?? '').trim().toLowerCase();
}

/**
 * Provider identity is `(machine,provider,nativeSessionId,canonicalCwd)`. The title is
 * deliberately absent, and `null` native ids remain meaningful alongside the
 * cwd rather than collapsing every fresh session into one row.
 */
export function sessionIdentityKey(input: {
  provider: string | null | undefined;
  nativeSessionId: string | null | undefined;
  cwd: string | null | undefined;
  machine?: string;
}): string {
  return [
    input.machine ?? 'local',
    canonicalProvider(input.provider),
    input.nativeSessionId?.trim() || '',
    canonicalCwd(input.cwd)
  ]
    .map((part) => encodeURIComponent(part))
    .join('|');
}

export function deriveOwnedLibraryState(session: Pick<OwnedSession, 'completedAt' | 'settledAt'>):
  | 'working'
  | 'done'
  | 'settled' {
  // Settled is an explicit user transition. No timestamp age, title, or process
  // state participates in this decision.
  if (typeof session.settledAt === 'string' && session.settledAt.length > 0) return 'settled';
  if (typeof session.completedAt === 'string' && session.completedAt.length > 0) return 'done';
  return 'working';
}

function providerForOwned(session: Pick<OwnedSession, 'agent' | 'viaCmux'>): string {
  return canonicalProvider(session.viaCmux ? `cmux-${session.agent}` : session.agent);
}

function bestUpdatedAt(session: OwnedSession): string | null {
  return session.lastActivity ?? session.settledAt ?? session.completedAt ?? null;
}

/**
 * The turns an expanded card shows.
 *
 * The scanner sends the real pair — who spoke and the whole of what they said —
 * so that is what is used when it is there. The one-line preview is the
 * fallback for records that have no scan behind them, and it is genuinely one
 * line: 120 characters with `You: ` or `Agent: ` already written into it. It
 * cannot say who spoke, so it does not claim to.
 */
function latestTurnsFor(
  turns: AgentSessionTurn[] | null | undefined,
  preview: string | null | undefined
): SessionLibraryTurn[] {
  const scanned = (turns ?? []).filter((turn) => turn.text.trim().length > 0);
  if (scanned.length > 0) {
    return scanned.map((turn) => ({ speaker: turn.speaker, text: turn.text }));
  }

  return preview ? [{ speaker: 'agent', text: preview }] : [];
}

export function ownedSessionLibraryRecord(
  session: OwnedSession,
  includeDetails = false
): SessionLibraryRecord {
  const provider = providerForOwned(session);
  const cwd = canonicalCwd(session.cwd);
  const machine = session.executionEnvironment === 'remote' ? session.remoteProfileId ?? '' : 'local';
  return {
    key: `owned:${session.ownedId}`,
    source: 'owned',
    machine,
    ownedId: session.ownedId,
    provider,
    nativeSessionId: session.nativeSessionId,
    canonicalCwd: cwd,
    title: session.title || session.ownedId,
    description: session.latestTurnPreview,
    projectGroupKey: session.projectGroupKey,
    projectGroupLabel: session.projectGroupLabel,
    model: session.executionEnvironment === 'remote' ? session.model ?? null : null,
    state: deriveOwnedLibraryState(session),
    runtimeState: session.runtimeState ?? null,
    lastActivity: session.lastActivity,
    updatedAt: bestUpdatedAt(session),
    messageCount: session.messageCount ?? null,
    logPath: null,
    firstPrompt: null,
    latestTurns: includeDetails ? latestTurnsFor(null, session.latestTurnPreview) : [],
    subagents: [],
    owned: session,
    available: null
  };
}

export function providerSessionLibraryRecord(
  session: AgentSession,
  includeDetails = false
): SessionLibraryRecord {
  const provider = canonicalProvider(session.provider);
  const cwd = canonicalCwd(session.projectPath);
  const machine = session.machine || 'local';
  const identity = sessionIdentityKey({ machine, provider, nativeSessionId: session.id, cwd });
  return {
    key: `provider:${identity}`,
    source: 'provider',
    machine,
    ownedId: null,
    provider,
    nativeSessionId: session.id || null,
    canonicalCwd: cwd,
    title: session.title || session.id,
    description: session.description ?? session.latestTurnPreview ?? null,
    projectGroupKey: session.projectGroupKey,
    projectGroupLabel: session.projectGroupLabel,
    model: session.model ?? null,
    state: 'resumable',
    runtimeState: null,
    lastActivity: session.lastActivity,
    updatedAt: session.lastActivity,
    messageCount: session.messageCount ?? null,
    logPath: session.logPath ?? null,
    firstPrompt: null,
    latestTurns: includeDetails ? latestTurnsFor(session.latestTurns, session.latestTurnPreview) : [],
    subagents: [],
    owned: null,
    available: includeDetails ? session : { ...session, latestTurns: undefined }
  };
}

/**
 * Merge the current owned rail and provider scanner snapshots. Owned records
 * win when the same identity appears in both, and duplicate ownedIds are also
 * collapsed before compound identity matching.
 */
export function buildSessionLibrary(
  owned: readonly OwnedSession[],
  available: readonly AgentSession[],
  options: { includeDetails?: boolean } = {}
): SessionLibraryRecord[] {
  const records: SessionLibraryRecord[] = [];
  const ownedIds = new Set<string>();
  const identities = new Map<string, SessionLibraryRecord>();

  for (const session of owned) {
    if (!session || ownedIds.has(session.ownedId)) continue;
    ownedIds.add(session.ownedId);
    const record = ownedSessionLibraryRecord(session, options.includeDetails === true);
    const identity = sessionIdentityKey({
      machine: record.machine,
      provider: record.provider,
      nativeSessionId: record.nativeSessionId,
      cwd: record.canonicalCwd
    });
    if (identities.has(identity)) continue;
    identities.set(identity, record);
    records.push(record);
  }

  for (const session of available) {
    if (!session || !session.id) continue;
    const record = providerSessionLibraryRecord(session, options.includeDetails === true);
    const identity = sessionIdentityKey({
      machine: record.machine,
      provider: record.provider,
      nativeSessionId: record.nativeSessionId,
      cwd: record.canonicalCwd
    });
    const existing = identities.get(identity);
    if (existing) {
      // The row already on the list wins, except for the transcript path: only
      // the scanner knows which file a session is being written to, so an owned
      // row that the scanner has also seen can offer its log like any other.
      existing.logPath ??= record.logPath;
      continue;
    }
    identities.set(identity, record);
    records.push(record);
  }
  return records;
}

function textFor(record: SessionLibraryRecord): string {
  return [
    record.title,
    record.description,
    record.provider,
    record.model,
    record.projectGroupLabel,
    record.canonicalCwd,
    record.nativeSessionId,
    record.state,
    record.messageCount,
    ...record.latestTurns.map((turn) => turn.text)
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
}

function includesFilter(value: string | null | undefined, filter: string | undefined): boolean {
  const needle = filter?.trim().toLowerCase();
  return !needle || (value ?? '').toLowerCase().includes(needle);
}

function dateInRange(value: string | null, from: string | null | undefined, to: string | null | undefined): boolean {
  if (!from && !to) return true;
  const stamp = value ? Date.parse(value) : Number.NaN;
  if (!Number.isFinite(stamp)) return false;
  if (from) {
    const start = Date.parse(from);
    if (Number.isFinite(start) && stamp < start) return false;
  }
  if (to) {
    const end = Date.parse(to);
    if (Number.isFinite(end) && stamp > end) return false;
  }
  return true;
}

export function filterSessionLibrary(
  records: readonly SessionLibraryRecord[],
  filters: SessionLibraryFilters = {}
): SessionLibraryRecord[] {
  const query = filters.query?.trim().toLowerCase();
  return records.filter((record) => {
    if (query && !textFor(record).includes(query)) return false;
    if (!includesFilter(record.provider, filters.provider)) return false;
    if (!includesFilter(record.projectGroupLabel, filters.project)) return false;
    if (!includesFilter(record.canonicalCwd, filters.worktree)) return false;
    if (filters.state && filters.state !== 'all' && record.state !== filters.state) return false;
    if (!includesFilter(record.model, filters.model)) return false;
    return dateInRange(record.updatedAt, filters.dateFrom, filters.dateTo);
  });
}

export function groupSessionLibrary(records: readonly SessionLibraryRecord[]): SessionLibraryGroup[] {
  const order: SessionLibraryState[] = ['working', 'resumable', 'done', 'settled'];
  const labels: Record<SessionLibraryState, string> = {
    working: 'Working',
    resumable: 'Resumable',
    done: 'Done',
    settled: 'Settled'
  };
  return order
    .map((state) => ({ state, label: labels[state], items: records.filter((record) => record.state === state) }))
    .filter((group) => group.items.length > 0);
}

/** One row click expands one details card; clicking it again closes the card. */
export function toggleSessionLibraryExpansion(
  expandedKey: string | null,
  key: string
): string | null {
  return expandedKey === key ? null : key;
}

// Friendly aliases for callers/tests that prefer “merge” terminology.
export const mergeSessionLibrary = buildSessionLibrary;
export const applySessionLibraryFilters = filterSessionLibrary;
