<script lang="ts">
  /**
   * GitInsightsPanel.svelte — the right-rail "Git" insights dock: the working-tree
   * command drawer (stage/unstage/fetch/pull/push/commit), the changed-files list,
   * the commit-history graph + task map + selected-commit detail, and the
   * selected-file diff.
   *
   * Presentational only: it renders chrome + the git body and emits every action
   * through callbacks. It holds NO `$state` of its own. The page owns all git
   * state (`gitStore`), the loaders + Tauri git calls, the request-id guards, the
   * action handlers, and the whole git `$derived` graph; they are passed in as
   * props (the gitStore-backed values + derivations as the grouped `git` value
   * object, the pure display helpers as `format`, the mutating handlers as
   * `actions`).
   *
   * Teleport: the root `<aside>` keeps `use:panelAction={'insights'}` — Dockview
   * teleports it to the right-rail group regardless of where it is authored. The
   * action stays in the page (it wires the nested Dockview workspace) and is
   * supplied as a prop, mirroring `EditorPanel`'s `panelAction={'editor'}`. The
   * `{#if sourceIntelligencePanelMounted()}` mount gate and the
   * `source-dockview-insights-parking` parking shell stay in the page and wrap
   * this component.
   *
   * `files.*` is deliberately NOT imported here (it is a cross-domain store): the
   * stage/unstage closures over the selected record are emitted as zero-arg
   * `onStageSelected`/`onUnstageSelected` callbacks (the page closes over
   * `files.selectedRecord`), and the changed-file row's selected-state compares
   * against the `selectedRelativePath` prop.
   */
  import type { Action } from 'svelte/action';
  import {
    Check,
    ChevronDown,
    Copy,
    ExternalLink,
    FileCode2,
    FolderGit2,
    History,
    Plus,
    RefreshCw,
    RotateCcw
  } from '@lucide/svelte';
  import type { SourceDockPanelID } from '$lib/sourceDockLayout';
  import type { GitTaskSourceGroup, GitBranchHealthSummary } from '$lib/sourceData';
  import type { GitCommitHistoryEntry, ProjectGitFileStatus, SourceGitDiff } from '$lib/tauriSource';
  import type { GitGraphViewModel, GitGraphCommitRow } from '$lib/shell/git/gitGraphViewModel';

  /** Mirrors the page-local `GitStatusFileGroup` (assigns structurally). */
  type GitStatusGroupID = 'staged' | 'unstaged' | 'untracked';
  type GitStatusFileGroup = {
    id: GitStatusGroupID;
    label: string;
    files: ProjectGitFileStatus[];
    action: 'stage' | 'unstage';
  };

  /**
   * The gitStore-backed values + page `$derived` git data this panel reads,
   * grouped into one object prop (mirrors EditorPanel's grouped `monaco` prop)
   * so the panel surface stays manageable. All computed in the page over
   * `gitStore.*` + cross-domain deps; this panel is a pure function of them.
   */
  interface GitPanelData {
    // ── command drawer (working-tree actions) ──
    actionBusy: 'stage' | 'unstage' | 'commit' | 'fetch' | 'pull' | 'push' | '';
    actionStatus: string;
    actionError: string;
    hasStagedChanges: boolean;
    selectedSourceDirty: boolean;
    pathActionDisabled: boolean;
    unstageDisabled: boolean;
    remoteActionDisabled: boolean;
    commitDisabled: boolean;

    // ── changed-files list ──
    statusLoading: boolean;
    statusError: string;
    changedFiles: ProjectGitFileStatus[];
    fileGroups: GitStatusFileGroup[];
    fileGroupSummary: string;

    // ── history / tasks / commit-detail ──
    graph: GitGraphViewModel;
    commitHistorySummary: string;
    graphSummary: string;
    taskSearchSummary: string;
    branchHealth: GitBranchHealthSummary;
    taskIDs: string[];
    taskSourceGroups: GitTaskSourceGroup[];
    selectedCommit: GitCommitHistoryEntry | null;
    selectedCommitRow: GitGraphCommitRow | null;
    selectedCommitSha: string;
    commitHistoryLoading: boolean;
    commitHistoryError: string;

    // ── selected-file diff ──
    sourceSummary: string;
    selectedDiff: SourceGitDiff | null;
    selectedDiffLoading: boolean;
    selectedDiffError: string;
  }

  /** Pure display helpers called from markup (page-owned, no side effects). */
  interface GitPanelFormatters {
    badge: () => string;
    statusGroupActionLabel: (group: GitStatusFileGroup) => string;
    statusFileTitle: (fileStatus: ProjectGitFileStatus) => string;
    statusFileSummary: (fileStatus: ProjectGitFileStatus) => string;
    taskUrl: (taskID: string | null) => string | null;
    commitTaskSourceLabel: (row: GitGraphCommitRow) => string;
    commitEntryForRow: (row: GitGraphCommitRow) => GitCommitHistoryEntry | null;
  }

  /** Mutating git action callbacks (page-owned; touch Tauri / state / clipboard). */
  interface GitPanelActions {
    onStageSelected: () => void;
    onUnstageSelected: () => void;
    onRemoteAction: (action: 'fetch' | 'pull' | 'push') => void;
    onCommit: () => void;
    onStatusGroupAction: (group: GitStatusFileGroup) => void;
    onSelectStatusFile: (fileStatus: ProjectGitFileStatus) => void;
    onSelectCommit: (entry: GitCommitHistoryEntry) => void;
    onCommitRowKeydown: (event: KeyboardEvent, entry: GitCommitHistoryEntry) => void;
    onCopyTaskSourceGroup: (group: GitTaskSourceGroup) => void;
    onCopySelectedCommitDetail: () => void;
    onCopyCommitHandoff: (entry: GitCommitHistoryEntry) => void;
    onCopyCommitSha: (entry: GitCommitHistoryEntry) => void;
    onCopyCommitSummary: (entry: GitCommitHistoryEntry) => void;
    onOpenTaskReference: (taskID: string | null) => void;
    onCopyTaskReference: (taskID: string | null) => void;
  }

  interface Props {
    /** Dockview teleport action for the `'insights'` panel — the root node. */
    panelAction: Action<HTMLElement, SourceDockPanelID>;
    /** Relative path of the selected source file (for the changed-row selected state). */
    selectedRelativePath: string | null | undefined;
    /** Draft commit message (page-owned `gitStore.commitMessage`). */
    commitMessage: string;
    /** Emitted on commit-message input (page writes `gitStore.commitMessage`). */
    onCommitMessageChange: (value: string) => void;
    /** gitStore-backed values + page git derivations. */
    git: GitPanelData;
    /** Pure display helpers. */
    format: GitPanelFormatters;
    /** Mutating git action callbacks. */
    actions: GitPanelActions;
  }

  let {
    panelAction,
    selectedRelativePath,
    commitMessage,
    onCommitMessageChange,
    git,
    format,
    actions
  }: Props = $props();
