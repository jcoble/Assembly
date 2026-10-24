import {
  changeAgentConversationCheckoutFromTauri,
  createAgentConversationRequestId,
  listAgentConversationItemsAfterFromTauri,
  listAgentConversationItemsBeforeFromTauri,
  listAgentConversationSessionsFromTauri,
  listenHistoryDownloadProgress,
  listRemoteAgentConversationSessionsFromTauri,
  readAgentConversationCapabilitiesFromTauri,
  readAgentConversationChildHistoryFromTauri,
  readAgentConversationSelectionFromTauri,
  readHistoryDownloadProgressFromTauri,
  readRemoteAssemblyEnvironmentFromTauri,
  registerAgentConversationStream,
  registerAguiStream,
  stopAgentConversationChildHistoryFromTauri,
  writeTerminalSessionFromTauri,
  type AgentConversationSessionRecord,
  type BackgroundWorkItem,
  type ExecutionEnvironment,
  type HistoryDownloadProgress,
  type ProjectionStreamRegistration,
  type StreamEnvelope
} from '$lib/tauriSource';
import type { StreamChunk } from '@tanstack/ai/client';
import type { RunAgentInputContext } from '@tanstack/ai-client';
import { convertFileSrc, isTauri } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { get } from 'svelte/store';
import { findSessionRecord, normalizeProvider, ownedSessionFromBackend } from '../ownedSessions.ts';
import {
  revokeTrackedObjectUrl,
  trackTauriListener
} from '../resourceDiagnostics.svelte.ts';
import { sessionTitleFromPrompt } from '../sessionStrip.ts';
import type { RemoteConnectionState } from '../stores/sessionRailStore.svelte';
import { addOwnedSession, rail, setRemoteConnection, updateOwnedSession } from '../stores/sessionRailStore.svelte';
import { publishWorkspaceFileChange } from '../workspaceFileChangeBus.ts';
import {
  decideConversationActivation,
  generationForSend,
  sendSupportsImages,
  sendTargetGeneration,
  shouldReviveBeforeSend
} from './conversationActivation.ts';
import { setAgentConversationConfig } from './conversationConfig.ts';
import { ConversationDraftPersistence } from './conversationDraftPersistence.ts';
import { invokeConversationCommand as invoke } from './conversationInvoke.ts';
import { shouldClearConversationSending } from './conversationReducer.ts';
import {
  ACTIVE_EVENT_WINDOW_BYTES,
  ACTIVE_EVENT_WINDOW_EVENTS,
  applyChildConversationHistoryStatus,
  beginConversationConfigChange,
  clearConversationHistoryDownload,
  clearConversationWriterLeaseTransition,
  confirmConversationConfigChange,
  displayEventFrom,
  ensureConversationSession,
  failChildConversationTranscript,
  failConversationConfigChange,
  getConversationSession,
  recordAgentConversationPresenceEvent,
  recordSentConversationAttachments,
  setConversationAttachments,
  setConversationCapabilities,
  setConversationCapabilityError,
  setConversationConnection,
  setConversationDraft,
  setConversationHistoryDownload,
  setConversationSending,
  setConversationWriterLeaseTransition
} from './conversationStore.svelte.ts';
import { agentItemFromEvent, displayItemFromAgentItem } from './conversationTimeline.ts';
import type {
  AgentCapabilities,
  AgentConfigOption,
  AgentConfigValue,
  AgentConversationChildHistory,
  AgentConversationConnection,
  AgentConversationEvent,
  AgentConversationHandoffDirection,
  AgentConversationHandoffPhase,
  AgentConversationHandoffReceipt,
  AgentConversationHandoffRequest,
  AgentConversationProvider,
  AgentConversationSendReceipt,
  AguiPage,
  AguiSnapshot,
  AgentWriterLeaseOwner,
  AgentWriterLeaseTransition,
  ConversationAttachment
} from './conversationTypes.ts';
import {
  applyConversationHandoffReceipt,
  createConversationHandoffRequest,
  handoffGenerationMatches
} from './conversationTypes.ts';
import { sessionPresenceEventFromConversation, sessionPresenceHistory, synchronizeSessionPresenceWork } from './sessionPresence.ts';

let conversationStream: ProjectionStreamRegistration | null = null;
let conversationEventsController: AbortController | null = null;
let conversationEventsSetup: Promise<void> | null = null;
let conversationEventsDisposed = false;
let conversationEventsGeneration = 0;
let remoteActivityController: AbortController | null = null;
const railActivityEvents = new Map<string, { generation: number; sequence: number }>();
const CHILD_HISTORY_PAGE_BYTES = 512 * 1024;

type ConversationEnsure = {
  abortController: AbortController;
  invalidated: boolean;
  signature: string;
  token: object;
  work: Promise<AgentConversationConnection | null>;
};

type SelectedConversationRead = {
  workspaceOwnedId: string;
  ownedId: string;
  maxBytes: number;
  generation?: number;
  minimumGeneration?: number;
  pageWatermark: number;
  pendingSequence: number;
  events: AgentConversationEvent[];
  /** AG-UI envelopes that arrived during a snapshot read. */
  chunks: StreamEnvelope<StreamChunk[]>[];
  bytes: number;
  overflow: boolean;
  reading: boolean;
  reloadRequested: boolean;
  abortController: AbortController;
  onSnapshot: (snapshot: AguiSnapshot) => void;
  onEvent: (event: AgentConversationEvent) => void;
  onChunks: (chunks: StreamChunk[]) => void;
  onError: (error: unknown) => void;
};

/** A run of the stock chat client, for `run_agui`: answers on `resume`, and the id the chat gave the message it sends. */
export type AguiRun = Pick<RunAgentInputContext, 'threadId' | 'runId' | 'parentRunId' | 'resume'> & { messageId?: string };

const ensuring = new Map<string, ConversationEnsure>();
const terminalProjections = new Map<string, string>();
let selectedConversationRead: SelectedConversationRead | null = null;
let childConversationRead: SelectedConversationRead | null = null;

function isCurrentConversationRead(read: SelectedConversationRead): boolean {
  return !read.abortController.signal.aborted
    && (selectedConversationRead === read || childConversationRead === read);
}
const sessionDraftPersistence = new ConversationDraftPersistence(
  {
    set: (ownedId, text) => invoke('agent_conversation_set_session_draft', { ownedId, text }),
    get: (ownedId) => invoke<string | null>('agent_conversation_get_session_draft', { ownedId }),
    clear: (ownedId) => invoke('agent_conversation_clear_session_draft', { ownedId })
  },
  setConversationDraft
);

export function persistConversationSessionDraft(ownedId: string, text: string): void {
  if (isTauri()) sessionDraftPersistence.schedule(ownedId, text);
}

export async function flushConversationSessionDraft(ownedId: string): Promise<void> {
  if (isTauri()) await sessionDraftPersistence.flush(ownedId);
}

export async function loadConversationSessionDraft(ownedId: string): Promise<void> {
  if (isTauri()) await sessionDraftPersistence.load(ownedId);
}

export async function clearConversationSessionDraft(ownedId: string): Promise<void> {
  if (isTauri()) await sessionDraftPersistence.clear(ownedId);
}

/** Opens one selected child's durable history and owns its native watch. */
interface ChildHistoryRead {
  abortController: AbortController;
  requestId: number;
  childSessionId: string;
  active: boolean;
  settled: Promise<void>;
  stop(): void;
}

const childHistoryReads = new Map<string, ChildHistoryRead>();

export function stopChildConversationHistory(ownedId: string): void {
  const read = childHistoryReads.get(ownedId);
  if (!read) return;
  read.stop();
}

export async function readChildConversationHistory(input: {
  ownedId: string;
  childId: string;
  childSessionId: string;
  signal?: AbortSignal;
}): Promise<AgentConversationChildHistory | null> {
  if (input.signal?.aborted) return null;
  const previous = childHistoryReads.get(input.ownedId);
  stopChildConversationHistory(input.ownedId);
  const requestId = createAgentConversationRequestId();
  let readToken: ChildHistoryRead;
  const abortController = new AbortController();
  const stop = (): void => {
    readToken.active = false;
    abortController.abort();
    if (childHistoryReads.get(input.ownedId) === readToken) clearConversationHistoryDownload(input.ownedId, true);
    input.signal?.removeEventListener('abort', abortFromOwner);
    void stopAgentConversationChildHistoryFromTauri(input.ownedId, requestId);
    void forgetChildHistoryRead(input.ownedId, readToken);
  };
  const abortFromOwner = (): void => stop();
  readToken = { abortController, requestId, childSessionId: input.childId, active: true, settled: Promise.resolve(), stop };
  input.signal?.addEventListener('abort', abortFromOwner, { once: true });
  childHistoryReads.set(input.ownedId, readToken);
  const read = readChildHistoryPage(input, readToken, previous);
  readToken.settled = waitForChildHistoryRead(read);
  return await read;
}

