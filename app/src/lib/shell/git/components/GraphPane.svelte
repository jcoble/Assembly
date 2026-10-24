<script lang="ts">
  /**
   * GraphPane.svelte — the commit history, drawn as the branching picture it
   * actually is.
   *
   * Three pieces do the work, and none of them is in this file:
   *
   *  - `gitGraphLanes.ts` says which column each commit sits in and which line
   *    segments belong in its row. This file turns those numbers into an SVG.
   *  - `gitGraphViewModel.ts` (already built and tested, previously used only by
   *    the old shell) turns a commit into the branch and tag names to show, the
   *    task it belongs to, and what kind of commit it is.
   *  - `gitCommitFilesService.ts` reads what a commit changed when its row is
   *    opened, and puts a file's changes on screen when one is clicked.
   *
   * A row opens IN PLACE rather than replacing the list, so the line of history
   * either side of it stays visible — which is why the columns carry on being
   * drawn down the left of an open row's file list.
   *
   * The collapsible's content is hidden with the `hidden` attribute, which any
   * display rule of ours would beat, so the layout classes live on the div
   * inside it and never on the content itself.
   *
   * HOW MUCH HISTORY IS HERE. The list starts at the newest 24 commits and
   * grows on request. The count beside the heading says "so far" until a read
   * comes back short, and the line under the list says what actually came back
   * — never "that is all of them" — because an app build that reads fewer
   * commits than this panel asks for looks exactly like a repository that has
   * run out of history, and the two must not be confused.
   */
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { onMount } from 'svelte';

  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import * as Collapsible from '$lib/components/ui/collapsible/index.js';
  import { buildGitCommitGraphRows } from '$lib/shell/git/gitGraphViewModel';
  import {
    assignGitGraphLanes,
    gitGraphLaneWidth,
    gitGraphLaneX,
    gitGraphRowLanesBelow,
    type GitGraphLaneRow
  } from '$lib/shell/git/gitGraphLanes';
  import {
    commitFilesEntry,
    describeCommitFiles,
    gitCommitFilesView,
    isCommitExpanded,
    isUnreadableGitPath,
    splitRepositoryPath,
    summarizeCommitFiles,
    type GitCommitFilesState
  } from '$lib/shell/git/gitCommitFilesStore.svelte';
  import type { GitCommitFilesService } from '$lib/shell/git/gitCommitFilesService';
  import {
    canLoadMoreGitHistory,
    describeGitHistoryCount,
    describeGitHistoryFooter,
    type GitPanelState
  } from '$lib/shell/git/gitPanelStore.svelte';
  import { COMMIT_HISTORY_LIMIT, type GitService } from '$lib/shell/git/gitService';
  import { formatLastActivity } from '$lib/shell/relativeTime';
  import { cn } from '$lib/utils';
  import type { GitCommitFileChange } from '$lib/shell/git/gitBackendExtra';

  interface Props {
    panel: GitPanelState;
    service: GitService;
    commitFiles: GitCommitFilesService;
    /** The commit-file state the service writes, read here for the open rows. */
    files: GitCommitFilesState;
    /** A file inside a commit was picked, so the diff should come to the front. */
    onShowDiff?: () => void;
  }
  let { panel, service, commitFiles, files, onShowDiff }: Props = $props();

  let open = $state(true);
  let requestedRoot = '';
  const interaction = $derived(gitCommitFilesView(files));

  onMount(() => {
    service.ensureHistorySurface();
  });

  $effect(() => {
    const targetRoot = panel.root?.trim() ?? '';
    if (!targetRoot || targetRoot === requestedRoot) return;
    requestedRoot = targetRoot;
    service.ensureHistorySurface();
  });

  /** One row of the graph is exactly this tall, so the SVG can be drawn to size. */
  const ROW_HEIGHT = 40;
  /** Distance between two columns, and the gap either side of them. */
  const LANE_SPACING = 12;
  const LANE_OFFSET = 9;
  /** Past this the graph would eat the panel; extra columns fold into the last one. */
  const MAX_DRAWN_LANES = 6;

  /**
   * A colour per column, from the shell's own tokens, so a theme change carries
   * the graph with it. Six is enough to tell neighbouring branches apart; the
   * seventh column starts the list again.
   */
  const LANE_COLORS = [
    'var(--color-accent)',
    'var(--color-good)',
    'var(--color-attention)',
    'var(--color-bad)',
    'var(--color-live)',
    'var(--color-text-2)'
  ];

  const commits = $derived(buildGitCommitGraphRows(panel.history));
  // The lane assignment reads every commit's parents, so it is always handed the
  // whole accumulated list — which is why loading more replaces the list rather
  // than appending a page to it.
  const layout = $derived(assignGitGraphLanes(panel.history));
  const historyCount = $derived(describeGitHistoryCount(panel));
  const historyFooter = $derived(describeGitHistoryFooter(panel));
  const canLoadMore = $derived(canLoadMoreGitHistory(panel));
  const drawnLanes = $derived(Math.min(layout.laneCount, MAX_DRAWN_LANES));
  const graphWidth = $derived(
    Math.max(gitGraphLaneWidth(drawnLanes, LANE_SPACING, LANE_OFFSET), LANE_OFFSET * 2)
  );

  function laneColor(lane: number): string {
    return LANE_COLORS[lane % LANE_COLORS.length];
  }

  /** Columns past the drawn width are pinned to the last one rather than cut off. */
  function laneX(lane: number): number {
    return gitGraphLaneX(Math.min(lane, drawnLanes - 1), LANE_SPACING, LANE_OFFSET);
  }

  /** One line segment of a row, as an SVG path. */
  function edgePath(
    kind: 'child' | 'parent' | 'passing',
    fromLane: number,
    toLane: number
  ): string {
    const x1 = laneX(fromLane);
    const x2 = laneX(toLane);
    const middle = ROW_HEIGHT / 2;

    if (kind === 'passing') return `M ${x1} 0 L ${x1} ${ROW_HEIGHT}`;
    if (kind === 'child') return `M ${x1} 0 L ${x2} ${middle}`;
    if (x1 === x2) return `M ${x1} ${middle} L ${x1} ${ROW_HEIGHT}`;
    // A line changing column bends rather than cutting the corner, so which two
    // columns it joins stays readable when several do it at once.
    return `M ${x1} ${middle} C ${x1} ${middle + 10}, ${x2} ${ROW_HEIGHT - 12}, ${x2} ${ROW_HEIGHT}`;
  }

  function whenCommitted(committedAt: string): string {
    return formatLastActivity(committedAt, new Date());
  }

  function toggleCommit(row: GitGraphLaneRow): void {
    commitFiles.activate(panel.root);
    void commitFiles.toggleCommit(row.sha, row.isMerge);
  }

  async function pickFile(sha: string, file: GitCommitFileChange): Promise<void> {
    commitFiles.activate(panel.root);
    const read = commitFiles.selectCommitFile(sha, file);
    onShowDiff?.();
    await read;
  }

  /**
   * Branch names worth a pill of their own. The checked-out branch already
   * appears inside its `HEAD -> …` pill, so showing it twice would just eat the
   * room the commit's subject needs.
   */
  function branchPills(refs: { headLabels: string[]; branchLabels: string[] }): string[] {
    return refs.branchLabels.filter(
      (label) => !refs.headLabels.some((head) => head.includes(label))
    );
  }

  function badgeTone(badge: string): string {
    if (badge === 'A') return 'text-[var(--color-good)]';
    if (badge === 'D') return 'text-[var(--color-bad)]';
    if (badge === 'R' || badge === 'C') return 'text-[var(--color-accent)]';
    return 'text-[var(--color-attention)]';
  }

  /** A branch or tag name. Pills shrink and cut their text short, and the
   * subject keeps at least half the row, so a commit with several branch names
   * still shows what it says. */
  const PILL =
    'min-w-[3em] max-w-[45%] truncate rounded-[4px] px-1 text-[12px] leading-[16px]';
