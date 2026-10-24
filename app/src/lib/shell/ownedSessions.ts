/**
 * Owned-session identity and lifecycle records for the /next shell.
 *
 * ownedId (CommandBar-minted) is the PRIMARY key across rail, manager,
 * persistence, and active-tracking. Provider ids are mutable (a resume mints a
 * new one), so they are carried but never used as keys.
 */
import type {
  AgentConversationSessionMeta,
  AgentConversationSessionRecord,
  AgentSession,
  BackgroundWorkItem,
  ExecutionEnvironment,
  TerminalSessionInfo
} from '../tauriSource';

export type AgentKind = 'codex' | 'claude' | 'antigravity' | 'gemini' | 'opencode' | 'other';
export type OwnedSessionState = 'live' | 'background' | 'exited';

export type AgentExecutionOwner =
  | 'structured'
  | 'terminal'
  | 'transitioning-to-structured'
  | 'transitioning-to-terminal'
  | 'stopped';

export type AgentRuntimeState =
  | 'starting'
  | 'ready'
  | 'working'
  | 'waiting-approval'
  | 'waiting-input'
  | 'interrupting'
  | 'suspended'
  | 'failed'
  | 'closed';

export interface OwnedAgentRuntimeFields {
  executionOwner: AgentExecutionOwner; canChangeCheckout: boolean; // Provider support only; quiescence is checked separately.
  runtimeState: AgentRuntimeState;
  providerInstanceId: string | null;
  nativeSessionId: string | null;
  activeTurnId: string | null;
  pendingPermission?: boolean;
  pendingInput?: boolean;
  backgroundWork: BackgroundWorkItem[];
  capabilityRevision: number;
  lastRuntimeError: string | null;
}

export type OwnedSession = Omit<Partial<OwnedAgentRuntimeFields>, 'nativeSessionId'> & {
  ownedId: string;
  executionEnvironment: ExecutionEnvironment;
  remoteProfileId?: string | null;
  agent: AgentKind;
  /** App-created agent sessions use ACP; adopted sessions stay PTY projections. */
  origin?: 'app' | 'external';
  viaCmux: boolean;
  source: 'scanned' | 'fresh';
  title: string;
  /** The permanent handle (`brisk-heron`) other sessions message this one by. */
  handle?: string | null;
  /** Model reported by the session scanner, when one was available. */
  model?: string | null;
  /** The registry project the session was started in; null for older sessions and "No project". */
  projectId: string | null;
  /** The rail group SQL computed from projectId: `repo:<key>`, `project:<id>` or `none`, and its label. */
  projectGroupKey: string;
  projectGroupLabel: string;
  cwd: string;
  resumeCommand: string | null;
  nativeSessionId: string | null;
  ptySessionId: string | null;
  state: OwnedSessionState;
  /** The most recent app-owned ACP start failure, retained for an in-rail retry. */
  lastError?: string | null;
  /**
   * When this session was marked done, as an ISO stamp; `null` while it is
   * still being worked on.
   *
   * Deliberately NOT touched by anything that happens to the terminal. A process
   * exiting says the terminal is over, not that the work is; a session whose
   * agent has finished stays open until marked done. The user's "Mark done" and
   * "Reopen" write this field; a helper's backend may also mark it done once,
   * after the configured quiet period (one hour by default).
   */
  completedAt: string | null;
  /**
   * When the user archived this session out of the Done list into Settled, as
   * an ISO stamp; `null` while it still belongs in Working or Done. Written
   * only by the user's explicit "Settle"/"Unsettle" actions — never inferred
   * from age, title, or process state. Old records without the field load as
   * `null`.
   */
  settledAt: string | null;
  /** When the user pinned this session to the top of the rail; absent or `null` when unpinned. */
  pinnedAt?: string | null;
  /** When the owner marked this session as the main orchestrator; absent or `null` otherwise. */
  orchestratorMarkedAtMs?: number | null;
  /** The session that started this helper; the rail draws it under that parent. */
  helperOf?: string | null;
  /** The workflow run this worker belongs to; the rail groups a run's workers under its label. */
  runId?: string | null;
  runLabel?: string | null;
  /** The session that started this worker's run; the rail counts the run on that session's row. */
  runLead?: string | null;
  /**
   * What the scanner worked out about the session — the branch it is on, the
   * task it belongs to, the pull request it opened. Copied off the scanned
   * record when the session is adopted so a row keeps showing them afterwards,
   * and `null` whenever the scanner found nothing or the session was started
   * here rather than found on disk.
   */
  branch: string | null;
  taskId: string | null;
  pullRequest: string | null;
  /**
   * How many turns of the conversation the scanner saw, and the last one of them
   * already written as the row shows it (`You: …` / `Agent: …`). Copied off the
   * scanned record when the session is adopted, so the row keeps showing them
   * afterwards.
   *
   * The count is a floor, not a total — the scanner reads a bounded window of
   * each transcript. `null` whenever the scanner found no conversation, or the
   * session was started here rather than found on disk.
   */
  messageCount: number | null;
  latestTurnPreview: string | null;
  /**
   * When the scanner last saw anything happen in this conversation, as the
   * stamp it reported. Copied off the scanned record when the session is
   * adopted, so a row can say how old the work is.
   *
   * It is a reading of the transcript on disk taken at adopt time and nothing
   * moves it afterwards — a session started here has none at all. `null`
   * whenever the scanner had nothing to say, and the row then shows no stamp.
   */
  lastActivity: string | null;
  /**
   * When this session was started, in milliseconds since the epoch, as the
   * stored record reports it. The rail shows the row's age from it, which is
   * why it survives a turn ending. `null` for a session the app has not
   * persisted yet, and for one adopted off disk where nothing recorded a start.
   */
  startedAtMs?: number | null;
};

