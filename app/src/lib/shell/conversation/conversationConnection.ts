import { normalizeProvider } from '../ownedSessions.ts';
import { EventType, StreamProcessor, type StreamChunk } from '@tanstack/ai/client';
import {
  createChat,
  type CreateChatReturn,
  type SubscribeConnectionAdapter,
  type UIMessage
} from '@tanstack/ai-svelte';
import {
  attachmentDisplayMetadata,
  discardRestoredAttachments,
  readNewerSelectedConversationItems,
  readOlderSelectedConversationItems,
  readSelectedConversationAttachments,
  restoreAttachmentList,
  resumeConversationRun,
  sendStructuredMessage,
  subscribeSelectedConversation,
  type AguiRun
} from './conversationService.ts';
import { takeStreamItem } from './conversationMessages.ts';
import {
  applySelectedConversationEventState,
  applySelectedConversationHistoryEventState,
  applySelectedConversationLiveWindow,
  applySelectedConversationPageState,
  applySelectedConversationSnapshotState,
  getConversationSession,
  ensureConversationSession,
  evictConversationSession,
  failChildConversationTranscript,
  restoreSelectedConversationAttachments,
  setConversationAttachmentError,
  setConversationSendError,
  setSelectedConversationPageLoading,
  setSelectedConversationPageError
} from './conversationStore.svelte.ts';
import type {
  AgentConversationEvent,
  AgentConversationItemPage,
  AgentConversationSendReceipt,
  AguiSnapshot,
  ConversationAttachment
} from './conversationTypes.ts';
import { closeAguiViewInBackground, extendAgentConversationImportFromTauri } from '../../tauriSource.ts';
import { rail } from '../stores/sessionRailStore.svelte.ts';

const PAGE_BYTES = 512 * 1024;
const encoder = new TextEncoder();

type Direction = 'older' | 'newer';
type QueueItem = StreamChunk | { error: unknown };

interface PendingAdmission {
  resolve: (receipt: AgentConversationSendReceipt) => void;
  reject: (error: unknown) => void;
}

interface ActiveConversation {
  workspaceOwnedId: string;
  historyOwnedId: string;
  controller: AbortController;
  chat: CreateChatReturn;
  beforeCursor?: number;
  afterCursor?: number;
  /** The chat's copy of the message being sent, kept through a snapshot read until the backend's arrives. */
  sendingId: string | null;
  pendingAdmission: PendingAdmission | null;
  messageBytes: Map<string, number>;
  graphBytes: number;
  contentAdmitted: boolean;
  anchorRevision: number;
  anchorAdmission: Promise<void> | null;
  anchorSnapshot: AguiSnapshot | null;
  resubscribe?: () => Promise<void>;
  push?: (item: QueueItem) => void;
  ready: Promise<void>;
  resolveReady?: () => void;
  rejectReady?: (error: unknown) => void;
  releaseSelectionSignal?: () => void;
}

let active: ActiveConversation | null = null;
let childActive: ActiveConversation | null = null;

function isCurrent(selection: ActiveConversation): boolean {
  return !selection.controller.signal.aborted && (active === selection || childActive === selection);
}

// Snapshots and pages keep unchanged parts and messages; a changed one is a new object. So each is measured once.
const partBytes = new WeakMap<object, number>(), messageTotals = new WeakMap<UIMessage, number>();

function messageBytes(message: UIMessage): number {
  let total = messageTotals.get(message);
  if (total === undefined) messageTotals.set(message, total = message.parts.reduce((sum, part) => {
    let bytes = partBytes.get(part);
    if (bytes === undefined) partBytes.set(part, bytes = encoder.encode(JSON.stringify(part)).byteLength);
    return sum + bytes;
  }, encoder.encode(JSON.stringify({ ...message, parts: [] })).byteLength + Math.max(0, message.parts.length - 1)));
  return total;
}

function resetMessageBytes(selection: ActiveConversation, messages: readonly UIMessage[]): void {
  selection.messageBytes = new Map(messages.map((message) => [message.id, messageBytes(message)]));
  selection.graphBytes = [...selection.messageBytes.values()].reduce((total, bytes) => total + bytes, 0);
}

function pushMessagesSnapshot(
  selection: ActiveConversation,
  messages: readonly UIMessage[],
  push: (item: QueueItem) => void
): void {
  resetMessageBytes(selection, messages);
  push({ type: EventType.MESSAGES_SNAPSHOT, messages: [...messages] } as StreamChunk);
}