/** Waits for one read without its error; the read's caller already gets it. */
async function waitForChildHistoryRead(read: Promise<unknown>): Promise<void> {
  try {
    await read;
  } catch {
    // The caller of readChildConversationHistory handles the failure.
  }
}

async function forgetChildHistoryRead(ownedId: string, readToken: ChildHistoryRead): Promise<void> {
  await readToken.settled;
  if (childHistoryReads.get(ownedId) === readToken) childHistoryReads.delete(ownedId);
}

async function readChildHistoryPage(
  input: { ownedId: string; childId: string; childSessionId: string; signal?: AbortSignal },
  readToken: ChildHistoryRead,
  previous: ChildHistoryRead | undefined
): Promise<AgentConversationChildHistory | null> {
  try {
    await previous?.settled;
    if (!readToken.active || input.signal?.aborted
      || childHistoryReads.get(input.ownedId) !== readToken) return null;
    await attachHistoryDownload(input.ownedId, input.ownedId, input.childSessionId, input.childId, readToken.abortController.signal);
    if (readToken.abortController.signal.aborted) return null;
    const result = await readAgentConversationChildHistoryFromTauri({
      parentOwnedId: input.ownedId,
      childSessionId: input.childSessionId,
      requestId: readToken.requestId,
      maxBytes: CHILD_HISTORY_PAGE_BYTES,
      signal: readToken.abortController.signal
    });
    const current = getConversationSession(input.ownedId);
    if (!readToken.active || input.signal?.aborted || childHistoryReads.get(input.ownedId) !== readToken
      || current?.selectedChildId !== input.childId) {
      readToken.stop();
      return null;
    }
    applyChildConversationHistoryStatus(
      input.ownedId,
      input.childId,
      result.page.hasEarlierTranscript
    );
    return result;
  } catch (error) {
    const ownsRead = childHistoryReads.get(input.ownedId) === readToken;
    if (ownsRead) {
      readToken.stop();
    }
    const current = getConversationSession(input.ownedId);
    if (ownsRead && !input.signal?.aborted && current?.selectedChildId === readToken.childSessionId) {
      failChildConversationTranscript(
        input.ownedId,
        input.childId,
        error instanceof Error ? error.message : String(error)
      );
    }
    throw error;
  }
}

export type SavedAttachment = Omit<ConversationAttachment, 'previewUrl' | 'originalUrl'> & { byteLength: number };
type AttachmentWithBytes = ConversationAttachment & { byteLength?: number; bytes?: number[] };

export interface AgentPromptTextContent {
  type: 'text';
  text: string;
}

export interface AgentPromptImageContent {
  type: 'image';
  mimeType: string;
  data: number[];
  name: string;
}

export type AgentPromptContent = AgentPromptTextContent | AgentPromptImageContent;

/** Build ordered ACP content blocks without turning typed images into Markdown. */
export function buildConversationPrompt(
  text: string,
  attachments: readonly AttachmentWithBytes[],
  supportsImages: boolean
): { text: string; content: AgentPromptContent[] } {
  if (attachments.length > 0 && !supportsImages) {
    throw new Error('This conversation provider does not advertise image prompts');
  }
  const content: AgentPromptContent[] = [];
  if (text.length > 0) content.push({ type: 'text', text });
  for (const attachment of attachments) {
    if (!attachment.bytes?.length) throw new Error(`Attachment ${attachment.name} is not available for image upload`);
    content.push({
      type: 'image',
      mimeType: attachment.mimeType,
      data: attachment.bytes,
      name: attachment.name
    });
  }
  return { text, content };
}

/** Saves one clipboard image through the owner-scoped native attachment vault. */
export async function saveConversationClipboardImage(
  ownedId: string,
  file: File,
  onSavedChange?: (attachment: SavedAttachment, retained: boolean) => Promise<void>
): Promise<ConversationAttachment> {
  const data = new Uint8Array(await file.arrayBuffer());
  let binary = '';
  for (let index = 0; index < data.length; index += 0x8000) {
    binary += String.fromCharCode(...data.subarray(index, index + 0x8000));
  }
  const bytes = btoa(binary);
  const saved = await invoke<SavedAttachment>('save_agent_conversation_attachment', {
    ownedId,
    mimeType: file.type,
    bytes
  });
  if (isRemoteConversation(ownedId)) {
    try {
      await onSavedChange?.(saved, true);
      return { ...saved, remoteOwnedId: ownedId, previewUrl: await remoteAttachmentPreview(ownedId, saved) };
    } catch (error) {
      try {
        await invoke('delete_agent_conversation_attachment', {
          request: { ownedId, attachmentId: saved.id, path: saved.path }
        });
        await onSavedChange?.(saved, false);
      } catch (_cleanupError) {
        // Keep the saved ID if its owner-scoped delete did not complete.
      }
      throw error;
    }
  }
  // No bytes are kept here: held in the composer's reactive state, a number
  // array is read element by element through its proxy at send, which held the
  // main thread for hundreds of milliseconds. The send reads the saved file.
  return restoreConversationAttachmentPreview(saved);
}

function isRemoteConversation(ownedId: string): boolean {
  return rail.owned.find((session) => session.ownedId === ownedId)?.executionEnvironment === 'remote';
}

async function remoteAttachmentPreview(ownedId: string, attachment: SavedAttachment, signal?: AbortSignal): Promise<string> {
  if (attachment.thumbnailByteLength == null) return '';
  const path = await invoke<string>('read_agent_conversation_attachment_file', {
    ownedId, attachmentId: attachment.id, thumbnail: true,
    byteLength: attachment.thumbnailByteLength,
    mimeType: attachment.thumbnailMimeType ?? 'image/webp',
    transferId: crypto.randomUUID()
  });
  if (signal?.aborted) throw new Error('Attachment preview cancelled');
  return convertFileSrc(path);
}

/** Revoke only URLs owned by this surface; the managed path never goes through the DOM. */
export function cleanupConversationAttachmentPreview(attachment: ConversationAttachment): void {
  if (attachment.previewUrl.startsWith('blob:')) revokeTrackedObjectUrl(attachment.previewUrl);
}

async function hydrateAttachmentBytes(attachment: AttachmentWithBytes): Promise<AttachmentWithBytes> {
  if (attachment.bytes?.length) return attachment;
  if (!attachment.path) throw new Error(`Attachment ${attachment.name} has no managed path`);
  const response = await fetch(isTauri() ? convertFileSrc(attachment.path) : attachment.path);
  if (!response.ok) throw new Error(`Could not read attachment ${attachment.name}`);
  return {
    ...attachment,
    bytes: Array.from(new Uint8Array(await response.arrayBuffer()))
  };
}

/** The transcript keeps only display metadata: thumbnails show what went out,
 * and no provider echoes the image back for it to render from. */
export function attachmentDisplayMetadata(attachment: ConversationAttachment): ConversationAttachment {
  const metadata: ConversationAttachment = {
    id: attachment.id,
    name: attachment.name,
    mimeType: attachment.mimeType,
    path: attachment.path,
    previewUrl: attachment.previewUrl,
    byteLength: attachment.byteLength,
    remoteOwnedId: attachment.remoteOwnedId
  };
  if (attachment.thumbnailMimeType !== undefined) metadata.thumbnailMimeType = attachment.thumbnailMimeType;
  if (attachment.thumbnailPath !== undefined) metadata.thumbnailPath = attachment.thumbnailPath;
  if (attachment.thumbnailByteLength !== undefined) {
    metadata.thumbnailByteLength = attachment.thumbnailByteLength;
  }
  return metadata;
}

/** Rebuild a preview URL from a backend-owned attachment after a workspace restore. */
export function restoreConversationAttachmentPreview(
  attachment: Omit<ConversationAttachment, 'previewUrl'> & { previewUrl?: string }
): ConversationAttachment {
  const previewUrl = attachment.previewUrl
    ?? (attachment.thumbnailPath
      ? (isTauri() ? convertFileSrc(attachment.thumbnailPath) : attachment.thumbnailPath)
      : '');
  return { ...attachment, previewUrl };
}

function conversationGenerationMatches(ownedId: string, generation: number | undefined): generation is number {
  return generation !== undefined && getConversationSession(ownedId)?.generation === generation;
}

export function discardRestoredAttachments(attachments: readonly ConversationAttachment[]): void {
  attachments.forEach(cleanupConversationAttachmentPreview);
}