// ── Background work, in words ────────────────────────────────────────────────

/** The rail's count of running work, e.g. "1 sub-agent · 1 command running"; null when none runs. */
export function backgroundWorkSummary(work: readonly BackgroundWorkItem[]): string | null {
  const subagents = work.filter((item) => item.kind === 'subagent').length;
  const commands = work.length - subagents;
  const count = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
  if (subagents && commands) return `${count(subagents, 'sub-agent')} · ${count(commands, 'command')} running`;
  if (subagents) return `${count(subagents, 'sub-agent')} running`;
  if (commands) return `${count(commands, 'background command')} running`;
  return null;
}

// ── What a rail row counts ───────────────────────────────────────────────────

/** True while the session waits on the owner: a permission prompt or a question. */
export function sessionWaitsOnYou(session: OwnedSession): boolean {
  return session.pendingPermission === true || session.pendingInput === true
    || session.runtimeState === 'waiting-approval' || session.runtimeState === 'waiting-input';
}

// Not 'starting': a session whose start failed can keep that state on the rail.
const BUSY: readonly (AgentRuntimeState | undefined)[] = ['working', 'interrupting', 'waiting-approval', 'waiting-input'];

/** What one rail row's status icons show. `waiting` says each wait in words. */
export interface RailRowStatus { waiting: string[]; background: number; helpers: number; runs: number }

/**
 * Every rail row's status icons, built in one pass over the rail's records
 * and keyed by ownedId: who waits on the owner (the session itself, its
 * helpers, and the workers of runs it started), its background commands, its
 * running helpers and sub-agents, and its workflow runs with a worker at work.
 * A run counts only while a worker is busy: the engine leaves a finished
 * step's session open, so an open session is no sign that its run is still
 * going. A running helper also appears in the parent's background work as a
 * sub-agent; it is counted once.
 */
export function railRowStatuses(all: readonly OwnedSession[]): Map<string, RailRowStatus> {
  const asking = (other: OwnedSession) => other.pendingPermission || other.runtimeState === 'waiting-approval'
    ? 'is asking for approval' : 'is waiting for an answer';
  const children = new Map<string, { waiting: string[]; helpers: number; helperIds: Set<string>; runs: Set<string> }>();
  const childrenOf = (ownedId: string) => {
    let entry = children.get(ownedId);
    if (!entry) children.set(ownedId, entry = { waiting: [], helpers: 0, helperIds: new Set(), runs: new Set() });
    return entry;
  };
  for (const other of all) {
    const helperOf = other.helperOf;
    const runLead = other.runId && other.runLead !== helperOf ? other.runLead : null;
    if (!helperOf && !runLead) continue;
    const name = other.title || other.handle || other.ownedId.slice(0, 8);
    const waits = sessionWaitsOnYou(other);
    const busy = BUSY.includes(other.runtimeState);
    if (helperOf) {
      const parent = childrenOf(helperOf);
      parent.helperIds.add(`claude-child:${other.ownedId}`);
      if (waits) parent.waiting.push(`helper session ${name} ${asking(other)}`);
      if (busy) parent.helpers += 1;
    }
    if (runLead) {
      const lead = childrenOf(runLead);
      if (waits) lead.waiting.push(`${name} in ${other.runLabel ?? 'a workflow run'} ${asking(other)}`);
      if (busy) lead.runs.add(other.runId!);
    }
  }
  const statuses = new Map<string, RailRowStatus>();
  for (const session of all) {
    const own = children.get(session.ownedId);
    const work = session.backgroundWork ?? [];
    const subagents = work.filter((item) => item.kind === 'subagent');
    const nativeSubagents = subagents.filter((item) => !own?.helperIds.has(item.id)).length;
    statuses.set(session.ownedId, {
      waiting: [...(sessionWaitsOnYou(session) ? [`this session ${asking(session)}`] : []), ...(own?.waiting ?? [])],
      background: work.length - subagents.length,
      helpers: (own?.helpers ?? 0) + nativeSubagents,
      runs: own?.runs.size ?? 0,
    });
  }
  return statuses;
}

