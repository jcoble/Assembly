import { EventType, type StreamChunk, type UIMessage } from '@tanstack/ai/client';
import type { CreateChatReturn } from '@tanstack/ai-svelte';
import type { AgentConfigValue, AgentEvent, AgentConversationEvent, AgentItem, AgentPermissionOption, AgentPermissionRequest, AgentUserInputField, AgentUserInputRequest, ConversationAttachment } from './conversationTypes.ts';
import { agentItemFromEvent, displayItemFromAgentItem, reuseConversationDisplayItems, withoutRepeatedPlans, type ConversationDisplayItem } from './conversationTimeline.ts';

/** Unwrap stored provider projections before the one live/replay mapper. */
export function displayEventFrom(event: AgentConversationEvent): AgentConversationEvent | AgentEvent {
  if (event.payload.kind !== 'terminalProjection') return event;
  return {
    type: event.payload.eventType, ownedId: event.ownedId, provider: event.provider,
    providerInstanceId: event.payload.providerInstanceId, generation: event.generation,
    sequence: event.sequence, timestampMs: event.payload.timestampMs ?? event.timestampMs,
    nativeSessionId: event.payload.nativeSessionId, itemId: event.payload.itemId ?? undefined,
    payload: event.payload.payload, providerMetadata: event.payload.providerMetadata,
    rawFrameReference: event.payload.rawFrameReference
  };
}

/** The chat's open interrupts, in the shapes the composer's approval and input panels draw. */
export function pendingRequestsOf(interrupts: CreateChatReturn['interrupts'], ownedId: string): {
  approvals: AgentPermissionRequest[]; inputs: AgentUserInputRequest[];
} {
  const approvals: AgentPermissionRequest[] = [];
  const inputs: AgentUserInputRequest[] = [];
  for (const interrupt of interrupts) {
    if (interrupt.status !== 'pending') continue;
    const identity = { ownedId, generation: interrupt.generation, requestId: interrupt.id };
    const input = interrupt.metadata?.['assembly:input'] as { description?: string | null; canDecline?: boolean; fields?: AgentUserInputField[] } | undefined;
    if (interrupt.reason === 'input') inputs.push({ ...identity, title: interrupt.message || 'Input requested',
      description: input?.description ?? undefined, fields: input?.fields ?? [], canDecline: input?.canDecline === true });
    else approvals.push({ ...identity, title: 'Approval needed', toolTitle: interrupt.message || 'Permission requested',
      description: interrupt.message || undefined, state: 'requested',
      options: interrupt.metadata?.['assembly:options'] as AgentPermissionOption[] | undefined ?? [] });
  }
  return { approvals, inputs };
}

function metadataOf(message: UIMessage): Record<string, AgentConfigValue> {
  return (message.metadata ?? {}) as Record<string, AgentConfigValue>;
}

function textOf(message: UIMessage): string {
  return message.parts.map((part) => part.type === 'text' ? part.content : part.type === 'thinking' ? part.content : '').join('');
}

/** A row from one of the backend's messages: an activity carries its native
 * event, and a call its native payload (its start, updated by its result). The
 * stock client's empty reply to a failed run is no row. */