/** Restore attachment metadata supplied by the existing owner-scoped vault. */
export async function restoreConversationAttachments(
  ownedId: string,
  saved: readonly (Omit<ConversationAttachment, 'previewUrl'> & { previewUrl?: string })[] = [],
  signal?: AbortSignal
): Promise<ConversationAttachment[]> {
  if (signal?.aborted) return [];
  const generation = getConversationSession(ownedId)?.generation;
  if (generation === undefined) return [];
  if (saved.length > 0) {
    const restored = await restoreAttachmentList(ownedId, saved as SavedAttachment[], signal,
      (items) => { if (conversationGenerationMatches(ownedId, generation)) setConversationAttachments(ownedId, items); });
    if (signal?.aborted || !conversationGenerationMatches(ownedId, generation)) {
      discardRestoredAttachments(restored);
      return [];
    }
    setConversationAttachments(ownedId, restored);
    return restored;
  }
  if (!isTauri()) return [];
  try {
    const draftIds = new Set(getConversationSession(ownedId)?.attachmentIds ?? []);
    const records = await invoke<(Omit<ConversationAttachment, 'previewUrl'> & { previewUrl?: string })[]>(
      'read_agent_conversation_attachments',
      { ownedId }
    );
    const restored = Array.isArray(records) ? await restoreAttachmentList(ownedId, (records as SavedAttachment[]).filter((record) => draftIds.has(record.id)), signal,
      (items) => { if (conversationGenerationMatches(ownedId, generation)) setConversationAttachments(ownedId, items); }) : [];
    if (signal?.aborted || !conversationGenerationMatches(ownedId, generation)) {
      discardRestoredAttachments(restored);
      return [];
    }
    setConversationAttachments(ownedId, restored);
    return restored;
  } catch (_error) {
    // The invoke seam logged the sanitized failure before this fallback runs.
    // Older controller builds have no listing command. The draft and saved ids
    // remain intact so a later controller can hydrate them without data loss.
    return [];
  }
}

export async function restoreAttachmentList(
  ownedId: string, records: readonly SavedAttachment[], signal?: AbortSignal,
  onUpdate?: (attachments: ConversationAttachment[]) => void
): Promise<ConversationAttachment[]> {
  const remote = isRemoteConversation(ownedId);
  const restored = records.map((record) => {
    if (remote) return { ...record, remoteOwnedId: ownedId, previewUrl: '', previewLoading: record.thumbnailByteLength != null };
    try { return restoreConversationAttachmentPreview(record); }
    catch { return { ...record, previewUrl: '' }; }
  });
  onUpdate?.([...restored]);
  if (!remote) return restored;
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(3, records.length) }, async () => {
    while (!signal?.aborted && next < records.length) {
      const index = next++;
      try {
        const previewUrl = await remoteAttachmentPreview(ownedId, records[index], signal);
        if (signal?.aborted) return;
        restored[index] = { ...restored[index], previewUrl, previewLoading: false };
      } catch {
        if (signal?.aborted) return;
        restored[index] = { ...restored[index], previewLoading: false };
      }
      onUpdate?.([...restored]);
    }
  }));
  if (signal?.aborted) {
    discardRestoredAttachments(restored);
    return [];
  }
  return restored;
}

/** Ask the owner-validated vault to delete one managed file, then revoke its URL. */
export async function removeConversationAttachment(
  ownedId: string,
  attachment: ConversationAttachment
): Promise<void> {
  await cleanupConversationAttachment(ownedId, attachment);
  const state = getConversationSession(ownedId);
  if (state) setConversationAttachments(ownedId, state.attachments.filter((item) => item.id !== attachment.id));
}

/** Cleanup for a newly saved attachment that has not been added to the store yet. */
export async function cleanupConversationAttachment(
  ownedId: string,
  attachment: ConversationAttachment
): Promise<void> {
  try {
    if (isTauri()) {
      await invoke('delete_agent_conversation_attachment', {
        request: { ownedId, attachmentId: attachment.id, path: attachment.path }
      });
    }
  } finally {
    cleanupConversationAttachmentPreview(attachment);
  }
}

export async function loadConversationCapabilities(
  ownedId: string,
  provider: AgentConversationProvider,
  signal?: AbortSignal
): Promise<AgentCapabilities | null> {
  if (!isTauri() || signal?.aborted) return null;
  const generation = getConversationSession(ownedId)?.generation;
  if (generation === undefined) return null;
  try {
    const capabilities = await readAgentConversationCapabilitiesFromTauri(ownedId, signal);
    if (!capabilities) return null;
    if (signal?.aborted || !conversationGenerationMatches(ownedId, generation)) return null;
    if (capabilities.provider !== provider) throw new Error('Capability provider does not match this session');
    setConversationCapabilities(ownedId, generation, capabilities);
    // Same negotiated value as provider_supports_changing_checkout in capabilities.rs; the backend still rejects unsupported changes.
    updateOwnedSession(ownedId, { canChangeCheckout: capabilities.session.resume });
    return capabilities;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (!signal?.aborted && conversationGenerationMatches(ownedId, generation)) {
      setConversationCapabilityError(ownedId, message);
    }
    throw error;
  }
}

/** Applies one advertised config choice and rejects stale provider responses. */
export async function setConversationConfigOption(
  ownedId: string,
  optionId: string,
  value: AgentConfigValue
): Promise<AgentCapabilities | null> {
  const state = getConversationSession(ownedId);
  const option = state?.capabilities?.configOptions.find((candidate) => candidate.id === optionId);
  if (!state || !option || (option.choices && !option.choices.some((choice) => JSON.stringify(choice.value) === JSON.stringify(value)))) {
    throw new Error('This configuration value is not advertised for the session');
  }
  if (!beginConversationConfigChange(ownedId, optionId, value)) throw new Error('Configuration change is not available');
  try {
    if (!isTauri()) throw new Error('The structured conversation runtime is unavailable');
    const response = await invoke<{ capabilities?: AgentCapabilities; configOptions?: AgentConfigOption[] }>(
      'set_agent_conversation_config_option',
      { request: { ownedId, generation: state.generation, optionId, value } }
    );
    const latest = getConversationSession(ownedId);
    if (!latest || latest.generation !== state.generation) {
      throw new Error('Configuration response belongs to a stale conversation generation');
    }
    const capabilities = response?.capabilities
      ?? (response?.configOptions ? { ...state.capabilities, configOptions: response.configOptions } as AgentCapabilities : state.capabilities);
    if (!capabilities || capabilities.provider !== state.provider) {
      throw new Error('Configuration response provider does not match this session');
    }
    confirmConversationConfigChange(ownedId, optionId, value, capabilities);
    return capabilities;
  } catch (error) {
    if (getConversationSession(ownedId)?.generation === state.generation) {
      failConversationConfigChange(ownedId, optionId, error instanceof Error ? error.message : String(error));
    }
    throw error;
  }
}

/** Starts best-effort terminal transcript projection for one conversation owner. */
export function startConversationTerminalProjection(input: {
  ownedId: string;
  provider: AgentConversationProvider;
  nativeSessionId: string;
}): void {
  if (!isTauri()) return;
  const signature = `${input.provider}:${input.nativeSessionId}`;
  if (terminalProjections.get(input.ownedId) === signature) return;
  terminalProjections.set(input.ownedId, signature);
  void startTerminalProjection(input, signature);
}

/** Stops terminal transcript projection when this surface no longer needs it. */
export function stopConversationTerminalProjection(ownedId: string): void {
  if (!terminalProjections.delete(ownedId) || !isTauri()) return;
  void stopTerminalProjection(ownedId);
}

async function stopTerminalProjection(ownedId: string): Promise<void> {
  try {
    await invoke<boolean>('stop_agent_conversation_terminal_projection', { ownedId });
  } catch {
    // A stale projection owner was already released locally.
  }
}

async function startTerminalProjection(
  input: {
    ownedId: string;
    provider: AgentConversationProvider;
    nativeSessionId: string;
  },
  signature: string
): Promise<void> {
  try {
    await invoke<Record<string, never>>('start_agent_conversation_terminal_projection', {
      request: input
    });
    if (terminalProjections.get(input.ownedId) !== signature) {
      await stopTerminalProjection(input.ownedId);
    }
  } catch {
    // The invoke seam logged the sanitized failure before this retry state resets.
    // The transcript may not exist until the agent accepts its first prompt.
    // Dropping the signature lets the next activation register again.
    if (terminalProjections.get(input.ownedId) === signature) terminalProjections.delete(input.ownedId);
  }
}

type HandoffInput = Omit<AgentConversationHandoffRequest, 'phase'>;

function handoffTargetOwner(direction: AgentConversationHandoffDirection): AgentWriterLeaseOwner {
  return direction === 'structured-to-terminal' ? 'terminal' : 'structured';
}

function applyHandoffReceiptToStore(receipt: AgentConversationHandoffReceipt): void {
  const state = getConversationSession(receipt.ownedId);
  if (!state || !handoffGenerationMatches(receipt, receipt.ownedId, state.generation)) return;
  if (receipt.phase === 'prepare' || receipt.phase === 'prepared') {
    const transition: AgentWriterLeaseTransition = {
      ownedId: receipt.ownedId,
      generation: receipt.generation,
      from: receipt.previousOwner,
      to: handoffTargetOwner(receipt.direction),
      state: 'requested'
    };
    setConversationWriterLeaseTransition(transition);
    return;
  }
  const next = applyConversationHandoffReceipt(state, receipt);
  Object.assign(state, next);
}