/** The item's own label, or what kind of work it is when it has none. */
export function backgroundWorkLabel(item: BackgroundWorkItem): string {
  return item.label.trim() || (item.kind === 'subagent' ? 'Sub-agent' : 'Background command');
}

/** How long work has run: "14s", "2m 14s", then "1h 5m". */
export function formatBackgroundElapsed(elapsedMs: number): string {
  const seconds = Math.max(0, Math.floor(elapsedMs / 1_000));
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3_600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3_600)}h ${Math.floor((seconds % 3_600) / 60)}m`;
}

/** Where the chat line's link goes: the child in the Agents panel, or the command's tool row. */
export function backgroundWorkTarget(item: BackgroundWorkItem):
  | { kind: 'child'; childId: string }
  | { kind: 'tool'; itemId: string } {
  return item.kind === 'subagent'
    ? { kind: 'child', childId: item.id.replace(/^claude-child:/, '') }
    : { kind: 'tool', itemId: item.id };
}

/** Sessions whose work stops if this app quits. Remote work keeps running on its server. */
export function localBackgroundWorkSessions(sessions: readonly OwnedSession[]): OwnedSession[] {
  return sessions.filter((session) => session.executionEnvironment !== 'remote'
    && (session.backgroundWork?.length ?? 0) > 0);
}

const KNOWN_AGENTS: AgentKind[] = ['codex', 'claude', 'antigravity', 'gemini', 'opencode'];
const KNOWN_STATES: OwnedSessionState[] = ['live', 'background', 'exited'];
const KNOWN_EXECUTION_OWNERS: AgentExecutionOwner[] = [
  'structured',
  'terminal',
  'transitioning-to-structured',
  'transitioning-to-terminal',
  'stopped'
];
const KNOWN_RUNTIME_STATES: AgentRuntimeState[] = [
  'starting',
  'ready',
  'working',
  'waiting-approval',
  'waiting-input',
  'interrupting',
  'suspended',
  'failed',
  'closed'
];

function defaultMintId(): string {
  return globalThis.crypto.randomUUID();
}

export function normalizeProvider(provider: string): { agent: AgentKind; viaCmux: boolean } {
  const value = (provider ?? '').trim().toLowerCase();
  const viaCmux = value.startsWith('cmux-');
  const base = (viaCmux ? value.slice('cmux-'.length) : value).replaceAll(/[\s_-]+/g, '');
  const agent = ['anthropic', 'claudecode'].includes(base) ? 'claude' : base === 'openai' ? 'codex' : base === 'agy' ? 'antigravity' : base;
  return { agent: (KNOWN_AGENTS as string[]).includes(agent) ? (agent as AgentKind) : 'other', viaCmux };
}

export function adoptAgentSession(record: AgentSession, mintId: () => string = defaultMintId): OwnedSession {
  const { agent, viaCmux } = normalizeProvider(record.provider);
  return {
    ownedId: mintId(),
    executionEnvironment: 'local',
    remoteProfileId: null,
    agent,
    origin: 'external',
    viaCmux,
    source: 'scanned',
    title: record.title,
    model: isNonEmptyString(record.model) ? record.model : null,
    projectId: null,
    projectGroupKey: record.projectGroupKey,
    projectGroupLabel: record.projectGroupLabel,
    cwd: record.projectPath ?? '',
    resumeCommand: record.resumeCommands[0] ?? null,
    nativeSessionId: record.id,
    ptySessionId: null,
    state: 'background',
    lastError: null,
    executionOwner: 'stopped',
    runtimeState: 'closed',
    providerInstanceId: null,
    activeTurnId: null,
    capabilityRevision: 0,
    lastRuntimeError: null,
    completedAt: null,
    settledAt: null,
    branch: isNonEmptyString(record.branchHint) ? record.branchHint : null,
    taskId: isNonEmptyString(record.taskId) ? record.taskId : null,
    pullRequest: isNonEmptyString(record.pullRequestHint) ? record.pullRequestHint : null,
    messageCount: isTurnCount(record.messageCount) ? record.messageCount : null,
    latestTurnPreview: isNonEmptyString(record.latestTurnPreview) ? record.latestTurnPreview : null,
    lastActivity: isNonEmptyString(record.lastActivity) ? record.lastActivity : null,
  };
}

export function createFreshSession(
  opts: { cwd: string; title?: string; executionEnvironment?: ExecutionEnvironment; remoteProfileId?: string | null },
  mintId: () => string = defaultMintId
): OwnedSession {
  const segments = opts.cwd.split('/').filter((segment) => segment.length > 0);
  const defaultTitle = segments.length > 0 ? segments[segments.length - 1] : opts.cwd;
  return {
    ownedId: mintId(),
    executionEnvironment: opts.executionEnvironment ?? 'local',
    remoteProfileId: opts.remoteProfileId ?? null,
    agent: 'other',
    origin: 'external',
    viaCmux: false,
    source: 'fresh',
    title: opts.title ?? defaultTitle,
    model: null,
    projectId: null,
    projectGroupKey: 'none',
    projectGroupLabel: 'No project',
    cwd: opts.cwd,
    resumeCommand: null,
    nativeSessionId: null,
    ptySessionId: null,
    state: 'background',
    lastError: null,
    executionOwner: 'stopped',
    runtimeState: 'closed',
    providerInstanceId: null,
    activeTurnId: null,
    capabilityRevision: 0,
    lastRuntimeError: null,
    completedAt: null,
    settledAt: null,
    branch: null,
    taskId: null,
    pullRequest: null,
    messageCount: null,
    latestTurnPreview: null,
    lastActivity: new Date().toISOString(),
  };
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

/** The session lists `findSessionRecord` reads: this machine, then every connected one. */
export interface SessionRecordReaders {
  local: () => Promise<AgentConversationSessionRecord[] | null>;
  remote: (signal: AbortSignal) => Promise<AgentConversationSessionRecord[] | null>;
}

/** A session the rail has not seen yet (a helper or workflow worker), on any machine. */
export async function findSessionRecord(
  ownedId: string,
  readers: SessionRecordReaders,
  signal: AbortSignal
): Promise<AgentConversationSessionRecord | null> {
  const local = (await readers.local())?.find((record) => record.ownedId === ownedId);
  if (signal.aborted) return null;
  // A cached remote row may be stale (a pending permission, say); its machine has the live one.
  if (local && !local.remoteProfileId) return local;
  let remote: AgentConversationSessionRecord | undefined;
  try {
    remote = (await readers.remote(signal))?.find((record) => record.ownedId === ownedId);
  } catch (error) {
    // With no machine answering, the cached row is still the session.
    if (!local || signal.aborted) throw error;
  }
  return signal.aborted ? null : remote ?? local ?? null;
}

export function ownedSessionFromBackend(record: AgentConversationSessionRecord): OwnedSession {
  const { agent, viaCmux: providerViaCmux } = normalizeProvider(record.provider);
  const terminalOwned = record.origin === 'external' && record.ptySessionId !== null;
  return {
    ownedId: record.ownedId, canChangeCheckout: record.canChangeCheckout,
    executionEnvironment: record.executionEnvironment,
    remoteProfileId: record.remoteProfileId,
    agent,
    origin: record.origin ?? 'app',
    viaCmux: record.viaCmux || providerViaCmux,
    source: record.source ?? 'fresh',
    title: record.title ?? '',
    handle: record.handle ?? null,
    model: record.model,
    projectId: record.projectId ?? null,
    // Both are left out of the record when empty, which only happens for a record that skipped the
    // desktop's grouping pass; it reads as "No project" until the next list.
    projectGroupKey: record.projectGroupKey || 'none',
    projectGroupLabel: record.projectGroupLabel || 'No project',
    cwd: record.cwd,
    resumeCommand: record.resumeCommand,
    nativeSessionId: record.nativeSessionId,
    ptySessionId: record.ptySessionId,
    state: record.activeTurnId === null ? 'background' : 'live',
    lastError: null,
    executionOwner: terminalOwned ? 'terminal' : 'structured',
    runtimeState: record.suspended ? 'suspended' : record.state,
    providerInstanceId: null,
    activeTurnId: record.activeTurnId,
    pendingPermission: record.pendingPermission,
    pendingInput: record.pendingInput,
    backgroundWork: record.backgroundWork,
    capabilityRevision: 0,
    lastRuntimeError: null,
    completedAt: record.completedAt,
    settledAt: record.settledAt,
    pinnedAt: record.pinnedAt ?? null,
    orchestratorMarkedAtMs: record.orchestratorMarkedAtMs ?? null,
    helperOf: record.helperOf ?? null,
    runId: record.runId ?? null,
    runLabel: record.runLabel ?? null,
    runLead: record.runLead ?? null,
    // The rail prints the branch straight into the row, so anything that is not
    // a name has to stop here rather than reach the row as "[object Object]".
    // The two constructors below already read it this way.
    branch: isNonEmptyString(record.branch) ? record.branch : null,
    taskId: record.taskId,
    pullRequest: record.pullRequest,
    messageCount: record.messageCount,
    latestTurnPreview: record.latestTurnPreview,
    lastActivity: record.scannedLastActivity,
    startedAtMs: Number.isFinite(record.createdAtMs) ? record.createdAtMs : null
  };
}

export function ownedSessionMetaForBackend(session: OwnedSession): AgentConversationSessionMeta {
  return {
    worktree: session.cwd || null,
    branch: session.branch,
    title: session.title || null,
    project: null,
    ptySessionId: session.ptySessionId,
    origin: session.origin ?? null,
    source: session.source,
    viaCmux: session.viaCmux,
    resumeCommand: session.resumeCommand,
    completedAt: session.completedAt,
    settledAt: session.settledAt,
    pinnedAt: session.pinnedAt ?? null,
    taskId: session.taskId,
    pullRequest: session.pullRequest,
    messageCount: session.messageCount,
    latestTurnPreview: session.latestTurnPreview,
    scannedLastActivity: session.lastActivity
  };
}

/**
 * A count of turns worth showing: a whole number, at least one. Zero is nothing
 * to say rather than something to print, so a row shows no count at all instead
 * of "0 messages".
 */
function isTurnCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0;
}

export function parseStoredOwnedSessions(raw: string | null): OwnedSession[] {
  if (!raw) {
    return [];
  }
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return [];
  }
  if (!Array.isArray(data)) {
    return [];
  }
  const result: OwnedSession[] = [];
  for (const entry of data) {
    if (typeof entry !== 'object' || entry === null) {
      continue;
    }
    const candidate = entry as Record<string, unknown>;
    if (!isNonEmptyString(candidate.ownedId) || !isNonEmptyString(candidate.cwd)) {
      continue;
    }
    const agent = (KNOWN_AGENTS as string[]).includes(candidate.agent as string)
      ? (candidate.agent as AgentKind)
      : 'other';
    const state = (KNOWN_STATES as string[]).includes(candidate.state as string)
      ? (candidate.state as OwnedSessionState)
      : 'exited';
    const origin = candidate.origin === 'app' ? 'app' : 'external';
    const ptySessionId = isNonEmptyString(candidate.ptySessionId) ? candidate.ptySessionId : null;
    const executionOwner = (KNOWN_EXECUTION_OWNERS as string[]).includes(
      candidate.executionOwner as string
    )
      ? (candidate.executionOwner as AgentExecutionOwner)
      : ptySessionId && state !== 'exited'
        ? 'terminal'
        : 'stopped';
    const runtimeState = (KNOWN_RUNTIME_STATES as string[]).includes(candidate.runtimeState as string)
      ? (candidate.runtimeState as AgentRuntimeState)
      : executionOwner === 'terminal'
        ? 'ready'
        : 'closed';
    result.push({
      ownedId: candidate.ownedId,
      executionEnvironment:
        candidate.executionEnvironment === 'remote' ? 'remote' : 'local',
      remoteProfileId: isNonEmptyString(candidate.remoteProfileId) ? candidate.remoteProfileId : null,
      agent,
      origin,
      viaCmux: candidate.viaCmux === true,
      source: candidate.source === 'fresh' ? 'fresh' : 'scanned',
      title: isNonEmptyString(candidate.title) ? candidate.title : '',
      model: isNonEmptyString(candidate.model) ? candidate.model : null,
      projectId: isNonEmptyString(candidate.projectId) ? candidate.projectId : null,
      projectGroupKey: isNonEmptyString(candidate.projectGroupKey) ? candidate.projectGroupKey : 'none',
      projectGroupLabel: isNonEmptyString(candidate.projectGroupLabel) ? candidate.projectGroupLabel : 'No project',
      cwd: candidate.cwd,
      resumeCommand: isNonEmptyString(candidate.resumeCommand) ? candidate.resumeCommand : null,
      nativeSessionId: isNonEmptyString(candidate.nativeSessionId) ? candidate.nativeSessionId : null,
      ptySessionId,
      state,
      lastError: isNonEmptyString(candidate.lastError) ? candidate.lastError : null,
      executionOwner,
      runtimeState,
      providerInstanceId: isNonEmptyString(candidate.providerInstanceId)
        ? candidate.providerInstanceId
        : null,
      activeTurnId: isNonEmptyString(candidate.activeTurnId) ? candidate.activeTurnId : null,
      capabilityRevision: nonNegativeInteger(candidate.capabilityRevision) ?? 0,
      lastRuntimeError: isNonEmptyString(candidate.lastRuntimeError)
        ? candidate.lastRuntimeError
        : null,
      // Sessions saved before this field existed have no value here at all, and
      // that reads exactly like "not done" — which is the right answer for them.
      completedAt: isNonEmptyString(candidate.completedAt) ? candidate.completedAt : null,
      // Same "not there means not settled" reading for records saved before
      // the Settled shelf existed.
      settledAt: isNonEmptyString(candidate.settledAt) ? candidate.settledAt : null,
      // Same story here: a session saved before the scanner sent these, or one
      // the scanner had nothing to say about, simply shows no chips.
      branch: isNonEmptyString(candidate.branch) ? candidate.branch : null,
      taskId: isNonEmptyString(candidate.taskId) ? candidate.taskId : null,
      pullRequest: isNonEmptyString(candidate.pullRequest) ? candidate.pullRequest : null,
      messageCount: isTurnCount(candidate.messageCount) ? candidate.messageCount : null,
      latestTurnPreview: isNonEmptyString(candidate.latestTurnPreview)
        ? candidate.latestTurnPreview
        : null,
      lastActivity: isNonEmptyString(candidate.lastActivity) ? candidate.lastActivity : null,
    });
  }
  return result;
}

function nonNegativeInteger(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : undefined;
}

export function reconcileOwnedSessions(
  stored: OwnedSession[],
  live: Pick<TerminalSessionInfo, 'sessionId' | 'exited'>[]
): { owned: OwnedSession[]; reattachable: OwnedSession[] } {
  const liveById = new Map(live.map((entry) => [entry.sessionId, entry]));
  const owned: OwnedSession[] = [];
  const reattachable: OwnedSession[] = [];

  for (const session of stored) {
    // Structured sessions do not own a PTY. A reload must leave their native
    // record resumable instead of treating the absent terminal as proof that
    // the session stopped; otherwise the row paints the stopped affordance and
    // a later restart keeps the stale stopped owner.
    if (session.ptySessionId === null) {
      const failed = session.state === 'exited' && (session.runtimeState === 'failed' || session.lastError !== null);
      if (failed) {
        owned.push({ ...session, state: 'exited', executionOwner: 'stopped', runtimeState: 'failed' });
      } else {
        owned.push({ ...session, state: 'background', executionOwner: 'structured', runtimeState: 'closed' });
      }
      continue;
    }

    const match = session.ptySessionId ? liveById.get(session.ptySessionId) : undefined;
    if (match && !match.exited) {
      const next: OwnedSession = {
        ...session,
        state: 'background',
        executionOwner: 'terminal',
        runtimeState: 'ready'
      };
      owned.push(next);
      reattachable.push(next);
    } else if (match && match.exited) {
      owned.push({ ...session, state: 'exited', executionOwner: 'stopped', runtimeState: 'closed' });
    } else {
      // Keep the terminal id so a failed migration can be retried or diagnosed.
      owned.push({ ...session, state: 'exited', executionOwner: 'stopped', runtimeState: 'closed' });
    }
  }

  return { owned, reattachable };
}
