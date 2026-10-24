import { get } from 'svelte/store';

import type { AgentConversationProvider } from './conversationTypes.ts';
import {
  EMPTY_SESSION_PRESENCE_HISTORY,
  recordConversationPresenceEvent as recordPresenceEvent,
  sessionPresenceEventFromConversation,
  sessionPresenceHistory,
  type ConversationPresenceEventLike,
  type SessionPresenceHistory
} from './sessionPresence.ts';

export type SessionAttentionKind = 'turn-finished' | 'approval-requested';

export interface SessionNotification {
  title: string;
  body: string;
}

export interface SessionNotificationTransition {
  ownedId: string;
  kind: SessionAttentionKind;
  previous: SessionPresenceHistory;
  next: SessionPresenceHistory;
  title: string | null;
  provider: string;
  sessionViewed: boolean;
  windowFocused: boolean;
}

export type SessionNotificationSender = (
  notification: SessionNotification
) => void | Promise<void>;

function needsAttention(history: SessionPresenceHistory): boolean {
  return history.lastAttentionAt !== null
    && history.lastAttentionAt > (history.lastAckedAt ?? -1);
}

export function enteredNeedsAttention(
  previous: SessionPresenceHistory,
  next: SessionPresenceHistory
): boolean {
  return !needsAttention(previous) && needsAttention(next);
}

export function sessionNotificationBody(kind: SessionAttentionKind): string {
  return kind === 'approval-requested'
    ? 'Waiting for approval'
    : 'Finished — needs your attention';
}

function providerTitle(provider: string): string {
  const trimmed = provider.trim();
  return trimmed.length === 0
    ? 'Agent session'
    : `${trimmed[0].toUpperCase()}${trimmed.slice(1)}`;
}

export function notificationForAttentionTransition(
  transition: SessionNotificationTransition
): SessionNotification | null {
  if (!enteredNeedsAttention(transition.previous, transition.next)) return null;
  if (transition.sessionViewed && transition.windowFocused) return null;
  const title = transition.title?.trim() || providerTitle(transition.provider);
  return { title, body: sessionNotificationBody(transition.kind) };
}

export function createSessionNotificationDispatcher(
  sender: SessionNotificationSender
): (transition: SessionNotificationTransition) => Promise<boolean> {
  return async (transition) => {
    const notification = notificationForAttentionTransition(transition);
    if (!notification) return false;
    await sender(notification);
    return true;
  };
}

type NotificationPlugin = typeof import('@tauri-apps/plugin-notification');

let notificationPlugin: Promise<NotificationPlugin> | null = null;
let notificationPermission: Promise<boolean> | null = null;

async function loadNotificationPlugin(): Promise<NotificationPlugin> {
  notificationPlugin ??= importNotificationPlugin();
  const plugin = await notificationPlugin;
  return plugin;
}

async function importNotificationPlugin(): Promise<NotificationPlugin> {
  return await import('@tauri-apps/plugin-notification');
}

async function notificationPermissionGranted(): Promise<boolean> {
  if (notificationPermission) {
    const granted = await notificationPermission;
    return granted;
  }
  notificationPermission = requestNotificationPermission();
  const granted = await notificationPermission;
  return granted;
}

async function requestNotificationPermission(): Promise<boolean> {
  const plugin = await loadNotificationPlugin();
  if (await plugin.isPermissionGranted()) return true;
  return (await plugin.requestPermission()) === 'granted';
}

export const sendSystemNotification: SessionNotificationSender = async (notification) => {
  if (!(await notificationPermissionGranted())) return;
  const plugin = await loadNotificationPlugin();
  plugin.sendNotification(notification);
};

const dispatchSystemNotification = createSessionNotificationDispatcher(sendSystemNotification);

async function runtimeTransition(
  event: ConversationPresenceEventLike & { provider: AgentConversationProvider },
  kind: SessionAttentionKind,
  previous: SessionPresenceHistory,
  next: SessionPresenceHistory
): Promise<SessionNotificationTransition> {
  const [{ getCurrentWindow }, { rail }] = await Promise.all([
    import('@tauri-apps/api/window'),
    import('../stores/sessionRailStore.svelte.ts')
  ]);
  const session = rail.owned.find((entry) => entry.ownedId === event.ownedId);
  return {
    ownedId: event.ownedId,
    kind,
    previous,
    next,
    title: session?.title ?? null,
    provider: event.provider,
    sessionViewed: rail.activeOwnedId === event.ownedId,
    windowFocused: await getCurrentWindow().isFocused()
  };
}

/**
 * Records the existing presence event first, then emits at most one best-effort
 * notification for that session's transition into needs-attention.
 */
export function recordConversationPresenceEvent(
  event: ConversationPresenceEventLike & { provider: AgentConversationProvider }
): void {
  const presenceEvent = sessionPresenceEventFromConversation(event);
  if (!presenceEvent) return;

  const previous = get(sessionPresenceHistory)[event.ownedId]
    ?? EMPTY_SESSION_PRESENCE_HISTORY;
  recordPresenceEvent(event);
  const next = get(sessionPresenceHistory)[event.ownedId]
    ?? EMPTY_SESSION_PRESENCE_HISTORY;
  if (presenceEvent.kind === 'turn-started') return;

  void dispatchAttentionTransition(event, presenceEvent.kind, previous, next);
}

async function dispatchAttentionTransition(
  event: ConversationPresenceEventLike & { provider: AgentConversationProvider },
  kind: SessionAttentionKind,
  previous: SessionPresenceHistory,
  next: SessionPresenceHistory
): Promise<void> {
  try {
    const transition = await runtimeTransition(event, kind, previous, next);
    const current = get(sessionPresenceHistory)[event.ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY;
    if (current !== next) return;
    await dispatchSystemNotification(transition);
  } catch {
    // Notification delivery is best-effort; presence state was already recorded.
  }
}