/** Invokes one handoff phase and keeps its writer-lease transition consistent. */
async function invokeHandoff(
  input: HandoffInput,
  phase: AgentConversationHandoffPhase
): Promise<AgentConversationHandoffReceipt> {
  if (!isTauri()) throw new Error('Native handoff is available only in the desktop application');
  const request = createConversationHandoffRequest({ ...input, phase });
  const current = getConversationSession(request.ownedId);
  if (!current || current.generation !== request.generation) {
    throw new Error('Handoff request belongs to a stale conversation generation');
  }
  try {
    const receipt = await invoke<AgentConversationHandoffReceipt>('handoff_agent_conversation', { request });
    if (!handoffGenerationMatches(receipt, request.ownedId, request.generation)) {
      throw new Error('Handoff receipt belongs to a stale conversation generation');
    }
    applyHandoffReceiptToStore(receipt);
    return receipt;
  } catch (error) {
    clearConversationWriterLeaseTransition(request.ownedId, request.generation);
    throw error;
  }
}

export async function prepareConversationHandoff(input: HandoffInput): Promise<AgentConversationHandoffReceipt> {
  const receipt = await invokeHandoff(input, 'prepare');
  return receipt;
}

export async function commitConversationHandoff(input: HandoffInput): Promise<AgentConversationHandoffReceipt> {
  const receipt = await invokeHandoff(input, 'commit');
  return receipt;
}

export async function rollbackConversationHandoff(input: HandoffInput): Promise<AgentConversationHandoffReceipt> {
  const receipt = await invokeHandoff(input, 'rollback');
  return receipt;
}

function selectedEventUsesControlCursor(event: AgentConversationEvent): boolean {
  return event.payload.kind === 'approval'
    || event.payload.kind === 'userInputRequested'
    || event.payload.kind === 'userInputResolved'
    || event.payload.kind === 'turn'
    || event.payload.kind === 'connection'
    || event.payload.kind === 'error'
    || event.payload.kind === 'childUpdate'
    || (event.payload.kind === 'terminalProjection'
      && event.payload.eventType === 'children.updated');
}

function deliverSelectedConversationEvent(
  read: SelectedConversationRead,
  event: AgentConversationEvent
): void {
  if (event.generation !== read.generation) {
    if (event.generation > (read.generation ?? 0)) {
      read.minimumGeneration = Math.max(read.minimumGeneration ?? 0, event.generation);
      requestSelectedConversationReload(read);
    }
    return;
  }
  if (event.sequence <= read.pageWatermark) return;
  if (event.sequence !== read.pageWatermark + 1) {
    requestSelectedConversationReload(read);
    return;
  }
  read.pageWatermark = event.sequence;
  const control = selectedEventUsesControlCursor(event);
  if (control && event.sequence <= read.pendingSequence) return;
  if (control) read.pendingSequence = event.sequence;
  read.onEvent(event);
}

function requestSelectedConversationReload(read: SelectedConversationRead): void {
  read.reloadRequested = true;
  if (read.reading) return;
  void reloadOrReportSelectedConversation(read);
}

async function reloadOrReportSelectedConversation(read: SelectedConversationRead): Promise<void> {
  try {
    await reloadSelectedConversation(read);
  } catch (error) {
    if (isCurrentConversationRead(read) && !read.abortController.signal.aborted) {
      read.events.length = 0;
      read.bytes = 0;
      read.onError(error);
    }
  }
}

function bufferSelectedConversationEvent(
  read: SelectedConversationRead,
  event: AgentConversationEvent
): void {
  if (read.overflow || read.abortController.signal.aborted) return;
  read.bytes += new TextEncoder().encode(JSON.stringify(event)).byteLength;
  if (read.events.length >= ACTIVE_EVENT_WINDOW_EVENTS || read.bytes > ACTIVE_EVENT_WINDOW_BYTES) {
    read.events.length = 0;
    read.overflow = true;
    return;
  }
  read.events.push(event);
}

function admitSelectedConversationEvent(event: AgentConversationEvent): boolean {
  const read = selectedConversationRead?.ownedId === event.ownedId ? selectedConversationRead : childConversationRead;
  if (!read || read.ownedId !== event.ownedId || read.abortController.signal.aborted) return false;
  if (read.reading) bufferSelectedConversationEvent(read, event);
  else deliverSelectedConversationEvent(read, event);
  return true;
}

/** The chat's chunks for the selected session; during a snapshot read they wait for it. */
function admitSelectedConversationChunks(envelope: StreamEnvelope<StreamChunk[]>): void {
  const read = selectedConversationRead?.ownedId === envelope.sessionId ? selectedConversationRead : childConversationRead;
  if (!read || read.ownedId !== envelope.sessionId || read.abortController.signal.aborted) return;
  if (read.reading) read.chunks.push(envelope);
  else read.onChunks(envelope.chunk);
}

async function attachHistoryDownload(workspaceOwnedId: string, ownedId: string, childSessionId: string | null, childId: string | null, signal: AbortSignal): Promise<void> {
  const receive = (progress: HistoryDownloadProgress): void => {
    if (signal.aborted || progress.workspaceOwnedId !== workspaceOwnedId || progress.childSessionId !== childSessionId) return;
    setConversationHistoryDownload(workspaceOwnedId, { ...progress, childSessionId: childId });
  };
  await listenHistoryDownloadProgress(receive, signal);
  if (signal.aborted) return;
  const progress = await readHistoryDownloadProgressFromTauri(ownedId, signal);
  if (signal.aborted) return;
  if (progress) receive(progress);
}

async function reloadSelectedConversation(read: SelectedConversationRead): Promise<void> {
  if (!isCurrentConversationRead(read) || read.abortController.signal.aborted) return;
  if (read.reading) {
    read.reloadRequested = true;
    return;
  }
  read.reading = true;
  try {
    // One retry covers a generation change or bounded-buffer overflow that
    // races the first read. The second read is authoritative for both content
    // and pending controls; repeated churn is surfaced instead of looping.
    for (let attempt = 0; attempt < 2; attempt += 1) {
      read.reloadRequested = false;
      read.events.length = 0;
      read.chunks.length = 0;
      read.bytes = 0;
      read.overflow = false;
      const snapshot = await readAgentConversationSelectionFromTauri(
        read.ownedId,
        read.maxBytes,
        read.minimumGeneration,
        read.abortController.signal
      );
      if (!isCurrentConversationRead(read) || read.abortController.signal.aborted || !snapshot) return;
      if (read.overflow) {
        read.reloadRequested = true;
        continue;
      }
      if (snapshot.connection.generation < (read.minimumGeneration ?? 0)) {
        read.reloadRequested = true;
        continue;
      }
      read.generation = snapshot.connection.generation;
      read.minimumGeneration = undefined;
      read.pageWatermark = snapshot.page.coverage?.high ?? snapshot.page.watermark;
      read.pendingSequence = snapshot.pendingSequence;
      read.onSnapshot(snapshot);
      const replay = [...read.events].sort((left, right) => left.sequence - right.sequence);
      read.events.length = 0;
      for (const event of replay) deliverSelectedConversationEvent(read, event);
      // The snapshot covers everything up to its head; the view sent only what came after.
      const chunks = read.chunks.splice(0).filter((envelope) => envelope.sequence > snapshot.pendingSequence);
      for (const envelope of chunks) read.onChunks(envelope.chunk);
      if (!read.reloadRequested) return;
    }
    throw new Error('Conversation changed too quickly to open its selected history');
  } finally {
    read.reading = false;
  }
}

async function refreshSelectedConversation(ownedId: string): Promise<void> {
  const read = selectedConversationRead;
  if (!read || read.ownedId !== ownedId) return;
  await reloadSelectedConversation(read);
}

