export interface PerfHarnessSessionRecord {
  ownedId: string;
  provider: 'codex' | 'claude';
  model: string;
  effort: string;
  cwd: string;
  state: 'ready' | 'working' | 'suspended';
  suspended: boolean;
  createdAtMs: number;
  lastActivityAtMs: number;
  activeTurnId: string | null;
  pendingPermission: boolean;
  pendingInput: boolean;
  nativeSessionId: string;
  worktree: string;
  branch: string;
  title: string;
  project: string;
  ptySessionId: null;
  origin: 'app';
  source: 'fresh';
  viaCmux: boolean;
  resumeCommand: null;
  completedAt: string | null;
  settledAt: string | null;
  taskId: string;
  pullRequest: null;
  messageCount: number;
  latestTurnPreview: string;
  scannedLastActivity: string;
}

export interface PerfHarnessEvent {
  ownedId: string;
  provider: 'codex';
  generation: number;
  sequence: number;
  timestampMs: number;
  payload: Record<string, unknown>;
}

export interface PerfHarnessSnapshot {
  connection: {
    ownedId: string;
    provider: 'codex';
    generation: number;
    state: 'connected';
    nativeSessionId: string;
  };
  suspended: false;
  lastSequence: number;
  events: PerfHarnessEvent[];
}

export interface PerfHarnessMockPayload {
  sessions: PerfHarnessSessionRecord[];
  snapshots: Record<string, PerfHarnessSnapshot>;
}

const SESSION_COUNT = 15;
const LARGE_EVENT_COUNT = 2_000;

function eventPayload(index: number): Record<string, unknown> {
  if (index % 2 === 0) {
    return {
      kind: 'userMessage',
      itemId: `user-${index}`,
      text: `Investigate performance sample ${index} and keep the response grounded in measurements.`,
      completed: true
    };
  }
  return {
    kind: 'assistantMessage',
    itemId: `assistant-${index}`,
    text: `Measured response ${index}. The profile identifies repeated timeline projection work and preserves a concrete receipt for the next run.`,
    completed: true
  };
}

function snapshotFor(ownedId: string, eventCount: number): PerfHarnessSnapshot {
  const base = Date.now() - eventCount * 7;
  const events = Array.from({ length: eventCount }, (_, index): PerfHarnessEvent => ({
    ownedId,
    provider: 'codex',
    generation: 1,
    sequence: index + 1,
    timestampMs: base + index * 7,
    payload: eventPayload(index)
  }));
  return {
    connection: {
      ownedId,
      provider: 'codex',
      generation: 1,
      state: 'connected',
      nativeSessionId: `native-${ownedId}`
    },
    suspended: false,
    lastSequence: eventCount,
    events
  };
}

export function createPerfHarnessMockPayload(): PerfHarnessMockPayload {
  const now = Date.now();
  const sessions = Array.from({ length: SESSION_COUNT }, (_, index): PerfHarnessSessionRecord => {
    const ownedId = `perf-session-${String(index + 1).padStart(2, '0')}`;
    const largeTranscript = index % 6 === 0;
    const working = index % 5 === 0;
    const suspended = index % 5 === 3;
    return {
      ownedId,
      provider: index % 4 === 0 ? 'claude' : 'codex',
      model: index % 4 === 0 ? 'claude-sonnet-4-5' : 'gpt-5.6-codex',
      effort: 'medium',
      cwd: `/tmp/perf-worktree-${index + 1}`,
      state: suspended ? 'suspended' : working ? 'working' : 'ready',
      suspended,
      createdAtMs: now - (index + 1) * 83_000,
      lastActivityAtMs: now - index * 31_000,
      activeTurnId: working ? `turn-${index}` : null,
      pendingPermission: index === 7,
      pendingInput: false,
      nativeSessionId: `native-${ownedId}`,
      worktree: `/tmp/perf-worktree-${index + 1}`,
      branch: `lane/perf-${index + 1}`,
      title: index === 0 ? 'Large stored transcript' : `Performance session ${index + 1}`,
      project: '/tmp/mac-command-bar',
      ptySessionId: null,
      origin: 'app',
      source: 'fresh',
      viaCmux: false,
      resumeCommand: null,
      completedAt: index === 11 ? new Date(now - 86_000).toISOString() : null,
      settledAt: index === 14 ? new Date(now - 42_000).toISOString() : null,
      taskId: `TSK-${900 + index}`,
      pullRequest: null,
      messageCount: largeTranscript ? LARGE_EVENT_COUNT : 40,
      latestTurnPreview: largeTranscript ? 'Agent: stored 2k-event transcript' : `Agent: sample ${index + 1}`,
      scannedLastActivity: new Date(now - index * 31_000).toISOString()
    };
  });
  return {
    sessions,
    snapshots: Object.fromEntries(sessions.map((session, index) => [
      session.ownedId,
      snapshotFor(session.ownedId, index % 6 === 0 ? LARGE_EVENT_COUNT : 40)
    ]))
  };
}