function messagesFromPage(page: AgentConversationItemPage, chunks: readonly StreamChunk[]): UIMessage[] {
  const processor = new StreamProcessor();
  for (const chunk of chunks) processor.processChunk(chunk);
  const descriptors = new Map(page.items.map((item) => [item.itemId, item]));
  return processor.getMessages().map((message) => {
    const item = descriptors.get(message.id);
    return item ? {
      ...message,
      metadata: {
        ...message.metadata,
        historyStableId: item.stableId,
        // A page may hold only an item's last event; order it by its first, as live does.
        startedAtMs: item.firstTimestampMs,
        firstSequence: item.firstSequence,
        lastSequence: item.lastSequence,
        positionKnown: item.positionKnown,
        prefixComplete: item.prefixComplete
      }
    } : message;
  });
}

function historyPosition(message: UIMessage | undefined): number | undefined {
  const value = message?.metadata?.firstSequence;
  return typeof value === 'number' ? value : undefined;
}

function hasOlderHistory(page: AgentConversationItemPage): boolean {
  return page.hasBefore
    || page.hasEarlierTranscript
    || !!(page.coverage && !page.coverage.startComplete);
}

function mergeMessages(
  current: readonly UIMessage[],
  incoming: readonly UIMessage[],
  direction: Direction
): UIMessage[] {
  const preferred = direction === 'older' ? current : incoming;
  const byId = new Map([...incoming, ...current].map((message) => [message.id, message]));
  for (const message of preferred) byId.set(message.id, message);
  const order = direction === 'older' ? [...incoming, ...current] : [...current, ...incoming];
  const seen = new Set<string>();
  return order.flatMap((message) => {
    if (seen.has(message.id)) return [];
    seen.add(message.id);
    return [byId.get(message.id)!];
  });
}

function recordLiveMessages(
  selection: ActiveConversation,
  changedIds: ReadonlySet<string>,
  replaced: boolean
): void {
  const messages = selection.chat.messages;
  if (replaced) resetMessageBytes(selection, messages);
  else for (const id of changedIds) {
    const previous = selection.messageBytes.get(id) ?? 0;
    const message = messages.find((candidate) => candidate.id === id);
    const next = message ? messageBytes(message) : 0;
    selection.graphBytes += next - previous;
    if (message) selection.messageBytes.set(id, next);
    else selection.messageBytes.delete(id);
  }
  selection.beforeCursor = historyPosition(messages[0]) ?? selection.beforeCursor;
  applySelectedConversationLiveWindow(
    selection.historyOwnedId,
    messages.flatMap((message) => typeof message.metadata?.turnId === 'string'
      ? [message.metadata.turnId] : []),
    selection.graphBytes
  );
}

function optimisticUserId(messages: readonly UIMessage[]): string | null {
  return messages.findLast((message) => message.role === 'user')?.id ?? null;
}

function enqueueFactory(signal: AbortSignal, selection: ActiveConversation): {
  push(item: QueueItem): void;
  stream(): AsyncIterable<StreamChunk>;
} {
  const items: QueueItem[] = [];
  let wake: (() => void) | null = null;
  const abort = (): void => {
    wake?.();
    wake = null;
  };
  const push = (item: QueueItem): void => {
    if (signal.aborted) return;
    items.push(item);
    wake?.();
    wake = null;
  };
  return {
    push,
    async *stream() {
      signal.addEventListener('abort', abort, { once: true });
      try {
        while (!signal.aborted) {
          const item = await takeStreamItem(items, signal);
          if (item) {
            if ('error' in item) throw item.error;
            yield item;
            // A call's message is named after the call.
            const id = 'toolCallId' in item ? item.toolCallId : 'messageId' in item ? item.messageId : undefined;
            if (typeof id === 'string') recordLiveMessages(selection, new Set([id]), false);
            continue;
          }
          if (!items.length && !signal.aborted) await new Promise<void>((resolve) => { wake = resolve; });
        }
      } finally {
        signal.removeEventListener('abort', abort);
        items.length = 0;
        wake = null;
      }
    }
  };
}

function resetReady(selection: ActiveConversation): void {
  selection.ready = new Promise<void>((resolve, reject) => {
    selection.resolveReady = resolve;
    selection.rejectReady = reject;
  });
  void ignoreLeftSelection(selection.ready);
}