export async function subscribeSelectedConversation(input: {
  workspaceOwnedId: string;
  ownedId: string;
  maxBytes: number;
  signal?: AbortSignal;
  onSnapshot: (snapshot: AguiSnapshot) => void;
  onEvent: (event: AgentConversationEvent) => void;
  onChunks: (chunks: StreamChunk[]) => void;
  onError: (error: unknown) => void;
}): Promise<() => void> {
  if (input.signal?.aborted) return () => {};
  const child = input.ownedId !== input.workspaceOwnedId;
  (child ? childConversationRead : selectedConversationRead)?.abortController.abort();
  const abortController = new AbortController();
  const abortFromOwner = (): void => abortController.abort();
  input.signal?.addEventListener('abort', abortFromOwner, { once: true });
  const read: SelectedConversationRead = {
    workspaceOwnedId: input.workspaceOwnedId,
    ownedId: input.ownedId,
    maxBytes: input.maxBytes,
    pageWatermark: Number.MIN_SAFE_INTEGER,
    pendingSequence: Number.MIN_SAFE_INTEGER,
    events: [],
    chunks: [],
    bytes: 0,
    overflow: false,
    // Buffer any event delivered while the shared listener is registering.
    // The snapshot started immediately afterwards is authoritative for them.
    reading: true,
    reloadRequested: false,
    abortController,
    onSnapshot: input.onSnapshot,
    onEvent: input.onEvent,
    onChunks: input.onChunks,
    onError: input.onError
  };
  if (child) childConversationRead = read;
  else selectedConversationRead = read;
  const dispose = (): void => {
    input.signal?.removeEventListener('abort', abortFromOwner);
    abortController.signal.removeEventListener('abort', dispose);
    abortController.abort();
    read.events.length = 0;
    read.chunks.length = 0;
    read.bytes = 0;
    if ((child ? childConversationRead : selectedConversationRead) === read) clearConversationHistoryDownload(input.workspaceOwnedId, child);
    if (selectedConversationRead === read) selectedConversationRead = null;
    if (childConversationRead === read) childConversationRead = null;
  };
  abortController.signal.addEventListener('abort', dispose, { once: true });
  try {
    await startConversationEvents();
    if (abortController.signal.aborted) return dispose;
    // Initial child resolution owns its progress subscription before the child key exists.
    if (!child) await attachHistoryDownload(input.workspaceOwnedId, input.ownedId, null, null, abortController.signal);
    if (abortController.signal.aborted) return dispose;
    read.reading = false;
    await reloadSelectedConversation(read);
    return dispose;
  } catch (error) {
    dispose();
    throw error;
  }
}

export async function readOlderSelectedConversationItems(
  ownedId: string,
  beforeSequence: number,
  maxBytes: number,
  signal?: AbortSignal
): Promise<AguiPage | null> {
  return await listAgentConversationItemsBeforeFromTauri(
    ownedId, beforeSequence, maxBytes, signal
  );
}

export async function readNewerSelectedConversationItems(
  ownedId: string,
  afterSequence: number,
  maxBytes: number,
  signal?: AbortSignal
): Promise<AguiPage | null> {
  return await listAgentConversationItemsAfterFromTauri(
    ownedId, afterSequence, maxBytes, signal
  );
}

export async function readSelectedConversationAttachments(
  ownedId: string,
  attachmentIds: readonly string[],
  signal?: AbortSignal
): Promise<SavedAttachment[]> {
  if (!isTauri() || signal?.aborted || attachmentIds.length === 0) return [];
  const rows = await invoke<SavedAttachment[]>('read_agent_conversation_selected_attachments', {
    ownedId,
    attachmentIds: [...attachmentIds]
  });
  return signal?.aborted ? [] : rows;
}

export async function startConversationEvents(): Promise<void> {
  if (!isTauri() || conversationStream) return;
  if (conversationEventsSetup) return conversationEventsSetup;
  conversationEventsDisposed = false;
  const streamGeneration = ++conversationEventsGeneration;
  const controller = new AbortController();
  conversationEventsController = controller;
  const setup = setupConversationEvents(streamGeneration, controller.signal);
  conversationEventsSetup = setup;
  try {
    await setup;
  } finally {
    if (conversationEventsSetup === setup) conversationEventsSetup = null;
  }
}

async function setupConversationEvents(streamGeneration: number, signal: AbortSignal): Promise<void> {
  let registration: ProjectionStreamRegistration | null = null;
  let chunkRegistration: ProjectionStreamRegistration | null = null;
  let stopTitles: (() => void) | null = null;
  let stopRemoteConnections: (() => void) | null = null;
  const dispose = (): void => {
    stopTitles?.();
    stopTitles = null;
    stopRemoteConnections?.();
    stopRemoteConnections = null;
    if (conversationStream === registration) conversationStream = null;
    // unregister deactivates delivery immediately and handles native cleanup errors.
    void registration?.unregister();
    registration = null;
    void chunkRegistration?.unregister();
    chunkRegistration = null;
  };
  signal.addEventListener('abort', dispose, { once: true });
  const remoteConnectionEventsSeen = new Set<string>();
  try {
    registration = await registerAgentConversationStream(
      (envelope) => handleConversationStreamEnvelope(streamGeneration, envelope),
      () => handleConversationStreamResync(streamGeneration)
    );
    if (signal.aborted || !registration) { dispose(); return; }
    chunkRegistration = await registerAguiStream(
      (envelope) => {
        if (!conversationEventsDisposed && streamGeneration === conversationEventsGeneration) admitSelectedConversationChunks(envelope);
      },
      () => handleConversationStreamResync(streamGeneration)
    );
    if (signal.aborted) { dispose(); return; }
    stopTitles = trackTauriListener(await listen<{ ownedId: string; title: string }>(
      'session-title-changed', ({ payload }) => {
        if (registration && !signal.aborted) showNamedSession(payload.ownedId, payload.title, streamGeneration);
      }
    ));
    if (signal.aborted) { dispose(); return; }
    stopRemoteConnections = trackTauriListener(await listen<{
      profileId: string; state: RemoteConnectionState;
    }>('remote-connection-changed', async ({ payload }) => {
      if (!registration || signal.aborted) return;
      remoteConnectionEventsSeen.add(payload.profileId);
      setRemoteConnection(payload.profileId, payload.state);
      if (payload.state === 'connected') {
        await Promise.all([refreshRemoteSessionActivity(streamGeneration),
          reacquireSelectedChildHistory(payload.profileId, streamGeneration)]);
      }
    }));
    if (signal.aborted) { dispose(); return; }
    const remoteEnvironment = await readRemoteAssemblyEnvironmentFromTauri();
    if (signal.aborted) return;
    rail.remoteProfiles = remoteEnvironment.profiles;
    const readyRemoteProfiles = new Set(remoteEnvironment.readyProfileIds);
    for (const profile of remoteEnvironment.profiles) {
      if (remoteConnectionEventsSeen.has(profile.id)) continue;
      setRemoteConnection(profile.id, readyRemoteProfiles.has(profile.id) ? 'connected' : 'disconnected');
    }
    conversationStream = registration;
    // Chat startup does not wait for remote inventory; shutdown cancels its read.
    void refreshRemoteSessionActivity(streamGeneration);
  } catch (error) {
    dispose();
    signal.removeEventListener('abort', dispose);
    if (!signal.aborted) throw error;
  }
}

/** Reconcile compact runtime records after reconnect, without loading transcripts. */
async function refreshRemoteSessionActivity(streamGeneration: number, readAgain = true): Promise<void> {
  if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration) return;
  remoteActivityController?.abort();
  const controller = new AbortController();
  remoteActivityController = controller;
  const observed = new Map(railActivityEvents);
  let raced = false;
  try {
    const records = await listRemoteAgentConversationSessionsFromTauri(controller.signal);
    if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration || controller.signal.aborted) return;
    for (const record of records ?? []) {
      // An event during the read is newer than this reply. Read again: after a restart it may be a replay.
      if (observed.get(record.ownedId) !== railActivityEvents.get(record.ownedId)) { raced = true; continue; }
      if (rail.remoteConnections[record.remoteProfileId ?? ''] !== 'connected' || preparingSends.has(record.ownedId)) continue;
      updateOwnedSession(record.ownedId, {
        state: record.activeTurnId ? 'live' : 'background',
        runtimeState: record.suspended ? 'suspended' : record.state,
        activeTurnId: record.activeTurnId,
        pendingPermission: record.pendingPermission,
        pendingInput: record.pendingInput,
        backgroundWork: record.backgroundWork, completedAt: record.completedAt
      });
      synchronizeSessionPresenceWork(record.ownedId, record.activeTurnId, false, null);
      const current = getConversationSession(record.ownedId);
      if (current) {
        current.activeTurnId = record.activeTurnId ?? undefined;
        current.suspended = record.suspended;
        if (!record.activeTurnId) setConversationSending(record.ownedId, false);
      }
    }
    if (raced && readAgain) await refreshRemoteSessionActivity(streamGeneration, false);
  } catch (error) {
    if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration || controller.signal.aborted) return;
    rail.error = `Could not refresh remote turn activity: ${error instanceof Error ? error.message : String(error)}`;
  } finally {
    if (remoteActivityController === controller) remoteActivityController = null;
  }
}

// A session an agent started (a helper, a workflow worker) may live on any
// machine. The rail adds it the first time an event, its name or a click
// names it. A new session's first events can come before its row is stored, and
// a helper waiting on a permission sends nothing more, so a miss is looked up
// again twice, five seconds apart. After that the session is not looked up for
// five seconds.
const SESSION_LOOKUP_RETRY_MS = 5_000;
const SESSION_LOOKUPS = 3;
let adoptController = new AbortController();
const adoptingSessions = new Map<string, Promise<boolean>>();
const sessionsNotFound = new Map<string, number>();

