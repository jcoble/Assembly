import type {
  AgentEvent,
  AgentConversationEvent,
  AgentConversationProvider,
  ConversationSessionState
} from './conversationTypes.ts';

export function shouldClearConversationSending(
  event: AgentConversationEvent | AgentEvent
): boolean {
  if ('type' in event) {
    return ['turn.completed', 'turn.interrupted', 'runtime.error'].includes(event.type);
  }
  return event.payload.kind === 'error'
    || (event.payload.kind === 'turn' && event.payload.state !== 'started');
}

export function createConversationState(
  ownedId: string,
  provider: AgentConversationProvider
): ConversationSessionState {
  return {
    ownedId,
    provider,
    generation: 0,
    lastSequence: 0,
    desynchronized: false,
    connectionState: 'disconnected',
    suspended: false,
    timelineRevision: 0
  };
}

/** One event's sequence, connection, turn and usage bookkeeping, without the
 * transcript. A dropped event returns `state` itself. */
export function reduceConversationEvent(
  state: ConversationSessionState,
  event: AgentConversationEvent
): ConversationSessionState {
  if (event.ownedId !== state.ownedId || event.provider !== state.provider || event.generation < state.generation) return state;
  const newGeneration = event.generation > state.generation;
  const previousSequence = state.lastSequence;
  if (!newGeneration && event.sequence <= previousSequence) return state;
  const gap = event.sequence !== previousSequence + 1;
  const next: ConversationSessionState = {
    ownedId: state.ownedId, provider: state.provider,
    generation: event.generation, lastSequence: event.sequence,
    desynchronized: newGeneration ? gap : state.desynchronized || gap,
    connectionState: state.connectionState, suspended: state.suspended,
    timelineRevision: state.timelineRevision, nativeSessionId: state.nativeSessionId,
    activeTurnId: state.activeTurnId, usage: state.usage
  };
  if (next.desynchronized) return next;
  const payload = event.payload;
  if (payload.kind === 'connection') {
    next.connectionState = payload.state;
    next.nativeSessionId = payload.nativeSessionId ?? next.nativeSessionId;
  } else if (payload.kind === 'turn') {
    next.activeTurnId = payload.state === 'started' ? payload.turnId : undefined;
  } else if (payload.kind === 'error') {
    next.activeTurnId = undefined;
    if (!payload.recoverable) next.connectionState = 'failed';
  } else if (payload.kind === 'usage') {
    next.usage = {
      inputTokens: payload.inputTokens ?? next.usage?.inputTokens,
      outputTokens: payload.outputTokens ?? next.usage?.outputTokens,
      usedTokens: payload.usedTokens ?? next.usage?.usedTokens,
      contextWindow: payload.contextWindow ?? next.usage?.contextWindow,
      totalTokens: payload.totalTokens ?? next.usage?.totalTokens
    };
  }
  return next;
}