/** Leaving a selection rejects its readiness; nobody may be waiting for it then. */
async function ignoreLeftSelection(ready: Promise<void>): Promise<void> {
  try {
    await ready;
  } catch {
    // Whoever awaits the readiness sees the rejection themselves.
  }
}

function bindSelectionSignal(selection: ActiveConversation, signal?: AbortSignal): void {
  selection.releaseSelectionSignal?.();
  const abort = (): void => {
    if (isCurrent(selection)) {
      if (selection === childActive) disposeChildConversationChat(selection.workspaceOwnedId);
      else disposeSelectedConversationChat(selection.workspaceOwnedId);
    }
  };
  if (signal?.aborted) abort();
  else signal?.addEventListener('abort', abort, { once: true });
  selection.releaseSelectionSignal = () => signal?.removeEventListener('abort', abort);
}

async function restorePageAttachments(
  selection: ActiveConversation,
  wanted: ReadonlyMap<string, readonly string[]>,
  generation: number,
  retainedItemIds: readonly string[]
): Promise<void> {
  try {
    restoreSelectedConversationAttachments(
      selection.historyOwnedId, {}, generation, retainedItemIds
    );
    if (!wanted.size) return;
    const byItem: Record<string, ConversationAttachment[]> = {};
    const ids = [...new Set([...wanted.values()].flat())];
    const saved = await readSelectedConversationAttachments(selection.historyOwnedId, ids, selection.controller.signal);
    if (!isCurrent(selection) || selection.controller.signal.aborted) return;
    const restored = await restoreAttachmentList(selection.historyOwnedId, saved, selection.controller.signal);
    if (!isCurrent(selection) || selection.controller.signal.aborted) {
      discardRestoredAttachments(restored);
      return;
    }
    const byId = new Map(restored.map((attachment) => [attachment.id, attachment]));
    for (const [itemId, attachmentIds] of wanted) {
      // Some stored messages name one screenshot twice; show it once.
      byItem[itemId] = [...new Set(attachmentIds)].flatMap((id) => byId.get(id) ?? []);
    }
    if (isCurrent(selection)) {
      restoreSelectedConversationAttachments(
        selection.historyOwnedId,
        byItem,
        generation,
        selection.chat.messages.map((message) => message.id)
      );
    }
  } catch (error) {
    if (isCurrent(selection) && !selection.controller.signal.aborted) {
      setConversationAttachmentError(
        selection.historyOwnedId,
        error instanceof Error ? error.message : String(error)
      );
    }
  }
}

function pageAttachments(chunks: readonly StreamChunk[]): Map<string, readonly string[]> {
  const wanted = new Map<string, readonly string[]>();
  for (const chunk of chunks) {
    if (chunk.type !== EventType.TEXT_MESSAGE_START || chunk.role !== 'user') continue;
    const ids = (chunk as { metadata?: { attachmentIds?: string[] } }).metadata?.attachmentIds;
    if (ids?.length) wanted.set(chunk.messageId, ids);
  }
  return wanted;
}

