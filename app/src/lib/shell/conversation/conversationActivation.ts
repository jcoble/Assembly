import type {
  AgentConversationProvider,
  ConversationConnectionState
} from './conversationTypes.ts';
import type { AgentExecutionOwner, OwnedSessionState } from '../ownedSessions.ts';

export type ConversationActivationDecision =
  | { kind: 'view' }
  | { kind: 'terminal' }
  | { kind: 'structured'; nativeSessionMode: 'resume' | 'load' };

interface ConversationActivationSession {
  agent: string;
  origin?: 'app' | 'external';
  state: 'live' | 'background' | 'exited';
  executionOwner?: AgentExecutionOwner;
  ptySessionId: string | null;
  nativeSessionId: string | null;
}

interface ConversationConnection {
  provider: AgentConversationProvider;
  state?: ConversationConnectionState;
  connectionState?: ConversationConnectionState;
  nativeSessionId?: string | null;
}

export interface ConversationSendState {
  sessionState?: OwnedSessionState;
  executionOwner?: AgentExecutionOwner;
  connectionState?: ConversationConnectionState | null;
  generation: number;
}

const DEAD_CONNECTION_STATES: readonly ConversationConnectionState[] = [
  'disconnected',
  'failed',
  'closed'
];

/** A send must not be attempted against a stopped rail record or dead conversation connection. */
export function shouldReviveBeforeSend(state: ConversationSendState): boolean {
  return state.sessionState === 'exited'
    || state.executionOwner === 'stopped'
    || state.generation < 1
    || state.connectionState == null
    || DEAD_CONNECTION_STATES.includes(state.connectionState);
}

/**
 * Decide whether a send may carry image attachments.
 *
 * Only a connected session answers for itself: a suspended session still holds
 * the capability snapshot recorded when it was last connected, which can
 * predate the provider gaining image prompts. Refusing on that snapshot leaves
 * a screenshot that can never go out, so an unconnected session sends and the
 * provider — reconnected with a fresh handshake — decides.
 */
export function sendSupportsImages(
  capabilities: { prompt: { image: boolean } } | null | undefined,
  connectionState: ConversationConnectionState | null | undefined
): boolean {
  if (connectionState !== 'connected' || !capabilities) return true;
  return capabilities.prompt.image === true;
}

/** Select a usable activation generation that is not older than the pre-ensure state. */
export function generationForSend(previousGeneration: number, activationGeneration: number): number | null {
  if (!Number.isInteger(previousGeneration) || !Number.isInteger(activationGeneration)) return null;
  if (activationGeneration < 1 || activationGeneration < previousGeneration) return null;
  return activationGeneration;
}

/**
 * Read the generation a send must carry from the state as it stands when the
 * request goes out.
 *
 * A generation names the adapter incarnation, not the conversation: re-ensuring
 * a session after a suspend keeps the same owned id, working folder and
 * transcript, so a generation that moved while the message was being prepared
 * is no reason to drop what the user typed — the send follows the current one,
 * which is also the only one the backend will accept. A conversation that is
 * gone, never connected, closed or failed has nothing to send to, and only that
 * refuses.
 */
export function sendTargetGeneration(
  current: Pick<ConversationSendState, 'connectionState' | 'generation'> | null | undefined
): number | null {
  if (!current || !Number.isInteger(current.generation) || current.generation < 1) return null;
  if (current.connectionState === 'closed' || current.connectionState === 'failed') return null;
  return current.generation;
}

function connectedSessionMatches(
  session: ConversationActivationSession,
  connection: ConversationConnection | null
): boolean {
  const stopped = session.executionOwner === 'stopped' || session.state === 'exited';
  const state = connection?.state ?? connection?.connectionState;
  if (stopped || !connection || state !== 'connected' || connection.provider !== session.agent) {
    return false;
  }
  return !session.nativeSessionId || connection.nativeSessionId === session.nativeSessionId;
}

/**
 * Decide whether a rail selection is view-only, terminal-owned, or needs ACP.
 * Running external sessions are checked first: an old structured connection
 * never overrides their live CLI writer. A stopped external session may load
 * through a proven provider only when its native session id is known.
 */
export function decideConversationActivation(
  session: ConversationActivationSession,
  connection: ConversationConnection | null
): ConversationActivationDecision {
  if (session.origin !== 'app') {
    const terminalOwned = session.executionOwner === 'terminal'
      || session.state === 'live'
      || Boolean(session.ptySessionId);
    const stopped = session.executionOwner === 'stopped' || session.state === 'exited';
    if (terminalOwned || !stopped) return { kind: 'terminal' };
    // Claude ACP 0.90.0: claude -p "remember the word pelican", session/load replayed it, session/prompt answered "pelican".
    // Antigravity ACP 1.4.1: loading an agy CLI ID returned "Session not found in the current GEMINI_HOME".
    if (!['codex', 'claude'].includes(session.agent) || !session.nativeSessionId) return { kind: 'terminal' };
    return { kind: 'structured', nativeSessionMode: 'load' };
  }

  if (connectedSessionMatches(session, connection)) return { kind: 'view' };
  return { kind: 'structured', nativeSessionMode: 'resume' };
}
