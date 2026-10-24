<script lang="ts">
  /**
   * HistoryPanel.svelte — the History tab of the right column.
   *
   * Known sessions grouped by machine, project and checkout, most
   * recent first. A project with hundreds of sessions in it opens showing the
   * newest twenty-five per checkout and reads the rest as the reader scrolls,
   * because the alternative is a panel that takes a second to draw every time
   * it is shown.
   *
   * The panel owns running the actions, since it is the only thing here holding
   * the session service and the confirm dialog. Deciding WHICH actions a
   * session supports is `sessionHistoryActions`'s job, and drawing a session's
   * row is `SessionHistoryCard`'s.
   */
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import History from '@lucide/svelte/icons/history';
  import Laptop from '@lucide/svelte/icons/laptop';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Search from '@lucide/svelte/icons/search';
  import Server from '@lucide/svelte/icons/server';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
  import { onDestroy, onMount } from 'svelte';

  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import * as Collapsible from '$lib/components/ui/collapsible/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { PanelHeader } from '$lib/components/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { SegmentedControl } from '$lib/components/ui/segmented-control/index.js';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import {
    buildSessionHistoryViewModel,
    createSessionHistoryCollapseState,
    createSessionHistoryWindowState,
    extendSessionHistoryWindow,
    filterSessionHistoryRecords,
    isSessionHistoryGroupOpen,
    resetSessionHistoryWindowOnFilterChange,
    toggleSessionHistoryGroup,
    type SessionHistoryProjectGroup,
    type SessionHistoryRow,
    type SessionHistoryWorktreeGroup
  } from '$lib/shell/history/sessionHistoryViewModel.ts';
  import {
    sessionHistoryLoadOutcome,
    type SessionHistoryLoadOutcome
  } from '$lib/shell/history/sessionHistoryLoad.ts';
  import {
    buildSessionLibrary,
    type SessionHistoryScope,
    type SessionHistoryFilters,
    type SessionLibraryRecord
  } from '$lib/shell/history/library/sessionLibraryModel.ts';
  import { sessionLibraryHost } from '$lib/shell/history/library/sessionLibraryService.ts';
  import { resetSessionLibraryState, sessionLibraryState, setSessionLibraryQuery } from '$lib/shell/history/library/sessionLibraryStore.svelte.ts';
  import {
    ensureStructuredConversation
  } from '$lib/shell/conversation/conversationService.ts';
  import { refreshSelectedConversationChat } from '$lib/shell/conversation/conversationConnection';
  import { warmAgentConversationConfig } from '$lib/shell/conversation/conversationConfig.ts';
  import { setConversationAgentConfigState } from '$lib/shell/conversation/conversationStore.svelte.ts';
  import { ownedSessionFromBackend } from '$lib/shell/ownedSessions.ts';
  import { projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import { hydrateProjects, projectRegistry } from '$lib/shell/projects/projectRegistry.svelte.ts';
  import { addOwnedSession, rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import {
    beginAgentConversationImportFromTauri,
    finishAgentConversationImportFromTauri,
    listAgentConversationSessionsFromTauri,
    listRepositoryCheckoutsFromTauri,
    openPathFromTauri,
    revealPathFromTauri,
    type RepositoryCheckout
  } from '$lib/tauriSource.ts';
  import { openFileInEditor, showCenterTab } from '$lib/shell/workbenchNavigation.ts';

  const SCOPE_OPTIONS: readonly { value: SessionHistoryScope; label: string }[] = [
    { value: 'workspace', label: 'Workspace' },
    { value: 'project', label: 'Project' },
    { value: 'all', label: 'All' }
  ];

  import SessionHistoryCard from './SessionHistoryCard.svelte';
  import {
    sessionHistoryActions,
    sessionHistoryIdentity,
    sessionResumeCommand,
    sessionTranscriptImport,
    type SessionHistoryActionId
  } from './sessionHistoryActions.ts';

  interface Props {
    /** True while this panel's tab is the selected one. */
    visible: boolean;
    /** The active session's working folder, or '' when nothing is selected. */
    root: string;
    /** The active session's ownedId, or null. */
    ownedId: string | null;
  }
  // Keeps no state of its own between openings: every opening starts fresh.
  let { visible, root, ownedId }: Props = $props();

  // Read once, at init: the page registers its actions in its own component
  // body, which runs before this panel is created.
  const host = sessionLibraryHost();
  // The search and scope live in a shared singleton; clear them so each
  // opening starts fresh.
  resetSessionLibraryState();

  const query = $derived(sessionLibraryState.query);
  let expandedKey = $state<string | null>(null);
  let collapseState = $state(createSessionHistoryCollapseState());
  let windowState = $state(createSessionHistoryWindowState());
  let pendingDelete = $state<SessionLibraryRecord | null>(null);
  /** Redrawn only while the panel is on screen, so ages do not go stale in it. */
  let now = $state(new Date());

  const summaryLibrary = $derived(buildSessionLibrary(rail.owned, rail.available));
  const activeRecord = $derived(summaryLibrary.find((record) => record.ownedId === ownedId) ?? null);
  const projectKey = $derived(activeRecord?.projectGroupKey ?? null);
  const historyFilters = $derived<SessionHistoryFilters>({
    query: sessionLibraryState.query,
    provider: sessionLibraryState.provider,
    model: sessionLibraryState.model,
    dateFrom: sessionLibraryState.dateFrom || null,
    dateTo: sessionLibraryState.dateTo || null,
    scope: sessionLibraryState.scope,
    workspacePath: root.trim() || null,
    projectKey,
    machine: activeRecord?.machine ?? 'local'
  });
  const historyFilterKey = $derived([
    historyFilters.query,
    historyFilters.provider,
    historyFilters.model,
    historyFilters.dateFrom,
    historyFilters.dateTo,
    historyFilters.scope,
    historyFilters.workspacePath,
    historyFilters.projectKey,
    historyFilters.machine
  ].join('\0'));
  const summaryRecords = $derived(
    visible ? filterSessionHistoryRecords(summaryLibrary, historyFilters) : []
  );
  let loadedRecords = $state<SessionLibraryRecord[]>([]);
  let loadOutcome = $state<SessionHistoryLoadOutcome | null>(null);
  /** The project whose sessions are being read, so the row can say it is working.
   *  Opening one reads every session record it holds, which on a large project
   *  is seconds of nothing happening otherwise. */
  let loadingProjectKey = $state<string | null>(null);
  let loadingOlder = $state(false);
  let historyViewport = $state<HTMLElement | null>(null);
  // Project paging and card details are independent reads. Sharing one version
  // let opening a card cancel a still-finishing project refresh, which could
  // leave the project spinner on screen indefinitely.
  let historyLoadVersion = 0;
  let detailLoadVersion = 0;
  let loadStopController = new AbortController();
  type SessionHistoryDetail = Pick<SessionLibraryRecord, 'firstPrompt' | 'latestTurns' | 'messageCount' | 'startComplete'>;
  let detailStopController = new AbortController();
  let detailsByKey = $state<Record<string, SessionHistoryDetail>>({});
  let detailLoadingKey = $state<string | null>(null);

  function releaseDetails(keys?: ReadonlySet<string>): void {
    detailLoadVersion += 1;
    detailStopController.abort();
    detailStopController = new AbortController();
    if (!keys) {
      detailsByKey = {};
      detailLoadingKey = null;
      host.service.release();
      return;
    }
    const next = { ...detailsByKey };
    for (const key of keys) delete next[key];
    detailsByKey = next;
    if (detailLoadingKey && keys.has(detailLoadingKey)) detailLoadingKey = null;
    host.service.release(keys);
  }

  function stopHistoryLoads(): void {
    historyLoadVersion += 1;
    loadStopController.abort();
    loadStopController = new AbortController();
    loadingProjectKey = null;
    loadingOlder = false;
  }

  onDestroy(() => {
    stopHistoryLoads();
    releaseDetails();
  });

  // The registry says which folders on this Mac belong to each group, for its checkouts.
  onMount(() => {
    void hydrateProjects();
  });

  /** The checkouts git lists for a group's folders registered on this Mac, under the group's key. */
  async function groupCheckouts(key: string, machine: string): Promise<Record<string, RepositoryCheckout[]> | null> {
    if (machine !== 'local') return null;
    const roots = projectRegistry.projects
      .filter((project) => project.machine === 'local' && project.groupKey === key)
      .map((project) => project.rootPath);
    const byRoot = await listRepositoryCheckoutsFromTauri(roots);
    return byRoot && { [key]: Object.values(byRoot).flat() };
  }

  /**
   * The checkouts each repository still has, asked of git once the sessions have
   * named their repositories.
   *
   * Match local session subfolders to their existing checkout roots. Only
   * checkouts containing sessions appear; remote paths never use local Git.
   */
  let checkouts = $state<Record<string, RepositoryCheckout[]>>({});

  /** Lightweight rail summaries build only the top-level project headers. */
  const summaryViewModel = $derived(
    buildSessionHistoryViewModel(summaryRecords, { query, windowState })
  );
  const loadedProjects = $derived(new Map(
    buildSessionHistoryViewModel(summaryRecords, { query, windowState, checkouts })
      .projects.flatMap((project) => {
        const loadedKeys = new Set(loadedRecords.map((record) => record.key));
        const worktrees = project.worktrees.map((worktree) => {
          const rows = worktree.rows.filter((row) => loadedKeys.has(row.record.key));
          return {
            ...worktree,
            rows,
            olderCount: worktree.olderCount + worktree.rows.length - rows.length
          };
        });
        const hasRows = worktrees.some((worktree) => worktree.rows.length > 0);
        if (!hasRows && !isSessionHistoryGroupOpen(collapseState, 'project', project.key)) {
          return [];
        }
        return [[project.key, { ...project, worktrees }] as const];
      })
  ));

  /** A new search starts every checkout back at its first page of cards. */
  function search(value: string): void {
    setSessionLibraryQuery(value);
    windowState = resetSessionHistoryWindowOnFilterChange(windowState, { query: value });
    releaseDetails();
    loadedRecords = [];
    loadOutcome = null;
    checkouts = {};
    collapseState = createSessionHistoryCollapseState();
  }

  function setScope(value: string): void {
    if (!SCOPE_OPTIONS.some((option) => option.value === value)) return;
    sessionLibraryState.scope = value as SessionHistoryScope;
    windowState = createSessionHistoryWindowState({
      query,
      provider: sessionLibraryState.provider
    });
  }

  $effect(() => {
    if (visible) return;
    stopHistoryLoads();
    releaseDetails();
    loadedRecords = [];
    loadOutcome = null;
    checkouts = {};
    collapseState = createSessionHistoryCollapseState();
    expandedKey = null;
  });

  $effect(() => {
    root;
    ownedId;
    historyFilterKey;
    stopHistoryLoads();
    releaseDetails();
    loadedRecords = [];
    loadOutcome = null;
    checkouts = {};
    collapseState = createSessionHistoryCollapseState();
    loadingProjectKey = null;
    expandedKey = null;
  });

  $effect(() => {
    if (!visible) return;
    now = new Date();
  });

  async function toggleProject(
    project: SessionHistoryProjectGroup,
    reload = false
  ): Promise<void> {
    stopHistoryLoads();
    const stopSignal = loadStopController.signal;
    releaseDetails();
    expandedKey = null;
    const closing = !reload
      && isSessionHistoryGroupOpen(collapseState, 'project', project.key);
    const version = ++historyLoadVersion;
    loadedRecords = [];
    loadOutcome = null;
    checkouts = {};
    collapseState = createSessionHistoryCollapseState();
    loadingProjectKey = null;
    if (closing) return;

    const keys = new Set(project.worktrees.flatMap((worktree) =>
      worktree.rows.map((row) => row.record.key)
    ));
    collapseState = toggleSessionHistoryGroup(collapseState, 'project', project.key);
    loadingProjectKey = project.key;
    const heldRecords = host.service.records.filter((record) => keys.has(record.key));
    const fullyHeld = heldRecords.length === keys.size;
    if (!fullyHeld) host.service.release(keys);

    try {
      const [refreshed, nextCheckouts] = await Promise.allSettled([
        fullyHeld
          ? Promise.resolve(heldRecords)
          : host.service.refresh(keys),
        groupCheckouts(project.key, project.machine)
      ]);
      if (stopSignal.aborted || !visible || version !== historyLoadVersion) {
        if (!fullyHeld) host.service.release(keys);
        return;
      }
      const outcome = sessionHistoryLoadOutcome({
        requestedKeys: keys,
        refreshed,
        checkouts: nextCheckouts
      });
      loadOutcome = outcome;
      loadedRecords = [...outcome.records];
      checkouts = outcome.checkouts;
      host.service.release(keys);
      if (refreshed.status === 'rejected') {
        console.error('[history] could not load project', refreshed.reason);
      }
      if (outcome.state === 'incomplete' && !reload && host.rescan) {
        await host.rescan();
        if (!stopSignal.aborted && visible && version === historyLoadVersion) {
          await toggleProject(project, true);
        }
      }
    } finally {
      // Cleared whatever happened, and only for the read still in front: a
      // slow project answering after the reader has opened another one must
      // not take that one's spinner away with it.
      if (version === historyLoadVersion) loadingProjectKey = null;
    }
  }

  function toggleWorktree(key: string): void {
    releaseDetails();
    expandedKey = null;
    collapseState = toggleSessionHistoryGroup(collapseState, 'worktree', key);
  }

  async function showOlder(worktreeKey: string, stopSignal: AbortSignal): Promise<void> {
    if (stopSignal.aborted || !visible) return;
    const project = summaryViewModel.projects.find((candidate) =>
      candidate.worktrees.some((worktree) => worktree.key === worktreeKey)
    );
    const worktree = project?.worktrees.find((candidate) => candidate.key === worktreeKey);
    if (!project || !worktree) return;
    const loadedKeys = new Set(loadedRecords.map((record) => record.key));
    const nextWindowState = extendSessionHistoryWindow(windowState, worktreeKey);
    windowState = nextWindowState;
    const nextProject = buildSessionHistoryViewModel(summaryRecords, {
      query,
      windowState: nextWindowState
    }).projects.find((candidate) => candidate.key === project.key);
    const nextWorktree = nextProject?.worktrees.find((candidate) => candidate.key === worktreeKey);
    const keys = new Set((nextWorktree?.rows ?? [])
      .filter((row) => !loadedKeys.has(row.record.key))
      .map((row) => row.record.key));
    if (keys.size === 0) return;
    const version = ++historyLoadVersion;
    try {
      const refreshed = await host.service.refresh(keys);
      if (stopSignal.aborted || !visible || version !== historyLoadVersion) return;
      loadedRecords = [...new Map(
        [...loadedRecords, ...refreshed].map((record) => [record.key, record])
      ).values()];
    } catch (error) {
      if (version === historyLoadVersion) console.error('[history] could not load older sessions', error);
    } finally {
      host.service.release(keys);
    }
  }

  async function loadOlderVisibleSessions(): Promise<void> {
    if (loadingOlder || loadStopController.signal.aborted || !visible) return;
    const project = summaryViewModel.projects.find((candidate) =>
      isSessionHistoryGroupOpen(collapseState, 'project', candidate.key)
    );
    const loadedProject = project ? loadedProjects.get(project.key) : null;
    if (!loadedProject) return;
    const pending = loadedProject.worktrees.filter((worktree) =>
      worktree.olderCount > 0
      && (loadedProject.singleCheckout
        || isSessionHistoryGroupOpen(collapseState, 'worktree', worktree.key))
    );
    if (pending.length === 0) return;

    loadingOlder = true;
    const stopSignal = loadStopController.signal;
    try {
      for (const worktree of pending) {
        if (stopSignal.aborted) return;
        await showOlder(worktree.key, stopSignal);
      }
    } finally {
      if (!stopSignal.aborted) loadingOlder = false;
    }
  }

  async function handleHistoryScroll(): Promise<void> {
    const viewport = historyViewport;
    if (!viewport) return;
    if (viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight > 120) return;
    await loadOlderVisibleSessions();
  }

  $effect(() => {
    const viewport = historyViewport;
    if (!visible || !viewport) return;
    const onScroll = (): void => {
      void handleHistoryScroll();
    };
    viewport.addEventListener('scroll', onScroll, { passive: true });
    return () => viewport.removeEventListener('scroll', onScroll);
  });

  async function copyText(value: string | null): Promise<void> {
    if (!value || typeof navigator === 'undefined' || !navigator.clipboard?.writeText) return;
    await navigator.clipboard.writeText(value);
  }

  /**
   * The words an error carries.
   *
   * A native command rejects with the plain `{ code, message, recoverable }`
   * object the backend serialized, not with an Error, and `String()` turns that
   * into "[object Object]" — which is what the rail used to show. Read the
   * message off either shape.
   */
  function describeError(error: unknown): string {
    if (error instanceof Error) return error.message;
    if (error && typeof error === 'object') {
      const { message } = error as { message?: unknown };
      if (typeof message === 'string' && message.trim()) return message;
    }
    return String(error);
  }

  /**
   * Pick a past session back up as a session of this app's own.
   *
   * Two steps, and both are needed for the one button to mean what it says. The
   * import writes the past conversation into this app's storage as a new
   * app-owned session and hands back its id, so the rail is told about that row
   * before anything opens it — opening a session reads the rail for the agent it
   * belongs to. That much puts the transcript on screen. What makes it a session
   * you can SEND to is the second step: the ACP adapter is started for it, the
   * same call adopting a scanned session makes, which is also what fills in the
   * model and effort the composer offers. No CLI and no terminal are involved —
   * the adapter is the whole runtime here.
   */
  async function resumeAsAssemblySession(record: SessionLibraryRecord): Promise<void> {
    // Every row resumes through the import on its own machine; one already here only opens unless its transcript grew.
    const request = sessionTranscriptImport(record);
    const remoteProfileId = record.machine === 'local' ? null : record.machine;
    let named: [string, boolean] | null = null;
    try {
      if (request) named = await beginAgentConversationImportFromTauri({ ...request, remoteProfileId });
    } catch (error) {
      rail.error = `Could not resume "${record.title}": ${describeError(error)}`;
      if (rail.error.includes('still running outside Assembly')) { rail.runningElsewhere = [...rail.runningElsewhere, record.key]; return; }
    }
    if (record.ownedId && (!named || !named[1])) {
      await host.service.open(record);
      showCenterTab('session');
      return;
    }
    if (!request || !named) return;
    try {
      // Naming read none of the transcript, so the rail, title and agent are there at once; the two
      // slow halves, reading the records and starting the agent, run together below.
      const [ownedId] = named;
      const stored = (await listAgentConversationSessionsFromTauri()) ?? [];
      const imported = stored.find((session) => session.ownedId === ownedId);
      if (!imported) {
        rail.error = `the imported transcript for "${record.title}" was not stored`;
        console.error('[resume]', rail.error);
        return;
      }
      if (!rail.owned.some((session) => session.ownedId === ownedId)) addOwnedSession(ownedSessionFromBackend(imported));
      await host.service.open({ ...record, ownedId });
      // A session you asked to resume is one you want to look at, so bring it
      // forward the same way adopting a scanned session does.
      showCenterTab('session');
      const [transcript, conversation] = await Promise.allSettled([
        // That machine has read the newest page already.
        remoteProfileId ? Promise.resolve(0) : finishAgentConversationImportFromTauri(ownedId),
        ensureStructuredConversation({
          ownedId,
          executionEnvironment: remoteProfileId ? 'remote' : 'local',
          remoteProfileId,
          provider: request.provider,
          cwd: request.cwd,
          nativeSessionId: request.nativeSessionId,
          nativeSessionMode: 'resume'
        })
      ]);
      // Either half could land last, and starting the agent reads the conversation back as part of
      // its work; reading it once more after both is what shows the transcript, not an empty session.
      await refreshSelectedConversationChat(ownedId);
      // A transcript that would not load is a failed resume. An agent that would not start is not:
      // the conversation is on screen and readable, and the usual reason is that its folder was deleted.
      if (transcript.status === 'rejected') throw transcript.reason;
      if (conversation.status === 'rejected') {
        rail.error = `"${record.title}" is open and readable, but its agent could not start: ${describeError(conversation.reason)}`;
        console.warn('[resume]', rail.error, conversation.reason);
      } else if (conversation.value) {
        // A session picked up from a transcript has never run, so the models, efforts and approval
        // policies the composer offers do not exist yet: this asks once, stores them, and stops the adapter.
        try {
          const warmed = await warmAgentConversationConfig(ownedId, conversation.value.generation);
          setConversationAgentConfigState(ownedId, warmed);
        } catch (error) {
          // The conversation is imported, on screen and readable. Not knowing
          // what the agent offers is worth a sentence, not a failed resume.
          rail.error = `"${record.title}" is open, but its agent settings could not be read: ${describeError(error)}`;
          console.warn('[resume]', rail.error, error);
        }
      }
    } catch (error) {
      rail.error = `could not resume "${record.title}" as an Assembly session: ${describeError(error)}`;
      console.error('[resume]', rail.error, error);
    }
  }

  /**
   * Run one of a card's actions. Nothing here re-checks whether the action is
   * allowed beyond the roster's own answer, and nothing here fails quietly: an
   * action the record cannot support never becomes pressable in the first place.
   */
  async function runAction(record: SessionLibraryRecord, id: SessionHistoryActionId): Promise<void> {
    const allowed = sessionHistoryActions(record, rail.scanningMachines.includes(record.machine), rail.runningElsewhere.includes(record.key)).find((action) => action.id === id);
    if (!allowed?.enabled) return;

    switch (id) {
      case 'resume-assembly':
        await resumeAsAssemblySession(record);
        return;
      case 'view-log':
        if (record.logPath) openFileInEditor({ path: record.logPath });
        return;
      case 'open-log':
        if (record.logPath) await openPathFromTauri(record.logPath);
        return;
      case 'reveal-log':
        if (record.logPath) await revealPathFromTauri(record.logPath);
        return;
      case 'open-working-directory':
        await openPathFromTauri(record.canonicalCwd);
        return;
      case 'copy-resume-command':
        await copyText(sessionResumeCommand(record));
        return;
      case 'copy-session-id':
        await copyText(sessionHistoryIdentity(record));
        return;
      case 'copy-log-path':
        await copyText(record.logPath);
        return;
      case 'delete':
        // Nothing is removed until the question below is answered.
        pendingDelete = record;
        return;
    }
  }

  async function confirmDelete(): Promise<void> {
    const record = pendingDelete;
    pendingDelete = null;
    if (record) await host.service.delete(record);
  }

  function toggleCard(row: SessionHistoryRow): void {
    if (expandedKey === row.record.key) {
      releaseDetails(new Set([row.record.key]));
      expandedKey = null;
      return;
    }
    releaseDetails();
    expandedKey = row.record.key;
    void loadCardDetails(row.record, detailStopController.signal);
  }

  async function loadCardDetails(record: SessionLibraryRecord, signal: AbortSignal): Promise<void> {
    const key = record.key;
    const version = ++detailLoadVersion;
    detailLoadingKey = key;
    const projectPath = record.canonicalCwd.trim();
    try {
      const refreshed = await host.service.refresh(
        new Set([key]),
        projectPath ? { projectPath } : undefined,
        { includeDetails: true, signal }
      );
      if (signal.aborted || !visible || version !== detailLoadVersion || expandedKey !== key) {
        host.service.release(new Set([key]));
        return;
      }
      const detail = refreshed.find((candidate) => candidate.key === key);
      if (detail) {
        detailsByKey = {
          ...detailsByKey,
          [key]: { firstPrompt: detail.firstPrompt, latestTurns: detail.latestTurns, messageCount: detail.messageCount, startComplete: detail.startComplete }
        };
      }
      host.service.release(new Set([key]));
    } catch (error) {
      if (version === detailLoadVersion) console.error('[history] could not load session details', error);
    } finally {
      if (version === detailLoadVersion) detailLoadingKey = null;
    }
  }
</script>

<div class="history-panel flex h-full min-h-0 flex-col">
  <PanelHeader title="History">
    {#snippet actions()}
      <IconButton
        label="Look for sessions again"
        disabled={rail.scanning}
        onclick={() => host.rescan?.()}
      >
        <RefreshCw />
      </IconButton>
    {/snippet}
    <span data-testid="session-history-host-line" class="flex min-w-0 items-center gap-1.5 text-xs text-[var(--color-text-3)]">
      <span data-testid="session-history-host" class="shrink-0">{summaryViewModel.totalCount} {summaryViewModel.totalCount === 1 ? 'session' : 'sessions'}</span>
      <!-- One machine in view: the header names it, and no machine heading is drawn. -->
      {#if summaryViewModel.machines.length === 1}
        {@const machine = summaryViewModel.machines[0].key}
        <span aria-hidden="true">·</span>
        {#if machine === 'local'}
          <Laptop class="size-[13px] shrink-0" aria-hidden="true" />
        {:else}
          <Server class="size-[13px] shrink-0" aria-hidden="true" />
        {/if}
        <span class="truncate">{projectMachineLabel(machine, rail.remoteProfiles)}</span>
      {/if}
    </span>
  </PanelHeader>
  <div data-testid="session-history-scope" class="px-2 py-1">
    <SegmentedControl
      items={SCOPE_OPTIONS}
      value={sessionLibraryState.scope}
      size="sm"
      class="grid w-full grid-cols-3"
      aria-label="Session history scope"
      onValueChange={setScope}
    />
  </div>
  <div class="relative px-2 py-2">
    <Search class="pointer-events-none absolute top-1/2 left-6 size-[16px] -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
    <Input
      class="h-8 rounded-full border-transparent bg-muted dark:bg-muted pl-9 text-[13px]"
      placeholder="Search sessions"
      autocomplete="off"
      spellcheck="false"
      value={query}
      oninput={(event) => search(event.currentTarget.value)}
    />
  </div>

  <!-- The sessions of one checkout, drawn the same whether they sit under a
       worktree heading or directly under the project. -->
  {#snippet sessionRows(worktree: SessionHistoryWorktreeGroup)}
    {#each worktree.rows as row (row.record.key)}
      <SessionHistoryCard
        record={row.record}
        title={row.displayTitle}
        machineLabel={projectMachineLabel(row.record.machine, rail.remoteProfiles)} machineScanning={rail.scanningMachines.includes(row.record.machine)} runningElsewhere={rail.runningElsewhere.includes(row.record.key)}
        expanded={expandedKey === row.record.key}
        {now}
        onToggle={() => toggleCard(row)}
        onAction={(id) => void runAction(row.record, id)}
        onCopyText={(value) => void copyText(value)}
        onOpenLog={(path) => openFileInEditor({ path })}
        detail={detailsByKey[row.record.key]}
        detailsLoading={detailLoadingKey === row.record.key}
      />
    {/each}
    {#if worktree.olderCount > 0 && loadingOlder}
      <div class="flex justify-center px-3 py-1.5" aria-label="Loading older sessions">
        <WorkingSpinner size={12} />
      </div>
    {/if}
  {/snippet}

  <!-- The list only exists in the document while its tab is showing. A hidden
       tab used to keep every session card mounted under display:none, and the
       browser engine walks that invisible text character by character during
       mouse hit-tests and caret queries — over a hundred thousand hidden
       characters froze the whole app for seconds at a time. The panel's own
       state (search, open groups, paging) lives above and survives; reopening
       the tab rebuilds the cards from it instantly. -->
  {#if visible}
  <!-- The kit's scroll area ships as `type="hover"`, which leaves a reader who
       is not already pointing at the list with no sign that there is more of
       it. This one keeps its bar on screen. -->
  <ScrollArea
    type="always"
    class="min-h-0 flex-1"
    bind:viewportRef={historyViewport}
  >
    {#if summaryViewModel.projects.length === 0}
      <EmptyState
        title={query ? 'Nothing matches that search' : 'No past sessions yet'}
        body={query
          ? 'Try a shorter search, or part of a project name.'
          : 'Sessions you have run appear here, grouped by project, with everything needed to pick one back up.'}
      >
        {#snippet icon()}<History />{/snippet}
      </EmptyState>
    {:else}
      <!-- Groups start collapsed and open one click at a time — mounting every
           card at once is what used to freeze the app. A search is the
           exception: its results are already few, so they all show. -->
      {#each summaryViewModel.projects as project, projectIndex (project.key)}
        {#if summaryViewModel.showMachines && (projectIndex === 0 || summaryViewModel.projects[projectIndex - 1].machine !== project.machine)}
          {@const machineCount = summaryViewModel.machines
            .find((machine) => machine.key === project.machine)
            ?.projects.reduce((total, group) => total + group.count, 0)}
          <h2 class="sticky top-0 z-20 mt-1 flex h-7 items-center gap-1.5 bg-card px-3.5 text-[11px]
                     font-semibold tracking-[0.04em] text-[var(--color-text-3)] uppercase">
            {#if project.machine === 'local'}
              <Laptop class="size-[13px] shrink-0" aria-hidden="true" />
            {:else}
              <Server class="size-[13px] shrink-0" aria-hidden="true" />
            {/if}
            <span class="min-w-0 flex-1 truncate">{projectMachineLabel(project.machine, rail.remoteProfiles)}</span>
            <span class="text-xs font-normal tracking-normal tabular-nums">{machineCount}</span>
          </h2>
        {/if}
        {@const open = isSessionHistoryGroupOpen(collapseState, 'project', project.key)}
        {@const loadedProject = loadedProjects.get(project.key)}
        <Collapsible.Root {open} onOpenChange={() => void toggleProject(project)}>
          <h2 class={`sticky z-10 bg-card px-1.5 ${summaryViewModel.showMachines ? 'top-7' : 'top-0'}`}>
            <Collapsible.Trigger
              class="flex h-7 w-full items-center gap-1.5 rounded-[var(--radius-sm)] px-2 text-left
                     text-[13px] font-medium text-foreground outline-none transition-colors
                     hover:bg-[var(--color-hover)] focus-visible:shadow-(--focus-ring)"
            >
              {#if loadingProjectKey === project.key}
                <WorkingSpinner size={14} />
              {:else}
                <ChevronRight
                  class={`size-3.5 shrink-0 transition-transform duration-150 ${open ? 'rotate-90' : ''}`}
                  aria-hidden="true"
                />
              {/if}
              <span class="min-w-0 flex-1 truncate">{project.name}</span>
              <span class="pl-2 text-xs font-normal text-[var(--color-text-3)] tabular-nums">{project.count}</span>
            </Collapsible.Trigger>
          </h2>

          <Collapsible.Content>
            {#if loadOutcome?.state === 'failed'}
              <div class="flex items-center justify-between gap-2 px-3 py-2 text-sm text-muted-foreground">
                <span>Could not load sessions.</span>
                <Button size="sm" variant="ghost" onclick={() => void toggleProject(project, true)}>
                  Retry
                </Button>
              </div>
            {:else}
            {#if loadedProject}
            <!-- One checkout means no second heading to sit under, so its
                 sessions stay one step in from the project. Under a checkout
                 heading they step in once more. -->
            <div>
              {#each loadedProject.worktrees as worktree (worktree.key)}
                {@const worktreeOpen =
                  loadedProject.singleCheckout
                  || isSessionHistoryGroupOpen(collapseState, 'worktree', worktree.key)}
                {#if loadedProject.singleCheckout}
                  {@render sessionRows(worktree)}
                {:else}
                  <Collapsible.Root
                    open={worktreeOpen}
                    onOpenChange={() => toggleWorktree(worktree.key)}
                  >
                    {@const branch = worktree.rows
                      .map((row) => row.record.owned?.branch ?? row.record.available?.branchHint)
                      .find(Boolean)}
                    <Collapsible.Trigger
                      class="mx-1.5 flex h-[26px] w-[calc(100%-12px)] items-center gap-1.5 rounded-[var(--radius-sm)]
                             pr-2 pl-[22px] text-left text-xs text-[var(--color-text-2)] outline-none
                             transition-colors hover:bg-[var(--color-hover)] focus-visible:shadow-(--focus-ring)"
                      title={branch ? `${worktree.path} · branch ${branch}` : worktree.path}
                    >
                      <ChevronRight
                        class={`size-3 shrink-0 text-[var(--color-text-3)] transition-transform duration-150 ${worktreeOpen ? 'rotate-90' : ''}`}
                        aria-hidden="true"
                      />
                      <span class="min-w-0 truncate">{worktree.name}</span>
                      <span class="ml-auto pl-2 text-[var(--color-text-3)] tabular-nums">{worktree.count}</span>
                    </Collapsible.Trigger>
                    <Collapsible.Content style="--indent: 36px">
                      {@render sessionRows(worktree)}
                    </Collapsible.Content>
                  </Collapsible.Root>
                {/if}
              {/each}
            </div>
            {/if}
            {#if loadOutcome?.state === 'incomplete'}
              <div class="flex items-center justify-between gap-2 px-3 py-2 text-sm text-muted-foreground">
                <span>{loadOutcome.missingKeys.length} sessions could not be loaded.</span>
                <Button
                  size="sm"
                  variant="ghost"
                  onclick={async () => {
                    await host.rescan?.();
                    await toggleProject(project, true);
                  }}
                >
                  Retry
                </Button>
              </div>
            {/if}
            {/if}
          </Collapsible.Content>
        </Collapsible.Root>
      {/each}
    {/if}
  </ScrollArea>
  {/if}
</div>

<AlertDialog.Root
  open={pendingDelete !== null}
  onOpenChange={(open) => {
    if (!open) pendingDelete = null;
  }}
>
  <AlertDialog.Content data-testid="session-history-delete-confirm">
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-1.5 text-[14px] leading-snug font-semibold">
        <TriangleAlert class="size-4 shrink-0 text-[var(--color-bad)]" aria-hidden="true" />
        Delete this session?
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-relaxed text-muted-foreground">
        “{pendingDelete?.title ?? ''}” is removed from this app. The transcript file on disk is
        left alone, and this cannot be undone from here.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">Keep it</AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant="destructive"
        class="text-[13px]"
        onclick={() => void confirmDelete()}
      >
        Delete session
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<style>
  /*
    The kit's scroll thumb is a 7% hairline, which on this surface is a
    scrollbar nobody can see. The bar is the only thing telling a reader that a
    project has more sessions below the fold, so it is drawn to be read.
  */
  .history-panel :global([data-slot='scroll-area-thumb']) {
    background: color-mix(in srgb, var(--color-text) 24%, transparent);
  }
</style>
