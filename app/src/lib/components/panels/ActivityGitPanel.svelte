<script lang="ts">
  /**
   * ActivityGitPanel.svelte — the "git" activity mode: the left-rail Source
   * Control panel (changes / stage-unstage / commit / fetch-pull-push, the
   * repositories list, the task ledger, and recent commits).
   *
   * Presentational only: it renders the Source-Control chrome + body and emits
   * every action through callbacks. It holds NO `$state` of its own. The page
   * owns all git state (`gitStore`), the loaders + Tauri git calls, the
   * request-id guards, the action handlers, and the whole git `$derived` graph;
   * they are passed in as props (the gitStore-backed values + derivations as the
   * grouped `git` value object, the pure display helpers as `format`, the
   * mutating handlers as `actions`, mirroring `GitInsightsPanel`).
   *
   * It **binds `commitMessage`** (the page-owned `gitStore.commitMessage`) on the
   * commit textarea — supplied as a value + `onCommitMessageChange` callback so
   * the round-trip stays page-owned.
   *
   * Renders inline in the activity area (NO teleport). The activity rail is the
   * teleport host; the git mode is just a conditional child — the wrapping
   * `{:else if dock.activityMode === 'git'}` gate stays in the page, which
   * renders `<ActivityGitPanel … />` as the whole branch body. The component
   * root is `.activity-panel-list activity-git-panel` (the `.activity-panel-list`
   * base lives in `src/app.css` as `:global`).
   *
   * `files.*` is deliberately NOT imported (it is a cross-domain store): the
   * selected-source-record reads are reduced to the `selectedRelativePath` prop
   * (for the changed-row selected state) and the `fileActionBusy` prop (for the
   * task-ledger worktree-action disabled state).
   */
  import {
    Check,
    ChevronDown,
    Copy,
    ExternalLink,
    FolderOpen,
    FolderSearch,
    History,
    MoreHorizontal,
    RefreshCw,
    Save,
    Terminal,
    Trash2
  } from '@lucide/svelte';
  import type { GitBranchHealthSummary } from '$lib/sourceData';
  import type { GitCommitHistoryEntry, ProjectGitFileStatus } from '$lib/tauriSource';
  import type {
    GitGraphViewModel,
    GitGraphCommitRow,
    GitGraphRepositoryRow
  } from '$lib/shell/git/gitGraphViewModel';
  import type { WorktreePrimaryAction } from '$lib/worktreeSafety';
  import type { ProjectWorktree } from '$lib/tauriSource';

  /** Mirrors the page-local `GitStatusFileGroup` (assigns structurally). */
  type GitStatusGroupID = 'staged' | 'unstaged' | 'untracked';
  type GitStatusFileGroup = {
    id: GitStatusGroupID;
    label: string;
    files: ProjectGitFileStatus[];
    action: 'stage' | 'unstage';
  };

  /** Mirrors the page-local `GitTaskLedgerRow` (assigns structurally). */
  type GitTaskLedgerTone = 'blocked' | 'ready' | 'review' | 'protected' | 'clean';
  type GitTaskLedgerRow = {
    taskID: string;
    sourceSummary: string;
    detailSummary: string;
    worktreeCount: number;
    blockedWorktreeCount: number;
    readyWorktreeCount: number;
    cleanupCandidateCount: number;
    staleCleanWorktreeCount: number;
    backupRequiredWorktreeCount: number;
    activeSessionCount: number;
    savedWorkspaceCount: number;
    commitCount: number;
    runCount: number;
    ownerSummary: string;
    cleanupSummary: string;
    nextAction: string;
    tone: GitTaskLedgerTone;
    primaryWorktree: ProjectWorktree | null;
    latestCommit: GitCommitHistoryEntry | null;
  };

  /**
   * The gitStore-backed values + page `$derived` git data this panel reads,
   * grouped into one object prop (mirrors `GitInsightsPanel`'s grouped `git`
   * prop) so the panel surface stays manageable. All computed in the page over
   * `gitStore.*` + cross-domain deps; this panel is a pure function of them.
   */
  interface GitPanelData {
    // ── command drawer (working-tree actions) ──
    actionBusy: 'stage' | 'unstage' | 'commit' | 'fetch' | 'pull' | 'push' | '';
    actionStatus: string;
    actionError: string;
    statusLoading: boolean;
    statusError: string;
    remoteActionDisabled: boolean;
    commitDisabled: boolean;

    // ── changes (status list) ──
    branchHealth: GitBranchHealthSummary;
    changedFiles: ProjectGitFileStatus[];
    fileGroups: GitStatusFileGroup[];
    fileGroupSummary: string;

    // ── repositories ──
    repositoryRows: GitGraphRepositoryRow[];

    // ── task ledger ──
    taskLedger: GitTaskLedgerRow[];

    // ── recent commits ──
    graph: GitGraphViewModel;
    graphSummary: string;
    taskSearchSummary: string;
    commitRows: GitGraphCommitRow[];
  }

  /** Pure display helpers called from markup (page-owned, no side effects). */
  interface GitPanelFormatters {
    rootLabel: (path: string) => string;
    statusGroupActionLabel: (group: GitStatusFileGroup) => string;
    statusFileTitle: (fileStatus: ProjectGitFileStatus) => string;
    statusFileSummary: (fileStatus: ProjectGitFileStatus) => string;
    taskUrl: (taskID: string | null) => string | null;
    repoTitle: (row: GitGraphRepositoryRow) => string;
    repoTaskUrl: (row: GitGraphRepositoryRow) => string | null;
    repoTaskLabel: (row: GitGraphRepositoryRow) => string;
    repoDirtyLabel: (row: GitGraphRepositoryRow) => string;
    repoRemoteLabel: (row: GitGraphRepositoryRow) => string;
    taskLedgerTitle: (row: GitTaskLedgerRow) => string;
    commitTime: (committedAt: string) => string;
    commitEntryForRow: (row: GitGraphCommitRow) => GitCommitHistoryEntry | null;
    commitTaskSourceLabel: (row: GitGraphCommitRow) => string;
    worktreePrimaryAction: (worktree: ProjectWorktree) => WorktreePrimaryAction;
  }

  /** Mutating git action callbacks (page-owned; touch Tauri / state / clipboard). */
  interface GitPanelActions {
    onRefreshStatus: () => void;
    onRemoteAction: (action: 'fetch' | 'pull' | 'push') => void;
    onCommit: () => void;
    onStatusGroupAction: (group: GitStatusFileGroup) => void;
    onSelectStatusFile: (fileStatus: ProjectGitFileStatus) => void;
    onCopyCommand: (text: string, successStatus?: string) => void;
    onOpenPath: (path: string) => void;
    onOpenTerminalPath: (path: string) => void;
    onRevealPath: (path: string) => void;
    onOpenTaskReference: (taskID: string | null) => void;
    onCopyTaskLedger: (row: GitTaskLedgerRow) => void;
    onOpenWorktreeInSourceBrowser: (worktree: ProjectWorktree) => void;
    onRunWorktreePrimaryAction: (worktree: ProjectWorktree) => void;
    onCopyCommitSha: (entry: GitCommitHistoryEntry) => void;
    onCopyCommitSummary: (entry: GitCommitHistoryEntry) => void;
    onCopyTaskReference: (taskID: string | null) => void;

    // ── row action menus (repository / task-ledger / commit) ──
    repoRowActionMenuOpen: (id: string, scope: 'repository' | 'task-ledger') => boolean;
    onOpenRepoRowActionMenu: (id: string, scope: 'repository' | 'task-ledger') => void;
    onToggleRepoRowActionMenu: (id: string, scope: 'repository' | 'task-ledger') => void;
    onCloseRepoRowActionMenu: () => void;
    commitRowActionMenuOpen: (scope: string, id: string) => boolean;
    onOpenCommitRowActionMenu: (scope: string, id: string) => void;
    onToggleCommitRowActionMenu: (scope: string, id: string) => void;
    onCloseCommitRowActionMenu: () => void;
  }

  interface Props {
    /** Selected project display name (page `selectedProject.name`). */
    projectName: string;
    /** Selected project root path (page `selectedProject.path`). */
    projectPath: string;
    /** Relative path of the selected source file (for the changed-row selected state). */
    selectedRelativePath: string | null | undefined;
    /** Current busy file-action key (page `files.fileActionBusy`) — drives ledger worktree-action disabled state. */
    fileActionBusy: string;
    /** Draft commit message (page-owned `gitStore.commitMessage`). */
    commitMessage: string;
    /** gitStore-backed values + page git derivations. */
    git: GitPanelData;
    /** Pure display helpers. */
    format: GitPanelFormatters;
    /** Mutating git action callbacks. */
    actions: GitPanelActions;
  }

  let {
    projectName,
    projectPath,
    selectedRelativePath,
    fileActionBusy,
    commitMessage = $bindable(''),
    git,
    format,
    actions
  }: Props = $props();