function snapshotChunks(
  selection: ActiveConversation,
  snapshot: AguiSnapshot,
  push: (item: QueueItem) => void,
  applyControls = selection.historyOwnedId === selection.workspaceOwnedId
): void {
  selection.beforeCursor = snapshot.page.beforeCursor;
  if (selection.beforeCursor === undefined && hasOlderHistory(snapshot.page)) {
    selection.beforeCursor = snapshot.page.watermark + 1;
  }
  selection.afterCursor = snapshot.page.afterCursor;
  const pageMessages = messagesFromPage(snapshot.page, snapshot.chunks);
  const current = getConversationSession(selection.historyOwnedId);
  const prefixEnd = selection.chat.messages.findIndex((message) => message.id === pageMessages[0]?.id);
  const prefix = prefixEnd > 0 ? selection.chat.messages.slice(0, prefixEnd) : [];
  let messages = [...prefix, ...pageMessages];
  // A latest snapshot that overlaps loaded history refreshes its tail and keeps the earlier pages.
  if (prefix.length) snapshot = { ...snapshot, page: { ...snapshot.page,
    turns: [...new Map([...(current?.selectedTurns ?? []), ...snapshot.page.turns]
      .map((turn) => [turn.turnId, turn])).values()]
  } };
  const optimistic = selection.chat.messages.find((message) => message.id === selection.sendingId);
  if (optimistic && !messages.some((message) => message.id === optimistic.id)) messages.push(optimistic);
  selection.beforeCursor = historyPosition(messages.find((message) => historyPosition(message) !== undefined)) ?? selection.beforeCursor;
  selection.afterCursor = historyPosition(messages.findLast((message) => historyPosition(message) !== undefined)) ?? selection.afterCursor;
  resetMessageBytes(selection, messages);
  applySelectedConversationSnapshotState(
    selection.historyOwnedId,
    selection.historyOwnedId,
    snapshot,
    applyControls,
    {
      beforeCursor: selection.beforeCursor,
      afterCursor: selection.afterCursor,
      hasBefore: prefix.length ? current?.selectedHasBefore ?? hasOlderHistory(snapshot.page) : hasOlderHistory(snapshot.page),
      hasAfter: snapshot.page.hasAfter,
      retainedTurnIds: messages.flatMap((message) =>
        typeof message.metadata?.turnId === 'string' ? [message.metadata.turnId] : []),
      transferBytes: selection.graphBytes
    }
  );
  if (applyControls) {
    const snapshotError = snapshot.pendingEvents.findLast((event) => event.payload.kind === 'error');
    if (snapshotError?.payload.kind === 'error') {
      setConversationSendError(selection.workspaceOwnedId, snapshotError.payload.message);
    }
  }
  pushMessagesSnapshot(selection, messages, push);
  push({ type: EventType.CUSTOM, name: 'assembly:snapshot-ready', value: selection.historyOwnedId } as StreamChunk);
  for (const chunk of snapshot.runs) push(chunk);
  void restorePageAttachments(selection, pageAttachments(snapshot.chunks),
    snapshot.connection.generation, messages.map((message) => message.id));
}

function admitInitialSnapshot(
  selection: ActiveConversation,
  snapshot: AguiSnapshot,
  push: (item: QueueItem) => void
): void {
  if (selection.contentAdmitted) {
    snapshotChunks(selection, snapshot, push);
    return;
  }
  const view = getConversationSession(selection.workspaceOwnedId)
    ?.viewByHistoryId[selection.historyOwnedId];
  const anchor = view?.followLatest === false ? view.anchor : undefined;
  if (!selection.anchorAdmission && !anchor) {
    selection.contentAdmitted = true;
    snapshotChunks(selection, snapshot, push);
    return;
  }
  selection.anchorSnapshot = snapshot;
  applySelectedConversationSnapshotState(
    selection.historyOwnedId,
    selection.historyOwnedId,
    snapshot,
    selection.historyOwnedId === selection.workspaceOwnedId
  );
  const snapshotError = snapshot.pendingEvents.findLast((event) => event.payload.kind === 'error');
  if (snapshotError?.payload.kind === 'error') {
    const childId = getConversationSession(selection.workspaceOwnedId)?.selectedChildId;
    if (childId && selection.historyOwnedId !== selection.workspaceOwnedId) {
      failChildConversationTranscript(selection.workspaceOwnedId, childId, snapshotError.payload.message);
    } else {
      setConversationSendError(selection.workspaceOwnedId, snapshotError.payload.message);
    }
  }
  if (selection.anchorAdmission) return;
  const revision = ++selection.anchorRevision;
  selection.anchorAdmission = admitAnchorPage(selection, snapshot, anchor!.firstSequence, revision, push);
}

/** Reads the saved window's page; leaving the selection or a newer Jump drops the result. */
async function admitAnchorPage(
  selection: ActiveConversation,
  snapshot: AguiSnapshot,
  firstSequence: number,
  revision: number,
  push: (item: QueueItem) => void
): Promise<void> {
  const signal = selection.controller.signal;
  try {
    const page = await readNewerSelectedConversationItems(
      selection.historyOwnedId,
      firstSequence - 1,
      PAGE_BYTES,
      signal
    );
    if (signal.aborted
      || !isCurrent(selection)
      || revision !== selection.anchorRevision) return;
    const authority = selection.anchorSnapshot ?? snapshot;
    selection.anchorAdmission = null;
    selection.anchorSnapshot = null;
    selection.contentAdmitted = true;
    if (page && !page.hasAfter) {
      await selection.resubscribe?.();
      return;
    }
    snapshotChunks(selection, page ? { ...authority, page, chunks: page.chunks } : authority, push, false);
  } catch (error) {
    if (!isCurrent(selection)
      || signal.aborted
      || revision !== selection.anchorRevision) return;
    selection.anchorAdmission = null;
    selection.anchorSnapshot = null;
    selection.rejectReady?.(error);
    push({ error });
  }
}