/**
 * Adds a session the rail lacks from whichever machine has it; true when the rail has it.
 * A panel passes its own `signal` and gets a lookup of its own that stops when the panel does.
 */
export async function adoptSession(ownedId: string, signal?: AbortSignal): Promise<boolean> {
  if (rail.owned.some((session) => session.ownedId === ownedId)) return true;
  if (Date.now() - (sessionsNotFound.get(ownedId) ?? -Infinity) < SESSION_LOOKUP_RETRY_MS) return false;
  if (signal) return lookUpSession(ownedId, AbortSignal.any([adoptController.signal, signal]));
  const pending = adoptingSessions.get(ownedId);
  if (pending) return pending;
  const lookup = lookUpSession(ownedId, adoptController.signal);
  adoptingSessions.set(ownedId, lookup);
  try {
    return await lookup;
  } finally {
    adoptingSessions.delete(ownedId);
  }
}

const SESSION_READERS = {
  local: listAgentConversationSessionsFromTauri,
  remote: listRemoteAgentConversationSessionsFromTauri
};

async function lookUpSession(ownedId: string, signal: AbortSignal): Promise<boolean> {
  for (let lookup = 1; ; lookup += 1) {
    try {
      const seen = railActivityEvents.get(ownedId);
      const record = await findSessionRecord(ownedId, SESSION_READERS, signal);
      if (signal.aborted) return false;
      if (record) {
        sessionsNotFound.delete(ownedId);
        if (!rail.owned.some((session) => session.ownedId === ownedId)) addOwnedSession(ownedSessionFromBackend(record));
        // An event that came during the read found no row and changed nothing; read the state again.
        if (railActivityEvents.get(ownedId) !== seen) await refreshAdoptedSession(ownedId, signal);
        return true;
      }
    } catch (error) {
      if (signal.aborted) return false;
      rail.error = `Could not show a new session: ${error instanceof Error ? error.message : String(error)}`;
    }
    if (lookup === SESSION_LOOKUPS) {
      sessionsNotFound.set(ownedId, Date.now());
      return false;
    }
    await waitFor(SESSION_LOOKUP_RETRY_MS, signal);
    if (signal.aborted) return false;
    if (rail.owned.some((session) => session.ownedId === ownedId)) return true;
  }
}

/** Reads an adopted session's live state once more, unless an event during the read is newer. */
async function refreshAdoptedSession(ownedId: string, signal: AbortSignal): Promise<void> {
  const seen = railActivityEvents.get(ownedId);
  try {
    const record = await findSessionRecord(ownedId, SESSION_READERS, signal);
    if (signal.aborted || !record || railActivityEvents.get(ownedId) !== seen) return;
    const fresh = ownedSessionFromBackend(record);
    updateOwnedSession(ownedId, {
      state: fresh.state, canChangeCheckout: fresh.canChangeCheckout,
      runtimeState: fresh.runtimeState,
      activeTurnId: fresh.activeTurnId,
      pendingPermission: fresh.pendingPermission,
      pendingInput: fresh.pendingInput,
      backgroundWork: fresh.backgroundWork
    });
  } catch (error) {
    if (!signal.aborted) rail.error = `Could not refresh a new session: ${error instanceof Error ? error.message : String(error)}`;
  }
}

function showNamedSession(ownedId: string, title: string, streamGeneration: number): void {
  // A name heard after teardown, or from an older stream, starts nothing.
  if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration) return;
  if (rail.owned.some((session) => session.ownedId === ownedId)) updateOwnedSession(ownedId, { title });
  else void adoptSession(ownedId);
}

function updateBackgroundWork(
  work: BackgroundWorkItem[],
  item: Omit<BackgroundWorkItem, 'startedAtMs'>,
  timestampMs: number,
  terminal: boolean
): BackgroundWorkItem[] {
  if (terminal) return work.filter((entry) => entry.id !== item.id);
  const existing = work.find((entry) => entry.id === item.id);
  const next = {
    ...item,
    label: item.label || existing?.label || '',
    startedAtMs: existing?.startedAtMs ?? timestampMs
  };
  return existing
    ? work.map((entry) => entry.id === item.id ? next : entry)
    : [...work, next];
}

async function reacquireSelectedChildHistory(
  profileId: string,
  streamGeneration: number
): Promise<void> {
  if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration) return;
  const parent = selectedConversationRead;
  if (parent && rail.owned.find((session) => session.ownedId === parent.workspaceOwnedId)?.remoteProfileId === profileId) {
    requestSelectedConversationReload(parent);
  }
  const read = childConversationRead;
  if (!read || read.workspaceOwnedId === read.ownedId || read.abortController.signal.aborted) return;
  const helper = rail.owned.find((session) => session.ownedId === read.ownedId);
  if (helper) {
    if (helper.remoteProfileId === profileId) requestSelectedConversationReload(read);
    return;
  }
  const owned = rail.owned.find((session) => session.ownedId === read.workspaceOwnedId);
  if (owned?.remoteProfileId !== profileId) return;
  const current = getConversationSession(read.workspaceOwnedId);
  const childId = current?.selectedChildId;
  const child = current?.children.find((candidate) => candidate.childId === childId);
  if (!childId || !child?.transcriptAvailable) return;
  let history: AgentConversationChildHistory | null;
  try {
    history = await readChildConversationHistory({
      ownedId: read.workspaceOwnedId,
      childId,
      childSessionId: child.transcriptId ?? childId,
      signal: read.abortController.signal
    });
  } catch {
    // The child history read reports its own failure on the transcript.
    return;
  }
  if (!history || !isCurrentConversationRead(read) || read.abortController.signal.aborted
    || getConversationSession(read.workspaceOwnedId)?.selectedChildId !== childId
    || history.historyOwnedId !== read.ownedId) return;
  requestSelectedConversationReload(read);
}

function handleConversationStreamEnvelope(
  streamGeneration: number,
  envelope: StreamEnvelope<AgentConversationEvent>
): void {
  if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration) return;
  const payload = envelope.chunk;
  admitSelectedConversationEvent(payload);
  const previous = railActivityEvents.get(payload.ownedId);
  if (previous && (payload.generation < previous.generation
    || (payload.generation === previous.generation && payload.sequence <= previous.sequence))) {
    return;
  }
  const displayEvent = displayEventFrom(payload);
  const terminal = shouldClearConversationSending(displayEvent);
  const current = getConversationSession(payload.ownedId);
  const presence = get(sessionPresenceHistory)[payload.ownedId];
  const activeTurnId = presence?.activeTurnId || current?.activeTurnId;
  if (current && payload.generation < current.generation) return;
  if (terminal && activeTurnId && (
    (presence?.turnStartedAt !== null && presence?.turnStartedAt !== undefined && payload.timestampMs < presence.turnStartedAt)
    || (payload.payload.kind === 'turn' && payload.payload.turnId !== activeTurnId)
  )) return;
  railActivityEvents.set(payload.ownedId, { generation: payload.generation, sequence: payload.sequence });
  // The lookup reads the session's current state, a pending permission included.
  // An open sub-agent transcript has a private id that is never in the rail.
  if (childConversationRead?.ownedId !== payload.ownedId
    && !rail.owned.some((session) => session.ownedId === payload.ownedId)) void adoptSession(payload.ownedId);
  const item = agentItemFromEvent(displayEvent);
  const row = item ? displayItemFromAgentItem(item, displayEvent.timestampMs) : null;
  if (row?.kind === 'tool' && row.state === 'completed' && row.path && row.diff) {
    publishWorkspaceFileChange({ ownedId: payload.ownedId, path: row.path });
  }
  if (payload.payload.kind === 'sessionCompleted') updateOwnedSession(payload.ownedId, { completedAt: payload.payload.completedAt });
  if (payload.payload.kind === 'childUpdate') {
    const owned = rail.owned.find((session) => session.ownedId === payload.ownedId);
    if (owned) {
      const terminalChild = ['finished', 'failed', 'cancelled', 'disconnected'].includes(payload.payload.state);
      updateOwnedSession(payload.ownedId, {
        backgroundWork: updateBackgroundWork(
          owned.backgroundWork ?? [],
          {
            id: `claude-child:${payload.payload.childId}`,
            kind: 'subagent',
            label: payload.payload.label ?? ''
          },
          payload.timestampMs,
          terminalChild
        )
      });
    }
  }
  if (payload.payload.kind === 'tool' && payload.payload.itemId.startsWith('background-task:')) {
    const owned = rail.owned.find((session) => session.ownedId === payload.ownedId);
    if (owned) {
      updateOwnedSession(payload.ownedId, {
        backgroundWork: updateBackgroundWork(
          owned.backgroundWork ?? [],
          { id: payload.payload.itemId, kind: 'command', label: payload.payload.name },
          payload.timestampMs,
          payload.payload.state !== 'started' && payload.payload.state !== 'updated'
        )
      });
    }
  }
  const transition = sessionPresenceEventFromConversation(displayEvent);
  if (transition?.kind === 'turn-started' || terminal) {
    const working = transition?.kind === 'turn-started';
    updateOwnedSession(payload.ownedId, {
      state: working ? 'live' : 'background',
      runtimeState: working ? 'working' : transition?.kind === 'turn-finished' ? 'ready' : 'failed',
      activeTurnId: working ? transition?.turnId ?? null : null,
      pendingPermission: false,
      pendingInput: false,
      ...(working ? { lastError: null } : {})
    });
  }
  if (payload.runtimeState) updateOwnedSession(payload.ownedId, {
    runtimeState: payload.runtimeState,
    ...(['ready', 'suspended', 'closed', 'failed'].includes(payload.runtimeState) ? { backgroundWork: [] } : {})
  });
  if (terminal && !transition) synchronizeSessionPresenceWork(payload.ownedId, null, false);
  recordAgentConversationPresenceEvent(payload);
  if (
    payload.payload.kind === 'userMessage'
    && typeof payload.payload.text === 'string'
  ) {
    const owned = rail.owned.find((session) => session.ownedId === payload.ownedId);
    if (owned && !owned.title.trim()) {
      const title = sessionTitleFromPrompt(payload.payload.text);
      if (title) updateOwnedSession(payload.ownedId, { title });
    }
  }
  if (terminal && current) {
    setConversationSending(payload.ownedId, false);
  }
}

