<script lang="ts">
  /**
   * SourceControlPanel.svelte — the Source control tab of the right column.
   *
   * What the working copy looks like, and the changes this version can make to
   * it: stage or unstage one file or all of them, throw one file's changes away,
   * commit what is staged, and fetch, pull, push or publish from the header's
   * more-actions menu. Rebasing and pull requests are still absent.
   *
   * The picker at the top says which folder is being read. It defaults to the
   * session's own and can be pointed at any other checkout of the repository,
   * which is READ-ONLY: on somebody else's worktree the commit box, the two
   * buttons, the remote actions and the per-file write actions are all gone,
   * and a line says which folder is on screen. Selecting another checkout is
   * always read-only; supported Assembly sessions get a separate action to make
   * that folder their durable session checkout.
   *
   * THIS PANEL IS THE ONLY PLACE A DISCARD IS ASKED ABOUT. A row can request
   * one; nothing reaches `gitService.discardPaths` until the dialog is answered,
   * because git keeps no copy of a discarded change.
   *
   * The diff is NOT drawn here. Picking a file asks the workbench to show that
   * file's changes, which land in the Diff tab in the middle of the shell, so
   * this panel never needs to know where that tab is.
   *
   * No IO of its own: every git call goes through `gitService`
   * (the working copy) or `gitCommitFilesService` (a commit's files), and only
   * ever from a press. The shell points the service at a folder with
   * `gitService.activate(root)`; until that happens the panel says so and stays
   * quiet, which is what keeps a cold launch free of git commands.
   *
   * WRITING vs READING. In the desktop app both writes work. A browser tab can
   * read a repository through the dev server's bridge but deliberately cannot
   * change one, so Stage All and Commit are off there and say why: a page left
   * open on a repository must not be able to commit it by accident.
   */
  import { selectedConversationChat, selectedConversationHistoryOwnedId } from '$lib/shell/conversation/conversationConnection';
  import { untrack } from 'svelte';
  import { conversationHasRunningTool } from '$lib/shell/conversation/conversationMessages.ts';
  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { PanelHeader } from '$lib/components/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import { buttonVariants } from '$lib/components/ui/button/variants.js';
  import { Switch } from '$lib/components/ui/switch/index.js';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import GitBranch from '@lucide/svelte/icons/git-branch';
  import { canChangeRepository, READ_ONLY_IN_BROWSER_MESSAGE } from '$lib/shell/git/gitBackendExtra';
  import {
    gitCommitFilesService as defaultCommitFilesService,
    type GitCommitFilesService
  } from '$lib/shell/git/gitCommitFilesService';
  import {
    gitCommitFiles as defaultCommitFilesState,
    gitCommitFilesView,
    type GitCommitFilesState
  } from '$lib/shell/git/gitCommitFilesStore.svelte';
  import {
    describeGitBranchTitle,
    hasStagedChanges,
    isGitFileUntracked,
    isNotARepositoryError,
    isPlainFolderError
  } from '$lib/shell/git/gitPanelStore.svelte';
  import { gitService as defaultService, type GitService } from '$lib/shell/git/gitService';
  import BranchMenu from '$lib/shell/git/components/BranchMenu.svelte';
  import DiscardConfirmDialog from '$lib/shell/git/components/DiscardConfirmDialog.svelte';
  import AddRemoteDialog from '$lib/shell/git/components/AddRemoteDialog.svelte';
  import {
    describeDiscardQuestion,
    type DiscardQuestion,
    type DiscardTarget
  } from '$lib/shell/git/components/discardConfirm';
  import { worktreeManager } from '$lib/shell/worktrees/worktreeManagerStore.svelte';
  import { canonicalPath } from '$lib/shell/files/explorer/explorerStore.svelte';
  import { getConversationSession } from '$lib/shell/conversation/conversationStore.svelte';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  import type { SessionSourceControlWorkspace } from '$lib/shell/sessionWorkspaces';
  import { validateProjectRootFromTauri, type ProjectGitFileStatus } from '$lib/tauriSource';
  import { cn } from '$lib/utils';

  import ChangedFileRow from './ChangedFileRow.svelte';
  import CommitTimeline from './CommitTimeline.svelte';
  import {
    describeSourceControlScope,
    isSourceControlScopeReadOnly,
    sourceControlPublishAction,
    sourceControlRemoteActions,
    sourceControlScopeOptions,
    type SourceControlRemoteActionId
  } from './sourceControlPanelActions.ts';
  import {
    discardAllUntrackedCount,
    sourceControlDiffstat,
    sourceControlSections
  } from './sourceControlSections.ts';

  interface Props {
    /** True while this is the tab in front. */
    visible: boolean;
    /** The active session's folder. The shell activates the service with it. */
    root: string;
    /** The active session, when there is one. Unused: this panel is per folder. */
    ownedId: string | null;
    /** False when the active session's checkout has disappeared. */
    rootAvailable?: boolean;
    /** The working-copy service. The shell's singleton unless a test says otherwise. */
    service?: GitService;
    /** The per-commit file service, likewise. */
    commitFiles?: GitCommitFilesService;
    commitFilesState?: GitCommitFilesState;
    /** Can this page change the repository? Defaults to "only in the desktop app". */
    canWrite?: boolean;
    inspectionRoot?: string | null;
    onInspectionRootChange?(root: string | null): void;
    workspaceState?: SessionSourceControlWorkspace;
    onWorkspaceStateChange?(ownedId: string | null, state: SessionSourceControlWorkspace): void;
    /** Applies a selected linked worktree as the active session checkout. */
    onUseSessionCheckout?(root: string): void | Promise<void>;
    /** Set while another session works in the folder shown: nothing here may change it. */
    readOnlyReason?: string | null;
    /** True while `root` is a project the session only views, not the session's own folder. */
    viewing?: boolean;
  }
  let {
    visible,
    root,
    ownedId,
    rootAvailable = true,
    service = defaultService,
    commitFiles = defaultCommitFilesService,
    commitFilesState = defaultCommitFilesState,
    canWrite = canChangeRepository(),
    inspectionRoot,
    onInspectionRootChange,
    workspaceState,
    onWorkspaceStateChange,
    onUseSessionCheckout,
    readOnlyReason = null,
    viewing = false
  }: Props = $props();
  const workspaceOwnedId = untrack(() => ownedId);
  const initialWorkspaceState = untrack(() => workspaceState);

  /** Why nothing can be changed while the panel is on another checkout. */
  const READ_ONLY_SCOPE_MESSAGE =
    'This folder is open for reading only — switch back to the session folder to change it.';

  const panel = $derived(service.state);
  const commitInteraction = $derived(gitCommitFilesView(commitFilesState, 'compact'));
  const folder = $derived(panel.root ?? root ?? '');

  /** The session's own folder — the one the shell activates the service with. */
  const sessionRoot = $derived((root ?? '').trim());
  /** The checkout the reader picked, or '' while the session's own is showing. */
  let scopeRoot = $state('');

  const scopeOptions = $derived(sourceControlScopeOptions(sessionRoot, worktreeManager.worktrees, viewing));
  const readOnlyScope = $derived(isSourceControlScopeReadOnly(sessionRoot, scopeRoot));
  const scopeValue = $derived(scopeRoot === '' ? sessionRoot : scopeRoot);
  const scopeLabel = $derived(
    scopeOptions.find((option) => option.path === scopeValue)?.label ?? 'Session folder'
  );

  const ownedSession = $derived(
    ownedId === null ? null : rail.owned.find((session) => session.ownedId === ownedId) ?? null
  );
  const conversation = $derived(ownedId === null ? null : getConversationSession(ownedId));
  const activeTool = $derived.by(() => {
    if (!conversation) return false;
    conversation.timelineRevision;
    return selectedConversationHistoryOwnedId() === ownedId
      && conversationHasRunningTool(selectedConversationChat(ownedId)?.messages ?? []);
  });
  let checkoutBusy = $state(false);
  const checkoutDisabledReason = $derived.by(() => {
    if (!readOnlyScope) return null;
    if (rail.activeOwnedId !== ownedId) return 'Select this session before changing its checkout.';
    if (!ownedSession?.canChangeCheckout) {
      return "This session's agent can't keep its conversation in another checkout.";
    }
    if (ownedSession.origin !== 'app') {
      return 'Only Assembly sessions can change the session checkout.';
    }
    if (!onUseSessionCheckout) return 'Checkout changes are unavailable from this surface.';
    if (!conversation) return 'The Assembly session is not connected yet.';
    if (ownedSession.executionOwner !== 'structured') {
      return 'Checkout changes require the structured Assembly writer.';
    }
    if (conversation.sending || conversation.activeTurnId || ownedSession.activeTurnId) {
      return 'Finish the active turn before changing checkout.';
    }
    if (ownedSession.pendingPermission) {
      return 'Resolve the pending permission before changing checkout.';
    }
    if (ownedSession.pendingInput) {
      return 'Complete the pending input before changing checkout.';
    }
    if (activeTool) return 'Wait for the active tool operation to finish before changing checkout.';
    if (
      ownedSession.runtimeState !== undefined
      && !['ready', 'suspended'].includes(ownedSession.runtimeState)
    ) {
      return 'Wait for the session to become quiescent before changing checkout.';
    }
    return null;
  });
  const canUseSessionCheckout = $derived(
    readOnlyScope && !checkoutBusy && checkoutDisabledReason === null
  );
  const checkoutHint = $derived(
    checkoutBusy
      ? 'Changing the session checkout…'
      : checkoutDisabledReason ?? 'Use this linked worktree as the session checkout.'
  );

  /** A different session means a different repository: the old pick is dropped. */
  let scopedSessionRoot = '';
  let inspectionRestoreGeneration = 0;
  $effect(() => {
    if (sessionRoot === scopedSessionRoot) return;
    scopedSessionRoot = sessionRoot;
    inspectionRestoreGeneration += 1;
    scopeRoot = '';
  });

  $effect(() => {
    if (inspectionRoot === undefined) return;
    const requestedRoot = canonicalPath(inspectionRoot ?? '');
    const sessionRootPath = canonicalPath(sessionRoot);
    const target = requestedRoot && requestedRoot !== sessionRootPath ? requestedRoot : '';
    const currentRoot = canonicalPath(scopeRoot);
    const generation = ++inspectionRestoreGeneration;
    if (!visible || !sessionRootPath) return;
    if (requestedRoot === currentRoot && (!inspectionRoot || target !== '')) return;
    if (!target) {
      setInspectionRoot('');
      return;
    }
    const worktree = worktreeManager.worktrees.find(
      (entry) => canonicalPath(entry.path) === target && entry.isPrunable !== true
    );
    if (worktreeManager.loading || !worktreeManager.worktrees.some((entry) => canonicalPath(entry.path) === sessionRootPath)) return;
    if (!worktree || !scopeOptions.some((option) => canonicalPath(option.path) === target)) {
      setInspectionRoot('');
      return;
    }
    void validateInspectionRootForEffect(generation, target, sessionRootPath);
    return () => {
      inspectionRestoreGeneration += 1;
    };
  });

  async function validateInspectionRootForEffect(
    generation: number,
    target: string,
    sessionRootPath: string
  ): Promise<void> {
    let validation: Awaited<ReturnType<typeof validateProjectRootFromTauri>>;
    try {
      validation = await validateProjectRootFromTauri(target);
    } catch {
      validation = null;
    }
    if (
      generation !== inspectionRestoreGeneration
      || canonicalPath(root) !== sessionRootPath
      || canonicalPath(inspectionRoot ?? '') !== target
    ) return;
    if (validation === null || (validation.exists && validation.isDirectory)) {
      setInspectionRoot(target);
    } else {
      setInspectionRoot('');
    }
  }

  /** The one place a scope becomes a real read. `activate` ignores a repeat. */
  let historyRoot = '';
  let wasVisible = false;

  function releaseHistorySurface(): void {
    if (historyRoot === '') return;
    historyRoot = '';
    service.releaseHistorySurface();
  }

  $effect(() => {
    const becameVisible = visible && !wasVisible;
    wasVisible = visible;
    const readableTarget = rootAvailable ? (scopeRoot === '' ? sessionRoot : scopeRoot) : '';
    if (!visible || readableTarget === '') {
      releaseHistorySurface();
      return;
    }
    if (historyRoot !== readableTarget) {
      releaseHistorySurface();
      historyRoot = readableTarget;
    }
    service.activate(readableTarget);
    commitFiles.activate(readableTarget);
    if (
      !service.state.status &&
      !service.state.statusLoading &&
      service.state.statusError === '' &&
      !service.state.desktopOnly
    ) {
      void service.refreshStatus();
    }
    service.ensureHistorySurface();
    if (becameVisible && panel.diffOwner !== 'compact') {
      if (commitInteraction.selectedCommitSha) void commitFiles.restoreSelection();
      else if (panel.selectedPaths.compact) {
        void service.showStoredDiff(readableTarget, panel.selectedPaths.compact, 'compact');
      }
    }
  });

  /** A folder with no repository in it is an ordinary thing to be looking at,
   * not a fault — so it gets a plain sentence instead of git's `fatal:` line. */
  const notARepository = $derived(
    isNotARepositoryError(panel.statusError) || isNotARepositoryError(panel.historyError)
  );

  const sections = $derived(sourceControlSections(panel.status));
  const diffstat = $derived(sourceControlDiffstat(panel.status));
  const staged = $derived(hasStagedChanges(panel.status));
  const busy = $derived(panel.actionBusy !== '');
  const files = $derived(panel.status?.files ?? []);
  const scopeAvailable = $derived(rootAvailable || readOnlyScope);

  /** Can this panel change what it is looking at? Both answers have a sentence. */
  const canChange = $derived(rootAvailable && canWrite && !readOnlyScope && !readOnlyReason);
  const cannotChangeReason = $derived(
    !rootAvailable
      ? 'Checkout/Worktree deleted.'
      : readOnlyReason || (readOnlyScope ? READ_ONLY_SCOPE_MESSAGE : READ_ONLY_IN_BROWSER_MESSAGE)
  );

  /** A plain folder (never a bare repository) the panel may change can be made a repository. */
  const canInitRepository = $derived(canChange && isPlainFolderError(panel.statusError));
  /** Owns one Initialize press; a new folder or unmount aborts it. */
  let initRun = new AbortController();
  $effect(() => {
    panel.root;
    return () => initRun.abort();
  });

  function initRepository(): void {
    if (!canInitRepository || busy) return;
    initRun = new AbortController();
    void service.initRepository(initRun.signal);
  }

  /** The folder, branch and remotes captured when the more-actions menu opens
   * without an upstream, also used by Fetch at detached HEAD. Null otherwise.
   * Raw: each update replaces the whole object. */
  type PublishTarget = { root: string; branch: string; remotes: string[] | null; error: string };
  let publishTarget = $state.raw<PublishTarget | null>(null);
  /** Owns the remote-list read for one menu opening; closing it, reopening it or unmounting aborts it. */
  let remotesRead = new AbortController();
  $effect(() => () => remotesRead.abort());

  const remoteActions = $derived(
    sourceControlRemoteActions(panel.status, folder, {
      canWrite: canChange,
      readOnlyReason: cannotChangeReason,
      busy,
      remotes: publishTarget?.root === folder ? publishTarget.remotes : null
    })
  );

  function runRemote(id: SourceControlRemoteActionId): void {
    void service.runRemoteAction(id);
  }

  function moreMenuOpenChange(open: boolean): void {
    remotesRead.abort();
    const branch = panel.status?.branch;
    if (!open || panel.status?.hasUpstream) {
      publishTarget = null;
      return;
    }
    const target = { root: folder, branch: branch ?? '', remotes: null, error: '' };
    publishTarget = target;
    remotesRead = new AbortController();
    void readPublishRemotes(target, remotesRead.signal);
  }

  async function readPublishRemotes(target: PublishTarget, signal: AbortSignal): Promise<void> {
    try {
      const list = await service.listBranches(signal);
      if (!signal.aborted) publishTarget = { ...target, remotes: list?.remotes ?? [] };
    } catch (error) {
      if (!signal.aborted) publishTarget = { ...target, remotes: [], error: error instanceof Error ? error.message : String(error) };
    }
  }

  const publishAction = $derived(
    sourceControlPublishAction(panel.status, folder, {
      canWrite: canChange,
      readOnlyReason: cannotChangeReason,
      busy,
      remotes: publishTarget?.root === folder ? publishTarget.remotes : null
    })
  );
  const publishReason = $derived.by(() => {
    if (!publishTarget) return null;
    if (folder !== publishTarget.root || panel.status?.branch !== publishTarget.branch) {
      return 'The branch changed. Reopen this menu to publish it.';
    }
    if (!publishAction.enabled) return publishAction.disabledReason;
    if (publishTarget.error) return publishTarget.error;
    if (publishTarget.remotes === null) return 'Reading this repository’s remotes…';
    if (publishTarget.remotes.length === 0) {
      return 'This repository has no configured remote. Add one before publishing.';
    }
    return null;
  });

  function publish(remote: string): void {
    const target = publishTarget;
    if (!target || publishReason !== null || !target.remotes?.includes(remote)) return;
    void service.publishBranch(target.root, target.branch, remote);
  }

  const canAddRemote = $derived(canChange && !busy);
  let addRemoteOpen = $state(false);
  /** The folder the open Add remote dialog adds to, captured when it opens. */
  let addRemoteRoot = '';

  function openAddRemote(): void {
    if (!canAddRemote) return;
    addRemoteRoot = folder;
    addRemoteOpen = true;
  }

  async function addRemote(name: string, url: string): Promise<string | null> {
    // A skipped add leaves the panel's error from some other action; say why here instead.
    if (folder !== addRemoteRoot) return 'The folder changed. Close this dialog and add the remote again.';
    if (busy) return 'Wait for the current source-control action to finish.';
    if (await service.addRemote(addRemoteRoot, name, url)) return null;
    return panel.actionError || 'The remote was not added. Refresh Source Control and try again.';
  }

  async function useSessionCheckout(): Promise<void> {
    if (!canUseSessionCheckout || !onUseSessionCheckout) return;
    checkoutBusy = true;
    try {
      await onUseSessionCheckout(scopeValue);
    } finally {
      checkoutBusy = false;
    }
  }

  function setInspectionRoot(value: string): void {
    const target = canonicalPath(value);
    if (!target || target === canonicalPath(sessionRoot)) {
      if (scopeRoot === '' && !inspectionRoot) return;
      scopeRoot = '';
      onInspectionRootChange?.(null);
      return;
    }
    if (!scopeOptions.some((option) => canonicalPath(option.path) === target)) return;
    if (canonicalPath(scopeRoot) === target) return;
    scopeRoot = target;
    onInspectionRootChange?.(target);
  }

  /** Which file sections the reader has opened. Starting collapsed keeps a
   * large working copy from mounting every changed-file row in one pass. */
  let openSections = $state<Record<string, boolean>>(Object.fromEntries(
    (initialWorkspaceState?.openSectionIds ?? []).map((id) => [id, true])
  ));

  /** The list DOM is rebuilt on return, but the reader stays at the same place. */
  let scrollViewport = $state<HTMLElement | null>(null);
  let savedScrollTop = initialWorkspaceState?.scrollTop ?? 0;

  function publishWorkspaceState(): void {
    onWorkspaceStateChange?.(workspaceOwnedId, {
      openSectionIds: Object.entries(openSections)
        .filter(([, open]) => open)
        .map(([id]) => id),
      scrollTop: savedScrollTop
    });
  }

  $effect(() => {
    const viewport = scrollViewport;
    if (!viewport) return;
    viewport.scrollTop = savedScrollTop;
    const rememberScroll = () => {
      savedScrollTop = viewport.scrollTop;
      publishWorkspaceState();
    };
    viewport.addEventListener('scroll', rememberScroll, { passive: true });
    return () => {
      savedScrollTop = viewport.scrollTop;
      publishWorkspaceState();
      viewport.removeEventListener('scroll', rememberScroll);
    };
  });

  const stageablePaths = $derived(
    (panel.status?.files ?? [])
      .filter((file) => file.worktreeStatus !== '' || file.badge === '?')
      .map((file) => file.relativePath)
  );
  const canStageAll = $derived(canChange && !busy && stageablePaths.length > 0);
  const canCommit = $derived(canChange && !busy && staged && panel.commitMessage.trim() !== '');
  // An empty box is a valid amend: git keeps the message the commit already has.
  const canAmend = $derived(canChange && !busy);
  const canDiscardAll = $derived(canChange && !busy && files.length > 0);
  /** Why each more-actions row is off, or null while it can run. The menu disables a row exactly when it has a reason. */
  const stillRunning = 'Wait for the current source-control action to finish.';
  const refreshReason = $derived(!scopeAvailable ? cannotChangeReason
    : panel.statusLoading || panel.historyLoading ? 'Source control is already refreshing.' : null);
  const discardAllReason = $derived(canDiscardAll ? null : !canChange ? cannotChangeReason
    : busy ? stillRunning : 'There are no changes to discard.');
  const addRemoteReason = $derived(canAddRemote ? null : canChange ? stillRunning : cannotChangeReason);

  const stageHint = $derived.by(() => {
    if (!canChange) return cannotChangeReason;
    if (busy) return 'Wait for the current source-control action to finish.';
    if (stageablePaths.length === 0) {
      return diffstat.filesChanged > files.length
        ? 'Only the files listed can be staged here; commit, stash or ignore files to see the rest.'
        : 'Everything is already staged.';
    }
    return `Stage ${stageablePaths.length === 1 ? 'the one changed file' : `all ${stageablePaths.length} changed files`}`;
  });

  const commitHint = $derived.by(() => {
    if (!canChange) return cannotChangeReason;
    if (busy) return 'Wait for the current source-control action to finish.';
    if (panel.amend) {
      if (panel.commitMessage.trim() === '') return 'Rewrite the last commit and keep its message. Do not amend a commit that is already pushed.';
      return 'Rewrite the last commit. Do not amend a commit that is already pushed and shared.';
    }
    if (!staged) return 'Stage something first — a commit takes what is staged.';
    if (panel.commitMessage.trim() === '') return 'Type a commit message first.';
    return 'Commit the staged files';
  });

  function stageAll(): void {
    if (!canStageAll) return;
    void service.stagePaths(stageablePaths);
  }

  function commit(): void {
    if (panel.amend) {
      if (!canAmend) return;
      void service.amendCommit();
      return;
    }
    if (!canCommit) return;
    void service.commit();
  }

  /** Cmd/Ctrl+Enter commits, which is what the box's placeholder promises. */
  function commitOnShortcut(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || !(event.metaKey || event.ctrlKey)) return;
    event.preventDefault();
    commit();
  }

  function toggleSection(id: string): void {
    openSections = { ...openSections, [id]: !openSections[id] };
    publishWorkspaceState();
  }

  function stageOne(file: ProjectGitFileStatus): void {
    if (!canChange || busy) return;
    void service.stagePaths([file.relativePath]);
  }

  function unstageOne(file: ProjectGitFileStatus): void {
    if (!canChange || busy) return;
    void service.unstagePaths([file.relativePath]);
  }

  /** The changes being asked about, or null while nothing is. */
  let pendingDiscard = $state<{ question: DiscardQuestion; run: () => void } | null>(null);

  function askToDiscard(file: ProjectGitFileStatus): void {
    if (!canChange || busy) return;
    const target: DiscardTarget = {
      relativePath: file.relativePath,
      untracked: isGitFileUntracked(file)
    };
    pendingDiscard = {
      question: describeDiscardQuestion({ scope: 'file', targets: [target] }),
      run: () => void service.discardPaths([target.relativePath])
    };
  }

  function askToDiscardAll(): void {
    if (!canDiscardAll) return;
    const untrackedCount = discardAllUntrackedCount(panel.status);
    pendingDiscard = {
      question: describeDiscardQuestion({ scope: 'all', targets: [], untrackedCount }),
      run: () => void service.discardAll(untrackedCount > 0)
    };
  }

  /** The one route to a discard in this panel, and only from the dialog. */
  function confirmDiscard(): void {
    const pending = pendingDiscard;
    pendingDiscard = null;
    if (!pending || !canChange) return;
    pending.run();
  }
