<script lang="ts">
  /**
   * GitHistoryView.svelte — the whole commit history of a repository, given the
   * width of the middle of the shell.
   *
   * The right column already has a commit list (`GraphPane`), and it is a good
   * one — but it is a column, so it can only ever show a hash, a subject and a
   * date squeezed onto two lines. This is the same history as a TABLE, the way
   * VS Code's Git Graph extension shows it: one row per commit, the branching
   * picture down the left, and Message, Date, Author and Hash each in a column
   * of their own that lines up all the way down.
   *
   * NOTHING NEW DOES THE WORK. Every piece already existed:
   *
   *  - A view-owned `gitService` reads and pages this history independently
   *    from the Source Control panel beside it.
   *  - `gitGraphViewModel.ts` turns a commit into its branch and tag names.
   *  - `gitGraphLanes.ts` says which column each commit's dot sits in.
   *  - `gitHistoryFilters.ts` answers the toolbar: which repositories, which
   *    branches, which authors, and what survives the filters.
   *  - A view-owned `gitCommitFilesService` reads what a commit changed when
   *    its row opens, and puts a file's changes on the Diff tab when clicked.
   *
   * READ-ONLY. Nothing here checks anything out, resets anything or moves any
   * branch. Picking a repository changes only this history view, and picking a
   * row only reads.
   */
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import X from '@lucide/svelte/icons/x';

  import { Chip } from '$lib/components/ui/chip/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import SourceControlContextMenu from '$lib/shell/git/components/SourceControlContextMenu.svelte';
  import {
    snapshotSourceControlCommitFileMenu,
    snapshotSourceControlCommitMenu,
    sourceControlContextMenuAnchor,
    type SourceControlContextMenuAction,
    type SourceControlMenuSnapshot
  } from '$lib/shell/git/components/sourceControlContextMenu';
  import { buildGitCommitGraphRows } from '$lib/shell/git/gitGraphViewModel';
  import {
    assignGitGraphLanes,
    gitGraphLaneWidth,
    gitGraphLaneX
  } from '$lib/shell/git/gitGraphLanes';
  import {
    EMPTY_GIT_HISTORY_FILTER,
    filterGitHistoryRows,
    gitHistoryAuthors,
    gitHistoryBranches,
    gitHistoryRepositoryOptions,
    gitHistoryUncommittedRow,
    isGitHistoryFilterActive
  } from '$lib/shell/git/gitHistoryFilters';
  import {
    commitFilesEntry,
    gitCommitFiles,
    gitCommitFilesView,
    describeCommitFiles,
    isCommitExpanded,
    isUnreadableGitPath,
    splitRepositoryPath,
    summarizeCommitFiles
  } from '$lib/shell/git/gitCommitFilesStore.svelte';
  import { createGitCommitFilesService } from '$lib/shell/git/gitCommitFilesService';
  import {
    canLoadMoreGitHistory,
    describeGitHistoryFooter,
    gitPanel,
    isNotARepositoryError
  } from '$lib/shell/git/gitPanelStore.svelte';
  import { absolutePathWithin, COMMIT_HISTORY_LIMIT, gitService } from '$lib/shell/git/gitService';
  import { nativeWorkspacePath } from '$lib/workspacePaths.ts';
  import { worktreeManager } from '$lib/shell/worktrees/worktreeManagerStore.svelte';
  import { formatLastActivity } from '$lib/shell/relativeTime';
  import { openFileInEditor, showCenterTab, showRightTab } from '$lib/shell/workbenchNavigation';
  import type { GitCommitFileChange } from '$lib/shell/git/gitBackendExtra';

  interface Props {
    root?: string;
    rootAvailable?: boolean;
    historyPath?: string;
    showing?: boolean;
  }
  let { root = '', rootAvailable = true, historyPath = '', showing = false }: Props = $props();
  let requestedHistory = '';
  let wasShowing = false;
  let contextMenu = $state.raw<SourceControlMenuSnapshot | null>(null);

  const historyPanel = gitPanel;
  const historyService = gitService;
  const historyCommitFiles = gitCommitFiles;
  const historyCommitFilesService = createGitCommitFilesService({ owner: 'large' });
  const historyInteraction = $derived(gitCommitFilesView(historyCommitFiles, 'large'));

  $effect(() => {
    const becameShowing = showing && !wasShowing;
    wasShowing = showing;
    if (!showing) return;
    const targetRoot = rootAvailable ? root.trim() : '';
    const targetPath = historyPath.trim();
    const requestKey = `${targetRoot}\u0000${targetPath}`;
    if (requestKey === requestedHistory) {
      if (targetRoot) historyService.ensureHistorySurface();
      if (becameShowing && historyPanel.diffOwner !== 'large') {
        void historyCommitFilesService.restoreSelection();
      }
      return;
    }
    requestedHistory = requestKey;
    if (!targetRoot) {
      historyService.releaseHistorySurface();
      historyCommitFilesService.release();
      return;
    }
    historyService.activate(targetRoot);
    void historyService.refreshStatus();
    historyCommitFilesService.activate(targetRoot);
    if (targetPath) void historyService.showFileHistory(targetRoot, targetPath);
    else historyService.ensureHistorySurface();
    if (becameShowing && historyPanel.diffOwner !== 'large') {
      void historyCommitFilesService.restoreSelection();
    }
  });

  /** One row of the table is exactly this tall, so its SVG can be drawn to size. */
  const ROW_HEIGHT = 24;
  const LANE_SPACING = 12;
  const LANE_OFFSET = 10;
  /** The graph column is never narrower than its "Graph" header, so with few
   * lanes or none the label cannot run into "Message". */
  const GRAPH_MIN_WIDTH = 52;
  /** Past this the graph would eat the message column; extra columns fold in. */
  const MAX_DRAWN_LANES = 8;

  /**
   * What "no filter" is called inside a `Select`. It cannot be the empty string
   * the filter itself uses: bits-ui reads `''` as "nothing is selected", so the
   * All row would never show its tick — see `getLabelForValue` in
   * `bits-ui/dist/bits/select/select.svelte.js:136`. The two meanings are
   * mapped at the picker and nowhere else.
   */
  const ALL = '*';

  const LANE_COLORS = [
    'var(--color-accent)',
    'var(--color-good)',
    'var(--color-attention)',
    'var(--color-bad)',
    'var(--color-live)',
    'var(--color-text-2)'
  ];

  let filter = $state({ ...EMPTY_GIT_HISTORY_FILTER });

  const commits = $derived(buildGitCommitGraphRows(historyPanel.history));
  const rows = $derived(filterGitHistoryRows(commits, filter));
  // The lanes are worked out for what is ON SCREEN. Filtering removes commits,
  // and a picture drawn for the unfiltered list would run its lines into rows
  // that are no longer there.
  const layout = $derived(
    assignGitGraphLanes(rows.map((row) => ({ sha: row.sha, parentShas: row.parentHint.parentShas })))
  );
  const authors = $derived(gitHistoryAuthors(commits));
  const branches = $derived(gitHistoryBranches(commits));
  const repositories = $derived(
    gitHistoryRepositoryOptions(historyPanel.root, worktreeManager.worktrees)
  );
  const uncommitted = $derived(gitHistoryUncommittedRow(historyPanel.status, filter));
  /** What the repository picker's trigger reads. Its own value, the way Git
   * Graph's pickers carry theirs, so the toolbar needs no separate caption. */
  const repositoryLabel = $derived.by(() => {
    const chosen = repositories.find((option) => option.path === historyPanel.root);
    if (!chosen) return 'No repository';
    return chosen.branch ? `${chosen.label} (${chosen.branch})` : chosen.label;
  });
  const footer = $derived(describeGitHistoryFooter(historyPanel));
  const canLoadMore = $derived(canLoadMoreGitHistory(historyPanel));
  const filtering = $derived(isGitHistoryFilterActive(filter));
  const drawnLanes = $derived(Math.min(layout.laneCount, MAX_DRAWN_LANES));
  const graphWidth = $derived(
    Math.max(gitGraphLaneWidth(drawnLanes, LANE_SPACING, LANE_OFFSET), GRAPH_MIN_WIDTH)
  );

  function laneColor(lane: number): string {
    return LANE_COLORS[lane % LANE_COLORS.length];
  }

  /** Columns past the drawn width are pinned to the last one rather than cut off. */
  function laneX(lane: number): number {
    return gitGraphLaneX(Math.min(lane, Math.max(drawnLanes - 1, 0)), LANE_SPACING, LANE_OFFSET);
  }

  /** One line segment of a row, as an SVG path. */
  function edgePath(kind: 'child' | 'parent' | 'passing', fromLane: number, toLane: number): string {
    const x1 = laneX(fromLane);
    const x2 = laneX(toLane);
    const middle = ROW_HEIGHT / 2;

    if (kind === 'passing') return `M ${x1} 0 L ${x1} ${ROW_HEIGHT}`;
    if (kind === 'child') return `M ${x1} 0 L ${x2} ${middle}`;
    if (x1 === x2) return `M ${x1} ${middle} L ${x1} ${ROW_HEIGHT}`;
    return `M ${x1} ${middle} C ${x1} ${middle + 7}, ${x2} ${ROW_HEIGHT - 7}, ${x2} ${ROW_HEIGHT}`;
  }

  /** "17 Aug 2026 10:04" — the same shape every row, so the column lines up. */
  function committedOn(committedAt: string): string {
    const when = new Date(committedAt);
    if (Number.isNaN(when.getTime())) return committedAt;
    return when.toLocaleString(undefined, {
      day: '2-digit',
      month: 'short',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  }

  function whenCommitted(committedAt: string): string {
    return formatLastActivity(committedAt, new Date());
  }

  function pickRepository(path: string): void {
    if (!rootAvailable || !path || path === historyPanel.root) return;
    // This view owns the selection. It reads; it changes no working directory.
    historyService.activate(path);
    void historyService.refreshStatus();
    historyCommitFilesService.activate(path);
  }

  async function toggleCommit(sha: string, isMerge: boolean): Promise<void> {
    if (!rootAvailable) return;
    historyCommitFilesService.activate(historyPanel.root);
    await historyCommitFilesService.toggleCommit(sha, isMerge);
  }

  async function pickFile(sha: string, file: GitCommitFileChange): Promise<void> {
    if (!rootAvailable) return;
    historyCommitFilesService.activate(historyPanel.root);
    const read = historyCommitFilesService.selectCommitFile(sha, file);
    showCenterTab('diff');
    await read;
  }

  function openCommitMenu(
    sha: string,
    isMerge: boolean,
    expanded: boolean,
    event: MouseEvent
  ): void {
    event.preventDefault();
    contextMenu = snapshotSourceControlCommitMenu({
      sha,
      isMerge,
      expanded,
      anchor: sourceControlContextMenuAnchor(event)
    });
  }

  function openCommitFileMenu(sha: string, file: GitCommitFileChange, event: MouseEvent): void {
    event.preventDefault();
    contextMenu = snapshotSourceControlCommitFileMenu({
      sha,
      file,
      readable: !isUnreadableGitPath(file.relativePath),
      anchor: sourceControlContextMenuAnchor(event)
    });
  }

  async function copyText(value: string): Promise<void> {
    if (typeof navigator === 'undefined' || !navigator.clipboard) return;
    await navigator.clipboard.writeText(value);
  }

  async function runContextAction(action: SourceControlContextMenuAction): Promise<void> {
    const menu = contextMenu;
    contextMenu = null;
    if (!menu || menu.kind === 'file') return;

    if (menu.kind === 'commit') {
      if (action === 'toggle-commit') await toggleCommit(menu.target.sha, menu.target.isMerge);
      else if (action === 'copy-hash') await copyText(menu.target.sha);
      return;
    }

    if (action === 'open-commit-diff') await pickFile(menu.target.sha, menu.target.file);
    else if (action === 'open-current-file' && historyPanel.root) {
      openFileInEditor({
        path: absolutePathWithin(historyPanel.root, menu.target.file.relativePath),
        projectRoot: historyPanel.root
      });
    } else if (action === 'copy-commit-path') {
      await copyText(nativeWorkspacePath(
        historyPanel.root
          ? absolutePathWithin(historyPanel.root, menu.target.file.relativePath)
          : menu.target.file.relativePath
      ));
    }
  }

  /** The checked-out branch already appears inside its `HEAD -> …` chip. */
  function branchChips(refs: { headLabels: string[]; branchLabels: string[] }): string[] {
    return refs.branchLabels.filter(
      (label) => !refs.headLabels.some((head) => head.includes(label))
    );
  }

  function badgeTone(badge: string): string {
    if (badge === 'A') return 'var(--color-good)';
    if (badge === 'D') return 'var(--color-bad)';
    if (badge === 'R' || badge === 'C') return 'var(--color-accent)';
    return 'var(--color-attention)';
  }

  async function loadOlderHistory(): Promise<void> {
    if (!rootAvailable || !canLoadMore) return;
    await historyService.loadMoreHistory();
  }
</script>

<section class="git-history">
  {#if !rootAvailable}
    <p class="notice">Checkout/Worktree deleted.</p>
  {:else}
  <header class="toolbar">
    <!-- Every control here comes from the kit: `Select` (never a native
         `<select>` — see `src/lib/components/ui/DESIGN.md`), `Input`, and
         `IconButton`, which makes the refresh button's label both its tooltip
         and its accessible name. The pickers carry their own value as their
         label, the way Git Graph's do, so no separate caption is needed. -->
    <Select.Root
      type="single"
      value={historyPanel.root ?? ''}
      onValueChange={(value) => pickRepository(value)}
    >
      <Select.Trigger
        size="sm"
        class="min-w-[150px] max-w-[260px]"
        aria-label="Which repository's history to show"
        disabled={repositories.length === 0}
      >
        {repositoryLabel}
      </Select.Trigger>
      <Select.Content>
        {#each repositories as option (option.path)}
          <Select.Item
            value={option.path}
            label={option.branch ? `${option.label} (${option.branch})` : option.label}
          />
        {/each}
      </Select.Content>
    </Select.Root>

    {#if historyPanel.historyPath}
      <span class="history-path" title={historyPanel.historyPath}>{historyPanel.historyPath}</span>
      <IconButton
        label="Show all repository history"
        onclick={() => void historyService.clearHistoryPath()}
      >
        <X />
      </IconButton>
    {/if}

    <Select.Root
      type="single"
      value={filter.branch || ALL}
      onValueChange={(value) => (filter.branch = value === ALL ? '' : value)}
    >
      <Select.Trigger
        size="sm"
        class="min-w-[132px] max-w-[240px]"
        aria-label="Show one branch, or every branch"
        title="Filters the history shown here. It does not switch the working directory."
      >
        <span class="filter-label">History: {filter.branch || 'all branches'}</span>
      </Select.Trigger>
      <Select.Content>
        <Select.Item value={ALL} label="All branches" />
        {#each branches as branch (branch)}
          <Select.Item value={branch} label={branch} />
        {/each}
      </Select.Content>
    </Select.Root>

    <Select.Root
      type="single"
      value={filter.author || ALL}
      onValueChange={(value) => (filter.author = value === ALL ? '' : value)}
    >
      <Select.Trigger
        size="sm"
        class="min-w-[128px] max-w-[220px]"
        aria-label="Show one author, or every author"
      >
        {filter.author || 'All authors'}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value={ALL} label="All authors" />
        {#each authors as author (author)}
          <Select.Item value={author} label={author} />
        {/each}
      </Select.Content>
    </Select.Root>

    <Input
      type="search"
      class="h-7 max-w-[320px] rounded-[min(var(--radius-md),10px)]"
      placeholder="Search message, hash or author"
      aria-label="Search the loaded commits"
      bind:value={filter.search}
    />

    <span class="toolbar-count" title={footer}>
      {#if filtering}
        {rows.length} of {commits.length}
      {:else if commits.length > 0}
        {commits.length} commits
      {/if}
    </span>

    <IconButton
      label="Read the commit history again"
      size="sm"
      side="bottom"
      disabled={!historyPanel.activated || historyPanel.historyLoading}
      onclick={() => void historyService.refreshHistory()}
    >
      {#if historyPanel.historyLoading}<WorkingSpinner size={14} />{:else}<RefreshCw class="size-3.5" aria-hidden="true" />{/if}
    </IconButton>
  </header>

  <ScrollArea type="always" class="min-h-0 flex-1 overflow-hidden">
    <div class="row head-row" style="--graph-width: {graphWidth}px">
      <span>Graph</span>
      <span>Message</span>
      <span class="right">Date</span>
      <span>Author</span>
      <span>Hash</span>
    </div>

    {#if historyPanel.desktopOnly}
      <p class="notice">Source control runs in the desktop app only. Nothing is loaded here.</p>
    {:else if historyPanel.historyError && commits.length === 0}
      <p class:bad={!isNotARepositoryError(historyPanel.historyError)} class="notice">
        {isNotARepositoryError(historyPanel.historyError)
          ? "This folder's Git history is unavailable."
          : historyPanel.historyError}
      </p>
    {:else if historyPanel.historyLoading && commits.length === 0}
      <p class="notice"><WorkingSpinner size={12} /> Reading the commit history…</p>
    {:else if commits.length === 0}
      <p class="notice">No commits yet. The first commit you make will appear here.</p>
    {:else}
      {#if uncommitted}
        <!-- Work that is not committed sits above the newest commit, where it
             would be if it were one. It is not a commit, so it has no dot of
             its own: an open circle, and no line leaving it. -->
        <button
          type="button"
          class="row commit-row uncommitted"
          style="--graph-width: {graphWidth}px"
          title="{uncommitted.detail} — open Source Control"
          onclick={() => showRightTab('source-control')}
        >
          <svg class="graph" width={graphWidth} height={ROW_HEIGHT} aria-hidden="true">
            <circle
              cx={laneX(0)}
              cy={ROW_HEIGHT / 2}
              r="3.5"
              fill="var(--color-bg)"
              stroke="var(--color-text-3)"
              stroke-width="1.5"
              stroke-dasharray="2 2"
            />
          </svg>
          <span class="message">
            <span class="subject uncommitted-subject">Uncommitted Changes</span>
            <Chip tone="count">{uncommitted.countLabel}</Chip>
          </span>
          <span class="date right">Now</span>
          <span class="author">{uncommitted.detail}</span>
          <span class="hash">*</span>
        </button>
      {/if}

      {#each rows as commit, index (commit.sha)}
        {@const lane = layout.rows[index]}
        {@const expanded = isCommitExpanded(historyCommitFiles, commit.sha, 'large')}
        {@const entry = commitFilesEntry(historyCommitFiles, commit.sha)}
        {@const sentence = describeCommitFiles(entry, lane?.isMerge ?? false)}
        <button
          type="button"
          class="row commit-row"
          class:open={expanded}
          style="--graph-width: {graphWidth}px"
          title={commit.detailLabel}
          aria-expanded={expanded}
          onclick={() => void toggleCommit(commit.sha, lane?.isMerge ?? false)}
          oncontextmenu={(event) =>
            openCommitMenu(commit.sha, lane?.isMerge ?? false, expanded, event)}
        >
          <svg class="graph" width={graphWidth} height={ROW_HEIGHT} aria-hidden="true">
            {#if lane}
              {#each lane.edges as edge, edgeIndex (edgeIndex)}
                <path
                  d={edgePath(edge.kind, edge.fromLane, edge.toLane)}
                  fill="none"
                  stroke={laneColor(edge.kind === 'child' ? edge.toLane : edge.fromLane)}
                  stroke-width="1.5"
                  stroke-linecap="round"
                  opacity="0.8"
                />
              {/each}
              <circle
                cx={laneX(lane.lane)}
                cy={ROW_HEIGHT / 2}
                r={lane.isMerge ? 3 : 3.5}
                fill={lane.isMerge ? 'var(--color-bg)' : laneColor(lane.lane)}
                stroke={laneColor(lane.lane)}
                stroke-width="1.5"
              />
            {/if}
          </svg>

          <span class="message">
<!-- Kit `Chip`s, tones named for what they mean: a tag keeps the
                 shell's amber, and everything else, the branch you are ON
                 included, stays quiet so the subject is what you read. -->
            {#each commit.refs.headLabels as label (label)}
              <Chip tone="neutral" class="max-w-[220px] truncate">{label}</Chip>
            {/each}
            {#each branchChips(commit.refs) as label (label)}
              <Chip tone="neutral" class="max-w-[220px] truncate">{label}</Chip>
            {/each}
            {#each commit.refs.remoteLabels as label (label)}
              <Chip tone="count" class="max-w-[220px] truncate">{label}</Chip>
            {/each}
            {#each commit.refs.tagLabels as label (label)}
              <Chip tone="attention" class="max-w-[220px] truncate">{label}</Chip>
            {/each}
            <span class="subject">{commit.subject || '(no message)'}</span>
          </span>
          <span class="date right" title={whenCommitted(commit.committedAt)}>
            {committedOn(commit.committedAt)}
          </span>
          <span class="author">{commit.author}</span>
          <span class="hash">{commit.shortSha}</span>
        </button>

        {#if expanded}
          <div class="files" style="padding-left: {graphWidth + 8}px">
            {#if sentence}
              <p class="files-note">{sentence}</p>
            {:else}
              <p class="files-note">{summarizeCommitFiles(entry.files)} in this commit</p>
              {#each entry.files as file (file.relativePath)}
                {@const parts = splitRepositoryPath(file.relativePath)}
                {@const unreadable = isUnreadableGitPath(file.relativePath)}
                {@const chosen =
                  historyInteraction.selectedCommitSha === commit.sha &&
                  historyInteraction.selectedRelativePath === file.relativePath}
                <button
                  type="button"
                  class="file-row"
                  class:chosen
                  class:unreadable
                  title={unreadable
                    ? 'This file has a name git could not print in plain letters.'
                    : `${file.relativePath} — ${file.status}`}
                  onclick={() => void pickFile(commit.sha, file)}
                  oncontextmenu={(event) => openCommitFileMenu(commit.sha, file, event)}
                >
                  <span class="file-name">{parts.name}</span>
                  {#if parts.folder}<span class="file-folder">{parts.folder}</span>{/if}
                  <span class="file-badge" style="color: {badgeTone(file.badge)}">
                    {file.badge || '·'}
                  </span>
                </button>
              {/each}
            {/if}
          </div>
        {/if}
      {/each}

      <div class="history-footer">
        <p class="notice quiet">
          {#if rows.length === 0}
            No commit in the history that is loaded matches the current filters.
          {:else if filter.branch && !filter.author && !filter.search.trim()}
            Showing {rows.length} commits from {filter.branch} among {commits.length} loaded.
          {:else if filtering}
            {rows.length} of {commits.length} loaded commits match the current filters.
          {:else if historyPanel.historyLoadingMore}
            <WorkingSpinner size={12} /> Reading older commits…
          {:else}
            {footer}
          {/if}
        </p>
        <Button
          variant="ghost"
          size="sm"
          disabled={!canLoadMore}
          onclick={() => void loadOlderHistory()}
        >
          {#if historyPanel.historyLoadingMore}<WorkingSpinner size={12} />Reading older commits…{:else}{canLoadMore
              ? `Load ${COMMIT_HISTORY_LIMIT} older commits`
              : 'All commits loaded'}{/if}
        </Button>
      </div>
    {/if}
  </ScrollArea>

  {#if contextMenu}
    <SourceControlContextMenu
      anchor={contextMenu.anchor}
      items={contextMenu.items}
      onSelect={(action) => void runContextAction(action)}
      onClose={() => (contextMenu = null)}
    />
  {/if}
  {/if}
</section>

<style>
  /* Dark, dense and aligned: the point of giving the history the middle of the
     shell is that Message, Date, Author and Hash each get a column that lines up
     all the way down, which a side panel can never do. Every size below is in
     pixels — the shell's root font is 14px and rem-based sizing in here has
     produced 11px text before. */
  .git-history {
    display: flex;
    flex-direction: column;
    min-height: 0;
    height: 100%;
    overflow: hidden;
    background: var(--color-bg);
    color: var(--color-text);
  }

  .toolbar {
    display: flex;
    flex: 0 0 auto;
    align-items: center;
    gap: 8px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface);
    /* Clear of the floating centre-pane pills, which are laid over this pane. */
    padding-top: calc(var(--center-head-height, 0px) + 6px);
  }

  .toolbar-count {
    font-size: 12px;
    line-height: 16px;
    color: var(--color-text-3);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .history-path {
    max-width: 260px;
    overflow: hidden;
    padding: 3px 7px;
    border: 1px solid var(--color-border);
    border-radius: 5px;
    color: var(--color-text-2);
    font-size: 12px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .filter-label {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* One grid, one set of column widths, so every row lines up with the head. */
  .row {
    display: grid;
    grid-template-columns: var(--graph-width, 40px) minmax(0, 1fr) 156px 140px 78px;
    align-items: center;
    column-gap: 10px;
    width: 100%;
    padding: 0 10px 0 0;
    text-align: left;
    /* Hundreds of these can be loaded, each carrying an SVG graph cell and a
       handful of chips. Skipping layout and paint for the ones off screen is
       what keeps a deep history from taxing the webview; 24px is ROW_HEIGHT,
       so the scrollbar's arithmetic does not change. */
    content-visibility: auto;
    contain-intrinsic-size: auto 24px;
  }

  .head-row {
    position: sticky;
    top: 0;
    z-index: 1;
    height: 24px;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface);
    font-size: 12px;
    line-height: 16px;
    font-weight: 600;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    color: var(--color-text-3);
  }

  .right {
    text-align: right;
  }

  .commit-row {
    height: 24px;
    border: none;
    background: transparent;
    color: inherit;
    cursor: pointer;
  }

  .commit-row:hover {
    background: var(--color-hover);
  }

  .commit-row.open {
    background: var(--color-selected);
  }

  .commit-row:focus-visible {
    outline: 2px solid var(--color-accent);
    outline-offset: -2px;
  }

  .graph {
    display: block;
    overflow: visible;
  }

  .message {
    display: flex;
    align-items: center;
    gap: 5px;
    min-width: 0;
    overflow: hidden;
  }

  .subject {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 13px;
    line-height: 17px;
    color: var(--color-text);
  }

  .uncommitted-subject {
    font-style: italic;
    color: var(--color-text-2);
  }

  .date,
  .author,
  .hash {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 12px;
    line-height: 16px;
    color: var(--color-text-2);
  }

  .date {
    font-variant-numeric: tabular-nums;
  }

  /* Muted, because a hash is what you copy, not what you read. */
  .hash {
    font-variant-numeric: tabular-nums;
    font-family: var(--font-mono, ui-monospace, monospace);
    color: var(--color-text-3);
  }

  .files {
    display: flex;
    flex-direction: column;
    padding-bottom: 4px;
    background: color-mix(in srgb, var(--color-elevated) 40%, var(--color-surface));
    border-bottom: 1px solid var(--color-border);
  }

  .files-note {
    padding: 3px 10px 3px 0;
    font-size: 12px;
    line-height: 16px;
    color: var(--color-text-3);
  }

  .file-row {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    padding: 2px 10px 2px 4px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text);
    text-align: left;
    cursor: pointer;
  }

  .file-row:hover {
    background: var(--color-hover);
  }

  .file-row.chosen {
    background: var(--color-selected);
  }

  .file-row.unreadable {
    opacity: 0.7;
  }

  .file-name {
    flex: 0 0 auto;
    font-size: 13px;
    line-height: 17px;
  }

  .file-folder {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 12px;
    line-height: 16px;
    color: var(--color-text-3);
  }

  .file-badge {
    margin-left: auto;
    flex: 0 0 auto;
    width: 12px;
    text-align: center;
    font-size: 12px;
    line-height: 16px;
    font-weight: 500;
  }

  .history-footer {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 10px 12px;
  }

  .history-footer .notice {
    min-width: 0;
    padding: 0;
  }

  .notice {
    padding: 12px 10px;
    font-size: 12px;
    line-height: 16px;
    color: var(--color-text-2);
  }

  .notice.quiet {
    color: var(--color-text-3);
  }

  .notice.bad {
    color: var(--color-bad);
  }
</style>