export function conversationMessageDisplayItem(message: UIMessage): ConversationDisplayItem | null {
  const activity = message.parts.find((part) => part.type === 'activity');
  if (activity) {
    const event = displayEventFrom((activity.content as { event: AgentConversationEvent }).event);
    const item = agentItemFromEvent(event);
    return item && displayItemFromAgentItem({ ...item, id: message.id }, event.timestampMs);
  }
  const call = message.parts.find((part) => part.type === 'tool-call');
  if (call) {
    const facts = (call.metadata ?? {}) as { turnId?: string; startedAtMs?: number; firstSequence?: number };
    // A completion may carry only its output; the call's start still names it.
    const given = (value: object) => Object.fromEntries(Object.entries(value).filter(([, field]) => field !== '' && field != null));
    const payload = { kind: 'tool', itemId: call.id, name: call.name, state: 'started',
      ...given(call.input ?? JSON.parse(call.arguments || '{}')), ...given(call.output ?? {}) };
    const item = agentItemFromEvent({ ownedId: '', provider: 'claude', generation: 0, sequence: facts.firstSequence ?? 0,
      timestampMs: facts.startedAtMs ?? 0, turnId: facts.turnId, payload } as AgentConversationEvent);
    return item && displayItemFromAgentItem(item, facts.startedAtMs ?? 0);
  }
  if (!message.parts.length) return null;
  const metadata = metadataOf(message);
  return displayItemFromAgentItem({
    id: message.id,
    type: metadata.itemType as AgentItem['type'],
    turnId: typeof metadata.turnId === 'string' ? metadata.turnId : undefined,
    content: [{ channel: metadata.itemType === 'reasoning' ? 'reasoning' : 'assistant', text: textOf(message) }],
    providerMetadata: metadata
  }, Number(metadata.startedAtMs ?? 0));
}

// Messages are replaced, never changed in place, so an unchanged message keeps
// its display item instead of rebuilding the whole history on every chunk.
const displayItemsByMessage = new WeakMap<UIMessage, ConversationDisplayItem | null>();

export function conversationDisplayItems(
  messages: readonly UIMessage[],
  previous: readonly ConversationDisplayItem[] = [],
  sentAttachments: Readonly<Record<string, readonly ConversationAttachment[]>> = {}
): ConversationDisplayItem[] {
  const rows = messages.flatMap((message) => {
    let item = displayItemsByMessage.get(message);
    if (item === undefined) {
      item = conversationMessageDisplayItem(message);
      displayItemsByMessage.set(message, item);
    }
    if (!item) return [];
    // A message still on its way carries its own screenshots until the backend's copy claims them.
    const attachments = item.kind === 'user'
      ? sentAttachments[item.itemId] ?? message.metadata?.attachments as readonly ConversationAttachment[] | undefined
      : undefined;
    return [attachments?.length ? { ...item, attachments } : item];
  }).sort((a, b) => a.timestampMs - b.timestampMs);
  return reuseConversationDisplayItems(withoutRepeatedPlans(rows), previous);
}

/** A running call's result carries its output so far and its native state, so a result alone does not end it. */
export function conversationHasRunningTool(messages: readonly UIMessage[]): boolean {
  return messages.some((message) => message.parts.some((part) => part.type === 'tool-call' && part.state !== 'error'
    && (part.state !== 'complete' || ['started', 'updated'].includes((part.output as { state?: string } | undefined)?.state ?? ''))));
}

type TextContent = Extract<StreamChunk, { type: EventType.TEXT_MESSAGE_CONTENT }>;

function textContentOf(item: object | undefined): TextContent | null {
  return item && 'type' in item && item.type === EventType.TEXT_MESSAGE_CONTENT ? item as TextContent : null;
}

/** Waits for the next frame, or 50 ms when the window draws no frames (hidden), or abort. */
async function nextFrame(signal: AbortSignal): Promise<void> {
  await new Promise<void>((resolve) => {
    const done = (): void => {
      cancelAnimationFrame(frame);
      clearTimeout(timer);
      signal.removeEventListener('abort', done);
      resolve();
    };
    const frame = requestAnimationFrame(done);
    const timer = setTimeout(done, 50);
    signal.addEventListener('abort', done, { once: true });
  });
}

/** Takes the next queued item for the chat. Streamed text waits for the next frame,
 * and the text that reached one message meanwhile comes out as one delta, so the
 * chat updates once per frame and the text still streams in. Anything else keeps
 * its place in the queue and goes out at once. */
export async function takeStreamItem<T extends object>(items: T[], signal: AbortSignal): Promise<T | undefined> {
  const first = textContentOf(items[0]);
  if (!first) return items.shift();
  await nextFrame(signal);
  let delta = '';
  for (let next = textContentOf(items[0]); next && next.messageId === first.messageId; next = textContentOf(items[0])) {
    delta += next.delta;
    items.shift();
  }
  return { ...first, delta } as T;
}