async function handleConversationStreamResync(streamGeneration: number): Promise<void> {
  if (conversationEventsDisposed || streamGeneration !== conversationEventsGeneration) return;
  await Promise.allSettled([selectedConversationRead, childConversationRead].map(async (read) => {
    if (!read) return;
    try {
      await reloadSelectedConversation(read);
    } catch (error) {
      if (isCurrentConversationRead(read)) read.onError(error);
    }
  }));
  await refreshRemoteSessionActivity(streamGeneration);
}

export function stopConversationEvents(): void {
  conversationEventsDisposed = true;
  conversationEventsGeneration += 1;
  conversationEventsController?.abort();
  conversationEventsController = null;
  conversationEventsSetup = null;
  remoteActivityController?.abort();
  remoteActivityController = null;
  for (const ownedId of childHistoryReads.keys()) stopChildConversationHistory(ownedId);
  selectedConversationRead?.abortController.abort();
  selectedConversationRead = null;
  childConversationRead?.abortController.abort();
  childConversationRead = null;
  railActivityEvents.clear();
  adoptController.abort();
  adoptController = new AbortController();
  sessionsNotFound.clear();
  for (const ownedId of [...terminalProjections.keys()]) stopConversationTerminalProjection(ownedId);
}

/** Closes the exact structured generation currently held by the frontend. */
export async function closeStructuredConversation(ownedId: string): Promise<void> {
  if (!isTauri()) return;
  const generation = getConversationSession(ownedId)?.generation;
  if (generation === undefined) return;
  await invoke<boolean>('close_agent_conversation', { ownedId, generation });
}

/** Ensures one structured runtime and applies only its returned connection. */
export async function ensureStructuredConversation(input: {
  ownedId: string;
  executionEnvironment?: ExecutionEnvironment;
  remoteProfileId?: string | null;
  provider: AgentConversationProvider;
  cwd: string;
  nativeSessionId?: string | null;
  nativeSessionMode?: 'resume' | 'load';
  reasoningEffort?: string | null;
  /** The registry project; the backend records it once, on the first ensure. */
  projectId?: string | null;
  signal?: AbortSignal;
}): Promise<AgentConversationConnection | null> {
  if (input.signal?.aborted) return null;
  if (rail.activeOwnedId === input.ownedId) {
    ensureConversationSession(input.ownedId, input.provider);
  }
  if (!isTauri() || !input.cwd.trim()) return null;
  const request = {
    ...input,
    executionEnvironment: input.executionEnvironment ?? 'local',
    nativeSessionMode: input.nativeSessionMode ?? 'resume'
  };
  const signature = JSON.stringify(request);
  const active = ensuring.get(input.ownedId);
  if (active?.signature === signature) {
    const connection = await active.work;
    return input.signal?.aborted || active.invalidated ? null : connection;
  }
  const token = {};
  const abortController = new AbortController();
  const abortFromOwner = (): void => abortController.abort();
  input.signal?.addEventListener('abort', abortFromOwner, { once: true });
  const work = ensureStructuredConversationOnce(input.ownedId, request, token, abortController.signal);
  ensuring.set(input.ownedId, { abortController, invalidated: false, signature, token, work });
  try {
    const connection = await work;
    return input.signal?.aborted ? null : connection;
  } finally {
    input.signal?.removeEventListener('abort', abortFromOwner);
  }
}

async function ensureStructuredConversationOnce(
  ownedId: string,
  request: {
    ownedId: string;
    executionEnvironment: ExecutionEnvironment;
    remoteProfileId?: string | null;
    provider: AgentConversationProvider;
    cwd: string;
    nativeSessionId?: string | null;
    nativeSessionMode: 'resume' | 'load';
    reasoningEffort?: string | null;
    projectId?: string | null;
  },
  token: object,
  signal: AbortSignal
): Promise<AgentConversationConnection | null> {
  try {
    if (signal.aborted) return null;
    const connection = await invoke<AgentConversationConnection>('ensure_agent_conversation', {
      request
    });
    if (signal.aborted) return null;
    if (connection.nativeSessionId) updateOwnedSession(ownedId, { nativeSessionId: connection.nativeSessionId });
    if (rail.activeOwnedId !== ownedId) return connection;
    setConversationConnection(connection);
    const read = selectedConversationRead;
    if (read?.ownedId === ownedId) {
      read.minimumGeneration = Math.max(read.minimumGeneration ?? 0, connection.generation);
    }
    await refreshSelectedConversation(ownedId);
    return connection;
  } finally {
    if (ensuring.get(ownedId)?.token === token) ensuring.delete(ownedId);
  }
}

/** Changes a quiescent session's durable checkout without changing its
 * generation. The native command owns the gate and the SQLite transaction. */
export async function changeStructuredConversationCheckout(
  ownedId: string,
  cwd: string
): Promise<AgentConversationSessionRecord | null> {
  const state = getConversationSession(ownedId);
  const session = rail.owned.find((entry) => entry.ownedId === ownedId);
  if (!session?.canChangeCheckout) throw new Error("This session's agent can't keep its conversation in another checkout.");
  if (!state || !cwd.trim()) throw new Error('A connected session and checkout folder are required.');
  const record = await changeAgentConversationCheckoutFromTauri({ ownedId, generation: state.generation, cwd });
  if (!record) return null;
  updateOwnedSession(ownedId, {
    cwd: record.cwd,
    nativeSessionId: record.nativeSessionId,
    runtimeState: record.state
  });
  setConversationConnection({
    ownedId,
    provider: state.provider,
    generation: state.generation,
    nativeSessionId: record.nativeSessionId ?? state.nativeSessionId,
    state: record.suspended ? 'disconnected' : state.connectionState,
    suspended: record.suspended
  });
  return record;
}

const preparingSends = new Map<string, AbortController>();

/** Waits the given time; aborting the signal ends the wait at once. */
async function waitFor(ms: number, signal: AbortSignal): Promise<void> {
  await new Promise<void>((resolve) => {
    const finish = (): void => {
      clearTimeout(timer);
      signal.removeEventListener('abort', finish);
      resolve();
    };
    const timer = setTimeout(finish, ms);
    signal.addEventListener('abort', finish, { once: true });
    if (signal.aborted) finish();
  });
}

/** A run of the chat that only answers its interrupts. */
export async function resumeConversationRun(run: AguiRun): Promise<void> {
  await invoke('run_agui', { input: run });
}