</script>

<section
  class={cn('flex min-h-0 flex-col border-t border-[var(--color-border)]', open ? 'flex-1' : 'shrink-0')}
>
  <div class="flex shrink-0 items-center gap-1 px-2 py-1">
    <button
      type="button"
      class="flex min-w-0 flex-1 items-center gap-1 text-left text-[12px] tracking-[0.06em]
             text-[var(--color-text-2)] uppercase transition-colors
             hover:text-[var(--color-text)] focus-visible:ring-3 focus-visible:ring-ring/50
             outline-none"
      aria-expanded={open}
      onclick={() => (open = !open)}
    >
      {#if open}
        <ChevronDown class="size-3 shrink-0" aria-hidden="true" />
      {:else}
        <ChevronRight class="size-3 shrink-0" aria-hidden="true" />
      {/if}
      <span>Commits</span>
      <span class="ml-auto normal-case text-[var(--color-text-3)]" title={historyFooter}>
        {historyCount}
      </span>
    </button>
    <button
      type="button"
      class="flex size-5 shrink-0 items-center justify-center rounded-[4px]
             text-[var(--color-text-2)] transition-colors hover:bg-[var(--color-elevated)]
             hover:text-[var(--color-text)] focus-visible:ring-3 focus-visible:ring-ring/50
             outline-none disabled:opacity-45"
      aria-label="Read the commit history again"
      title="Read the commit history again"
      disabled={!panel.activated || panel.historyLoading}
      onclick={() => void service.refreshHistory()}
    >
      {#if panel.historyLoading}<WorkingSpinner size={12} />{:else}<RefreshCw class="size-3" aria-hidden="true" />{/if}
    </button>
  </div>

  {#if open}
    <div class="flex min-h-0 flex-1 flex-col overflow-y-auto pb-2">
      {#if panel.historyError && commits.length === 0}
        <p class="px-2 py-1 text-[13px] leading-[18px] text-[var(--color-bad)]">
          {panel.historyError}
        </p>
      {:else if panel.historyLoading && commits.length === 0}
        <p class="px-2 py-1 text-[13px] leading-[18px] text-[var(--color-text-2)]">
          <WorkingSpinner size={12} /> Reading the commit history…
        </p>
      {:else if commits.length === 0}
        <p class="px-2 py-1 text-[13px] leading-[18px] text-[var(--color-text-2)]">
          No commits yet.
        </p>
      {:else}
        {#each commits as commit, index (commit.sha)}
          {@const row = layout.rows[index]}
          {@const entry = commitFilesEntry(files, commit.sha)}
          {@const expanded = isCommitExpanded(files, commit.sha)}
          {@const sentence = describeCommitFiles(entry, row?.isMerge ?? false)}
          <Collapsible.Root
            open={expanded}
            onOpenChange={() => row && toggleCommit(row)}
            class="border-b border-[var(--color-border)]/45 last:border-b-0"
          >
            <Collapsible.Trigger
              class={cn(
                'flex w-full items-stretch gap-1.5 pr-2 text-left transition-colors',
                'hover:bg-[var(--color-elevated)] focus-visible:ring-3 focus-visible:ring-ring/50',
                'outline-none',
                expanded && 'bg-[var(--color-elevated)]'
              )}
              title={commit.detailLabel}
            >
              <svg
                class="shrink-0"
                width={graphWidth}
                height={ROW_HEIGHT}
                viewBox="0 0 {graphWidth} {ROW_HEIGHT}"
                aria-hidden="true"
              >
                {#if row}
                  {#each row.edges as edge, edgeIndex (edgeIndex)}
                    <path
                      d={edgePath(edge.kind, edge.fromLane, edge.toLane)}
                      fill="none"
                      stroke={laneColor(edge.kind === 'child' ? edge.toLane : edge.fromLane)}
                      stroke-width="1.5"
                      stroke-linecap="round"
                      opacity="0.75"
                    />
                  {/each}
                  <circle
                    cx={laneX(row.lane)}
                    cy={ROW_HEIGHT / 2}
                    r={row.isMerge ? 3 : 3.5}
                    fill={row.isMerge ? 'var(--color-bg)' : laneColor(row.lane)}
                    stroke={laneColor(row.lane)}
                    stroke-width="1.5"
                  />
                {/if}
              </svg>

              <span class="flex min-w-0 flex-1 flex-col justify-center py-[3px]">
                <span class="flex min-w-0 items-center gap-1">
                  {#each commit.refs.headLabels as label (label)}
                    <span class="{PILL} bg-[var(--color-live-bg)] text-[var(--color-live)]">
                      {label}
                    </span>
                  {/each}
                  {#each branchPills(commit.refs) as label (label)}
                    <span class="{PILL} bg-[var(--color-elevated)] text-[var(--color-text-2)]">
                      {label}
                    </span>
                  {/each}
                  {#each commit.refs.tagLabels as label (label)}
                    <span class="{PILL} bg-[var(--color-attention-bg)] text-[var(--color-attention)]">
                      {label}
                    </span>
                  {/each}
                  <span class="min-w-[50%] truncate text-[13px] leading-[17px]">
                    {commit.subject || '(no message)'}
                  </span>
                </span>
                <span class="truncate text-[12px] leading-[15px] text-[var(--color-text-3)]">
                  {commit.shortSha} · {commit.author} · {whenCommitted(commit.committedAt)}
                  {#if row?.isMerge}· merge{/if}
                </span>
              </span>
            </Collapsible.Trigger>

            <!-- Layout classes go on the div INSIDE: a closed collapsible is
                 hidden by the `hidden` attribute, which any display rule of
                 ours would beat, and the list would never close. -->
            <Collapsible.Content>
              <div class="relative flex flex-col pb-1">
                <!-- The columns carry on behind an open row, so the history
                     either side of it still reads as one picture. -->
                {#if row}
                  {#each gitGraphRowLanesBelow(row) as lane (lane)}
                    <span
                      class="pointer-events-none absolute top-0 bottom-0 w-px opacity-75"
                      style="left: {laneX(lane) - 0.5}px; background: {laneColor(lane)}"
                      aria-hidden="true"
                    ></span>
                  {/each}
                {/if}

                <div class="flex flex-col" style="padding-left: {graphWidth + 6}px">
                  {#if sentence}
                    <p class="py-1 pr-2 text-[12px] leading-[16px] text-[var(--color-text-2)]">
                      {sentence}
                    </p>
                  {:else}
                    <p class="py-0.5 pr-2 text-[12px] leading-[16px] text-[var(--color-text-3)]">
                      {summarizeCommitFiles(entry.files)} in this commit
                    </p>
                    {#each entry.files as file (file.relativePath)}
                      {@const parts = splitRepositoryPath(file.relativePath)}
                      {@const unreadable = isUnreadableGitPath(file.relativePath)}
                      {@const chosen =
                        interaction.selectedCommitSha === commit.sha &&
                        interaction.selectedRelativePath === file.relativePath}
                      <button
                        type="button"
                        class={cn(
                          'flex w-full items-center gap-1.5 rounded-[4px] py-[3px] pr-1.5 pl-1',
                          'text-left transition-colors hover:bg-[var(--color-elevated)]',
                          'focus-visible:ring-3 focus-visible:ring-ring/50 outline-none',
                          chosen && 'bg-[var(--color-elevated)]',
                          unreadable && 'opacity-70'
                        )}
                        title={unreadable
                          ? 'This file has a name git could not print in plain letters.'
                          : `${file.relativePath} — ${file.status}`}
                        onclick={() => pickFile(commit.sha, file)}
                      >
                        <span class="shrink-0 truncate text-[13px] leading-[17px]">
                          {parts.name}
                        </span>
                        {#if parts.folder}
                          <span
                            class="min-w-0 truncate text-[12px] leading-[16px] text-[var(--color-text-3)]"
                          >
                            {parts.folder}
                          </span>
                        {/if}
                        <span
                          class={cn(
                            'ml-auto w-3 shrink-0 text-center text-[12px] leading-[16px] font-medium',
                            badgeTone(file.badge)
                          )}
                        >
                          {file.badge || '·'}
                        </span>
                      </button>
                    {/each}
                  {/if}
                </div>
              </div>
            </Collapsible.Content>
          </Collapsible.Root>
        {/each}

        <!-- How much of the history is on screen, and how to get more of it.
             Inside the scrolling list on purpose: it belongs to the end of the
             list, the way the bottom of a page belongs to the page. -->
        <div class="flex shrink-0 flex-col gap-1 px-2 pt-1.5">
          {#if panel.historyError}
            <p class="text-[12px] leading-[16px] text-[var(--color-bad)]">
              {panel.historyError}
            </p>
          {/if}
          <p class="text-[12px] leading-[16px] text-[var(--color-text-2)]">
            {historyFooter}
          </p>
          {#if canLoadMore || panel.historyLoadingMore}
            <button
              type="button"
              class={cn(
                buttonVariants({ variant: 'secondary', size: 'xs' }),
                'w-full text-[12px] font-normal'
              )}
              disabled={!canLoadMore}
              title="Read another {COMMIT_HISTORY_LIMIT} commits further back in this branch's history"
              onclick={() => void service.loadMoreHistory()}
            >
              {#if panel.historyLoadingMore}<WorkingSpinner size={12} /> Reading older commits…{:else}Load {COMMIT_HISTORY_LIMIT} more{/if}
            </button>
          {/if}
        </div>
      {/if}
    </div>
  {/if}
</section>
