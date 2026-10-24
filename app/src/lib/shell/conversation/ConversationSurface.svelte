<script lang="ts">
  import { createChatUI, UIProvider } from '@tanstack/ai-svelte/ui';
  import ConversationMessageParts from '$lib/shell/conversation/components/ConversationMessageParts.svelte';
  import ConversationToolPart from '$lib/shell/conversation/components/ConversationToolPart.svelte';
  import { setContext } from 'svelte';
  import { conversationMessagesContext, type ConversationMessagesContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import { readRemoteAssemblyEnvironmentFromTauri, type RemoteAssemblyProfile } from '$lib/tauriSource';
  import { untrack } from 'svelte';
  import { normalizeProvider, type OwnedSession } from '$lib/shell/ownedSessions.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import type {
    AgentConfigValue,
    AgentUserInputAction,
    AgentConversationProvider,
    ConversationAttachment
  } from '$lib/shell/conversation/conversationTypes.ts';
  import ConversationTimeline from './components/ConversationTimeline.svelte';
  import ConversationComposer from './components/ConversationComposer.svelte';
  import {
    beginConversationAgentConfigChange,
    confirmConversationAgentConfigChange,
    conversationSessions,
    failConversationAgentConfigChange,
    setConversationAgentConfigError,
    setConversationAgentConfigState,
    setConversationAttachmentError,
    setConversationAttachmentIds,
    setConversationAttachments,
    setConversationDraft,
    setConversationSendError,
    setConversationMode,
    setConversationProviderNotice,
    selectedConversationViewState,
    setSelectedConversationViewState
  } from '$lib/shell/conversation/conversationStore.svelte';
  import {
    cleanupConversationAttachment,
    clearConversationSessionDraft,
    flushConversationSessionDraft,
    loadConversationCapabilities,
    persistConversationSessionDraft,
    removeConversationAttachment,
    restoreConversationAttachments,
    saveConversationClipboardImage,
    sendStructuredMessage,
    stopStructuredTurn
  } from '$lib/shell/conversation/conversationService';
  import { selectedConversationChat, sendSelectedConversationMessage, pageSelectedConversation, jumpSelectedConversationToLatest } from '$lib/shell/conversation/conversationConnection';
  import {
    readAgentConversationConfig,
    setAgentConversationConfig,
    type AgentConversationConfigField,
    type AgentConversationConfigRequest
  } from '$lib/shell/conversation/conversationConfig.ts';
  import {
    mergeConversationCommandCatalog,
    type ConversationCommand
  } from '$lib/shell/conversation/conversationCommandCatalog.ts';
  import {
    latestPlan,
    turnActivityLabel,
    turnFileChanges,
    type ConversationDisplayItem,
    type ConversationFileLinkProvenance
  } from '$lib/shell/conversation/conversationTimeline.ts';
  import { conversationDisplayItems, pendingRequestsOf } from '$lib/shell/conversation/conversationMessages.ts';
  import { contextMeterState } from '$lib/shell/conversation/composerSlashCommands.ts';
  import { sessionContextUsage } from '$lib/shell/panels/context/sessionContextModel.ts';
  import { requestOpenConversationFile } from '$lib/shell/openFileBus.ts';
  import { clearViewedSession, sessionPresenceHistory, setViewedSession } from '$lib/shell/conversation/sessionPresence.ts';
  import type { ConversationSendAnchorRequest } from '$lib/shell/conversation/conversationScrollAnchor.ts';
  import { providerErrorMessage } from '$lib/shell/errorPresentation.ts';
  import { rememberAgentConfigChoice } from '$lib/shell/conversation/agentConfigMemory';

  interface Props {
    owned: OwnedSession[];
    activeOwnedId: string | null;
    activeOrigin?: OwnedSession['origin'];
    rootAvailable?: boolean;
    pendingFirstMessage?: string | null;
    onOpenNativeCli?(ownedId: string): void | Promise<void>;
    onForkNativeCli?(ownedId: string): void | Promise<void>;
    onReturnToStructured?(ownedId: string): void | Promise<void>;
    onPersistAttachmentIds(ownedId: string, ids: readonly string[]): Promise<void>;
    /** Open one of this session's sub-agents in the Agents panel. */
    onOpenChild?(ownedId: string, childId: string): void;
  }
  let {
    owned,
    activeOwnedId,
    activeOrigin,
    rootAvailable = true,
    pendingFirstMessage = null,
    onOpenNativeCli,
    onForkNativeCli,
    onReturnToStructured,
    onPersistAttachmentIds,
    onOpenChild
  }: Props = $props();
  const active = $derived(owned.find((item) => item.ownedId === activeOwnedId) ?? null);
  let remoteMachine = $state<RemoteAssemblyProfile | null>(null);
  $effect(() => {
    const profileId = active?.executionEnvironment === 'remote' ? active.remoteProfileId : null;
    remoteMachine = null;
    if (!profileId) return;
    const controller = new AbortController();
    void loadRemoteMachine(profileId, controller.signal);
    return () => controller.abort();
  });

  async function loadRemoteMachine(profileId: string, signal: AbortSignal): Promise<void> {
    try {
      const environment = await readRemoteAssemblyEnvironmentFromTauri();
      if (signal.aborted) return;
      remoteMachine = environment.profiles.find((profile) => profile.id === profileId) ?? null;
    } catch {
      if (!signal.aborted) remoteMachine = null;
    }
  }
  const conversation = $derived(activeOwnedId ? conversationSessions[activeOwnedId] ?? null : null);
  const presence = $derived(activeOwnedId ? $sessionPresenceHistory[activeOwnedId] : null);
  const activeTurnId = $derived(presence ? presence.activeTurnId : (conversation?.activeTurnId ?? active?.activeTurnId ?? null));
  const turnActive = $derived(Boolean(conversation?.sending || activeTurnId || active?.runtimeState === 'working'));
  // A real turn, not background work outliving one: only a real turn is steered.
  const turnRunning = $derived(Boolean(conversation?.sending || activeTurnId));
  const origin = $derived(activeOrigin ?? active?.origin ?? 'external');
  const appOwned = $derived(origin === 'app');
  function isStructuredAgent(agent: string | undefined): boolean {
    const { agent: provider } = normalizeProvider(agent ?? '');
    return ['codex', 'claude', 'antigravity'].includes(provider);
  }
  const structured = $derived(!!active && isStructuredAgent(active.agent) && (appOwned || conversation?.mode !== 'raw'));
  const transcriptChat = $derived.by(() => {
    if (!conversation) return null;
    conversation.timelineRevision;
    return selectedConversationChat(conversation.ownedId);
  });
  const transcriptMessages = $derived(transcriptChat?.messages ?? []);
  const openRequestIds = $derived(new Set((transcriptChat?.interrupts ?? []).map((interrupt) => interrupt.id)));
  const transcriptMessagesById = $derived(new Map(transcriptMessages.map((message) => [message.id, message])));
  const toolComponents = $state<Record<string, typeof ConversationToolPart>>({});
  const ui = createChatUI({}, {
    components: { layout: ConversationMessageParts, message: ConversationMessageParts },
    partsComponents: {},
    toolsComponents: toolComponents
  });
  $effect.pre(() => {
    const names = new Set(transcriptMessages.flatMap((message) => message.parts.flatMap((part) =>
      part.type === 'tool-call' ? [part.name] : []
    )));
    untrack(() => {
      for (const name of Object.keys(toolComponents)) if (!names.has(name)) delete toolComponents[name];
      for (const name of names) toolComponents[name] = ConversationToolPart;
    });
  });
  setContext<ConversationMessagesContext>(conversationMessagesContext, {
    ui,
    get messages() { return transcriptMessagesById; }
  });
  let previousTimelineKey = '';
  let previousVisibleTimeline: ConversationDisplayItem[] = [];
  const visibleTimeline = $derived.by((): ConversationDisplayItem[] => {
    if (!conversation) return [];
    const timelineKey = conversation.ownedId;
    if (timelineKey !== previousTimelineKey) {
      previousTimelineKey = timelineKey;
      previousVisibleTimeline = [];
    }
    previousVisibleTimeline = conversationDisplayItems(
      transcriptMessages,
      previousVisibleTimeline,
      conversation.sentAttachments
    ).filter((item, index, items) => !((item.kind === 'approval' || item.kind === 'input') && openRequestIds.has(item.requestId))
        // The composer owns the latest failed send; keep its stored error for history.
        && !(item.kind === 'error' && index === items.length - 1
          && !conversation.selectedHasAfter && !activeTurnId && conversation.sendError
          && providerErrorMessage(item.text).trim() === providerErrorMessage(conversation.sendError).trim()));
    return previousVisibleTimeline;
  });
  const activePlan = $derived(latestPlan(visibleTimeline));
  /* Keep the footer's latest-turn count while older transcript pages replace
     the displayed window. Those pages can belong to different turns. */
  let fileChangesViewKey = '';
  let planFileChanges = $state<ReturnType<typeof turnFileChanges>>(null);
  $effect(() => {
    const viewKey = activeOwnedId ?? '';
    if (viewKey !== fileChangesViewKey) {
      fileChangesViewKey = viewKey;
      planFileChanges = null;
    }
    if (conversation && !conversation.selectedHasAfter && visibleTimeline.length) {
      planFileChanges = turnFileChanges(visibleTimeline);
    }
  });
  const remoteDisconnected = $derived(active?.executionEnvironment === 'remote'
    && rail.remoteConnections[active.remoteProfileId ?? ''] !== 'connected');
  const controlsDisabled = $derived(conversation?.connectionState !== 'connected' || remoteDisconnected);
  const openRequests = $derived(pendingRequestsOf(transcriptChat?.interrupts ?? [], conversation?.ownedId ?? ''));
  const pendingApprovals = $derived(openRequests.approvals);
  const pendingInputs = $derived(openRequests.inputs);
  /* The live status line under the reply. Only the root transcript at its live
     end can speak for the running turn; a window trimmed
     while reading upward shows none. */
  const activityLabel = $derived(
    turnActive && conversation && !conversation.selectedHasAfter
      ? turnActivityLabel(visibleTimeline, activeTurnId, pendingApprovals.length, pendingInputs.length)
      : null
  );
  /* Between turns, the same place lists the sub-agents and commands still
     running. A disconnected remote session cannot vouch for its list. */
  const backgroundWork = $derived(
    !turnActive && active?.backgroundWork?.length && conversation && !conversation.selectedHasAfter
      && (active.executionEnvironment !== 'remote' || rail.remoteConnections[active.remoteProfileId ?? ''] === 'connected')
      ? active.backgroundWork
      : undefined
  );
  const commandCatalog = $derived(mergeConversationCommandCatalog(conversation?.availableCommands ?? conversation?.capabilities?.commands ?? []).filter((command) => !appOwned || command.name !== 'terminal'));
  /* The same numbers the Context panel shows. This read only `metadata`, and a
     provider that reports its usage as it goes puts those numbers on `usage` —
     so the panel had a figure and the composer had nothing, from one session. */
  const contextUsage = $derived(
    sessionContextUsage(conversation?.metadata ?? null, conversation?.usage)
  );
  const contextMeter = $derived(
    contextMeterState(contextUsage.usedTokens, contextUsage.contextWindow, {
      inputTokens: contextUsage.inputTokens,
      outputTokens: contextUsage.outputTokens
    })
  );

  // Read from the session rather than held here: this surface is mounted once
  // for the whole shell, so a failure kept in component state was shown under
  // every conversation and survived the send that fixed it.
  const attachmentError = $derived(conversation?.attachmentError ?? '');
  const sendError = $derived(conversation?.sendError ?? '');
  const providerNotice = $derived(conversation?.providerNotice ?? '');
  const capabilityTarget = $derived(structured && active && conversation
    && isStructuredAgent(active.agent)
    ? `${active.ownedId}:${conversation.generation}:${conversation.provider}:${conversation.connectionState}:${conversation.nativeSessionId ?? ''}`
    : '');
  let configRequest = '';
  /** A request key whose failure has already bought its one retry. The guard
   * above is claimed before the call, so without this a read that lost a
   * start-up race left the composer empty for good: the key still matched, so
   * the effect never asked again. Clearing the guard lets it ask once more —
   * and this remembers that it did, because the effect reads the guard and
   * would otherwise retry forever against a failure that is not going away. */
  let configRetried = '';
  let sendAnchorRequest = $state<ConversationSendAnchorRequest | null>(null);
  let surfaceController = new AbortController();

  $effect(() => {
    activeOwnedId;
    surfaceController.abort();
    const controller = new AbortController();
    surfaceController = controller;
    return () => controller.abort();
  });
  let sendAnchorRequestId = 0;
  let localTurnActive = $state(false);
  let localTurnStarted = $state(false);
  let composerHeight = $state(0);
  let composer = $state<{ focus(): void; expandPlan(): void } | null>(null);
  const pendingAttachmentUploads = new Map<string, Promise<void>>();
  let pendingImageCounts = $state<Record<string, number>>({});
  const sendsWaitingForUpload = new Set<string>();

  /** Put the caret in the prompt box. The page calls this when a panel hands
   * the composer something — an attachment, a line of text — so the reader ends
   * up typing beside it rather than hunting for the box. */
  export function focusComposer(): void {
    composer?.focus();
  }

  $effect(() => {
    if (!localTurnActive) {
      localTurnStarted = false;
      return;
    }
    if (conversation?.sending) {
      localTurnStarted = true;
      return;
    }
    if (localTurnStarted) {
      localTurnActive = false;
      localTurnStarted = false;
      sendAnchorRequest = null;
    }
  });

  $effect(() => {
    const ownedId = activeOwnedId;
    setViewedSession(ownedId);
    if (!ownedId) return;
    return () => clearViewedSession(ownedId);
  });

  $effect(() => {
    if (appOwned && activeOwnedId && conversation?.mode === 'raw') {
      setConversationMode(activeOwnedId, 'structured');
    }
  });

  $effect(() => {
    if (pendingFirstMessage || remoteDisconnected) return;
    if (!structured || !active || !conversation || !isStructuredAgent(active.agent)) return;
    const ownedId = active.ownedId;
    const generation = conversation.generation;
    const key = `${ownedId}:${generation}:${conversation.connectionState}`;
    if (configRequest === key) return;
    configRequest = key;
    const controller = new AbortController();
    void readAgentConfigForSurface(controller.signal, ownedId, generation, key);
    return () => {
      controller.abort();
      if (configRequest === key) configRequest = '';
    };
  });

  $effect(() => {
    if (!capabilityTarget) return;
    const controller = new AbortController();
    // Only a changed session/generation/connection owns a new request. Writing
    // a reactive "requested" flag here cancelled the effect's own request.
    untrack(() => {
      if (active && conversation) {
        void loadCapabilitiesForSurface(controller.signal, active.ownedId, conversation.provider, conversation.generation);
      }
    });
    return () => {
      controller.abort();
    };
  });

  $effect(() => {
    if (!active || !conversation || !structured) return;
    const ownedId = active.ownedId;
    const generation = conversation.generation;
    if (!conversation.attachments.length && conversation.attachmentIds.length) {
      void restoreAttachmentsForSurface(surfaceController.signal, ownedId, generation);
    }
  });

  function ownsConversationGeneration(signal: AbortSignal, ownedId: string, generation: number): boolean {
    return !signal.aborted && conversationSessions[ownedId]?.generation === generation;
  }

  async function readAgentConfigForSurface(
    signal: AbortSignal,
    ownedId: string,
    generation: number,
    key: string
  ): Promise<void> {
    try {
      const state = await readAgentConversationConfig(ownedId, signal);
      if (state && ownsConversationGeneration(signal, ownedId, generation)) {
        setConversationAgentConfigState(ownedId, state);
      }
    } catch (error) {
      if (ownsConversationGeneration(signal, ownedId, generation)) {
        setConversationAgentConfigError(ownedId, error instanceof Error ? error.message : String(error));
      }
      // The usual failure here is a race, not a refusal: the session is stored
      // but not yet in the manager's map. Asking a second time is what fills the
      // composer in; asking forever would be a retry storm.
      if (!signal.aborted && configRequest === key && configRetried !== key) {
        configRetried = key;
        configRequest = '';
      }
    }
  }

  async function loadCapabilitiesForSurface(
    signal: AbortSignal,
    ownedId: string,
    provider: AgentConversationProvider,
    generation: number
  ): Promise<void> {
    try {
      if (ownsConversationGeneration(signal, ownedId, generation)) {
        await loadConversationCapabilities(ownedId, provider, signal);
      }
    } catch (_error) {
      // The store owns capability errors; this effect only prevents unhandled
      // promise noise if the owning surface changes while the request is out.
    }
  }

  async function restoreAttachmentsForSurface(
    signal: AbortSignal,
    ownedId: string,
    generation: number
  ): Promise<void> {
    try {
      if (ownsConversationGeneration(signal, ownedId, generation)) {
        // Publishing metadata reruns the effect; only the generation check may discard its thumbnail results.
        await restoreConversationAttachments(ownedId, []);
      }
    } catch (_error) {
      // Attachment restore is best effort; saved ids remain in the session.
    }
  }

  async function ignoreDraftFlushFailure(ownedId: string): Promise<void> {
    try {
      await flushConversationSessionDraft(ownedId);
    } catch (_error) {
      // Losing this best-effort flush must not hide the send failure.
    }
  }

  async function cleanupSavedAttachments(
    ownedId: string,
    attachments: ConversationAttachment[],
    onDeleted: (id: string) => Promise<void>
  ): Promise<void> {
    for (const attachment of attachments) {
      try {
        await cleanupConversationAttachment(ownedId, attachment);
        await onDeleted(attachment.id);
      } catch (_error) {
        // A failed cleanup is already best-effort; previews are revoked by the service.
      }
    }
  }

  async function stopActiveStructuredTurn(ownedId: string, generation: number): Promise<void> {
    // A send wakes a sleeping adapter, so Stop must work before it reads connected.
    if (remoteDisconnected || conversationSessions[ownedId]?.generation !== generation) return;
    try {
      await stopStructuredTurn(ownedId);
    } catch (_error) {
      // Stop remains best-effort; the stream/projection owns terminal state.
    }
  }

  async function send(): Promise<void> {
    const ownedId = activeOwnedId;
    if (!ownedId || !conversation || remoteDisconnected) return;
    if (pendingAttachmentUploads.has(ownedId)) {
      if (sendsWaitingForUpload.has(ownedId)) return;
      sendsWaitingForUpload.add(ownedId);
      try {
        let upload = pendingAttachmentUploads.get(ownedId);
        while (upload) {
          await upload;
          const next = pendingAttachmentUploads.get(ownedId);
          upload = next === upload ? undefined : next;
        }
      } finally {
        sendsWaitingForUpload.delete(ownedId);
      }
      if (activeOwnedId !== ownedId || !conversation || conversation.attachmentError) return;
    }
    if (!conversation.draft.trim() && conversation.attachments.length === 0) return;
    const steering = turnRunning;
    const text = conversation.draft;
    const deliveredIds = new Set(conversation.attachments.map((attachment) => attachment.id));
    const retainedIds = conversation.attachmentIds.filter((id) => !deliveredIds.has(id));
    if (!steering) {
      localTurnActive = true;
      localTurnStarted = false;
    }
    setConversationDraft(ownedId, '');
    setConversationSendError(ownedId, '');
    // A new message glides to the top as it is drawn, not when the backend
    // admits it; the receipt below re-anchors the same row under its real id.
    const messageId = steering ? undefined : crypto.randomUUID();
    if (messageId) sendAnchorRequest = { requestId: ++sendAnchorRequestId, conversationId: ownedId, userItemId: messageId };
    const attachments = conversation.attachments;
    // The saved draft is cleared alongside the send, not before it: the clear is
    // a round trip (to the workbox for a remote session) and the message draws
    // when it is sent. Best effort, like the flush below: a delivered message is
    // not reported unsent because its saved draft lingered.
    void clearSavedDraft(ownedId);
    try {
      const receipt = steering
        ? await sendStructuredMessage(ownedId, text)
        : await sendSelectedConversationMessage(ownedId, text, messageId, attachments);
      if (receipt && activeOwnedId === ownedId) {
        sendAnchorRequest = {
          requestId: ++sendAnchorRequestId,
          conversationId: ownedId,
          userItemId: receipt.userItemId
        };
      }
    } catch (error) {
      // Restore the draft. The service puts the attachments back in the
      // composer on every failure, so the user can retry without data loss. The
      // reason has to be said out loud: a swallowed failure here reads as a
      // composer that silently refuses every Enter.
      setConversationSendError(ownedId, error instanceof Error ? error.message : String(error));
      setConversationDraft(ownedId, text);
      persistConversationSessionDraft(ownedId, text);
      await ignoreDraftFlushFailure(ownedId);
      if (!steering) {
        localTurnActive = false;
        localTurnStarted = false;
        sendAnchorRequest = null;
      }
      return;
    }
    try {
      await onPersistAttachmentIds(ownedId, retainedIds);
    } catch (error) {
      if (activeOwnedId === ownedId) setConversationAttachmentError(ownedId, error instanceof Error ? error.message : String(error));
    }
  }

  async function clearSavedDraft(ownedId: string): Promise<void> {
    try {
      await clearConversationSessionDraft(ownedId);
    } catch {
      // Best effort: a lingering saved draft is not a failed send.
    }
  }

  /** Cmd+V checks files first; text paste is untouched when there are no files. */
  async function paste(event: ClipboardEvent): Promise<void> {
    if (!active || !conversation) return;
    const files = [...(event.clipboardData?.files ?? [])];
    if (files.length === 0) return;
    event.preventDefault();
    await attachImages(files, 'The clipboard file is not a supported image.');
  }

  /** Files dragged onto the composer take the same path as a paste. */
  async function dropFiles(files: File[]): Promise<void> {
    await attachImages(files, 'That file is not a supported image.');
  }

  async function attachImages(files: File[], rejectedMessage: string): Promise<void> {
    const ownedId = active?.ownedId;
    if (!ownedId) return;
    pendingImageCounts[ownedId] = (pendingImageCounts[ownedId] ?? 0) + files.filter((file) => file.type.startsWith('image/')).length;
    const previous = pendingAttachmentUploads.get(ownedId);
    const upload = saveImagesAfter(previous, ownedId, files, rejectedMessage);
    pendingAttachmentUploads.set(ownedId, upload);
    try {
      await upload;
    } finally {
      pendingImageCounts[ownedId] = Math.max(0, (pendingImageCounts[ownedId] ?? 0) - files.filter((file) => file.type.startsWith('image/')).length);
      if (pendingAttachmentUploads.get(ownedId) === upload) pendingAttachmentUploads.delete(ownedId);
    }
  }

  // Uploads for one session run in order; one that finds its session no longer
  // active stops before saving, as saveImages does after each await.
  async function saveImagesAfter(
    previous: Promise<void> | undefined,
    ownedId: string,
    files: File[],
    rejectedMessage: string
  ): Promise<void> {
    await previous;
    if (active?.ownedId === ownedId) await saveImages(files, rejectedMessage);
  }

  async function saveImages(files: File[], rejectedMessage: string): Promise<void> {
    if (!active || !conversation) return;
    const ownedId = active.ownedId;
    const generation = conversation.generation;
    const images = files.filter((file) => file.type.startsWith('image/'));
    setConversationAttachmentError(ownedId, '');
    if (images.length === 0) {
      setConversationAttachmentError(ownedId, rejectedMessage);
      return;
    }
    const saved: ConversationAttachment[] = [];
    let pendingIds = [...conversation.attachmentIds];
    async function checkpointSavedId(id: string, retained: boolean): Promise<void> {
      const current = conversationSessions[ownedId];
      const ids = current?.generation === generation ? current.attachmentIds : pendingIds;
      pendingIds = retained ? [...new Set([...ids, id])] : ids.filter((item) => item !== id);
      if (current?.generation === generation) setConversationAttachmentIds(ownedId, pendingIds);
      await onPersistAttachmentIds(ownedId, pendingIds);
    }
    try {
      for (const file of images) {
        saved.push(await saveConversationClipboardImage(ownedId, file, (attachment, retained) => checkpointSavedId(attachment.id, retained)));
        if (active?.ownedId !== ownedId || conversation?.generation !== generation) {
          await cleanupSavedAttachments(ownedId, saved, (id) => checkpointSavedId(id, false));
          return;
        }
      }
      const savedIds = new Set(saved.map((item) => item.id));
      const attachments = [...conversation.attachments.filter((item) => !savedIds.has(item.id)), ...saved];
      const ids = [...new Set([...conversation.attachmentIds, ...attachments.map((item) => item.id)])];
      setConversationAttachmentIds(ownedId, ids);
      await onPersistAttachmentIds(ownedId, ids);
      if (active?.ownedId !== ownedId || conversation?.generation !== generation) {
        await cleanupSavedAttachments(ownedId, saved, (id) => checkpointSavedId(id, false));
        return;
      }
      setConversationAttachments(ownedId, attachments);
    } catch (error) {
      await cleanupSavedAttachments(ownedId, saved, (id) => checkpointSavedId(id, false));
      if (active?.ownedId === ownedId && conversation?.generation === generation) {
        setConversationAttachmentError(ownedId, error instanceof Error ? error.message : String(error));
      }
      // Draft and existing attachments remain untouched after a failed paste.
    }
  }

  async function removeAttachment(id: string): Promise<void> {
    if (!active || !conversation) return;
    const ownedId = active.ownedId;
    const generation = conversation.generation;
    const found = conversation.attachments.find((item) => item.id === id);
    if (!found) return;
    const remainingIds = conversation.attachmentIds.filter((item) => item !== id);
    try {
      await removeConversationAttachment(ownedId, found);
      await onPersistAttachmentIds(ownedId, remainingIds);
      if (active?.ownedId === ownedId && conversation?.generation === generation) setConversationAttachmentError(ownedId, '');
    } catch (error) {
      if (active?.ownedId === ownedId && conversation?.generation === generation) {
        setConversationAttachmentError(ownedId, error instanceof Error ? error.message : String(error));
      }
    }
  }

  function selectCommand(command: ConversationCommand): void {
    if (!active) return;
    if (appOwned && command.name === 'terminal') return;
    if (command.action === 'insert') {
      setConversationDraft(active.ownedId, `/${command.name} `);
      return;
    }
    if (command.name === 'terminal') setConversationMode(active.ownedId, 'raw');
    else if (command.name === 'conversation') setConversationMode(active.ownedId, 'structured');
    else setConversationDraft(active.ownedId, `/${command.name} `);
  }

  /* An answer resolves the chat's interrupt; the chat sends it back as `resume`. */
  function openInterrupt(requestId: string) {
    const interrupt = transcriptChat?.interrupts.find((candidate) => candidate.id === requestId);
    return interrupt?.kind === 'generic' ? interrupt : null;
  }

  function onApprovalDecision(requestId: string, optionId: string): void {
    if (!optionId || controlsDisabled) return;
    openInterrupt(requestId)?.resolveInterrupt({ optionId });
  }

  function onInputSubmit(requestId: string, action: AgentUserInputAction, content: Record<string, AgentConfigValue>): void {
    if (controlsDisabled) return;
    const interrupt = openInterrupt(requestId);
    if (action === 'accept') interrupt?.resolveInterrupt(content);
    else interrupt?.cancel();
  }

  function openConversationFile(reference: string, provenance?: ConversationFileLinkProvenance): void {
    if (!active) return;
    const error = requestOpenConversationFile(active, reference, provenance);
    if (error) setConversationAttachmentError(active.ownedId, error);
  }

  async function changeConfig(field: AgentConversationConfigField, value: string): Promise<void> {
    if (!active || !conversation) return;
    const generation = conversation.generation;
    const previous = beginConversationAgentConfigChange(active.ownedId, field, value);
    if (!previous) return;
    const request: AgentConversationConfigRequest = {
      ownedId: active.ownedId,
      generation,
      [field]: value
    };
    try {
      const state = await setAgentConversationConfig(request);
      if (conversationSessions[active.ownedId]?.generation !== generation) throw new Error('Configuration response belongs to a stale conversation generation');
      confirmConversationAgentConfigChange(active.ownedId, field, state);
      // The next new session of this agent opens on what was just chosen.
      rememberAgentConfigChoice(conversation.provider, { [field]: state[field] });
    } catch (error) {
      if (conversationSessions[active.ownedId]?.generation === generation) {
        failConversationAgentConfigChange(
          active.ownedId,
          field,
          previous,
          error instanceof Error ? error.message : String(error)
        );
      }
    }
  }
