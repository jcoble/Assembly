import { writable } from 'svelte/store';

import type { AgentRuntimeState, OwnedSessionState } from '../ownedSessions.ts';
import type { ConversationConnectionState } from './conversationTypes.ts';

export type SessionPresence = 'working' | 'needs-attention' | 'idle' | 'disconnected';

export interface SessionPresenceHistory {
  activeTurnId: string | null;
  turnStartedAt: number | null;
  lastAttentionAt: number | null;
  lastAckedAt: number | null;
}

export interface SessionPresenceSignals {
  terminalState: OwnedSessionState;
  connectionState?: ConversationConnectionState | null;
  suspended?: boolean;
  activeTurnId?: string | null;
  sending?: boolean;
  pendingApprovalCount?: number;
  runtimeState?: AgentRuntimeState | null;
}

export interface SessionPresenceView {
  state: SessionPresence;
  elapsedMs: number | null;
}

export interface SessionPresenceEvent {
  ownedId: string;
  kind: 'turn-started' | 'turn-finished' | 'approval-requested';
  timestampMs: number;
  turnId?: string | null;
}

export interface ConversationPresenceEventLike {
  ownedId: string;
  timestampMs: number;
  type?: string;
  turnId?: string;
  payload: Record<string, unknown>;
}

export const EMPTY_SESSION_PRESENCE_HISTORY: Readonly<SessionPresenceHistory> = {
  activeTurnId: null,
  turnStartedAt: null,
  lastAttentionAt: null,
  lastAckedAt: null
};

export const sessionPresenceHistory = writable<Record<string, SessionPresenceHistory>>({});

let viewedOwnedId: string | null = null;

function timestamp(value: number): number {
  return Number.isFinite(value) && value >= 0 ? value : 0;
}

function nextAttention(history: SessionPresenceHistory, at: number, viewed: boolean): SessionPresenceHistory {
  const lastAttentionAt = Math.max(history.lastAttentionAt ?? 0, at);
  return {
    ...history,
    lastAttentionAt,
    lastAckedAt: viewed ? Math.max(history.lastAckedAt ?? 0, lastAttentionAt) : history.lastAckedAt
  };
}

export function reduceSessionPresenceHistory(
  history: SessionPresenceHistory,
  event: SessionPresenceEvent,
  viewed = false
): SessionPresenceHistory {
  const at = timestamp(event.timestampMs);
  if (event.kind === 'turn-started') {
    const turnId = event.turnId ?? null;
    if (history.activeTurnId === turnId && history.turnStartedAt !== null) return history;
    return { ...history, activeTurnId: turnId, turnStartedAt: at };
  }

  if (event.kind === 'turn-finished') {
    return nextAttention(
      { ...history, activeTurnId: null, turnStartedAt: null },
      at,
      viewed
    );
  }

  return nextAttention(history, at, viewed);
}

export function acknowledgePresenceHistory(
  history: SessionPresenceHistory,
  acknowledgedAt: number
): SessionPresenceHistory {
  return {
    ...history,
    lastAckedAt: Math.max(
      history.lastAckedAt ?? 0,
      history.lastAttentionAt ?? 0,
      timestamp(acknowledgedAt)
    )
  };
}

export function deriveSessionPresence(
  signals: SessionPresenceSignals,
  history: SessionPresenceHistory,
  nowMs: number
): SessionPresenceView {
  const unhealthy = signals.terminalState === 'exited'
    || signals.connectionState === 'failed'
    || signals.connectionState === 'closed';
  if (unhealthy) return { state: 'disconnected', elapsedMs: null };
  if (signals.suspended) return { state: 'idle', elapsedMs: null };

  const needsAttention = history.lastAttentionAt !== null
    && history.lastAttentionAt > (history.lastAckedAt ?? -1);

  const disconnected = signals.connectionState === 'disconnected'
    && signals.runtimeState !== 'starting';
  if (disconnected) return { state: 'disconnected', elapsedMs: null };

  const waitingForApproval = (signals.pendingApprovalCount ?? 0) > 0;
  const working = !waitingForApproval && (
    !!signals.activeTurnId
    || signals.sending === true
    || signals.runtimeState === 'working'
  );
  if (!working) return { state: needsAttention ? 'needs-attention' : 'idle', elapsedMs: null };

  const startedAt = history.turnStartedAt ?? nowMs;
  return { state: 'working', elapsedMs: Math.max(0, timestamp(nowMs) - startedAt) };
}

