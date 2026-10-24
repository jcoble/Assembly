<!-- Agents > Session owns the list and one bounded child transcript. -->
<script lang="ts">
  import { createChatUI, UIProvider } from '@tanstack/ai-svelte/ui';
  import ConversationMessageParts from '$lib/shell/conversation/components/ConversationMessageParts.svelte';
  import ConversationToolPart from '$lib/shell/conversation/components/ConversationToolPart.svelte';
  import { setContext, untrack } from 'svelte';
  import Bot from '@lucide/svelte/icons/bot';
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import Workflow from '@lucide/svelte/icons/workflow';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { PanelHeader } from '$lib/components/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { SegmentedControl } from '$lib/components/ui/segmented-control/index.js';
  import ConversationTimeline from '$lib/shell/conversation/components/ConversationTimeline.svelte';
  import {
    getConversationSession,
    setConversationSelectedChild,
    selectedConversationViewState,
    setSelectedConversationViewState
  } from '$lib/shell/conversation/conversationStore.svelte.ts';
  import {
    adoptSession,
    readChildConversationHistory,
    stopChildConversationHistory
  } from '$lib/shell/conversation/conversationService.ts';
  import {
    disposeChildConversationChat,
    selectConversationChat,
    selectedConversationChat,
    selectedConversationChatReady,
    pageSelectedConversation,
    jumpSelectedConversationToLatest
  } from '$lib/shell/conversation/conversationConnection.ts';
  import {
    conversationMessagesContext,
    type ConversationMessagesContext
  } from '$lib/shell/conversation/conversationChatUI.ts';
  import { conversationDisplayItems } from '$lib/shell/conversation/conversationMessages.ts';
  import type { ConversationDisplayItem, ConversationFileLinkProvenance } from '$lib/shell/conversation/conversationTimeline.ts';
  import { requestOpenConversationFile } from '$lib/shell/openFileBus.ts';
  import { sessionRowJump } from '$lib/shell/components/sessionRowJump.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { parseRemoteWorkspacePath } from '$lib/workspacePaths.ts';
  import { listenAgentMessagesChanged, listOwnerInboxFromTauri } from '$lib/tauriSource.ts';

  import AgentRow from './AgentRow.svelte';
  import { AGENT_STATUS_TONE, AGENT_STATUS_WORD, agentActivityRows } from './agentActivityModel.ts';
  import AgentMessages from '$lib/shell/messages/AgentMessages.svelte';
  import OwnerInbox from '$lib/shell/workflows/components/OwnerInbox.svelte';
  import WorkflowRuns from '$lib/shell/workflows/components/WorkflowRuns.svelte';

  interface Props {
    visible: boolean;
    root: string;
    ownedId: string | null;
    /** New messages for this session since its Messages view was last open. */
    messagesUnread: number;
    onMessagesSeen: () => void;
    /** Opens the full Messages screen filtered to one workflow run. */
    onShowRunMessages: (runId: string, label: string) => void;
    /** Opens the full Workflows screen. */
    onOpenWorkflows: () => void;
  }
  let { visible, root, ownedId, messagesUnread, onMessagesSeen, onShowRunMessages, onOpenWorkflows }: Props = $props();
  let view = $state<'session' | 'workflows' | 'messages' | 'inbox'>('session');
  let inboxStepCount = $state(0);
  let transcriptState = $state<'loading' | 'unavailable' | 'ready' | 'error'>('unavailable');
  let transcriptError = $state('');
  let fileError = $state('');
  let reader: AbortController | null = null;
  /** The child whose transcript this panel last opened; a different selection opens it. */
  let openedChildId: string | null = null;

  // Sessions waiting on the owner, by the same rule as the rail's "needs you".
  const waitingSessions = $derived(rail.owned.filter((session) =>
    session.pendingPermission === true || session.pendingInput === true
      || session.runtimeState === 'waiting-approval' || session.runtimeState === 'waiting-input'));
  const inboxCount = $derived(inboxStepCount + waitingSessions.length);
  const inboxOpen = $derived(view === 'inbox');
  // The count covers waiting steps before the Inbox is opened: read when the panel shows and again
  // on every run change, one read at a time. The open Inbox keeps the count itself.
  $effect(() => {
    if (!visible || inboxOpen) return;
    const controller = new AbortController();
    let reading = false;
    let again = false;
    async function recount(): Promise<void> {
      if (reading) { again = true; return; }
      reading = true;
      do { again = false; await countInboxSteps(controller.signal); } while (again && !controller.signal.aborted);
      reading = false;
    }
    void recount();
    void listenAgentMessagesChanged(() => void recount(), controller.signal);
    return () => controller.abort();
  });
  async function countInboxSteps(signal: AbortSignal): Promise<void> {
    try {
      const steps = await listOwnerInboxFromTauri(signal);
      if (!signal.aborted && steps) inboxStepCount = steps.length;
    } catch {
      // The Inbox view reports a failed read when it is opened.
    }
  }
  const VIEW_OPTIONS = $derived([
    { value: 'session', label: 'Session' },
    { value: 'workflows', label: 'Workflows' },
    { value: 'messages', label: messagesUnread > 0 ? `Messages ${messagesUnread}` : 'Messages' },
    { value: 'inbox', label: inboxCount > 0 ? `Inbox ${inboxCount}` : 'Inbox' }
  ]);
  const conversation = $derived(ownedId ? getConversationSession(ownedId) : null);
  // A remote project's workflows run on that machine's own engine, at its own path.
  const remoteRoot = $derived(parseRemoteWorkspacePath(root));
  const machine = $derived(remoteRoot?.profileId ?? null);
  const workflowRoot = $derived(remoteRoot?.path ?? root);
  const selectedChildId = $derived(conversation?.selectedChildId ?? null);
  // List rows use metadata; returning to the list releases the child body.
  const waitingIds = $derived(new Set(waitingSessions.map((session) => session.ownedId)));
  const rows = $derived(agentActivityRows(conversation?.children ?? [], {}, waitingIds));
  const selectedRow = $derived(rows.find((row) => row.childId === selectedChildId) ?? null);
  // A helper is a separate session (no parent tool call); its own chat holds its composer.
  const selectedIsHelper = $derived(conversation?.children.some((child) =>
    child.childId === selectedChildId && !child.parentToolCallId) ?? false);
  const historyOwnedId = $derived(conversation?.selectedChildHistoryOwnedId ?? null);
  const childConversation = $derived(historyOwnedId ? getConversationSession(historyOwnedId) : null);
  const transcriptChat = $derived.by(() => {
    if (!ownedId || !historyOwnedId || !childConversation) return null;
    childConversation.timelineRevision;
    return selectedConversationChat(ownedId, historyOwnedId);
  });
  const messages = $derived(transcriptChat?.messages ?? []);
  const messagesById = $derived(new Map(messages.map((message) => [message.id, message])));
  const toolComponents = $state<Record<string, typeof ConversationToolPart>>({});
  const ui = createChatUI({}, {
    components: { layout: ConversationMessageParts, message: ConversationMessageParts },
    partsComponents: {},
    toolsComponents: toolComponents
  });
  $effect.pre(() => {
    const names = new Set(messages.flatMap((message) => message.parts.flatMap((part) =>
      part.type === 'tool-call' ? [part.name] : []
    )));
    untrack(() => {
      for (const name of Object.keys(toolComponents)) if (!names.has(name)) delete toolComponents[name];
      for (const name of names) toolComponents[name] = ConversationToolPart;
    });
  });
  setContext<ConversationMessagesContext>(conversationMessagesContext, {
    ui,
    get messages() { return messagesById; }
  });
  let previousHistoryId: string | null = null;
  let previousItems: ConversationDisplayItem[] = [];
  const items = $derived.by(() => {
    if (historyOwnedId !== previousHistoryId) {
      previousHistoryId = historyOwnedId;
      previousItems = [];
    }
    previousItems = conversationDisplayItems(messages, previousItems, childConversation?.sentAttachments ?? {});
    return previousItems;
  });
  const error = $derived(transcriptError || conversation?.childTranscriptError || '');
  const childDownload = $derived(conversation?.childHistoryDownload ?? null);
  const childDownloadPercent = $derived(childDownload?.totalBytes ? Math.floor(childDownload.coveredBytes * 100 / childDownload.totalBytes) : 0);

  function release(parentId: string): void {
    reader?.abort();
    reader = null;
    helperOpen?.abort();
    helperOpen = null;
    stopChildConversationHistory(parentId);
    disposeChildConversationChat(parentId);
    setConversationSelectedChild(parentId, null);
    openedChildId = null;
    previousHistoryId = null;
    previousItems = [];
    // These lazy projections must drop their old body even when the list is shown.
    untrack(() => { void messagesById; void items; });
    transcriptState = 'unavailable';
    transcriptError = '';
    fileError = '';
  }

  $effect(() => {
    const parentId = ownedId;
    const active = visible && view === 'session';
    if (!active && parentId) untrack(() => release(parentId));
    return () => { if (parentId) untrack(() => release(parentId)); };
  });

  // A child chosen outside this panel (the chat's sub-agent link) opens here too.
  $effect(() => {
    const childId = selectedChildId;
    if (childId && visible && view === 'session') untrack(() => void select(childId));
  });

  async function select(childId: string): Promise<void> {
    if (!ownedId || !conversation || !visible || view !== 'session' || openedChildId === childId) return;
    const parentId = ownedId;
    release(parentId);
    openedChildId = childId;
    setConversationSelectedChild(parentId, childId);
    const child = conversation.children.find((entry) => entry.childId === childId);
    if (!child) return;
    const helper = !child.parentToolCallId;
    if (!helper && !child.transcriptAvailable) return;
    const controller = new AbortController();
    reader = controller;
    transcriptState = 'loading';
    const current = (): boolean => !controller.signal.aborted && ownedId === parentId
      && visible && view === 'session' && getConversationSession(parentId)?.selectedChildId === childId;
    try {
      let historyOwnedId: string;
      if (helper) {
        const adopted = await adoptSession(childId, controller.signal);
        if (!current()) return;
        if (!adopted) {
          transcriptState = 'error';
          transcriptError = 'This helper’s session was not found yet. Try again in a moment.';
          openedChildId = null;
          return;
        }
        historyOwnedId = childId;
      } else {
        const history = await readChildConversationHistory({
          ownedId: parentId,
          childId,
          childSessionId: child.transcriptId ?? childId,
          signal: controller.signal
        });
        if (!current()) return;
        if (!history) {
          transcriptState = 'unavailable';
          return;
        }
        historyOwnedId = history.historyOwnedId;
      }
      selectConversationChat(parentId, historyOwnedId, controller.signal);
      await selectedConversationChatReady(parentId, historyOwnedId);
      if (current()) transcriptState = 'ready';
    } catch (error) {
      if (!current()) return;
      transcriptState = 'error';
      transcriptError = error instanceof Error ? error.message : String(error);
    }
  }

  // Back or another selection stops a pending open, so its result never lands in the next sub-view.
  let helperOpen: AbortController | null = null;

  async function openHelperChat(childId: string): Promise<void> {
    helperOpen?.abort();
    const controller = new AbortController();
    helperOpen = controller;
    fileError = '';
    let opened = false;
    try {
      opened = await sessionRowJump(childId, 'session');
    } catch {
      opened = false;
    }
    if (controller.signal.aborted) return;
    helperOpen = null;
    if (!opened) fileError = 'This helper’s chat could not be opened.';
  }

  function openFile(reference: string, provenance?: ConversationFileLinkProvenance): void {
    const active = rail.owned.find((session) => session.ownedId === ownedId);
    fileError = active ? requestOpenConversationFile(active, reference, provenance) ?? ''
      : 'That file link could not be opened.';
  }