</script>

<div class="activity-panel-list activity-git-panel" data-testid="git-activity-panel" aria-label="Source Control">
  <section class="activity-git-source-control" aria-label="Source Control changes">
    <div class="activity-git-heading">
      <div>
        <strong>Source Control</strong>
        <small>{projectName} · {format.rootLabel(projectPath)}</small>
      </div>
      <button
        class="activity-git-refresh"
        type="button"
        aria-label="Refresh source control status"
        title="Refresh source control status"
        disabled={git.statusLoading}
        onclick={actions.onRefreshStatus}
      >
        <RefreshCw size={13} strokeWidth={2} />
      </button>
    </div>
    <div
      class="git-branch-health-strip activity-git-health-strip"
      data-testid="git-branch-summary"
      aria-label="Source control branch health"
      title={git.branchHealth.detail}
    >
      {#each git.branchHealth.chips as chip (`activity:${chip.label}:${chip.value}`)}
        <span class={`git-branch-health-chip ${chip.tone}`}>
          <strong>{chip.label}</strong>
          <span>{chip.value}</span>
        </span>
      {/each}
    </div>
    <details class="git-command-drawer activity-git-command-drawer" data-testid="git-command-drawer">
      <summary>
        <span>Commands</span>
        <small>fetch, pull, push, commit</small>
      </summary>
      <div class="activity-git-command-strip" aria-label="Source control commands">
        <div class="activity-git-remote-row">
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
        <div class="git-commit-row activity-git-commit-row">
          <textarea
            class="git-commit-input"
            bind:value={commitMessage}
            aria-label="Git commit message"
            placeholder="Message (Cmd+Enter to commit staged changes)"
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
      </div>
    </details>
    <div class="activity-git-status-heading">
      <strong>Changes</strong>
      <span>{git.fileGroupSummary}</span>
    </div>
    <div
      class="git-status-list activity-source-control-list"
      data-testid="git-changed-files"
      aria-label="Source Control changed files"
    >
      {#if git.statusLoading}
        <div class="activity-empty compact">Loading changed files</div>
      {:else if git.statusError}
        <div class="activity-empty compact">{git.statusError}</div>
      {:else if git.changedFiles.length === 0}
        <div class="activity-empty compact">No changed files</div>
      {:else}
        {#each git.fileGroups as group (group.id)}
          {#if group.files.length > 0}
            <details
              class="git-status-group"
              data-testid={`git-status-group-${group.id}`}
              aria-label={`${group.label} Git files`}
              open
            >
              <summary class="git-status-group-heading">
                <strong>{group.label}</strong>
                <span>{group.files.length}</span>
                <button
                  type="button"
                  disabled={git.actionBusy !== ''}
                  onclick={(event) => {
                    event.preventDefault();
                    event.stopPropagation();
                    void actions.onStatusGroupAction(group);
                  }}
                >
                  {format.statusGroupActionLabel(group)}
                </button>
              </summary>
              {#each group.files as fileStatus (`activity:${group.id}:${fileStatus.relativePath}`)}
                <button
                  class="git-status-row"
                  class:selected={selectedRelativePath === fileStatus.relativePath}
                  data-git-path={fileStatus.relativePath}
                  data-git-status={fileStatus.status}
                  type="button"
                  title={format.statusFileTitle(fileStatus)}
                  onclick={() => actions.onSelectStatusFile(fileStatus)}
                >
                  <strong>{fileStatus.badge}</strong>
                  <span>{fileStatus.relativePath}</span>
                  <small>{format.statusFileSummary(fileStatus)}</small>
                </button>
              {/each}
            </details>
          {/if}
        {/each}
      {/if}
    </div>
    {#if git.actionError || git.actionStatus}
      <div class:error={Boolean(git.actionError)} class="git-action-message">
        {git.actionError || git.actionStatus}
      </div>
    {/if}
  </section>

  <details class="activity-git-secondary-section" data-testid="git-repositories-section">
    <summary>
      <span>Repositories</span>
      <small>{git.repositoryRows.length}</small>
    </summary>
  {#if git.repositoryRows.length === 0}
    <div class="activity-empty">No repositories</div>
  {:else}
    {#each git.repositoryRows as row (`activity:${row.id}`)}
      <div
        class="activity-repo-row"
        class:menu-open={actions.repoRowActionMenuOpen(row.id, 'repository')}
        class:dirty={row.dirty.isDirty || row.error}
        title={format.repoTitle(row)}
        oncontextmenu={(event) => {
          event.preventDefault();
          actions.onOpenRepoRowActionMenu(row.id, 'repository');
        }}
      >
        <div class="activity-row-main">
          <strong>{row.projectName}</strong>
          <small>{row.rootLabel}</small>
        </div>
        <span class="repo-branch-badge">{row.branchLabel}</span>
        {#if row.taskID && format.repoTaskUrl(row)}
          <a
            class="repo-task-link"
            href={format.repoTaskUrl(row) ?? ''}
            target="_blank"
            rel="noreferrer"
          >
            {row.taskID}
          </a>
        {:else}
          <span class="repo-branch-badge">{format.repoTaskLabel(row)}</span>
        {/if}
        <small>{format.repoDirtyLabel(row)} · {format.repoRemoteLabel(row)}</small>
        <div
          class="activity-row-actions repository-activity-actions row-action-menu-anchor"
          class:menu-open={actions.repoRowActionMenuOpen(row.id, 'repository')}
          aria-label="Repository actions"
        >
          <button
            type="button"
            aria-label="Repository actions"
            aria-haspopup="menu"
            aria-expanded={actions.repoRowActionMenuOpen(row.id, 'repository')}
            title="Repository actions"
            onclick={() => actions.onToggleRepoRowActionMenu(row.id, 'repository')}
          >
            <MoreHorizontal size={13} strokeWidth={2} />
          </button>
          {#if actions.repoRowActionMenuOpen(row.id, 'repository')}
            <div class="row-action-menu" role="menu" aria-label="Repository actions">
              <button
                type="button"
                role="menuitem"
                aria-label="Copy repository path"
                title="Copy repository path"
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  actions.onCopyCommand(row.path, 'Repository path copied');
                }}
              >
                <Copy size={12} strokeWidth={2} />
                <span>Copy path</span>
              </button>
              <button
                type="button"
                role="menuitem"
                aria-label="Open repository path"
                title="Open repository path"
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  actions.onOpenPath(row.path);
                }}
              >
                <ExternalLink size={12} strokeWidth={2} />
                <span>Open path</span>
              </button>
              <button
                type="button"
                role="menuitem"
                aria-label="Open repository in terminal"
                title="Open repository in terminal"
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  actions.onOpenTerminalPath(row.path);
                }}
              >
                <Terminal size={12} strokeWidth={2} />
                <span>Open terminal</span>
              </button>
              <button
                type="button"
                role="menuitem"
                aria-label="Reveal repository path"
                title="Reveal repository path"
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  actions.onRevealPath(row.path);
                }}
              >
                <FolderSearch size={12} strokeWidth={2} />
                <span>Reveal path</span>
              </button>
            </div>
          {/if}
        </div>
      </div>
    {/each}
  {/if}
  </details>

  {#if git.taskLedger.length > 0}
    <details class="activity-git-secondary-section" data-testid="git-task-ledger-section">
      <summary>
        <span>Task ledger</span>
        <small>{git.taskLedger.length}</small>
      </summary>
    {#each git.taskLedger as row (row.taskID)}
      {@const ledgerWorktree = row.primaryWorktree}
      {@const ledgerAction = ledgerWorktree ? format.worktreePrimaryAction(ledgerWorktree) : null}
      <div
        class={`activity-task-ledger-row ${row.tone}`}
        class:menu-open={actions.repoRowActionMenuOpen(row.taskID, 'task-ledger')}
        data-task-ledger-id={row.taskID}
        tabindex="-1"
        title={format.taskLedgerTitle(row)}
        oncontextmenu={(event) => {
          event.preventDefault();
          actions.onOpenRepoRowActionMenu(row.taskID, 'task-ledger');
        }}
      >
        <div class="activity-row-main">
          <strong class="task-ledger-title">
            {#if format.taskUrl(row.taskID)}
              <a class="git-task-link" href={format.taskUrl(row.taskID) ?? ''} target="_blank" rel="noreferrer">
                {row.taskID}
              </a>
            {:else}
              <span>{row.taskID}</span>
            {/if}
            <span>{row.nextAction}</span>
          </strong>
          <small>{row.sourceSummary} · {row.ownerSummary} · {row.cleanupSummary}</small>
        </div>
        <div class="activity-task-ledger-chips" aria-label={`${row.taskID} task metadata`}>
          {#if row.worktreeCount > 0}
            <span class="task-ledger-chip">wt {row.worktreeCount}</span>
          {/if}
          {#if row.blockedWorktreeCount > 0}
            <span class="task-ledger-chip blocked">blocked {row.blockedWorktreeCount}</span>
          {/if}
          {#if row.readyWorktreeCount > 0}
            <span class="task-ledger-chip ready">ready {row.readyWorktreeCount}</span>
          {/if}
          {#if row.staleCleanWorktreeCount > 0}
            <span class="task-ledger-chip stale">stale {row.staleCleanWorktreeCount}</span>
          {/if}
          {#if row.backupRequiredWorktreeCount > 0}
            <span class="task-ledger-chip backup">backup {row.backupRequiredWorktreeCount}</span>
          {/if}
          {#if row.activeSessionCount > 0}
            <span class="task-ledger-chip active">active {row.activeSessionCount}</span>
          {/if}
          {#if row.savedWorkspaceCount > 0}
            <span class="task-ledger-chip saved">saved {row.savedWorkspaceCount}</span>
          {/if}
          {#if row.runCount > 0}
            <span class="task-ledger-chip">runs {row.runCount}</span>
          {/if}
          {#if row.commitCount > 0}
            <span class="task-ledger-chip">commits {row.commitCount}</span>
          {/if}
        </div>
        <div
          class="activity-row-actions task-ledger-actions row-action-menu-anchor"
          class:menu-open={actions.repoRowActionMenuOpen(row.taskID, 'task-ledger')}
          aria-label="Task ledger actions"
        >
          <button
            type="button"
            aria-label={`Task ledger actions for ${row.taskID}`}
            aria-haspopup="menu"
            aria-expanded={actions.repoRowActionMenuOpen(row.taskID, 'task-ledger')}
            title="Task ledger actions"
            onclick={() => actions.onToggleRepoRowActionMenu(row.taskID, 'task-ledger')}
          >
            <MoreHorizontal size={13} strokeWidth={2} />
          </button>
          {#if actions.repoRowActionMenuOpen(row.taskID, 'task-ledger')}
            <div class="row-action-menu" role="menu" aria-label={`Task ledger actions for ${row.taskID}`}>
              <button
                type="button"
                role="menuitem"
                aria-label={`Open task reference for ${row.taskID}`}
                title="Open task reference"
                disabled={!format.taskUrl(row.taskID)}
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  actions.onOpenTaskReference(row.taskID);
                }}
              >
                <ExternalLink size={12} strokeWidth={2} />
                <span>Open task reference</span>
              </button>
              <button
                type="button"
                role="menuitem"
                aria-label={`Copy task ledger for ${row.taskID}`}
                title="Copy task ledger"
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  actions.onCopyTaskLedger(row);
                }}
              >
                <Copy size={12} strokeWidth={2} />
                <span>Copy task ledger</span>
              </button>
              <button
                type="button"
                role="menuitem"
                aria-label={`Open worktree for ${row.taskID}`}
                title="Open task worktree in source browser"
                disabled={!ledgerWorktree}
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  if (ledgerWorktree) actions.onOpenWorktreeInSourceBrowser(ledgerWorktree);
                }}
              >
                <FolderOpen size={12} strokeWidth={2} />
                <span>Open worktree</span>
              </button>
              <button
                class={ledgerAction ? `worktree-primary-action ${ledgerAction.kind}` : 'worktree-primary-action'}
                type="button"
                role="menuitem"
                aria-label={`Run worktree action for ${row.taskID}`}
                title={ledgerAction?.title ?? 'No worktree action'}
                disabled={!ledgerWorktree || (ledgerWorktree ? fileActionBusy === `worktree-primary:${ledgerWorktree.path}` : false)}
                onclick={() => {
                  actions.onCloseRepoRowActionMenu();
                  if (ledgerWorktree) actions.onRunWorktreePrimaryAction(ledgerWorktree);
                }}
              >
                {#if ledgerAction?.kind === 'cleanup'}
                  <Trash2 size={12} strokeWidth={2} />
                {:else if ledgerAction?.kind === 'backup'}
                  <Save size={12} strokeWidth={2} />
                {:else}
                  <History size={12} strokeWidth={2} />
                {/if}
                <span>{ledgerAction?.label ?? 'Run worktree action'}</span>
              </button>
            </div>
          {/if}
        </div>
      </div>
    {/each}
    </details>
  {/if}

  <details class="activity-git-secondary-section" data-testid="git-history-section">
    <summary>
      <span>Recent commits</span>
      <small>{git.commitRows.length}</small>
    </summary>
    <div
      class="git-graph-summary-strip activity-git-graph-summary"
      aria-label="Git graph view model summary"
      title={git.taskSearchSummary}
    >
      <span>{git.graphSummary}</span>
      {#if git.graph.taskSearchTargets.length > 0}
        <small>{git.graph.taskSearchTargets.length} search targets</small>
      {/if}
    </div>
    {#if git.commitRows.length === 0}
      <div class="activity-empty">No commits</div>
    {:else}
      {#each git.commitRows.slice(0, 8) as row (row.sha)}
      {@const entry = format.commitEntryForRow(row)}
      <div
        class="activity-commit-row"
        class:menu-open={actions.commitRowActionMenuOpen('commit', row.sha)}
        title={row.detailLabel}
        oncontextmenu={(event) => {
          event.preventDefault();
          actions.onOpenCommitRowActionMenu('commit', row.sha);
        }}
      >
        <span
          class={`git-graph-marker ${row.graphKind}`}
          aria-label={row.topologyLabel}
          title={row.topologyLabel}
        ></span>
        <div class="activity-row-main">
          <strong>{row.subject}</strong>
          <small>{row.shortSha} · {format.commitTime(row.committedAt)}</small>
        </div>
        <div
          class="activity-commit-meta"
          class:menu-open={actions.commitRowActionMenuOpen('commit', row.sha)}
        >
          {#if row.taskID && format.taskUrl(row.taskID)}
            <a
              class="git-task-link"
              href={format.taskUrl(row.taskID) ?? ''}
              target="_blank"
              rel="noreferrer"
              title={`Task from ${format.commitTaskSourceLabel(row) || 'Git metadata'}`}
            >
              {row.taskID}
            </a>
          {/if}
          <div
            class="activity-row-actions commit-activity-actions row-action-menu-anchor"
            class:menu-open={actions.commitRowActionMenuOpen('commit', row.sha)}
            aria-label="Commit actions"
          >
            <button
              type="button"
              aria-label="Commit actions"
              aria-haspopup="menu"
              aria-expanded={actions.commitRowActionMenuOpen('commit', row.sha)}
              title="Commit actions"
              onclick={() => actions.onToggleCommitRowActionMenu('commit', row.sha)}
            >
              <MoreHorizontal size={13} strokeWidth={2} />
            </button>
            {#if actions.commitRowActionMenuOpen('commit', row.sha)}
              <div class="row-action-menu" role="menu" aria-label="Commit actions">
                <button
                  type="button"
                  role="menuitem"
                  aria-label="Copy commit SHA"
                  title="Copy commit SHA"
                  disabled={!entry}
                  onclick={() => {
                    actions.onCloseCommitRowActionMenu();
                    if (entry) actions.onCopyCommitSha(entry);
                  }}
                >
                  <Copy size={12} strokeWidth={2} />
                  <span>Copy SHA</span>
                </button>
                <button
                  type="button"
                  role="menuitem"
                  aria-label="Copy commit summary"
                  title="Copy commit summary"
                  disabled={!entry}
                  onclick={() => {
                    actions.onCloseCommitRowActionMenu();
                    if (entry) actions.onCopyCommitSummary(entry);
                  }}
                >
                  <History size={12} strokeWidth={2} />
                  <span>Copy summary</span>
                </button>
                {#if row.taskID}
                  <button
                    type="button"
                    role="menuitem"
                    aria-label="Copy task reference"
                    title="Copy task reference"
                    onclick={() => {
                      actions.onCloseCommitRowActionMenu();
                      actions.onCopyTaskReference(row.taskID);
                    }}
                  >
                    <ExternalLink size={12} strokeWidth={2} />
                    <span>Copy task ref</span>
                  </button>
                {/if}
              </div>
            {/if}
          </div>
        </div>
      </div>
    {/each}
  {/if}
  </details>
</div>

<style>
  /*
   * Source-Control activity-mode styling — moved verbatim from `+page.svelte`'s
   * `<style>` when this panel was extracted (Phase B, TSK-346/324/321).
   *
   * Two kinds of rules live here:
   *
   * 1. The base `.git-*` chrome (`.git-command-drawer`, `.git-action-button`,
   *    `.git-commit-input`, `.git-status-list`, `.git-status-group(-heading)`,
   *    `.git-status-row`, `.git-action-message`, `.git-branch-health-strip`,
   *    `.git-branch-health-chip`, `.git-graph-summary-strip`, `.git-graph-marker`)
   *    was PAGE-SCOPED and styled this Source-Control markup only — after the
   *    move, zero page markup references those classes, so they come here. The
   *    right-rail `GitInsightsPanel` carries its own self-sufficient (token-based)
   *    scoped copy, so this is a faithful move with no duplication surprise — the
   *    two git panels are styled independently. (Grouped page selectors that
   *    mixed these with dead/other arms were split so only the arms this markup
   *    renders are copied.)
   *
   * 2. The `.activity-git-*` / `.activity-repo-row` / `.activity-commit-*` /
   *    `.activity-task-ledger-*` / `task-ledger-*` rules — git-activity-only, so
   *    they move here too. Where a page rule grouped a git arm with another
   *    panel's arm (`.activity-session-row`/`.activity-runtime-row`/
   *    `.activity-run-row`, the `.activity-row-main` ellipsis group), only the
   *    git arm's body is restated here; the page keeps the other arms.
   *
   * Shared bases that STAY global in `src/app.css` and reach this component
   * unchanged: `.activity-panel-list`, `.activity-row-main`, `.activity-row-actions`,
   * `.row-action-menu`, `.row-action-menu-anchor`, `.git-task-link`. The
   * `.repo-branch-badge` / `.repo-task-link` chrome is SHARED with the runs
   * activity panel (page-rendered), so the page keeps its copy and a scoped copy
   * is restated here (a scoped copy can't reach the page's runs rows and vice
   * versa).
   *
   * The `.activity-panel-list .X.menu-open` open-menu reflow combos
   * keep `.activity-panel-list` as the ancestor — this component's root IS
   * `.activity-panel-list`, so the scoped selectors match the rows it renders.
   * In the page these arms were grouped with the other panels' arms; only the
   * git arms (`.activity-repo-row`/`.activity-task-ledger-row`/`.activity-commit-row`,
   * `.repository-activity-actions`/`.task-ledger-actions`/`.commit-activity-actions`,
   * `.activity-commit-meta`) are restated here. The shared
   * `.activity-panel-list .row-action-menu { position: static }` override stays
   * scoped in the page (serves every panel); it is restated here narrowed to the
   * git rows so it reaches this component's menus.
   */

  /* ── Panel root + Source-Control card ───────────────────────── */
  .activity-git-panel {
    grid-template-rows: minmax(0, auto);
    gap: 8px;
  }

  .activity-git-source-control {
    display: grid;
    gap: 7px;
    min-width: 0;
    padding: 7px;
    border: 1px solid rgba(92, 226, 207, 0.12);
    border-radius: 8px;
    background: rgba(92, 226, 207, 0.035);
  }

  .activity-git-heading {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }

  .activity-git-heading div {
    display: grid;
    gap: 2px;
    min-width: 0;
  }

  .activity-git-heading strong,
  .activity-git-heading small,
  .activity-git-status-heading strong,
  .activity-git-status-heading span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-git-heading strong {
    color: #eef6f3;
    font-size: 13px;
    font-weight: 860;
  }

  .activity-git-heading small {
    color: #8d9995;
    font-size: 9px;
    font-weight: 760;
  }

  .activity-git-refresh {
    display: grid;
    place-items: center;
    width: 25px;
    height: 25px;
    padding: 0;
    color: #91a19d;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 7px;
    background: rgba(255, 255, 255, 0.035);
    cursor: pointer;
  }

  .activity-git-refresh:hover,
  .activity-git-refresh:focus-visible {
    color: #eaf5f2;
    border-color: rgba(92, 226, 207, 0.36);
    outline: 0;
    background: rgba(92, 226, 207, 0.12);
  }

  .activity-git-refresh:disabled {
    cursor: default;
    opacity: 0.48;
  }

  .activity-git-status-heading {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: 7px;
    min-width: 0;
    color: #dfe8e5;
    font-size: 10px;
    font-weight: 850;
  }

  .activity-git-status-heading span {
    color: #8d9995;
    font-size: 9px;
    font-weight: 760;
  }

  /* ── Secondary sections (Repositories / Task ledger / Commits) ─ */
  .activity-git-secondary-section {
    display: grid;
    min-width: 0;
    border-top: 1px solid rgba(255, 255, 255, 0.06);
    padding-top: 5px;
  }

  .activity-git-secondary-section summary {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    align-items: center;
    gap: 6px;
    min-width: 0;
    min-height: 25px;
    color: #cbd6d3;
    list-style: none;
    cursor: pointer;
    font-size: 10px;
    font-weight: 850;
  }

  .activity-git-secondary-section summary::-webkit-details-marker {
    display: none;
  }

  .activity-git-secondary-section summary::before {
    width: 0;
    height: 0;
    border-top: 4px solid transparent;
    border-bottom: 4px solid transparent;
    border-left: 5px solid rgba(174, 184, 181, 0.76);
    content: "";
    transition: transform 140ms ease, border-left-color 140ms ease;
  }

  .activity-git-secondary-section[open] summary::before {
    transform: rotate(90deg);
  }

  .activity-git-secondary-section summary span,
  .activity-git-secondary-section summary small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-git-secondary-section summary small {
    color: #7ff0df;
    font-family: var(--font-mono);
    font-size: 9px;
    font-weight: 820;
  }

  /* ── Command drawer ─────────────────────────────────────────── */
  .git-command-drawer {
    flex: 0 0 auto;
    min-width: 0;
    border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  }

  .git-command-drawer summary {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: 7px;
    min-width: 0;
    height: 28px;
    padding: 0 8px;
    color: #dce5e2;
    list-style: none;
    cursor: pointer;
    font-size: 10px;
    font-weight: 840;
  }

  .git-command-drawer summary::-webkit-details-marker {
    display: none;
  }

  .git-command-drawer summary::before {
    width: 0;
    height: 0;
    border-top: 4px solid transparent;
    border-bottom: 4px solid transparent;
    border-left: 5px solid #8d9995;
    content: "";
  }

  .git-command-drawer[open] summary::before {
    transform: rotate(90deg);
  }

  .git-command-drawer summary small {
    min-width: 0;
    overflow: hidden;
    color: #899591;
    font-size: 9px;
    font-weight: 760;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-git-command-drawer {
    min-width: 0;
    overflow: hidden;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 6px;
    background: rgba(0, 0, 0, 0.1);
  }

  .activity-git-command-drawer summary {
    height: 27px;
    padding: 0 8px;
  }

  .activity-git-command-strip {
    display: grid;
    gap: 5px;
    min-width: 0;
    padding: 0 6px 6px;
  }

  .activity-git-remote-row {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 5px;
    min-width: 0;
  }

  .git-commit-row {
    display: grid;
    min-width: 0;
    gap: 5px;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: stretch;
  }

  .activity-git-commit-row {
    grid-template-columns: minmax(0, 1fr) minmax(78px, auto);
  }

  .git-action-button {
    display: grid;
    grid-template-columns: 13px minmax(0, auto);
    align-items: center;
    justify-content: center;
    gap: 5px;
    min-width: 0;
    min-height: 24px;
    padding: 0 6px;
    color: #cfd8d5;
    border: 1px solid rgba(255, 255, 255, 0.09);
    border-radius: 5px;
    background: rgba(255, 255, 255, 0.045);
    font-size: 9px;
    font-weight: 820;
    cursor: pointer;
  }

  .git-action-button.commit {
    color: #dff8f4;
    border-color: rgba(92, 226, 207, 0.28);
    background: rgba(92, 226, 207, 0.12);
  }

  .git-action-button:disabled {
    cursor: default;
    opacity: 0.48;
  }

  .git-action-button span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-git-command-strip .git-action-button {
    min-height: 25px;
  }

  .git-commit-input {
    width: 100%;
    min-width: 0;
    min-height: 32px;
    padding: 6px 7px;
    resize: none;
    color: #e6ecea;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 5px;
    outline: none;
    background: rgba(0, 0, 0, 0.22);
    font: inherit;
    font-size: 10px;
    line-height: 1.3;
  }

  .git-commit-input:focus {
    border-color: rgba(92, 226, 207, 0.42);
  }

  .activity-git-command-strip .git-commit-input {
    min-height: 34px;
    background: rgba(0, 0, 0, 0.24);
  }

  .git-action-message {
    min-width: 0;
    overflow: hidden;
    color: #8d9995;
    font-size: 10px;
    font-weight: 760;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .git-action-message.error {
    color: #ff8f8f;
  }

  /* ── Branch health ──────────────────────────────────────────── */
  .git-branch-health-strip {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(92px, 1fr));
    gap: 4px;
    min-width: 0;
  }

  .activity-git-health-strip {
    grid-template-columns: repeat(auto-fit, minmax(96px, 1fr));
  }

  .git-branch-health-chip {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: 5px;
    min-width: 0;
    min-height: 22px;
    padding: 3px 6px;
    border: 1px solid rgba(255, 255, 255, 0.06);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.032);
  }

  .git-branch-health-chip strong,
  .git-branch-health-chip span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .git-branch-health-chip strong {
    color: #8d9995;
    font-size: 8px;
    font-weight: 860;
    text-transform: uppercase;
  }

  .git-branch-health-chip span {
    color: #dce5e2;
    font-size: 9px;
    font-weight: 780;
  }

  .git-branch-health-chip.clean span {
    color: #72e2cf;
  }

  .git-branch-health-chip.dirty span {
    color: #d8aa55;
  }

  .git-branch-health-chip.warning span {
    color: #9fd0f0;
  }

  .git-branch-health-chip.error span {
    color: #ff8d8d;
  }

  .git-branch-health-chip.muted span {
    color: #9fa9a6;
  }

  /* ── Changes (status list) ──────────────────────────────────── */
  .git-status-list {
    display: grid;
    flex: 0 0 auto;
    gap: 5px;
    max-height: 150px;
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
    padding: 8px;
    border-bottom: 1px solid rgba(255, 255, 255, 0.06);
    scrollbar-color: rgba(174, 184, 181, 0.54) rgba(255, 255, 255, 0.045);
    scrollbar-gutter: stable;
    scrollbar-width: thin;
  }

  .activity-source-control-list {
    max-height: none;
    padding: 0;
    border-bottom: 0;
    overflow: visible;
  }

  .activity-source-control-list .git-status-group {
    gap: 2px;
  }

  .activity-source-control-list .git-status-group + .git-status-group {
    padding-top: 5px;
    border-top: 1px solid rgba(255, 255, 255, 0.055);
  }

  .activity-source-control-list .git-status-group-heading {
    grid-template-columns: auto minmax(0, 1fr) auto auto;
    cursor: pointer;
    min-height: 21px;
    padding: 0 2px;
  }

  .activity-source-control-list .git-status-group-heading::-webkit-details-marker {
    display: none;
  }

  .activity-source-control-list .git-status-group-heading::before {
    width: 0;
    height: 0;
    border-top: 4px solid transparent;
    border-bottom: 4px solid transparent;
    border-left: 5px solid rgba(174, 184, 181, 0.76);
    content: "";
    transform: rotate(90deg);
    transition: transform 140ms ease, border-left-color 140ms ease;
  }

  .activity-source-control-list .git-status-group:not([open]) .git-status-group-heading::before {
    transform: rotate(0deg);
  }

  .activity-source-control-list .git-status-row {
    min-height: 27px;
    padding: 4px 5px;
    border-color: transparent;
    border-radius: 5px;
    background: transparent;
  }

  .activity-source-control-list .git-status-row:hover,
  .activity-source-control-list .git-status-row:focus-visible {
    border-color: rgba(92, 226, 207, 0.16);
    outline: 0;
    background: rgba(255, 255, 255, 0.045);
  }

  .activity-source-control-list .git-status-row.selected {
    border-color: rgba(92, 226, 207, 0.3);
    background: rgba(92, 226, 207, 0.095);
  }

  .git-status-group {
    display: grid;
    gap: 4px;
    min-width: 0;
  }

  .git-status-group-heading {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto auto;
    align-items: center;
    gap: 5px;
    min-width: 0;
    min-height: 22px;
    color: #aeb8b5;
    font-size: 9px;
    font-weight: 850;
    text-transform: uppercase;
  }

  .git-status-group-heading strong,
  .git-status-group-heading span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .git-status-group-heading button {
    min-width: 0;
    height: 20px;
    padding: 0 6px;
    color: #cfd8d5;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 5px;
    background: rgba(255, 255, 255, 0.04);
    font-size: 8.5px;
    font-weight: 820;
    cursor: pointer;
    text-transform: none;
  }

  .git-status-group-heading button:disabled {
    cursor: default;
    opacity: 0.48;
  }

  .git-status-group-heading button:hover:not(:disabled),
  .git-status-group-heading button:focus-visible {
    color: #eaf5f2;
    border-color: rgba(92, 226, 207, 0.34);
    outline: 0;
    background: rgba(92, 226, 207, 0.11);
  }

  .git-status-row {
    display: grid;
    grid-template-columns: 24px minmax(0, 1fr) minmax(0, 76px);
    align-items: center;
    gap: 7px;
    min-width: 0;
    min-height: 30px;
    padding: 5px 7px;
    color: #cbd3d1;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 7px;
    background: rgba(255, 255, 255, 0.035);
    text-align: left;
    cursor: pointer;
  }

  .git-status-row.selected {
    border-color: rgba(92, 226, 207, 0.32);
    background: rgba(92, 226, 207, 0.11);
  }

  .git-status-row strong,
  .git-status-row span,
  .git-status-row small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .git-status-row strong {
    color: #6fdfcf;
    font-family: var(--font-mono);
    font-size: 11px;
    font-weight: 860;
  }

  .git-status-row span {
    font-size: 10px;
    font-weight: 780;
  }

  .git-status-row small {
    color: #8d9995;
    font-size: 9px;
    font-weight: 760;
  }

  /* ── Repository rows ────────────────────────────────────────── */
  .activity-repo-row {
    display: grid;
    align-items: center;
    gap: 8px;
    min-width: 0;
    min-height: 40px;
    padding: 8px;
    color: #cbd3d1;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 8px;
    background: rgba(0, 0, 0, 0.14);
    grid-template-columns: minmax(0, 1fr) auto auto auto;
  }

  .activity-repo-row.dirty {
    background: rgba(216, 170, 85, 0.09);
  }

  /* .activity-row-main base lives in src/app.css (:global). */
  .activity-row-main strong,
  .activity-row-main small,
  .activity-repo-row small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-row-main strong {
    color: #f0f4f3;
    font-size: 11px;
    font-weight: 790;
  }

  .activity-row-main small,
  .activity-repo-row small {
    color: #8d9995;
    font-size: 10px;
    font-weight: 720;
  }

  .activity-repo-row > small {
    grid-column: 1 / 4;
  }

  /*
   * `.repo-branch-badge` / `.repo-task-link` are SHARED with the runs activity
   * panel (page-rendered) — the page keeps its copy; this scoped copy styles the
   * repo rows this component renders. The page's grouped ellipsis rule cannot
   * reach here, so ellipsis is folded into `.repo-branch-badge` directly.
   */
  .repo-branch-badge {
    min-width: 0;
    overflow: hidden;
    color: #6fdfcf;
    font-family: var(--font-mono);
    font-size: 10px;
    font-weight: 820;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .repo-task-link {
    display: inline-flex;
    align-items: center;
    justify-self: start;
    max-width: 100%;
    min-height: 20px;
    padding: 0 7px;
    color: #071b18;
    border-radius: 999px;
    background: #6fdfcf;
    font-size: 9px;
    font-weight: 900;
    line-height: 1;
    text-decoration: none;
    white-space: nowrap;
  }

  /* ── Task ledger rows ───────────────────────────────────────── */
  .activity-task-ledger-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto auto;
    align-items: center;
    gap: 7px;
    min-width: 0;
    padding: 8px;
    border: 1px solid rgba(255, 255, 255, 0.075);
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.03);
  }

  .activity-task-ledger-row.blocked {
    border-color: rgba(216, 170, 85, 0.28);
    background: rgba(216, 170, 85, 0.055);
  }

  .activity-task-ledger-row.ready {
    border-color: rgba(92, 226, 207, 0.24);
    background: rgba(92, 226, 207, 0.055);
  }

  .task-ledger-title {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    min-width: 0;
  }

  .task-ledger-title > span {
    min-width: 0;
    overflow: hidden;
    color: #dce6e3;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-task-ledger-chips {
    display: inline-flex;
    flex-wrap: wrap;
    justify-content: flex-end;
    gap: 3px;
    max-width: 138px;
    min-width: 0;
  }

  .task-ledger-chip {
    max-width: 72px;
    height: 18px;
    padding: 0 6px;
    overflow: hidden;
    color: #9ba7a4;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.035);
    font-size: 9px;
    font-weight: 820;
    line-height: 18px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .task-ledger-chip.blocked,
  .task-ledger-chip.active {
    color: #e6c170;
    border-color: rgba(216, 170, 85, 0.24);
    background: rgba(216, 170, 85, 0.08);
  }

  .task-ledger-chip.ready {
    color: #7ff0df;
    border-color: rgba(92, 226, 207, 0.26);
    background: rgba(92, 226, 207, 0.08);
  }

  .task-ledger-chip.stale {
    color: #7ff0df;
    border-color: rgba(92, 226, 207, 0.22);
    background: rgba(92, 226, 207, 0.065);
  }

  .task-ledger-chip.saved {
    color: #8fe7dc;
    border-color: rgba(92, 226, 207, 0.18);
    background: rgba(92, 226, 207, 0.055);
  }

  .task-ledger-chip.backup {
    color: #e6c170;
    border-color: rgba(216, 170, 85, 0.22);
    background: rgba(216, 170, 85, 0.075);
  }

  /* ── Recent commits ─────────────────────────────────────────── */
  .activity-commit-row {
    display: grid;
    align-items: center;
    gap: 8px;
    min-width: 0;
    min-height: 40px;
    padding: 8px;
    color: #cbd3d1;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 8px;
    background: rgba(0, 0, 0, 0.14);
    grid-template-columns: auto minmax(0, 1fr) auto;
  }

  .activity-commit-meta {
    display: inline-flex;
    align-items: center;
    justify-content: flex-end;
    gap: 5px;
    min-width: 0;
  }

  .git-graph-summary-strip {
    display: flex;
    align-items: center;
    gap: 5px;
    min-width: 0;
    min-height: 20px;
    overflow: hidden;
    padding: 3px 5px;
    color: #9aa7a3;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.028);
    font-size: 8.5px;
    font-weight: 780;
  }

  .git-graph-summary-strip span,
  .git-graph-summary-strip small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .git-graph-summary-strip span {
    flex: 1 1 auto;
    color: #dce5e2;
    font-weight: 830;
  }

  .git-graph-summary-strip small {
    flex: 0 0 auto;
    color: #8d9995;
  }

  .activity-git-graph-summary {
    margin-top: -3px;
  }

  .git-graph-marker {
    position: relative;
    display: grid;
    place-items: center;
    width: 14px;
    height: 22px;
  }

  .git-graph-marker::before {
    position: absolute;
    inset: -8px auto;
    width: 1px;
    background: rgba(111, 223, 207, 0.22);
    content: "";
  }

  .git-graph-marker::after {
    z-index: 1;
    width: 8px;
    height: 8px;
    border: 2px solid rgba(111, 223, 207, 0.72);
    border-radius: 999px;
    background: #171b1b;
    content: "";
  }

  .git-graph-marker.head::before {
    width: 2px;
    background: rgba(111, 223, 207, 0.44);
  }

  .git-graph-marker.head::after {
    width: 10px;
    height: 10px;
    border-color: #6fdfcf;
    background: #6fdfcf;
    box-shadow: 0 0 0 3px rgba(111, 223, 207, 0.14);
  }

  .git-graph-marker.branch::after {
    border-color: rgba(132, 201, 222, 0.84);
    background: #171b1b;
  }

  .git-graph-marker.merge::before {
    background: linear-gradient(
      180deg,
      rgba(216, 170, 85, 0.15),
      rgba(216, 170, 85, 0.5),
      rgba(111, 223, 207, 0.2)
    );
  }

  .git-graph-marker.merge::after {
    width: 10px;
    height: 10px;
    border-color: rgba(216, 170, 85, 0.9);
    border-radius: 3px;
    background: #171b1b;
  }

  .git-graph-marker.root::before {
    inset: -8px auto 50%;
    background: rgba(174, 184, 181, 0.2);
  }

  .git-graph-marker.root::after {
    border-color: rgba(174, 184, 181, 0.75);
    background: rgba(174, 184, 181, 0.2);
  }

  /*
   * Row-action chrome. The `.activity-row-actions` / `.row-action-menu` /
   * `.row-action-menu-anchor` bases live in src/app.css (:global); the
   * descendant rules below stay scoped to this component (the page keeps its own
   * copies for the panels whose markup stays inline).
   */
  .activity-row-actions button {
    display: grid;
    place-items: center;
    width: 23px;
    height: 23px;
    padding: 0;
    color: #91a19d;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 7px;
    background: rgba(255, 255, 255, 0.035);
    cursor: pointer;
  }

  .activity-row-actions button.worktree-primary-action.backup {
    color: #d8aa55;
    border-color: rgba(216, 170, 85, 0.2);
    background: rgba(216, 170, 85, 0.08);
  }

  .activity-row-actions button.worktree-primary-action.cleanup {
    color: #79eadb;
    border-color: rgba(92, 226, 207, 0.28);
    background: rgba(92, 226, 207, 0.1);
  }

  .activity-row-actions button:hover,
  .activity-row-actions button:focus-visible {
    color: #eaf5f2;
    border-color: rgba(92, 226, 207, 0.36);
    outline: 0;
    background: rgba(92, 226, 207, 0.12);
  }

  .row-action-menu button {
    display: grid;
    grid-template-columns: 15px minmax(0, 1fr);
    align-items: center;
    justify-content: stretch;
    gap: 7px;
    width: 100%;
    height: 26px;
    padding: 0 7px;
    color: #cbd3d1;
    border: 0;
    border-radius: 5px;
    background: transparent;
    font-size: 11px;
    font-weight: 730;
    text-align: left;
  }

  .row-action-menu button:disabled {
    cursor: default;
    opacity: 0.48;
  }

  .row-action-menu button:hover:not(:disabled),
  .row-action-menu button:focus-visible {
    color: #f2f6f5;
    outline: 0;
    background: rgba(92, 226, 207, 0.1);
  }

  .row-action-menu button span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /*
   * Open-menu reflow combos — the component root IS `.activity-panel-list`, so
   * these scoped selectors reach the rows this component renders. Restated from
   * the page's shared multi-panel `.menu-open` groups, narrowed to
   * the git arms only.
   */
  .activity-panel-list .activity-repo-row.menu-open,
  .activity-panel-list .activity-task-ledger-row.menu-open,
  .activity-panel-list .activity-commit-row.menu-open {
    align-items: start;
  }

  .activity-panel-list .repository-activity-actions.menu-open,
  .activity-panel-list .task-ledger-actions.menu-open,
  .activity-panel-list .activity-commit-meta.menu-open,
  .activity-panel-list .commit-activity-actions.menu-open {
    display: contents;
  }

  .activity-panel-list .repository-activity-actions.menu-open > button,
  .activity-panel-list .task-ledger-actions.menu-open > button,
  .activity-panel-list .commit-activity-actions.menu-open > button {
    justify-self: end;
  }

  .activity-panel-list .commit-activity-actions.menu-open > button {
    grid-column: 3;
    grid-row: 1;
  }

  .activity-panel-list .task-ledger-actions.menu-open > button {
    grid-column: 3;
    grid-row: 1;
  }

  .activity-panel-list .repository-activity-actions.menu-open > button {
    grid-column: 4;
    grid-row: 1;
  }

  /*
   * The shared `.activity-panel-list .row-action-menu { position: static }`
   * override stays scoped in the page (serves every activity panel). Scoped
   * there it cannot reach this component's menus, so the same positioning is
   * restated here, narrowed to the git rows so it does not broaden anything.
   */
  .activity-panel-list .activity-repo-row .row-action-menu,
  .activity-panel-list .activity-task-ledger-row .row-action-menu,
  .activity-panel-list .activity-commit-row .row-action-menu {
    position: static;
    grid-column: 1 / -1;
    width: 100%;
    margin-top: -1px;
  }
</style>