</script>

<aside
  class="source-intelligence-panel"
  aria-label="Language intelligence"
  use:panelAction={'insights'}
>
  <div class="intelligence-tabs" aria-label="Source insights">
    <span class="intelligence-tab-static">
      <FolderGit2 size={13} strokeWidth={1.9} />
      <span>Git</span>
      <strong>{format.badge()}</strong>
    </span>
  </div>

  <div class="git-diff-panel" aria-label="Selected file Git diff">
    <details class="git-command-drawer">
      <summary>
        <span>Commands</span>
        <small>{git.actionError || git.actionStatus || (git.hasStagedChanges ? 'staged changes ready' : 'stage, fetch, pull, push')}</small>
      </summary>
      <div class="git-controls" aria-label="Git working tree controls">
        <div class="git-action-row">
          <button
            class="git-action-button"
            type="button"
            aria-label="Stage selected source file"
            title={git.selectedSourceDirty ? 'Save the source file before staging it' : 'Stage selected source file'}
            disabled={git.pathActionDisabled}
            onclick={actions.onStageSelected}
          >
            <Plus size={12} strokeWidth={2} />
            <span>{git.actionBusy === 'stage' ? 'Staging' : 'Stage'}</span>
          </button>
          <button
            class="git-action-button"
            type="button"
            aria-label="Unstage selected source file"
            title="Unstage selected source file"
            disabled={git.unstageDisabled}
            onclick={actions.onUnstageSelected}
          >
            <RotateCcw size={12} strokeWidth={2} />
            <span>{git.actionBusy === 'unstage' ? 'Unstaging' : 'Unstage'}</span>
          </button>
        </div>
        <div class="git-remote-row">
          <button
            class="git-action-button"
            type="button"
            aria-label="Fetch selected repository"
            title="Fetch selected repository"
            disabled={git.remoteActionDisabled}
            onclick={() => actions.onRemoteAction('fetch')}
          >
            <RefreshCw size={12} strokeWidth={2} />
            <span>{git.actionBusy === 'fetch' ? 'Fetching' : 'Fetch'}</span>
          </button>
          <button
            class="git-action-button"
            type="button"
            aria-label="Pull selected repository"
            title="Pull selected repository with fast-forward only"
            disabled={git.remoteActionDisabled}
            onclick={() => actions.onRemoteAction('pull')}
          >
            <ChevronDown size={12} strokeWidth={2} />
            <span>{git.actionBusy === 'pull' ? 'Pulling' : 'Pull'}</span>
          </button>
          <button
            class="git-action-button"
            type="button"
            aria-label="Push selected repository"
            title="Push selected repository"
            disabled={git.remoteActionDisabled}
            onclick={() => actions.onRemoteAction('push')}
          >
            <ExternalLink size={12} strokeWidth={2} />
            <span>{git.actionBusy === 'push' ? 'Pushing' : 'Push'}</span>
          </button>
        </div>
        <div class="git-commit-row">
          <textarea
            class="git-commit-input"
            value={commitMessage}
            oninput={(event) => onCommitMessageChange(event.currentTarget.value)}
            aria-label="Git commit message"
            placeholder="Commit message"
            rows="2"
          ></textarea>
          <button
            class="git-action-button commit"
            type="button"
            aria-label="Commit staged Git changes"
            title="Commit staged Git changes"
            disabled={git.commitDisabled}
            onclick={actions.onCommit}
          >
            <Check size={12} strokeWidth={2} />
            <span>{git.actionBusy === 'commit' ? 'Committing' : 'Commit'}</span>
          </button>
        </div>
        {#if git.actionError || git.actionStatus}
          <div class:error={Boolean(git.actionError)} class="git-action-message">
            {git.actionError || git.actionStatus}
          </div>
        {/if}
      </div>
    </details>
    <div class="git-status-list" aria-label="Changed Git files">
      {#if git.statusLoading}
        <div class="intelligence-empty">Loading changed files</div>
      {:else if git.statusError}
        <div class="intelligence-empty">{git.statusError}</div>
      {:else if git.changedFiles.length === 0}
        <div class="intelligence-empty">No changed files</div>
      {:else}
        <div class="git-insights-section-heading">
          <span class="git-insights-section-title">Changes</span>
          <span class="git-insights-section-count">{git.changedFiles.length}</span>
        </div>
        <div class="git-status-overview">{git.fileGroupSummary}</div>
        {#each git.fileGroups as group (group.id)}
          <section class="git-status-group" data-group={group.id} aria-label={`${group.label} Git files`}>
            <div class="git-status-group-heading">
              <strong>{group.label}</strong>
              <span>{group.files.length}</span>
              <button
                type="button"
                disabled={git.actionBusy !== ''}
                onclick={() => actions.onStatusGroupAction(group)}
              >
                {format.statusGroupActionLabel(group)}
              </button>
            </div>
            {#each group.files as fileStatus (`${group.id}:${fileStatus.relativePath}`)}
              <button
                class="git-status-row"
                class:selected={selectedRelativePath === fileStatus.relativePath}
                type="button"
                title={format.statusFileTitle(fileStatus)}
                onclick={() => actions.onSelectStatusFile(fileStatus)}
              >
                <strong>{fileStatus.badge}</strong>
                <span>{fileStatus.relativePath}</span>
                <small>{format.statusFileSummary(fileStatus)}</small>
              </button>
            {/each}
          </section>
        {/each}
      {/if}
    </div>
    <div class="git-history-panel" aria-label="Git commit history">
      <div class="git-history-heading git-insights-section-heading">
        <span class="git-insights-section-title">History</span>
        <span class="git-insights-section-count">{git.graph.commits.length}</span>
        <small>{git.commitHistorySummary}</small>
      </div>
      <div class="git-graph-summary-strip" aria-label="Git graph view model summary" title={git.taskSearchSummary}>
        <span>{git.graphSummary}</span>
        {#if git.graph.summary.mergeCommitCount > 0}
          <small>{git.graph.summary.mergeCommitCount} merges</small>
        {/if}
      </div>
      <div
        class="git-branch-health-strip"
        aria-label="Git branch health"
        title={git.branchHealth.detail}
      >
        {#each git.branchHealth.chips as chip (`${chip.label}:${chip.value}`)}
          <span class={`git-branch-health-chip ${chip.tone}`}>
            <strong>{chip.label}</strong>
            <span>{chip.value}</span>
          </span>
        {/each}
      </div>
      {#if git.taskIDs.length > 0 || git.taskSourceGroups.length > 0}
        <section class="git-insights-section" aria-label="Git tasks">
          <div class="git-insights-section-heading">
            <span class="git-insights-section-title">Tasks</span>
            <span class="git-insights-section-count">{git.taskSourceGroups.length || git.taskIDs.length}</span>
          </div>
          {#if git.taskSourceGroups.length === 0 && git.taskIDs.length > 0}
            <div class="git-task-trail" aria-label="Git task links">
              {#each git.taskIDs as taskID (taskID)}
                {#if format.taskUrl(taskID)}
                  <a class="git-task-link" href={format.taskUrl(taskID) ?? ''} target="_blank" rel="noreferrer">
                    {taskID}
                  </a>
                {:else}
                  <span class="git-task-link">{taskID}</span>
                {/if}
              {/each}
            </div>
          {/if}
          {#if git.taskSourceGroups.length > 0}
            <div class="git-task-source-map" aria-label="Git task source map">
              {#each git.taskSourceGroups as group (group.taskID)}
                <div class="git-task-source-row" title={group.detailSummary}>
                  {#if format.taskUrl(group.taskID)}
                    <a class="git-task-link" href={format.taskUrl(group.taskID) ?? ''} target="_blank" rel="noreferrer">
                      {group.taskID}
                    </a>
                  {:else}
                    <span class="git-task-link">{group.taskID}</span>
                  {/if}
                  <small>{group.sourceSummary}</small>
                  <button
                    type="button"
                    aria-label={`Copy task sources for ${group.taskID}`}
                    title={group.detailSummary}
                    onclick={() => actions.onCopyTaskSourceGroup(group)}
                  >
                    <Copy size={11} strokeWidth={2} />
                  </button>
                </div>
              {/each}
            </div>
          {/if}
        </section>
      {/if}
      {#if git.selectedCommit && git.selectedCommitRow}
        <details
          class="git-commit-detail-drawer"
          aria-label="Selected commit detail"
          title={git.selectedCommitRow.detailLabel}
        >
          <summary class="git-commit-detail-summary">
            <span class="git-commit-detail-summary-main">
              <strong>{git.selectedCommitRow.subject}</strong>
              <small>{git.selectedCommitRow.metaLabel}</small>
            </span>
            <span class="git-commit-detail-summary-ref">{git.selectedCommitRow.refs.label}</span>
          </summary>
          <div class="git-commit-detail-body">
            <div class="git-commit-detail-facts" aria-label="Selected commit metadata">
              <span>
                <strong>Commit</strong>
                <small>{git.selectedCommitRow.sha}</small>
              </span>
              <span>
                <strong>Refs</strong>
                <small>{git.selectedCommitRow.refs.label}</small>
              </span>
              <span>
                <strong>Parents</strong>
                <small>{git.selectedCommitRow.parentHint.label}</small>
              </span>
              {#if git.selectedCommitRow.taskID}
                <span>
                  <strong>Task</strong>
                  <small>{git.selectedCommitRow.taskID}</small>
                </span>
              {/if}
            </div>
            <div class="git-commit-detail-actions" aria-label="Selected commit actions">
              <button
                type="button"
                aria-label="Copy selected commit detail"
                title="Copy selected commit detail"
                onclick={actions.onCopySelectedCommitDetail}
              >
                <Copy size={11} strokeWidth={2} />
              </button>
              <button
                type="button"
                aria-label="Copy selected commit handoff"
                title="Copy selected commit handoff"
                onclick={() => git.selectedCommit && actions.onCopyCommitHandoff(git.selectedCommit)}
              >
                <FileCode2 size={11} strokeWidth={2} />
              </button>
              <button
                type="button"
                aria-label="Copy selected commit SHA"
                title="Copy selected commit SHA"
                onclick={() => git.selectedCommit && actions.onCopyCommitSha(git.selectedCommit)}
              >
                <History size={11} strokeWidth={2} />
              </button>
              {#if git.selectedCommitRow.taskID}
                {#if format.taskUrl(git.selectedCommitRow.taskID)}
                  <button
                    type="button"
                    aria-label="Open selected commit task reference"
                    title="Open selected commit task reference"
                    onclick={() => git.selectedCommitRow && actions.onOpenTaskReference(git.selectedCommitRow.taskID)}
                  >
                    <ExternalLink size={11} strokeWidth={2} />
                  </button>
                {/if}
                <button
                  type="button"
                  aria-label="Copy selected commit task reference"
                  title="Copy selected commit task reference"
                  onclick={() => git.selectedCommitRow && actions.onCopyTaskReference(git.selectedCommitRow.taskID)}
                >
                  <Copy size={11} strokeWidth={2} />
                </button>
              {/if}
            </div>
          </div>
        </details>
      {/if}
      <div class="git-history-list">
        {#if git.commitHistoryLoading}
          <div class="intelligence-empty">Loading history</div>
        {:else if git.commitHistoryError}
          <div class="intelligence-empty">{git.commitHistoryError}</div>
        {:else if git.graph.commits.length === 0}
          <div class="intelligence-empty">No commits</div>
        {:else}
          {#each git.graph.commits as row (row.sha)}
            {@const entry = format.commitEntryForRow(row)}
            <div
              class={`git-history-row ${row.graphKind}`}
              class:selected={git.selectedCommitSha === row.sha}
              role="button"
              tabindex="0"
              title={row.detailLabel}
              onclick={() => entry && actions.onSelectCommit(entry)}
              onkeydown={(event) => entry && actions.onCommitRowKeydown(event, entry)}
            >
              <span
                class={`git-graph-marker ${row.graphKind}`}
                aria-label={row.topologyLabel}
                title={row.topologyLabel}
              ></span>
              <div class="git-history-main">
                <strong>{row.subject}</strong>
                <small>{row.metaLabel}</small>
              </div>
              <div class="git-history-meta">
                <div class="git-history-badges" aria-label="Commit ownership badges">
                  {#each row.ownershipBadges as badge (`${badge.tone}:${badge.label}`)}
                    <span class={`git-history-badge ${badge.tone}`} title={badge.title}>{badge.label}</span>
                  {/each}
                </div>
                {#if row.taskID}
                  {#if format.taskUrl(row.taskID)}
                    <a
                      class="git-task-link"
                      href={format.taskUrl(row.taskID) ?? ''}
                      target="_blank"
                      rel="noreferrer"
                      title={`Task from ${format.commitTaskSourceLabel(row) || 'Git metadata'}`}
                    >
                      {row.taskID}
                    </a>
                  {:else}
                    <span
                      class="git-task-link"
                      title={`Task from ${format.commitTaskSourceLabel(row) || 'Git metadata'}`}
                    >{row.taskID}</span>
                  {/if}
                {/if}
                <div class="git-history-actions" aria-label="Commit quick actions">
                  <button
                    type="button"
                    aria-label="Copy commit SHA"
                    title="Copy commit SHA"
                    disabled={!entry}
                    onclick={() => entry && actions.onCopyCommitSha(entry)}
                  >
                    <Copy size={11} strokeWidth={2} />
                  </button>
                  <button
                    type="button"
                    aria-label="Copy commit summary"
                    title="Copy commit summary"
                    disabled={!entry}
                    onclick={() => entry && actions.onCopyCommitSummary(entry)}
                  >
                    <History size={11} strokeWidth={2} />
                  </button>
                  <button
                    type="button"
                    aria-label="Copy commit handoff"
                    title="Copy commit handoff"
                    disabled={!entry}
                    onclick={() => entry && actions.onCopyCommitHandoff(entry)}
                  >
                    <FileCode2 size={11} strokeWidth={2} />
                  </button>
                  {#if row.taskID}
                    <button
                      type="button"
                      aria-label="Copy task reference"
                      title="Copy task reference"
                      onclick={() => actions.onCopyTaskReference(row.taskID)}
                    >
                      <ExternalLink size={11} strokeWidth={2} />
                    </button>
                  {/if}
                </div>
              </div>
            </div>
          {/each}
        {/if}
      </div>
    </div>
    <div class="intelligence-summary">{git.sourceSummary}</div>
    {#if git.selectedDiffLoading}
      <div class="intelligence-empty">Loading Git diff</div>
    {:else if git.selectedDiffError}
      <div class="intelligence-empty">{git.selectedDiffError}</div>
    {:else if git.selectedDiff?.diff}
      <pre class="git-diff-block">{git.selectedDiff.diff}</pre>
    {:else}
      <div class="intelligence-empty">No diff for selected file</div>
    {/if}
  </div>
</aside>

<style>
  /*
   * Git insights panel styling — the readability/aesthetic pass (TSK-346),
   * relocated here from `+page.svelte` when this panel was extracted (Phase B).
   *
   * These are the SOLE styles for this panel. They were originally authored as
   * `.source-intelligence-panel <descendant>` rules layered over a page-side base
   * copy, but that page base was scoped to `+page.svelte` and — because this
   * component's `<aside>` is teleported into a Dockview host (its own scope, no
   * page hash) — never actually reached this panel; it was dead and has been
   * deleted from the page. So these scoped rules stand alone: the ancestor prefix
   * is dropped (the component root IS `.source-intelligence-panel`), and the
   * shell/list/row layout intentionally rides default block flow with token-based
   * spacing/borders layered on top. ActivityGitPanel.svelte styles the Activity
   * source-control view separately from its own scoped copy (different classes).
   *
   * Aesthetic: modern, minimal, spacious, borderless (VS Code), driven entirely
   * by design tokens.
   */
  /* ── Panel shell ────────────────────────────────────────────── */
  .source-intelligence-panel {
    display: flex;
    flex-direction: column;
    min-height: 0;
    overflow: hidden;
    border-left: 1px solid var(--color-border);
    background: var(--color-bg);
    color: var(--color-text);
  }

  /* ── Establishing `display` restoration ────────────────────────
   * Every structural container below carries gap / grid-template /
   * place-items, but its establishing `display` (+ base grid-template /
   * align-items) lived in a page-scoped BASE block. When this panel was
   * extracted (17a9d4a) only the `.source-intelligence-panel <desc>`
   * OVERRIDE layer moved here; the base never did, and because the
   * `<aside>` is teleported into Dockview (its own scope) the page copy
   * could not reach it. With no `display` the containers fell back to
   * block flow and their rows stacked on top of each other (the broken
   * insights render). Restored verbatim from the pre-extraction page CSS
   * (the override-layer props — the 16px/14px templates, flex-wrap — are
   * already declared on these selectors below, so this block is purely
   * additive: no property is declared twice). */
  .intelligence-tabs { display: flex; }
  .intelligence-tab-static {
    display: grid;
    grid-template-columns: 14px minmax(0, 1fr) auto;
    align-items: center;
    flex: 1 1 auto;
  }
  .git-diff-panel {
    display: flex;
    flex: 1 1 auto;
    flex-direction: column;
    min-height: 0;
    overflow: hidden;
  }
  .git-command-drawer summary {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
  }
  .git-command-drawer summary::-webkit-details-marker,
  .git-commit-detail-drawer summary::-webkit-details-marker {
    display: none;
  }
  .git-controls { display: grid; flex: 0 0 auto; }
  .git-action-row,
  .git-remote-row,
  .git-commit-row {
    display: grid;
  }
  .git-action-row { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .git-remote-row { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .git-commit-row { grid-template-columns: minmax(0, 1fr) auto; align-items: stretch; }
  .git-action-button {
    display: grid;
    grid-template-columns: 13px minmax(0, auto);
    align-items: center;
    justify-content: center;
  }
  .git-status-list {
    display: grid;
    grid-auto-rows: min-content;
    flex: 0 0 auto;
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
  }
  .git-status-group { display: grid; }
  .git-status-group-heading {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto auto;
    align-items: center;
  }
  .git-status-row { display: grid; align-items: center; }
  .git-history-panel { display: grid; flex: 0 0 auto; min-height: 0; }
  .git-graph-summary-strip { display: flex; align-items: center; }
  .git-branch-health-strip {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(92px, 1fr));
  }
  .git-branch-health-chip {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
  }
  .git-task-trail { display: flex; align-items: center; }
  .git-task-source-map { display: grid; }
  .git-task-source-row {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    align-items: center;
  }
  .git-task-source-row button { display: grid; place-items: center; }
  .git-commit-detail-drawer { display: grid; }
  .git-commit-detail-summary {
    display: grid;
    grid-template-columns: 10px minmax(0, 1fr) minmax(0, 86px);
    align-items: center;
  }
  .git-commit-detail-summary-main { display: grid; }
  .git-commit-detail-body {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: end;
  }
  .git-commit-detail-facts {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(74px, 1fr));
  }
  .git-commit-detail-facts span { display: grid; }
  .git-commit-detail-actions { display: inline-flex; justify-content: flex-end; }
  .git-history-list {
    display: grid;
    grid-auto-rows: min-content;
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
  }
  .git-history-row { display: grid; align-items: center; }
  .git-history-main { display: grid; }
  .git-history-meta { display: grid; justify-items: end; }
  .git-history-badges { display: inline-flex; justify-content: flex-end; }
  .git-history-actions { display: inline-flex; justify-content: flex-end; }
  .git-history-actions button,
  .git-commit-detail-actions button {
    display: grid;
    place-items: center;
  }
  .intelligence-empty { display: grid; place-items: center; }

  /* ── Panel header — static Git label (lists removed) ─────────── */
  .intelligence-tabs {
    gap: var(--space-1);
    padding: var(--space-2);
    border-bottom: 1px solid var(--color-border);
  }

  .intelligence-tab-static {
    height: 28px;
    gap: var(--space-1);
    padding: 0 var(--space-2);
    color: var(--color-on-accent);
    border: none;
    border-radius: var(--radius-sm);
    background: var(--color-accent);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.02em;
  }

  .intelligence-tab-static strong {
    color: var(--color-on-accent);
    font-family: inherit;
    font-variant-numeric: tabular-nums;
    font-weight: var(--weight-semibold);
    opacity: 0.78;
  }

  /* ── Summaries / empty / loading states ─────────────────────── */
  .intelligence-summary {
    padding: var(--space-2) var(--space-3);
    color: var(--color-text-3);
    border-bottom: 1px solid var(--color-border);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
  }

  .intelligence-empty {
    min-height: 96px;
    color: var(--color-text-3);
    font-size: var(--text-sm);
    font-weight: var(--weight-medium);
  }

  /* ── Shared section heading (Changes / History / Tasks) ──────── */
  .git-insights-section {
    display: grid;
    gap: var(--space-1);
    min-width: 0;
  }

  .git-insights-section-heading {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    min-width: 0;
    padding: var(--space-1) 0;
  }

  .git-insights-section-title {
    flex: 0 0 auto;
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.05em;
    text-transform: uppercase;
  }

  .git-insights-section-count {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 18px;
    height: 16px;
    padding: 0 var(--space-2);
    color: var(--color-text-3);
    background: var(--color-elevated);
    border-radius: var(--radius-pill);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
    font-variant-numeric: tabular-nums;
    line-height: 1;
  }

  /* a trailing muted note inside a section heading (e.g. history summary) */
  .git-insights-section-heading small {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
    text-align: right;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* ── Git container ──────────────────────────────────────────── */
  .git-diff-panel {
    gap: 0;
  }

  /* ── Commands drawer ────────────────────────────────────────── */
  .git-command-drawer {
    border-bottom: 1px solid var(--color-border);
  }

  .git-command-drawer summary {
    height: 34px;
    gap: var(--space-2);
    padding: 0 var(--space-3);
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }

  .git-command-drawer summary::before {
    border-left-color: var(--color-text-3);
  }

  .git-command-drawer summary small {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
    letter-spacing: 0;
    text-transform: none;
  }

  .git-controls {
    gap: var(--space-2);
    padding: 0 var(--space-3) var(--space-3);
  }

  .git-action-row,
  .git-remote-row,
  .git-commit-row {
    gap: var(--space-2);
  }

  .git-action-button {
    min-height: 28px;
    gap: var(--space-1);
    padding: 0 var(--space-2);
    color: var(--color-text-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
    transition: background 120ms ease, color 120ms ease, border-color 120ms ease;
  }

  .git-action-button:hover:not(:disabled),
  .git-action-button:focus-visible {
    color: var(--color-text);
    border-color: var(--color-border);
    outline: none;
    background: var(--color-elevated);
  }

  .git-action-button.commit {
    color: var(--color-on-accent);
    border-color: transparent;
    background: var(--color-accent);
  }

  .git-action-button.commit:hover:not(:disabled) {
    color: var(--color-on-accent);
    background: var(--color-accent);
    opacity: 0.9;
  }

  .git-commit-input {
    padding: var(--space-2);
    color: var(--color-text);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    font-size: var(--text-sm);
    line-height: 1.4;
  }

  .git-commit-input:focus {
    border-color: transparent;
    box-shadow: var(--focus-ring);
  }

  .git-action-message {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  .git-action-message.error {
    color: var(--color-bad);
  }

  /* ── Changes (status list) ──────────────────────────────────── */
  .git-status-list {
    gap: var(--space-1);
    max-height: 180px;
    padding: var(--space-3);
    border-bottom: 1px solid var(--color-border);
  }

  .git-status-overview {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  .git-status-group {
    gap: var(--space-1);
  }

  .git-status-group-heading {
    min-height: 22px;
    gap: var(--space-2);
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.04em;
  }

  .git-status-group-heading span {
    color: var(--color-text-3);
    font-variant-numeric: tabular-nums;
  }

  .git-status-group-heading button {
    height: 22px;
    padding: 0 var(--space-2);
    color: var(--color-text-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: transparent;
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
    transition: background 120ms ease, color 120ms ease, border-color 120ms ease;
  }

  .git-status-group-heading button:hover:not(:disabled),
  .git-status-group-heading button:focus-visible {
    color: var(--color-text);
    border-color: var(--color-border);
    outline: none;
    background: var(--color-elevated);
  }

  /* File rows — VS Code source-control style */
  .git-status-row {
    grid-template-columns: 16px minmax(0, 1fr) auto;
    gap: var(--space-2);
    min-height: 28px;
    padding: var(--space-1) var(--space-2);
    color: var(--color-text);
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    transition: background 120ms ease;
  }

  .git-status-row:hover,
  .git-status-row:focus-visible {
    border-color: transparent;
    outline: none;
    background: var(--color-surface);
  }

  .git-status-row.selected {
    border-color: transparent;
    background: color-mix(in srgb, var(--color-accent) 14%, transparent);
  }

  .git-status-row:focus-visible {
    box-shadow: var(--focus-ring);
  }

  /* status glyph (M/A/D/U) — tone-coded via tokens */
  .git-status-row strong {
    justify-self: center;
    color: var(--color-text-3);
    font-family: inherit;
    font-size: var(--text-xs);
    font-weight: var(--weight-bold);
  }

  .git-status-row span {
    color: var(--color-text);
    font-size: var(--text-sm);
    font-weight: var(--weight-normal);
  }

  /* dim the directory portion of the path, keep the filename emphasized:
     handled by markup elsewhere; here we keep the secondary note muted */
  .git-status-row small {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  /* Tone the status glyph by group (VS Code source-control convention):
     staged = good/live, unstaged (modified) = attention, untracked = muted. */
  .git-status-group[data-group="staged"] .git-status-row strong {
    color: var(--color-good);
  }

  .git-status-group[data-group="unstaged"] .git-status-row strong {
    color: var(--color-attention);
  }

  .git-status-group[data-group="untracked"] .git-status-row strong {
    color: var(--color-text-3);
  }

  /* ── History panel ──────────────────────────────────────────── */
  .git-history-panel {
    gap: var(--space-2);
    padding: var(--space-3);
    border-bottom: 1px solid var(--color-border);
  }

  .git-history-heading {
    color: var(--color-text);
  }

  /* compact summary strip — borderless, muted */
  .git-graph-summary-strip {
    min-height: 0;
    gap: var(--space-2);
    padding: 0;
    color: var(--color-text-3);
    border: none;
    background: transparent;
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  .git-graph-summary-strip span {
    color: var(--color-text-2);
    font-weight: var(--weight-medium);
  }

  .git-graph-summary-strip small {
    color: var(--color-text-3);
  }

  /* BRANCH/SYNC/WORKTREE/ROOT/HEAD — clean key→value chips */
  .git-branch-health-strip {
    gap: var(--space-1);
  }

  .git-branch-health-chip {
    gap: var(--space-2);
    min-height: 24px;
    padding: var(--space-1) var(--space-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
  }

  .git-branch-health-chip strong {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.04em;
  }

  .git-branch-health-chip span {
    color: var(--color-text);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
  }

  .git-branch-health-chip.clean span {
    color: var(--color-good);
  }

  .git-branch-health-chip.dirty span {
    color: var(--color-attention);
  }

  .git-branch-health-chip.warning span {
    color: var(--color-attention);
  }

  .git-branch-health-chip.error span {
    color: var(--color-bad);
  }

  .git-branch-health-chip.muted span {
    color: var(--color-text-3);
  }

  /* ── Tasks ──────────────────────────────────────────────────── */
  .git-task-trail {
    flex-wrap: wrap;
    gap: var(--space-1);
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
  }

  .git-task-source-map {
    gap: var(--space-1);
  }

  .git-task-source-row {
    gap: var(--space-2);
    min-height: 28px;
    padding: var(--space-1) var(--space-2);
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    transition: background 120ms ease;
  }

  .git-task-source-row:hover {
    background: var(--color-surface);
  }

  .git-task-source-row small {
    color: var(--color-text-2);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  .git-task-source-row button {
    width: 22px;
    height: 22px;
    color: var(--color-text-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: transparent;
    transition: background 120ms ease, color 120ms ease, border-color 120ms ease;
  }

  .git-task-source-row button:hover,
  .git-task-source-row button:focus-visible {
    color: var(--color-text);
    border-color: var(--color-border);
    outline: none;
    background: var(--color-elevated);
  }

  /* task pill — readable accent chip */
  .git-task-link {
    min-height: 20px;
    padding: 0 var(--space-2);
    color: var(--color-on-accent);
    background: var(--color-accent);
    border-radius: var(--radius-pill);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
  }

  /* ── Selected commit detail drawer ──────────────────────────── */
  .git-commit-detail-drawer {
    gap: var(--space-1);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
  }

  .git-commit-detail-drawer[open] {
    background: var(--color-elevated);
  }

  .git-commit-detail-summary {
    min-height: 30px;
    gap: var(--space-2);
    padding: var(--space-1) var(--space-2);
  }

  .git-commit-detail-summary::before {
    color: var(--color-text-3);
  }

  .git-commit-detail-summary-main strong {
    color: var(--color-text);
    font-size: var(--text-sm);
    font-weight: var(--weight-semibold);
  }

  .git-commit-detail-summary-main small,
  .git-commit-detail-summary-ref {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  .git-commit-detail-facts span {
    gap: var(--space-1);
    padding: var(--space-1) var(--space-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-bg);
  }

  .git-commit-detail-facts strong {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.04em;
  }

  .git-commit-detail-facts small {
    color: var(--color-text-2);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  /* ── Commit history rows — clean two-line rows ──────────────── */
  .git-history-list {
    gap: var(--space-1);
    max-height: 220px;
  }

  .git-history-row {
    grid-template-columns: 14px minmax(0, 1fr) minmax(42px, auto);
    gap: var(--space-2);
    min-height: 36px;
    padding: var(--space-1) var(--space-2);
    color: var(--color-text);
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    transition: background 120ms ease;
  }

  .git-history-row:hover {
    background: var(--color-surface);
  }

  .git-history-row.selected {
    border-color: transparent;
    background: color-mix(in srgb, var(--color-accent) 14%, transparent);
  }

  .git-history-row.head,
  .git-history-row.branch,
  .git-history-row.merge,
  .git-history-row.root {
    border-color: transparent;
  }

  .git-history-row:focus-visible {
    outline: none;
    box-shadow: var(--focus-ring);
  }

  .git-history-main strong {
    color: var(--color-text);
    font-size: var(--text-sm);
    font-weight: var(--weight-medium);
  }

  .git-history-main small {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  /* subtle graph lane markers */
  .git-graph-marker::before {
    background: var(--color-border);
  }

  .git-graph-marker::after {
    border-color: var(--color-text-3);
    background: var(--color-bg);
  }

  .git-graph-marker.head::before {
    background: color-mix(in srgb, var(--color-accent) 50%, transparent);
  }

  .git-graph-marker.head::after {
    border-color: var(--color-accent);
    background: var(--color-accent);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--color-accent) 16%, transparent);
  }

  .git-graph-marker.merge::after {
    border-color: var(--color-attention);
  }

  /* ownership / ref badges — small tone chips */
  .git-history-badge {
    padding: 1px var(--space-2);
    color: var(--color-text-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-pill);
    background: var(--color-surface);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
  }

  .git-history-badge.head {
    color: var(--color-live);
    border-color: var(--color-border);
    background: var(--color-live-bg);
  }

  .git-history-badge.upstream {
    color: var(--color-text-2);
    border-color: var(--color-border);
    background: var(--color-surface);
  }

  .git-history-badge.task {
    color: var(--color-text-2);
    border-color: var(--color-border);
  }

  .git-history-badge.tag,
  .git-history-badge.merge {
    color: var(--color-attention);
    border-color: var(--color-border);
    background: var(--color-attention-bg);
  }

  .git-history-actions {
    gap: var(--space-1);
    padding: var(--space-1);
    border-radius: var(--radius-sm);
    background: var(--color-elevated);
    box-shadow: var(--shadow-sm);
  }

  .git-history-actions button,
  .git-commit-detail-actions button {
    width: 22px;
    height: 22px;
    color: var(--color-text-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: transparent;
    transition: background 120ms ease, color 120ms ease, border-color 120ms ease;
  }

  .git-history-actions button:hover,
  .git-history-actions button:focus-visible,
  .git-commit-detail-actions button:hover,
  .git-commit-detail-actions button:focus-visible {
    color: var(--color-text);
    border-color: var(--color-border);
    outline: none;
    background: var(--color-elevated);
  }

  .git-ref-label {
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  /* ── Diff block ─────────────────────────────────────────────── */
  .git-diff-block {
    margin: var(--space-3);
    padding: var(--space-3);
    color: var(--color-text-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-bg);
    font-size: var(--text-sm);
    line-height: 1.5;
  }

  @media (max-width: 720px) {
    .source-intelligence-panel {
      border-left: 0;
      border-top: 1px solid rgba(255, 255, 255, 0.08);
    }
  }
</style>