/** A native event updates the store; the chat gets its own chunks for it. */
function emitEvent(selection: ActiveConversation, event: AgentConversationEvent): void {
  if (selection.historyOwnedId === selection.workspaceOwnedId) {
    applySelectedConversationEventState(selection.workspaceOwnedId, event);
  } else {
    applySelectedConversationHistoryEventState(selection.historyOwnedId, event);
  }
  if (event.payload.kind === 'error') {
    const childId = getConversationSession(selection.workspaceOwnedId)?.selectedChildId;
    if (childId && selection.historyOwnedId !== selection.workspaceOwnedId) {
      failChildConversationTranscript(selection.workspaceOwnedId, childId, event.payload.message);
    } else {
      setConversationSendError(selection.workspaceOwnedId, event.payload.message);
    }
  }
}

/** The view's chunks go to the chat as they are. While it shows older history, only runs and interrupts do. */
function emitChunks(selection: ActiveConversation, chunks: readonly StreamChunk[], push: (item: QueueItem) => void): void {
  const showingLatest = selection.contentAdmitted && !getConversationSession(selection.historyOwnedId)?.selectedHasAfter;
  for (const chunk of chunks) if (showingLatest || String(chunk.type).startsWith('RUN_')) push(chunk);
}

function rejectPendingAdmission(selection: ActiveConversation, error: unknown): void {
  const admission = selection.pendingAdmission;
  if (!admission) return;
  selection.pendingAdmission = null;
  admission.reject(error);
}

function createConnection(selection: ActiveConversation): SubscribeConnectionAdapter {
  return {
    subscribe(signal) {
      const joined = new AbortController();
      const abort = (): void => joined.abort();
      signal?.addEventListener('abort', abort, { once: true });
      selection.controller.signal.addEventListener('abort', abort, { once: true });
      if (signal?.aborted || selection.controller.signal.aborted) abort();
      const queue = enqueueFactory(joined.signal, selection);
      return (async function* () {
        let dispose = (): void => {};
        let opening: Promise<void> = Promise.resolve();
        // Opens run one at a time; each replaces the watch the previous one opened.
        async function open(): Promise<void> {
          const run = openAfter(opening);
          opening = run;
          await run;
        }
        async function openAfter(previous: Promise<void>): Promise<void> {
          try {
            await previous;
          } catch {
            // The earlier caller already saw its error.
          }
          if (joined.signal.aborted) return;
          dispose();
          dispose = () => {};
          let found = false;
          const next = await subscribeSelectedConversation({
            workspaceOwnedId: selection.workspaceOwnedId,
            ownedId: selection.historyOwnedId,
            maxBytes: PAGE_BYTES,
            signal: joined.signal,
            onSnapshot: (snapshot) => {
              found = true;
              admitInitialSnapshot(selection, snapshot, queue.push);
            },
            onEvent: (event) => emitEvent(selection, event),
            onChunks: (chunks) => emitChunks(selection, chunks, queue.push),
            onError: (error) => queue.push({ error })
          });
          if (joined.signal.aborted) next();
          else {
            dispose = next;
            // A new session has no backend row or snapshot until its first send.
            // Its empty chat is ready; the first event reloads the real snapshot.
            if (!found) queue.push({ type: EventType.CUSTOM, name: 'assembly:snapshot-ready', value: selection.historyOwnedId } as StreamChunk);
          }
        }
        selection.push = queue.push;
        selection.resubscribe = open;
        try {
          await open();
          yield* queue.stream();
        } catch (error) {
          selection.rejectReady?.(error);
          throw error;
        } finally {
          dispose();
          if (selection.resubscribe === open) selection.resubscribe = undefined;
          if (selection.push === queue.push) selection.push = undefined;
          signal?.removeEventListener('abort', abort);
          selection.controller.signal.removeEventListener('abort', abort);
        }
      })();
    },
    async send(messages, _data, _signal, runContext) {
      const run: AguiRun = {
        threadId: runContext?.threadId ?? selection.historyOwnedId, runId: runContext?.runId ?? crypto.randomUUID(),
        parentRunId: runContext?.parentRunId, resume: runContext?.resume ?? []
      };
      if (run.resume?.length) {
        await resumeConversationRun(run);
        return;
      }
      const user = [...messages].reverse().find((message) =>
        message.role === 'user' && 'parts' in message
      );
      const prompt = user && 'parts' in user
        ? user.parts.map((part: UIMessage['parts'][number]) => part.type === 'text' ? part.content : '').join('')
        : '';
      selection.sendingId = optimisticUserId(messages as UIMessage[]);
      try {
        if (getConversationSession(selection.historyOwnedId)?.selectedHasAfter) {
          if (!selection.resolveReady) resetReady(selection);
          await selection.resubscribe?.();
          await selection.ready;
        }
        // The backend records the message under the chat's own id, and the reply streams under this run.
        const receipt = await sendStructuredMessage(selection.workspaceOwnedId, prompt, undefined, true,
          { ...run, messageId: selection.sendingId ?? undefined });
        if (!receipt) throw new Error('The native conversation did not admit the message.');
        const admission = selection.pendingAdmission;
        if (admission) {
          selection.pendingAdmission = null;
          admission.resolve(receipt);
        }
      } catch (error) {
        rejectPendingAdmission(selection, error);
        throw error;
      } finally {
        selection.sendingId = null;
      }
    }
  };
}

