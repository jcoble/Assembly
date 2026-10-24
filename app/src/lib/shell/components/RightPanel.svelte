<script lang="ts">
  /**
   * RightPanel.svelte — the body of the right drawer.
   *
   * One panel body fills the drawer under its tab row. The browser is not
   * here: it is a tab of the top row, in the tab pane. Closing the drawer
   * hides it without unmounting: Files keeps one bounded panel instance (so
   * its scroll and expansion survive in the DOM), and every other panel
   * mounts only while it is visible.
   *
   * No backend IO and no state of its own beyond the layout. Which tab is open
   * is decided by the page (it is remembered per session) and handed in; every
   * click goes straight back out.
   */
  import type { RightTabId } from '$lib/shell/workbenchNavigation';
  import type { SessionSourceControlWorkspace } from '$lib/shell/sessionWorkspaces';
  import { untrack } from 'svelte';
  import { canonicalPath, explorer } from '$lib/shell/files/explorer/explorerStore.svelte';
  import { activate as activateExplorer } from '$lib/shell/files/explorer/explorerService';
  import { sessionWorkingIn } from '$lib/shell/projects/projects.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  import { settings } from '$lib/shell/settings/settingsStore.svelte';

  import AgentsPanel from '$lib/shell/panels/agents/AgentsPanel.svelte';
  import FilesPanel from '$lib/shell/files/components/FilesPanel.svelte';
  import HistoryPanel from '$lib/shell/history/components/HistoryPanel.svelte';
  import RunPanel from '$lib/shell/panels/run/RunPanel.svelte';
  import SessionContextPanel from '$lib/shell/panels/context/SessionContextPanel.svelte';
  import SourceControlPanel from '$lib/shell/git/components/sourceControl/SourceControlPanel.svelte';
  import WorktreesPanel from '$lib/shell/panels/worktrees/WorktreesPanel.svelte';
  import TasksPanel from '$lib/shell/panels/tasks/TasksPanel.svelte';
  import WorkspaceTerminal from '../terminal/WorkspaceTerminal.svelte';
  import OpenOtherProject from './OpenOtherProject.svelte';

  interface Props {
    /** False while the drawer is closed. */
    visible: boolean;
    /** The tab on screen. */
    activeId: RightTabId;
    /** The active session's working folder, or '' when nothing is selected. */
    root: string;
    /** False when the active session's checkout has disappeared. */
    rootAvailable?: boolean;
    /** The folder the session works in, which Source Control reads; `root` when unset. */
    sourceControlRoot?: string;
    /** The active session's ownedId, or null. */
    ownedId: string | null;
    /** Optional tree-only projection while rail selection is rebuilt in layers. */
    filesRoot?: string;
    filesOwnedId?: string | null;
    /** Bumped by the page to focus the Files filter. */
    filesFilterFocusRequest?: number;
    onRootUnavailable?(root: string): void | Promise<void>;
    expandedPathsByRoot?: Readonly<Record<string, readonly string[]>>;
    onExpandedPathsChange?(ownedId: string, root: string, paths: readonly string[]): void;
    filesInspectionRoot?: string | null;
    checkoutDiscoveryRoots?: readonly string[];
    sourceControlInspectionRoot?: string | null;
    onFilesInspectionRootChange?(root: string | null): void;
    onSourceControlInspectionRootChange?(root: string | null): void;
    sourceControlWorkspace?: SessionSourceControlWorkspace;
    onSourceControlWorkspaceChange?(ownedId: string | null, state: SessionSourceControlWorkspace): void;
    onUseSessionCheckout?(root: string): void | Promise<void>;
    /** Another project's folder Files and Git show instead of the session's, or null. */
    viewedRoot?: string | null;
    onViewedRootChange?(root: string | null): void;
    onNewSessionHere?(projectId: string, folder: string): void;
    messagesUnread: number;
    onMessagesSeen(): void;
    onShowRunMessages(runId: string, label: string): void;
    /** Opens the full Workflows screen. */
    onOpenWorkflows(): void;
  }
  let {
    visible,
    activeId,
    root,
    rootAvailable = true,
    sourceControlRoot,
    ownedId,
    filesRoot,
    filesOwnedId,
    filesFilterFocusRequest = 0,
    onRootUnavailable,
    expandedPathsByRoot,
    onExpandedPathsChange,
    filesInspectionRoot,
    checkoutDiscoveryRoots,
    sourceControlInspectionRoot,
    onFilesInspectionRootChange,
    onSourceControlInspectionRootChange,
    sourceControlWorkspace,
    onSourceControlWorkspaceChange,
    onUseSessionCheckout,
    viewedRoot = null,
    onViewedRootChange,
    onNewSessionHere,
    messagesUnread,
    onMessagesSeen,
    onShowRunMessages,
    onOpenWorkflows
  }: Props = $props();

  // While another project is viewed, Files and Git read it and nothing there can move the session.
  const viewing = $derived(root !== '' && Boolean(viewedRoot) && canonicalPath(viewedRoot ?? '') !== canonicalPath(root));
  const working = $derived(viewing ? sessionWorkingIn(viewedRoot ?? '', rail.owned) : null);
  const readOnlyReason = $derived(
    working ? `${working.title ? `"${working.title}"` : 'Another session'} is working in this folder, so it is read-only here.` : null
  );
  // Switching sessions points the shared file tree at the session's own folder; while viewing, point it back.
  $effect(() => {
    if (!viewing || !visible || activeId !== 'files') return;
    if (canonicalPath(explorer.root ?? '') === canonicalPath(filesRoot ?? root)) untrack(() => activateExplorer(viewedRoot));
  });