</script>

<section class="agents-panel flex h-full min-h-0 flex-col" data-testid="agents-panel">
  <PanelHeader title="Agents" count={view === 'session' && rows.length > 0 ? rows.length : null} />
  <div class="px-2 pb-2">
    <SegmentedControl
      items={VIEW_OPTIONS}
      value={view}
      size="sm"
      aria-label="Agent view"
      class="w-full"
      onValueChange={(value) => (view = value as typeof view)}
    />
  </div>

  {#if view === 'workflows'}
    <!-- Runs are watched on the full Workflows screen; starting a run stays here until the screen can. -->
    <div class="px-2 pb-2">
      <Button size="sm" class="w-full" onclick={onOpenWorkflows}><Workflow />Open Workflows</Button>
    </div>
    <WorkflowRuns
      visible={visible}
      root={workflowRoot}
      {machine}
      onShowMessages={onShowRunMessages}
    />
  {:else if view === 'messages'}
    <AgentMessages
      visible={visible && view === 'messages'}
      layout="drawer"
      session={ownedId}
      onShowRun={onShowRunMessages}
      onSeen={onMessagesSeen}
    />
  {:else if view === 'inbox'}
    <OwnerInbox visible={visible && view === 'inbox'} sessions={waitingSessions} bind:stepCount={inboxStepCount} />
  {:else if visible && selectedRow && ownedId}
    <div class="flex min-w-0 items-center gap-2 px-2 pb-2" data-testid="agent-transcript-header">
      <Button variant="ghost" size="sm" iconPosition="start" onclick={() => ownedId && release(ownedId)}>
        <ArrowLeft data-icon="inline-start" />Back
      </Button>
      <span class="min-w-0 flex-1 truncate">{selectedRow.label}</span>
      <Chip tone={AGENT_STATUS_TONE[selectedRow.status]}>{AGENT_STATUS_WORD[selectedRow.status]}</Chip>
      {#if selectedIsHelper}
        <Button variant="ghost" size="sm" iconPosition="start" onclick={() => void openHelperChat(selectedRow.childId)}>
          <MessageSquare data-icon="inline-start" />Open chat
        </Button>
      {/if}
    </div>
    {#if fileError}<p role="alert" class="px-2 pb-2 text-sm text-muted-foreground">{fileError}</p>{/if}
    {#if error}
      <EmptyState title="Could not load transcript" body={error} data-testid="agent-transcript-error" />
    {:else if transcriptState === 'loading' && childDownload?.phase === 'error'}
      <EmptyState title="History download stopped" body={childDownload.error ?? ''} />
    {:else if transcriptState === 'loading' && childDownload && !childDownload.ready}
      <EmptyState title={childDownload.phase === 'paused' ? 'History download paused · reconnecting' : 'Downloading history'}
        body={childDownload.totalBytes ? `${(childDownload.coveredBytes / 1_000_000).toFixed(1)} of ${(childDownload.totalBytes / 1_000_000).toFixed(1)} MB · ${childDownloadPercent}%` : undefined}>
        {#snippet actions()}<progress class="history-progress" value={childDownload.coveredBytes} max={childDownload.totalBytes || 1}></progress>{/snippet}
      </EmptyState>
    {:else if transcriptState === 'loading'}
      <EmptyState title="Loading transcript" body="Reading this agent’s saved conversation." data-testid="agent-transcript-loading" />
    {:else if transcriptState === 'unavailable'}
      <EmptyState title="Transcript unavailable" body="This agent’s transcript is not available yet." data-testid="agent-transcript-unavailable" />
    {:else if historyOwnedId && childConversation}
      {#if transcriptChat}
        {#key transcriptChat}
          <UIProvider {ui} chat={transcriptChat}>
      <ConversationTimeline
        {items}
        conversationId={ownedId}
        {historyOwnedId}
        renderWindowId={`${ownedId}:${historyOwnedId}`}
        timelineRevision={childConversation.timelineRevision}
        viewState={selectedConversationViewState(ownedId, historyOwnedId)}
        onViewChange={setSelectedConversationViewState}
        itemFirstSequence={(itemId) => {
          const sequence = messagesById.get(itemId)?.metadata?.firstSequence;
          return typeof sequence === 'number' ? sequence : undefined;
        }}
        activeTurnId={childConversation.activeTurnId ?? null}
        turnFacts={childConversation.selectedTurns}
        assistantLabel={selectedRow.label}
        emptyText="This agent’s transcript is empty."
        hasOlder={childConversation.selectedHasBefore}
        loadingOlder={childConversation.selectedLoadingOlder}
        pageError={childConversation.selectedPageError}
        onLoadOlder={() => void pageSelectedConversation('older', historyOwnedId)}
        hasNewer={childConversation.selectedHasAfter}
        loadingNewer={childConversation.selectedLoadingNewer}
        onLoadNewer={() => void pageSelectedConversation('newer', historyOwnedId)}
        oldestSequence={childConversation.selectedBeforeCursor ?? 0}
        newestSequence={childConversation.selectedAfterCursor ?? 0}
        onJumpToLatest={() => jumpSelectedConversationToLatest(ownedId!, historyOwnedId!)}
        onFileLink={openFile}
        historyDownload={childDownload}
      />
          </UIProvider>
        {/key}
      {/if}
    {/if}
  {:else if rows.length === 0}
    <EmptyState
      title="No session agents yet"
      body="When this thread starts helper sessions or sub-agents, or runs a workflow, they show up here with live status, activity, and token usage."
      data-testid="agents-panel-empty"
    >
      {#snippet icon()}<Bot />{/snippet}
    </EmptyState>
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <ul class="flex flex-col gap-1 p-2">
        {#each rows as row (row.childId)}
          <li class="min-w-0">
            <AgentRow {row} selected={false} onselect={(id) => void select(id)} />
          </li>
        {/each}
      </ul>
    </ScrollArea>
  {/if}
</section>

<style>
  /* The drawer is a few hundred pixels wide, so the shared chat components
     are tightened here only. The main chat keeps its own spacing. */
  .agents-panel :global(.timeline-scroll){padding:0 10px}
  .agents-panel :global(article .turn-body){overflow-wrap:anywhere}
  .agents-panel :global(article.user){max-width:100%;padding:8px 12px;border-radius:var(--radius-lg)}
  .agents-panel :global(article.user .turn-body){font-size:13px}
  .agents-panel :global(article .turn-body ul),
  .agents-panel :global(article .turn-body ol){padding-left:16px}
  .agents-panel :global(article .turn-body li){padding-left:2px}
  .agents-panel :global(article .file-link){display:inline}
  .agents-panel :global(.tool-item summary){padding:2px 4px}
  .agents-panel :global(.tool-item .tool-body){padding:0 6px 8px}
  .history-progress{width:220px;height:6px;accent-color:var(--color-accent)}
</style>