export function selectConversationChat(
  workspaceOwnedId: string,
  historyOwnedId = workspaceOwnedId,
  signal?: AbortSignal
): CreateChatReturn {
  const child = historyOwnedId !== workspaceOwnedId;
  const current = child ? childActive : active;
  if (current?.workspaceOwnedId === workspaceOwnedId && current.historyOwnedId === historyOwnedId) {
    bindSelectionSignal(current, signal);
    return current.chat;
  }
  if (child) {
    disposeChildConversationChat();
    const parent = getConversationSession(workspaceOwnedId);
    if (!parent) throw new Error('The parent conversation is not selected.');
    const { agent } = normalizeProvider(rail.owned.find((session) => session.ownedId === historyOwnedId)?.agent ?? ''); // A helper owns its provider; a native child shares its parent's.
    ensureConversationSession(historyOwnedId, agent === 'claude' || agent === 'codex' || agent === 'antigravity' ? agent : parent.provider);
    parent.selectedChildHistoryOwnedId = historyOwnedId;
  } else {
    disposeSelectedConversationChat();
  }
  const selection = {
    workspaceOwnedId, historyOwnedId, controller: new AbortController(), sendingId: null, pendingAdmission: null,
    messageBytes: new Map(), graphBytes: 0, contentAdmitted: false, anchorRevision: 0, anchorAdmission: null, anchorSnapshot: null
  } as ActiveConversation;
  resetReady(selection);
  const connection = createConnection(selection);
  selection.chat = createChat({
    connection,
    live: true,
    queue: 'drop',
    threadId: historyOwnedId,
    onCustomEvent(name, value) {
      if (selection.controller.signal.aborted) return;
      if (name === 'assembly:snapshot-ready' && value === selection.historyOwnedId) {
        selection.resolveReady?.();
        selection.resolveReady = undefined;
        selection.rejectReady = undefined;
      }
    },
    onError(error) {
      if (!selection.controller.signal.aborted) {
        const childId = getConversationSession(selection.workspaceOwnedId)?.selectedChildId;
        if (childId && selection.historyOwnedId !== selection.workspaceOwnedId) {
          failChildConversationTranscript(selection.workspaceOwnedId, childId, error.message);
        } else {
          setConversationSendError(selection.workspaceOwnedId, error.message);
        }
      }
    }
  });
  if (child) childActive = selection;
  else active = selection;
  bindSelectionSignal(selection, signal);
  return selection.chat;
}

export function selectedConversationChat(
  workspaceOwnedId?: string | null,
  historyOwnedId = workspaceOwnedId
): CreateChatReturn | null {
  const selection = historyOwnedId && historyOwnedId !== workspaceOwnedId ? childActive : active;
  return selection && (!workspaceOwnedId || selection.workspaceOwnedId === workspaceOwnedId)
    && (!historyOwnedId || selection.historyOwnedId === historyOwnedId) ? selection.chat : null;
}