export function formatPresenceElapsed(elapsedMs: number): string {
  const totalSeconds = Math.max(0, Math.floor(elapsedMs / 1_000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
}

export function sessionPresenceEventFromConversation(
  event: ConversationPresenceEventLike
): SessionPresenceEvent | null {
  const payloadKind = typeof event.payload.kind === 'string' ? event.payload.kind : '';
  const payloadState = typeof event.payload.state === 'string' ? event.payload.state : '';
  const turnId = event.turnId
    ?? (typeof event.payload.turnId === 'string' ? event.payload.turnId : null);

  if (event.type === 'turn.started' || (payloadKind === 'turn' && payloadState === 'started')) {
    return { ownedId: event.ownedId, kind: 'turn-started', timestampMs: event.timestampMs, turnId };
  }
  if (
    event.type === 'turn.completed'
    || event.type === 'turn.interrupted'
    || (payloadKind === 'turn' && payloadState !== 'started')
  ) {
    return { ownedId: event.ownedId, kind: 'turn-finished', timestampMs: event.timestampMs, turnId };
  }
  if (
    event.type === 'approval.requested'
    || payloadKind === 'permissionRequest'
    || (payloadKind === 'approval' && payloadState === 'requested')
  ) {
    return { ownedId: event.ownedId, kind: 'approval-requested', timestampMs: event.timestampMs, turnId };
  }
  return null;
}

export function recordSessionPresenceEvent(event: SessionPresenceEvent): void {
  sessionPresenceHistory.update((records) => {
    const current = records[event.ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY;
    const next = reduceSessionPresenceHistory(current, event, viewedOwnedId === event.ownedId);
    return next === current ? records : { ...records, [event.ownedId]: next };
  });
}

export function recordConversationPresenceEvent(event: ConversationPresenceEventLike): void {
  const presenceEvent = sessionPresenceEventFromConversation(event);
  if (presenceEvent) recordSessionPresenceEvent(presenceEvent);
}

export function synchronizeSessionPresenceWork(
  ownedId: string,
  activeTurnId: string | null | undefined,
  sending: boolean,
  at: number | null = Date.now()
): void {
  sessionPresenceHistory.update((records) => {
    const current = records[ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY;
    if (!activeTurnId && !sending) {
      if (current.activeTurnId === null && current.turnStartedAt === null && records[ownedId]) return records;
      return { ...records, [ownedId]: { ...current, activeTurnId: null, turnStartedAt: null } };
    }
    const turnId = activeTurnId ?? current.activeTurnId ?? 'sending';
    if (current.activeTurnId === turnId && current.turnStartedAt !== null) return records;
    return {
      ...records,
      [ownedId]: { ...current, activeTurnId: turnId, turnStartedAt: at === null ? null : timestamp(at) }
    };
  });
}

export function acknowledgeSessionPresence(ownedId: string, at = Date.now()): void {
  sessionPresenceHistory.update((records) => {
    const current = records[ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY;
    if (current.lastAttentionAt === null || current.lastAttentionAt <= (current.lastAckedAt ?? -1)) {
      return records;
    }
    return { ...records, [ownedId]: acknowledgePresenceHistory(current, at) };
  });
}

export function setViewedSession(ownedId: string | null, at = Date.now()): void {
  viewedOwnedId = ownedId;
  if (ownedId) acknowledgeSessionPresence(ownedId, at);
}

export function clearViewedSession(ownedId: string): void {
  if (viewedOwnedId === ownedId) viewedOwnedId = null;
}

export function clearSessionPresence(ownedId?: string): void {
  if (ownedId) {
    sessionPresenceHistory.update((records) => {
      if (!(ownedId in records)) return records;
      const next = { ...records };
      delete next[ownedId];
      return next;
    });
    if (viewedOwnedId === ownedId) viewedOwnedId = null;
    return;
  }
  viewedOwnedId = null;
  sessionPresenceHistory.set({});
}