</script>

<div class="right-panel">
  {#if visible && root !== '' && (activeId === 'files' || activeId === 'source-control')}
    <OpenOtherProject
      sessionRoot={root}
      viewedRoot={viewing ? viewedRoot : null}
      {readOnlyReason}
      onView={(next) => onViewedRootChange?.(next)}
      {onNewSessionHere}
    />
  {/if}
  <div class="panel-bodies">
    <div
      class="panel-body"
      class:showing={visible && activeId === 'files'}
      aria-hidden={!visible || activeId !== 'files'}
    >
      <FilesPanel
        visible={visible && activeId === 'files'}
        root={viewing ? viewedRoot ?? '' : filesRoot ?? root}
        ownedId={filesOwnedId === undefined ? ownedId : filesOwnedId}
        filterFocusRequest={filesFilterFocusRequest}
        {onRootUnavailable}
        {expandedPathsByRoot}
        {onExpandedPathsChange}
        inspectionRoot={viewing ? undefined : filesInspectionRoot}
        onInspectionRootChange={viewing ? undefined : onFilesInspectionRootChange}
        checkoutDiscoveryRoots={viewing ? [] : checkoutDiscoveryRoots}
        onUseSessionCheckout={viewing ? undefined : onUseSessionCheckout}
        {readOnlyReason}
        {viewing}
      />
    </div>
    {#if visible && activeId === 'source-control'}
      <div class="panel-body showing">
        <SourceControlPanel
          visible={true}
          root={viewing ? viewedRoot ?? '' : sourceControlRoot || root}
          {ownedId}
          rootAvailable={viewing || rootAvailable}
          inspectionRoot={viewing ? undefined : sourceControlInspectionRoot}
          onInspectionRootChange={viewing ? undefined : onSourceControlInspectionRootChange}
          workspaceState={sourceControlWorkspace}
          onWorkspaceStateChange={onSourceControlWorkspaceChange}
          onUseSessionCheckout={viewing ? undefined : onUseSessionCheckout}
          {readOnlyReason}
          {viewing}
        />
      </div>
    {:else if visible && activeId === 'worktrees'}
      <div class="panel-body showing"><WorktreesPanel visible={true} {root} {ownedId} /></div>
    {:else if visible && activeId === 'terminal'}
      <div class="panel-body showing">
        <!-- Ghostty takes its colours only when a terminal is made: a theme change remakes it. -->
        {#key settings.appearance.themeId}{#key ownedId}
          {#key root}<WorkspaceTerminal {root} {ownedId} />{/key}
        {/key}{/key}
      </div>
    {:else if visible && activeId === 'run'}
      <div class="panel-body showing"><RunPanel visible={true} {root} {ownedId} /></div>
    {:else if visible && activeId === 'context'}
      <div class="panel-body showing"><SessionContextPanel visible={true} {root} {ownedId} /></div>
    {:else if visible && activeId === 'agents'}
      <div class="panel-body showing"><AgentsPanel visible={true} {root} {ownedId} {messagesUnread} {onMessagesSeen} {onShowRunMessages} {onOpenWorkflows} /></div>
    {:else if visible && activeId === 'history'}
      <div class="panel-body showing">
        <HistoryPanel visible={true} {root} {ownedId} />
      </div>
    {:else if visible && activeId === 'tasks'}
      <div class="panel-body showing"><TasksPanel {root} {ownedId} /></div>
    {/if}
  </div>
</div>

<style>
  .right-panel {
    display: grid;
    height: 100%;
    width: 100%;
    min-width: 0;
    min-height: 0;
    grid-template-rows: auto minmax(0, 1fr);
    overflow: hidden;
    /* The card behind this paints the surface and its gradient. */
    background: transparent;
  }

  /* Every panel stacks in the same space; only the open one is displayed.
     `contain: paint` is the belt to `overflow`'s braces: it makes this box a
     containing block for every positioned descendant and clips their painting
     to it, so a panel's own floating strip cannot be drawn over the middle of
     the shell. Same reasoning as the column this replaced. */
  .panel-bodies {
    grid-row: 2;
    position: relative;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    contain: paint;
  }

  .panel-body {
    position: absolute;
    inset: 0;
    display: none;
    overflow: hidden;
  }

  .panel-body.showing {
    display: block;
  }
</style>