function disposeConversationChat(selection: ActiveConversation): void {
  const generation = getConversationSession(selection.historyOwnedId)?.generation ?? 0;
  restoreSelectedConversationAttachments(selection.historyOwnedId, {}, generation, []);
  selection.controller.abort();
  selection.rejectReady?.(new DOMException('Conversation selection disposed', 'AbortError'));
  selection.releaseSelectionSignal?.();
  // A pending send is not rejected here: leaving the session does not stop it,
  // so its receipt or its own failure settles the admission.
  selection.chat.dispose();
  void closeAguiViewInBackground(selection.historyOwnedId);
}

export function disposeChildConversationChat(workspaceOwnedId?: string): void {
  const selection = childActive;
  if (!selection || (workspaceOwnedId && selection.workspaceOwnedId !== workspaceOwnedId)) return;
  childActive = null;
  const parent = getConversationSession(selection.workspaceOwnedId);
  if (parent?.selectedChildHistoryOwnedId === selection.historyOwnedId) parent.selectedChildHistoryOwnedId = null;
  disposeConversationChat(selection);
  if (rail.owned.some((session) => session.ownedId === selection.historyOwnedId)) {
    // The helper owns its draft and send; only its displayed turn facts leave with the reader.
    const helper = getConversationSession(selection.historyOwnedId);
    if (helper) helper.selectedTurns = [];
  } else {
    evictConversationSession(selection.historyOwnedId);
  }
}

export function disposeSelectedConversationChat(workspaceOwnedId?: string): void {
  disposeChildConversationChat(workspaceOwnedId);
  const selection = active;
  if (!selection || (workspaceOwnedId && selection.workspaceOwnedId !== workspaceOwnedId)) return;
  active = null;
  disposeConversationChat(selection);
}

export async function selectedConversationChatReady(
  workspaceOwnedId: string,
  historyOwnedId = workspaceOwnedId
): Promise<void> {
  const selection = historyOwnedId === workspaceOwnedId ? active : childActive;
  if (!selection || selection.workspaceOwnedId !== workspaceOwnedId || selection.historyOwnedId !== historyOwnedId) return;
  await selection.ready;
}

export async function sendSelectedConversationMessage(
  workspaceOwnedId: string,
  text: string,
  id?: string,
  attachments: readonly ConversationAttachment[] = []
): Promise<AgentConversationSendReceipt> {
  const selection = active;
  if (!selection || selection.workspaceOwnedId !== workspaceOwnedId) {
    throw new Error('The conversation is not ready to send.');
  }
  if (selection.pendingAdmission) {
    throw new Error('A message is already being admitted.');
  }
  let resolve!: (receipt: AgentConversationSendReceipt) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<AgentConversationSendReceipt>((accept, decline) => {
    resolve = accept;
    reject = decline;
  });
  const admission = { resolve, reject };
  selection.pendingAdmission = admission;
  // The timeline orders rows by start time. Without one, the chat's own copy of
  // the message drew above the whole history, and the send anchor scrolled there.
  // The id lets the surface scroll to this copy at once; its screenshots draw
  // with it rather than when the backend's copy claims them.
  void sendChatMessage(selection, admission, {
    id,
    content: [{ type: 'text', content: text }],
    metadata: {
      itemType: 'user-message',
      startedAtMs: Date.now(),
      ...(attachments.length ? { attachments: attachments.map(attachmentDisplayMetadata) } : {})
    }
  });
  return await promise;
}

/** Runs the chat's send; a send that fails or ends without a receipt rejects its admission. */
async function sendChatMessage(
  selection: ActiveConversation,
  admission: PendingAdmission,
  message: Parameters<CreateChatReturn['sendMessage']>[0]
): Promise<void> {
  try {
    await selection.chat.sendMessage(message);
  } catch (error) {
    rejectPendingAdmission(selection, error);
  } finally {
    if (selection.pendingAdmission === admission) {
      rejectPendingAdmission(selection, new Error('The message was not admitted.'));
    }
  }
}

export async function refreshSelectedConversationChat(workspaceOwnedId: string, historyOwnedId = workspaceOwnedId): Promise<void> {
  const selection = historyOwnedId === workspaceOwnedId ? active : childActive;
  if (!selection || selection.workspaceOwnedId !== workspaceOwnedId || selection.historyOwnedId !== historyOwnedId || !selection.resubscribe) return;
  if (!selection.resolveReady) resetReady(selection);
  await selection.resubscribe();
  await selection.ready;
}