</script>

<div class="conversation-shell" data-testid="conversation-shell" role="presentation" onpointerdown={() => window.getSelection()?.removeAllRanges()}>
  {#if structured && active && conversation}
    <section class="structured" data-testid="structured-conversation" aria-label={`${active.agent} conversation`}>
      {#if active.executionEnvironment === 'remote'}
        <div class="remote-location" title={remoteMachine?.sshTarget ?? undefined}>
          Remote: {remoteMachine?.name ?? 'Saved machine'}{#if remoteMachine} ({remoteMachine.sshTarget}){/if} · {active.cwd}{#if remoteDisconnected} · Offline, showing the saved chat{/if}
        </div>
      {/if}
      {#if !appOwned}
        <div class="handoff-actions" aria-label="Conversation handoff actions">
          <button type="button" data-testid="open-native-cli" disabled={!rootAvailable} onclick={() => void onOpenNativeCli?.(active.ownedId)}>
            Open in native CLI
          </button>
          {#if conversation.capabilities?.session.fork}
            <button type="button" data-testid="fork-native-cli" disabled={!rootAvailable} onclick={() => void onForkNativeCli?.(active.ownedId)}>
              Fork to native CLI
            </button>
          {/if}
          <button type="button" data-testid="conversation-open-raw" disabled={!rootAvailable} onclick={() => setConversationMode(active.ownedId, 'raw')}>
            Open raw terminal
          </button>
        </div>
      {/if}
      {#if transcriptChat}
        {#key transcriptChat}
          <UIProvider {ui} chat={transcriptChat}>
      <ConversationTimeline
        {pendingFirstMessage}
        items={visibleTimeline}
        conversationId={active.ownedId}
        renderWindowId={`${active.ownedId}:${active.ownedId}`}
        historyOwnedId={active.ownedId}
        viewState={selectedConversationViewState(active.ownedId, active.ownedId)}
        onViewChange={setSelectedConversationViewState}
        itemFirstSequence={(itemId) => {
          const sequence = transcriptMessagesById.get(itemId)?.metadata?.firstSequence;
          return typeof sequence === 'number' ? sequence : undefined;
        }}
        timelineRevision={conversation.timelineRevision}
        anchorRequest={sendAnchorRequest}
        {activeTurnId}
        turnFacts={conversation.selectedTurns}
        {localTurnActive}
        {activityLabel}
        {backgroundWork}
        onOpenChild={(childId) => onOpenChild?.(active.ownedId, childId)}
        {composerHeight}
        assistantLabel={active.agent}
        emptyText="Start the conversation below."
        hasOlder={conversation.selectedHasBefore}
        loadingOlder={conversation.selectedLoadingOlder}
        pageError={conversation.selectedPageError}
        onLoadOlder={() => void pageSelectedConversation('older')}
        hasNewer={conversation.selectedHasAfter}
        loadingNewer={conversation.selectedLoadingNewer}
        onLoadNewer={() => void pageSelectedConversation('newer')}
        oldestSequence={conversation.selectedBeforeCursor ?? 0}
        newestSequence={conversation.selectedAfterCursor ?? 0}
        onJumpToLatest={() => jumpSelectedConversationToLatest(active.ownedId)}
        onApprovalDecision={onApprovalDecision}
        onFileLink={openConversationFile}
        onPlanOpen={() => composer?.expandPlan()}
        historyDownload={conversation.historyDownload}
      />
          </UIProvider>
        {/key}
      {/if}
        {#key active.ownedId}
        <ConversationComposer
          bind:this={composer}
          provider={active.agent}
          draft={conversation.draft}
          attachments={conversation.attachments}
          pendingImageCount={pendingImageCounts[active.ownedId] ?? 0}
          sending={turnActive}
          {turnRunning}
          supportsSteering={Boolean(activeTurnId) && conversation.capabilities?.session.steering === true}
          configState={conversation.agentConfig}
          pendingConfig={conversation.pendingAgentConfig}
          configError={conversation.agentConfigError}
          commands={commandCatalog}
          contextMeter={contextMeter}
          plan={activePlan}
          planFileChanges={planFileChanges}
          pendingApproval={pendingApprovals[0] ?? null}
          pendingApprovalCount={pendingApprovals.length}
          {pendingInputs} pendingInterrupts={transcriptChat?.interrupts.length ?? 0}
          {controlsDisabled}
          sendDisabled={remoteDisconnected}
          {attachmentError}
          {sendError}
          {providerNotice}
          onDismissAttachmentError={() => setConversationAttachmentError(active.ownedId, '')}
          onDismissSendError={() => setConversationSendError(active.ownedId, '')}
          onDismissProviderNotice={() => setConversationProviderNotice(active.ownedId, '')}
          onDraftChange={(value) => {
            setConversationDraft(active.ownedId, value);
            persistConversationSessionDraft(active.ownedId, value);
          }}
          onDraftBlur={() => flushConversationSessionDraft(active.ownedId)}
          onSend={send}
          onStop={() => {
            if (activeOwnedId && conversation) {
              void stopActiveStructuredTurn(activeOwnedId, conversation.generation);
            }
          }}
          onPaste={paste}
          onDropFiles={dropFiles}
          onRemoveAttachment={removeAttachment}
          onCommandSelected={selectCommand}
          onApprovalDecision={onApprovalDecision}
          onInputSubmit={onInputSubmit}
          onConfigChange={(optionId, value) => void changeConfig(optionId, value)}
          onHeightChange={(height) => (composerHeight = height)}
        />
        {/key}
    </section>
  {:else if active && isStructuredAgent(active.agent) && conversation?.mode === 'raw'}
    <div class="raw-actions" aria-label="Conversation handoff actions">
      <button class="structured-toggle" data-testid="conversation-structured-toggle" type="button" onclick={() => void onReturnToStructured?.(active.ownedId)}>
        Return to structured
      </button>
    </div>
  {/if}
</div>

<style>.conversation-shell,.structured{position:relative;width:100%;height:100%;min-height:0}.structured{position:absolute;inset:0;display:flex;flex-direction:column;background:transparent;color:var(--color-text);font:13px ui-sans-serif,system-ui}.remote-location{padding:5px 12px;border-bottom:1px solid var(--color-border);color:var(--color-text-2);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.handoff-actions{display:flex;gap:6px;align-items:center;padding:8px 12px;border-bottom:1px solid var(--color-border)}.handoff-actions button,.structured-toggle{border:0;border-radius:7px;background:var(--color-elevated);color:inherit;padding:6px 9px}.handoff-actions button:hover,.structured-toggle:hover{background:var(--color-hover)}.raw-actions{position:absolute;right:12px;top:12px;z-index:2}</style>