/** Sends one message through the current writer while preserving attachment recovery; `withAttachments` false leaves the composer's screenshots. */
export async function sendStructuredMessage(
  ownedId: string,
  text: string,
  startConfig?: { reasoningEffort?: string | null; model?: string | null; approvalPolicy?: string | null },
  withAttachments = true,
  run?: AguiRun
): Promise<AgentConversationSendReceipt | undefined> {
  let state = getConversationSession(ownedId);
  if (!state) return;
  if (!text.trim() && state.attachments.length === 0) return;
  const presence = get(sessionPresenceHistory)[ownedId];
  const turnWasAlreadyActive = state.sending || Boolean(
    presence ? presence.activeTurnId : (state.activeTurnId ?? rail.owned.find((session) => session.ownedId === ownedId)?.activeTurnId)
  );
  const preparation = new AbortController();
  preparingSends.set(ownedId, preparation);
  setConversationSending(ownedId, true);
  // The composer empties the moment Send is pressed. The screenshots are held
  // for the transcript until the backend's copy of the message claims them —
  // recorded before the request, because the backend records and dispatches
  // its own copy while the request is still running.
  const attachments = withAttachments ? state.attachments : [];
  const releaseHold = (): void => recordSentConversationAttachments(ownedId,
    (getConversationSession(ownedId)?.unclaimedSentAttachments ?? []).filter((held) => !attachments.some((item) => item.id === held.id)));
  if (attachments.length) {
    recordSentConversationAttachments(ownedId, [...(state.unclaimedSentAttachments ?? []), ...attachments.map(attachmentDisplayMetadata)]);
    setConversationAttachments(ownedId, []);
  }
  try {
    const owned = rail.owned.find((session) => session.ownedId === ownedId) ?? null;
    let terminalSessionId = owned?.ptySessionId;
    const terminalStillOwnsSession = Boolean(
      terminalSessionId
      && owned
      && owned.origin === 'external'
      && owned.state !== 'exited'
      && owned.executionOwner !== 'stopped'
    );

    if (
      owned
      && !terminalStillOwnsSession
      && shouldReviveBeforeSend({
        sessionState: owned.state,
        executionOwner: owned.executionOwner,
        connectionState: state.connectionState,
        generation: state.generation
      })
    ) {
      const provider = normalizeProvider(owned.agent).agent;
      if (provider !== 'codex' && provider !== 'claude' && provider !== 'antigravity') {
        throw new Error('This stopped session cannot be revived as a conversation');
      }

      const previousGeneration = state.generation;
      const activation = decideConversationActivation({ ...owned, agent: provider }, state);
      if (activation.kind === 'terminal') {
        throw new Error('This stopped session must be started from its session row');
      }
      const nativeSessionMode = activation.kind === 'structured' ? activation.nativeSessionMode : 'resume';
      const activated = await ensureStructuredConversation({
        ownedId,
        executionEnvironment: owned.executionEnvironment,
        remoteProfileId: owned.remoteProfileId ?? null,
        provider,
        cwd: owned.cwd,
        nativeSessionId: owned.nativeSessionId,
        nativeSessionMode,
        projectId: owned.projectId,
        reasoningEffort: startConfig
          ? startConfig.reasoningEffort
          : state.agentConfig.reasoningEffort
      });
      // The ensure applies only the selected chat's connection; a background session's (a Board answer) is applied here.
      if (activated && rail.activeOwnedId !== ownedId) setConversationConnection(activated);
      const revived = getConversationSession(ownedId);
      const nextGeneration = generationForSend(previousGeneration, activated?.generation ?? -1);
      if (nextGeneration === null || !revived) {
        throw new Error('The ensured conversation is not current');
      }
      state = revived;
      terminalSessionId = null;
    }

    if (terminalSessionId) {
      if (state.writerLease.ownedId !== ownedId
        || state.writerLease.generation !== state.generation
        || state.writerLease.owner !== 'terminal') {
        throw new Error('The terminal writer lease is not confirmed for this session');
      }
      const attachmentText = attachments.length
        ? `\n\nAttached screenshots:\n${attachments.map((item) => `- ${item.path}`).join('\n')}`
        : '';
      const outgoingText = `${text}${attachmentText}`;
      // Match a real terminal paste followed by a separate Enter key. Sending
      // both in one PTY write makes Claude's TUI treat Enter as part of its
      // multiline paste buffer, leaving the prompt staged but never submitted.
      const pasted = await writeTerminalSessionFromTauri(
        terminalSessionId,
        `\u001b[200~${outgoingText}\u001b[201~`
      );
      if (!pasted) throw new Error('The terminal session is no longer running');
      const submitted = await writeTerminalSessionFromTauri(terminalSessionId, '\r');
      if (!submitted) throw new Error('The terminal session is no longer running');
      setConversationSending(ownedId, false);
      // The terminal echoes no user message to claim the hold.
      releaseHold();
      attachments.forEach(cleanupConversationAttachmentPreview);
      return;
    }
    if (state.generation < 1) throw new Error('The structured conversation is not connected');
    // An unread or stale capability snapshot is not a refusal. Blocking the
    // send here left a screenshot that could never go out and no way to learn
    // why, so only a connected session's own answer refuses.
    const supportsImages = sendSupportsImages(state.capabilities, state.connectionState);
    if (attachments.length && !supportsImages) {
      throw new Error('This conversation provider does not advertise image prompts');
    }
    const remoteSend = owned?.executionEnvironment === 'remote';
    if (attachments.length && supportsImages && !remoteSend) {
      // Image bytes cross the IPC as a JSON number array, which holds the main
      // thread (~80 ms per 600 KB measured). Wait out the send's scroll glide
      // (~250 ms in WebKit) so the message lands smoothly before that work.
      await waitFor(300, preparation.signal);
      if (preparation.signal.aborted) {
        if (!turnWasAlreadyActive) await invoke('stop_agent_conversation_turn', {
          request: { ownedId, generation: state.generation }
        });
        throw new Error('Message cancelled before sending.');
      }
    }
    const hydratedAttachments = supportsImages && !remoteSend
      ? await Promise.all(attachments.map((attachment) => hydrateAttachmentBytes(attachment as AttachmentWithBytes)))
      : attachments as AttachmentWithBytes[];
    const prompt = remoteSend ? { text, content: [] as AgentPromptContent[] }
      : buildConversationPrompt(text, hydratedAttachments, supportsImages);
    const validatedState = getConversationSession(ownedId);
    const validatedGeneration = sendTargetGeneration(validatedState);
    if (validatedGeneration === null || !validatedState) {
      throw new Error('This conversation is closed, so the message was not sent');
    }
    state = validatedState;
    if (owned) {
      updateOwnedSession(ownedId, {
        state: 'live',
        executionOwner: 'structured',
        runtimeState: 'ready',
        lastError: null,
        nativeSessionId: state.nativeSessionId ?? owned.nativeSessionId
      });
    }
    if (startConfig?.model && !state.nativeSessionId) {
      // Stored together, so the start applies the model before the effort. Stop
      // pressed while this is saved is still honoured below.
      await setAgentConversationConfig({
        ownedId, generation: validatedGeneration, model: startConfig.model,
        ...(startConfig.reasoningEffort ? { reasoningEffort: startConfig.reasoningEffort } : {})
      });
    }
    if (preparation.signal.aborted) {
      if (!turnWasAlreadyActive) await invoke('stop_agent_conversation_turn', {
        request: { ownedId, generation: validatedGeneration }
      });
      throw new Error('Message cancelled before sending.');
    }
    if (preparingSends.get(ownedId) === preparation) preparingSends.delete(ownedId);
    const requestedModel = startConfig?.model ?? null;
    const requestedApprovalPolicy = startConfig?.approvalPolicy ?? null;
    const request = {
        ownedId,
        generation: validatedGeneration,
        text: prompt.text,
        content: prompt.content,
        // Named on the recorded user message so a restart can find the saved
        // files again; the in-memory hold above does not survive one.
        attachmentIds: attachments.map((attachment) => attachment.id),
        model: requestedModel,
        approvalPolicy:
          requestedApprovalPolicy
          && state.agentConfig.availableApprovalPolicies.includes(requestedApprovalPolicy)
            ? requestedApprovalPolicy
            : null
    };
    // The chat's own run sends it, so the reply streams back under that run.
    return run
      ? await invoke<AgentConversationSendReceipt | null>('run_agui', { input: { ...run, message: request } }) ?? undefined
      : await invoke<AgentConversationSendReceipt>('send_agent_conversation_message', { request });
  } catch (error) {
    // A send that never went out puts its screenshots back in the composer, so
    // nothing is left waiting to be hung on a later message.
    if (attachments.length) {
      releaseHold();
      setConversationAttachments(ownedId, [...attachments, ...(getConversationSession(ownedId)?.attachments ?? [])]);
    }
    if (!turnWasAlreadyActive) setConversationSending(ownedId, false);
    throw error;
  } finally {
    if (preparingSends.get(ownedId) === preparation) preparingSends.delete(ownedId);
  }
}

/** Stops the active turn for the conversation's current generation. */
export async function stopStructuredTurn(ownedId: string): Promise<void> {
  const preparation = preparingSends.get(ownedId);
  const state = getConversationSession(ownedId);
  const presence = get(sessionPresenceHistory)[ownedId];
  const activeTurn = presence ? presence.activeTurnId : (state?.activeTurnId ?? rail.owned.find((session) => session.ownedId === ownedId)?.activeTurnId);
  if (preparation) {
    preparation.abort();
    if (!activeTurn) return;
  }
  if (!state || state.generation < 1) return;
  await invoke('stop_agent_conversation_turn', {
    request: { ownedId, generation: state.generation }
  });
}