</script>

<div
  class="flex h-full min-h-0 w-full flex-col text-foreground"
  aria-label="Source control"
>
  <PanelHeader title="Source control" count={diffstat.filesChanged} data-testid="source-control-header">
    {#snippet actions()}
      <!-- Refresh, discard all, fetch, pull, push, publish and add remote. A row that cannot run
           shows why on a second, muted line: a disabled row never shows a tooltip. The row keeps
           full opacity and dims only its label, so the reason stays readable. -->
      {#snippet menuAction(testId: string, label: string, reason: string | null, onSelect: () => void)}
        <DropdownMenu.Item data-testid={testId} disabled={reason !== null} class="data-disabled:opacity-100" {onSelect}>
          <span class="flex min-w-0 flex-col">
            <span class="group-data-disabled/dropdown-menu-item:opacity-50">{label}</span>
            {#if reason !== null}<span class="text-sm text-muted-foreground">{reason}</span>{/if}
          </span>
        </DropdownMenu.Item>
      {/snippet}
      <DropdownMenu.Root onOpenChange={moreMenuOpenChange}>
        <DropdownMenu.Trigger
          class={cn(buttonVariants({ variant: 'ghost', size: 'icon-sm' }), 'rounded-full')}
          aria-label="More source-control actions"
          data-testid="source-control-more-actions"
        >
          <Ellipsis aria-hidden="true" />
        </DropdownMenu.Trigger>
        <!-- Wide enough for a reason to wrap onto two or three lines instead of one word per line. -->
        <DropdownMenu.Content align="end" class="w-auto max-w-72">
          {@render menuAction('source-control-refresh', 'Refresh', refreshReason, () => void service.refresh())}
          {@render menuAction('source-control-discard-all', 'Discard all…', discardAllReason, askToDiscardAll)}
          <DropdownMenu.Separator />
          {#each remoteActions as item (item.id)}
            {@render menuAction(`source-control-remote-${item.id}`, item.label, item.disabledReason, () => runRemote(item.id))}
          {/each}
          <!-- Only while the branch has no upstream. One remote publishes straight
               to it; several open a submenu so the remote is always chosen. -->
          {#if publishTarget}
            {@const remotes = publishTarget.remotes ?? []}
            {#if publishReason === null && remotes.length > 1}
              <DropdownMenu.Sub>
                <DropdownMenu.SubTrigger data-testid="source-control-publish">Publish to</DropdownMenu.SubTrigger>
                <DropdownMenu.SubContent side="left">
                  {#each remotes as remote (remote)}
                    <DropdownMenu.Item onSelect={() => publish(remote)}>{remote}</DropdownMenu.Item>
                  {/each}
                </DropdownMenu.SubContent>
              </DropdownMenu.Sub>
            {:else}
              {@const label = publishReason === null ? `Publish to ${remotes[0]}` : 'Publish branch'}
              {@render menuAction('source-control-publish', label, publishReason, () => publish(remotes[0]))}
            {/if}
          {/if}
          <DropdownMenu.Separator />
          {@render menuAction('source-control-add-remote', 'Add remote…', addRemoteReason, openAddRemote)}
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    {/snippet}

    <span class="min-w-0" title={describeGitBranchTitle(panel.status)}>
      <BranchMenu {panel} {service} canWrite={canChange} readOnlyReason={cannotChangeReason} {onUseSessionCheckout} />
    </span>
  </PanelHeader>

  {#if !panel.activated && rootAvailable}
    <EmptyState
      title="No project selected yet"
      body="Pick a session and this panel shows that folder's source control."
    >
      {#snippet icon()}<GitBranch />{/snippet}
    </EmptyState>
  {:else if panel.desktopOnly}
    <EmptyState
      title="Source control runs in the desktop app"
      body="Nothing is loaded here. Open this project in the desktop app to see its changes."
    >
      {#snippet icon()}<GitBranch />{/snippet}
    </EmptyState>
  {:else if notARepository}
    {#snippet initRepositoryAction()}
      <div class="flex flex-col items-center gap-2">
        <Button size="sm" disabled={busy} onclick={initRepository}>
          {panel.actionBusy === 'init' ? 'Initializing…' : 'Initialize repository'}
        </Button>
        {#if panel.actionError !== ''}
          <p class="max-w-[42ch] text-sm text-[var(--color-bad)]">{panel.actionError}</p>
        {/if}
      </div>
    {/snippet}
    <EmptyState
      title="This folder is not a git work tree"
      body="It is a plain folder or a bare repository, so there are no changes to show."
      actions={canInitRepository ? initRepositoryAction : undefined}
    >
      {#snippet icon()}<GitBranch />{/snippet}
    </EmptyState>
  {:else}
    <!-- This body only exists in the document while its tab is showing. WebKit
         re-checks sibling styling after every inserted node, so mounting a long
         hidden file list grows quadratically and can freeze the whole app. The
         selection and open-section state live above this gate and survive. -->
    {#if visible}
    <ScrollArea bind:viewportRef={scrollViewport} class="min-h-0 flex-1">
      <div class="flex flex-col gap-2 p-2">
        {#if !rootAvailable}
          <p class="text-sm text-muted-foreground" data-testid="source-control-checkout-deleted">
            Checkout/Worktree deleted.
          </p>
        {/if}

        {#if scopeOptions.length > 1 || (!rootAvailable && scopeOptions.length > 0)}
          <Select.Root type="single" value={scopeValue} onValueChange={setInspectionRoot}>
            <Select.Trigger
              size="sm"
              class="w-full min-w-0"
              aria-label="Folder this panel reads"
              data-testid="source-control-scope"
            >
              <span class="min-w-0 truncate">{scopeLabel}</span>
            </Select.Trigger>
            <Select.Content>
              {#each scopeOptions as option (option.path)}
                <Select.Item value={option.path} label={option.label} />
              {/each}
            </Select.Content>
          </Select.Root>
        {/if}

        {#if readOnlyScope}
          <p class="text-sm text-muted-foreground" data-testid="source-control-scope-note">
            {describeSourceControlScope(scopeRoot)}
          </p>
          <Button
            variant="outline"
            size="sm"
            class="w-full"
            disabled={!canUseSessionCheckout}
            title={checkoutHint}
            aria-label="Use as session checkout"
            data-testid="source-control-use-session-checkout"
            onclick={useSessionCheckout}
          >{checkoutBusy ? 'Changing checkout…' : 'Use as session checkout'}</Button>
          <p class="text-sm text-muted-foreground" data-testid="source-control-checkout-hint">
            {checkoutHint}
          </p>
        {:else}
          <label class="flex flex-col gap-1">
            <span class="sr-only">Commit message</span>
            <textarea
              bind:value={panel.commitMessage}
              rows="3"
              data-testid="source-control-commit-message"
              placeholder="Message. Press Command-Enter to commit."
              disabled={!canChange}
              onkeydown={commitOnShortcut}
              class="dark:bg-input/30 border-input focus-visible:border-(color:--focus-border)
                     w-full min-w-0 resize-y rounded-lg border bg-transparent px-2.5 py-1.5 text-[13px]
                     leading-snug outline-none transition-colors placeholder:text-muted-foreground
                     disabled:pointer-events-none disabled:opacity-50"
            ></textarea>
          </label>

          <div class="flex items-center gap-2">
            <label
              class="flex shrink-0 items-center gap-1.5 text-sm text-muted-foreground"
              title="Rewrite the last commit instead of adding one. Never do this to a commit that is already pushed and shared."
            >
              <Switch
                size="sm"
                disabled={!canChange}
                checked={panel.amend} onCheckedChange={(on) => service.setAmend(on)}
                data-testid="source-control-amend-toggle"
              />
              Amend
            </label>
            <Button
              variant="secondary"
              size="sm"
              class="flex-1 rounded-full"
              disabled={!canStageAll}
              title={stageHint}
              data-testid="source-control-stage-all"
              onclick={stageAll}>Stage All</Button
            >
            <!-- A commit that cannot run yet is a plain grey button, not a
                 dimmed green one. -->
            <Button
              variant={(panel.amend ? canAmend : canCommit) ? 'default' : 'secondary'}
              size="sm"
              class="flex-1 rounded-full"
              disabled={panel.amend ? !canAmend : !canCommit}
              title={commitHint}
              data-testid="source-control-commit"
              onclick={commit}>{panel.amend ? 'Amend' : 'Commit'}</Button
            >
          </div>
        {/if}

        {#if panel.actionError !== ''}
          <p class="text-sm text-[var(--color-bad)]">{panel.actionError}</p>
        {:else if panel.actionStatus !== ''}
          <p class="text-sm text-muted-foreground">{panel.actionStatus}</p>
        {/if}

        {#if panel.statusError !== ''}
          <p class="text-sm text-[var(--color-bad)]">{panel.statusError}</p>
        {/if}

        {#if diffstat.filesChanged > files.length}
          <p class="text-sm text-muted-foreground" data-testid="source-control-capped">
            Showing {files.length.toLocaleString()} of {diffstat.filesChanged.toLocaleString()} changed files.
            Commit, stash or ignore files to see the rest.
          </p>
        {/if}

        {#each sections as section (section.id)}
          <section class="flex min-w-0 flex-col">
            <button
              type="button"
              class="flex min-h-6 items-center gap-1 rounded-md px-2 text-left text-sm
                     font-medium tracking-wide text-muted-foreground uppercase outline-none
                     transition-colors hover:text-foreground focus-visible:ring-3
                     focus-visible:ring-ring/50"
              aria-expanded={Boolean(openSections[section.id])}
              data-testid={`source-control-section-${section.id}`}
              onclick={() => toggleSection(section.id)}
            >
              <ChevronRight
                class={`chevron size-3.5 ${openSections[section.id] ? 'is-open' : ''}`}
                aria-hidden="true"
              />
              <span>{section.label}</span>
              <Chip tone="count">{section.files.length}</Chip>
            </button>

            {#if openSections[section.id]}
              {#if section.files.length === 0}
                <p class="px-2 py-1 text-sm text-muted-foreground">
                  {section.id === 'staged'
                    ? 'Nothing is staged yet.'
                    : section.id === 'untracked'
                      ? 'No new files.'
                      : 'No changes to files git already knows about.'}
                </p>
              {:else}
                {#each section.files as file (file.relativePath)}
                  <ChangedFileRow
                    {file}
                    root={scopeAvailable ? folder : ''}
                    selected={panel.selectedPaths.compact === file.relativePath}
                    canWrite={canChange}
                    canOpenInEditor={scopeAvailable && !readOnlyScope && !readOnlyReason}
                    readOnlyReason={cannotChangeReason}
                    {busy}
                    onStage={stageOne}
                    onUnstage={unstageOne}
                    onRequestDiscard={askToDiscard}
                  />
                {/each}
              {/if}
            {/if}
          </section>
        {/each}

        <CommitTimeline {panel} {service} {commitFiles} {commitFilesState} readOnly={Boolean(readOnlyReason)} />
      </div>
    </ScrollArea>
    {/if}
  {/if}
</div>

<DiscardConfirmDialog
  question={pendingDiscard?.question ?? null}
  open={pendingDiscard !== null}
  onOpenChange={(next) => {
    if (!next) pendingDiscard = null;
  }}
  onConfirm={confirmDiscard}
/>

<AddRemoteDialog bind:open={addRemoteOpen} onAdd={addRemote} />

<style>
  /* A first import or a generated-file commit puts thousands of changed files in
     these sections. Skipping layout and paint for the rows off screen keeps that
     off the webview's main thread. 28px is the row height (ListRow's min-h-7);
     `auto` means the browser uses the real height it last measured.

     Scoped to this component's sections on purpose: the commit list below them
     is `CommitTimeline`, which carries the same rule for its own rows. */
  section :global(.row) {
    content-visibility: auto;
    contain-intrinsic-size: auto 28px;
  }

  /* The chevron turns to point down while its section is open, and stops there. */
  section :global(.chevron) {
    transition: transform 120ms ease-out;
  }
  section :global(.chevron.is-open) {
    transform: rotate(90deg);
  }
  @media (prefers-reduced-motion: reduce) {
    section :global(.chevron) {
      transition: none;
    }
  }
</style>