export async function jumpSelectedConversationToLatest(workspaceOwnedId: string, historyOwnedId = workspaceOwnedId): Promise<void> {
  const selection = historyOwnedId === workspaceOwnedId ? active : childActive;
  if (selection?.workspaceOwnedId === workspaceOwnedId && selection.historyOwnedId === historyOwnedId) {
    selection.anchorRevision += 1;
    selection.anchorAdmission = null;
    selection.anchorSnapshot = null;
    selection.contentAdmitted = true;
  }
  await refreshSelectedConversationChat(workspaceOwnedId, historyOwnedId);
}

export async function pageSelectedConversation(direction: Direction, historyOwnedId?: string): Promise<void> {
  const selection = historyOwnedId && historyOwnedId !== active?.historyOwnedId ? childActive : active;
  if (!selection || (historyOwnedId && selection.historyOwnedId !== historyOwnedId)) return;
  if (!setSelectedConversationPageLoading(selection.historyOwnedId, direction, true)) return;
  try {
    const cursor = direction === 'older'
      ? selection.beforeCursor
      : selection.afterCursor;
    if (cursor === undefined) return;
    let page = await (direction === 'older' ? readOlderSelectedConversationItems : readNewerSelectedConversationItems)(
      selection.historyOwnedId, cursor, PAGE_BYTES, selection.controller.signal);
    if (!page || !isCurrent(selection)) return;
    if (direction === 'older' && page.items.length === 0 && !page.hasBefore && page.hasEarlierTranscript) {
      const extended = await extendAgentConversationImportFromTauri(
        selection.historyOwnedId,
        selection.controller.signal
      );
      if (!isCurrent(selection)) return;
      if (extended && (extended.added > 0 || !extended.reachedStart)) {
        page = await readOlderSelectedConversationItems(
          selection.historyOwnedId, cursor, PAGE_BYTES, selection.controller.signal
        );
        if (!page || !isCurrent(selection)) return;
      }
      if (extended?.reachedStart && page.hasEarlierTranscript) {
        page = { ...page, hasEarlierTranscript: false };
      }
    }
    const current = getConversationSession(selection.historyOwnedId);
    const messages = mergeMessages(
      selection.chat.messages,
      messagesFromPage(page, page.chunks),
      direction
    );
    if (!selection.push) return;
    resetReady(selection);
    pushMessagesSnapshot(selection, messages, selection.push);
    selection.beforeCursor = historyPosition(messages[0]) ?? page.beforeCursor;
    selection.afterCursor = historyPosition(messages.at(-1)) ?? page.afterCursor;
    applySelectedConversationPageState(selection.historyOwnedId, page, direction, {
      beforeCursor: selection.beforeCursor,
      afterCursor: selection.afterCursor,
      hasBefore: direction === 'older' ? hasOlderHistory(page) : current?.selectedHasBefore ?? hasOlderHistory(page),
      hasAfter: direction === 'newer' ? page.hasAfter : current?.selectedHasAfter ?? page.hasAfter,
      retainedTurnIds: messages.flatMap((message) =>
        typeof message.metadata?.turnId === 'string' ? [message.metadata.turnId] : []
      ),
      // The snapshot above already measured every message; measuring the
      // whole history again cost as much as the snapshot on each page.
      transferBytes: selection.graphBytes
    });
    const generation = getConversationSession(selection.historyOwnedId)?.generation ?? 0;
    void restorePageAttachments(
      selection,
      pageAttachments(page.chunks),
      generation,
      messages.map((message) => message.id)
    );
    // Keep paging locked until this page is consumed; the next merge must see its messages.
    selection.push({ type: EventType.CUSTOM, name: 'assembly:snapshot-ready', value: selection.historyOwnedId } as StreamChunk);
    await selection.ready;
    if (!isCurrent(selection)) return;
    if (direction === 'newer' && !page.hasAfter) {
      resetReady(selection);
      await selection.resubscribe?.();
      await selection.ready;
    }
  } catch (error) {
    if (isCurrent(selection)) {
      const detail = typeof error === 'string' ? error
        : error && typeof error === 'object' && 'message' in error && typeof error.message === 'string'
          ? error.message : 'The history request failed.';
      setSelectedConversationPageError(selection.historyOwnedId, `Could not load ${direction} messages: ${detail}`);
    }
  } finally {
    if (isCurrent(selection)) {
      setSelectedConversationPageLoading(selection.historyOwnedId, direction, false);
    }
  }
}

export function selectedConversationHistoryOwnedId(): string | null {
  return active?.historyOwnedId ?? null;
}
