<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * CommitTimeline.svelte — the commits under the changed files.
   *
   * One row per commit: where it sits (the branch, tag or HEAD names pointing at
   * it), its subject, who wrote it, and when. Opening a row reads what that
   * commit changed and lists it; picking one of those files shows its changes in
   * the middle of the shell. Hovering the subject shows it in full, because a
   * narrow panel cuts most subjects off and two commits cut off at the same word
   * are impossible to tell apart.
   *
   * The ref names are drawn on the row rather than left in the hover card: which
   * commit a branch is on is the question a history is read for, and an answer
   * that needs a hover is an answer nobody sees.
   *
   * WHAT IS SHOWN IS WHAT THE APP RETURNS. The commit list carries a subject, an
   * author, a date and the commit id — not the message body — so the hover card
   * shows those and never pads them out with anything it did not read.
   *
   * No IO of its own: reading a commit's files goes through
   * `gitCommitFilesService`, reading more commits through `gitService`, and both
   * only ever from a click.
   */
  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';
  import FileIcon from '$lib/shell/components/explorer/FileIcon.svelte';
  import SourceControlContextMenu from '$lib/shell/git/components/SourceControlContextMenu.svelte';
  import {
    snapshotSourceControlCommitFileMenu,
    snapshotSourceControlCommitMenu,
    sourceControlContextMenuAnchor,
    type SourceControlContextMenuAction,
    type SourceControlMenuSnapshot
  } from '$lib/shell/git/components/sourceControlContextMenu';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import { gitCommitRefSummary } from '$lib/shell/git/gitGraphViewModel';
  import type { GitCommitFileChange } from '$lib/shell/git/gitBackendExtra';
  import type { GitCommitFilesService } from '$lib/shell/git/gitCommitFilesService';
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
  import {
    canLoadMoreGitHistory,
    describeGitHistoryCount,
    describeGitHistoryFooter,
    type GitPanelState
  } from '$lib/shell/git/gitPanelStore.svelte';
  import { absolutePathWithin, type GitService } from '$lib/shell/git/gitService';
  import { nativeWorkspacePath } from '$lib/workspacePaths.ts';
  import { formatLastActivity } from '$lib/shell/relativeTime';
  import { openFileInEditor, showCenterTab } from '$lib/shell/workbenchNavigation';

  interface Props {
    panel: GitPanelState;
    service: GitService;
    commitFiles: GitCommitFilesService;
    commitFilesState: GitCommitFilesState;
    /** Files open read-only: another session is working in this folder. */
    readOnly?: boolean;
  }
  let { panel, service, commitFiles, commitFilesState, readOnly = false }: Props = $props();
  let contextMenu = $state.raw<SourceControlMenuSnapshot | null>(null);
  const interaction = $derived(gitCommitFilesView(commitFilesState, 'compact'));

  const countLabel = $derived(describeGitHistoryCount(panel));
  const footer = $derived(describeGitHistoryFooter(panel));
  const canLoadMore = $derived(canLoadMoreGitHistory(panel));

  function whenCommitted(committedAt: string): string {
    return formatLastActivity(committedAt, new Date());
  }

  /**
   * The names pointing at one commit, as short pills. A branch that HEAD is on
   * is already spelled out in the HEAD label, so it is not repeated beside it.
   *
   * `HEAD -> name` is drawn as just the name: the panel's title bar already says
   * which branch this is, and in a column this narrow the four extra characters
   * come straight out of the commit's subject.
   */
  function refPills(refs: string): { label: string; tone: 'good' | 'attention' | 'neutral' }[] {
    const summary = gitCommitRefSummary(refs);
    const pills: { label: string; tone: 'good' | 'attention' | 'neutral' }[] =
      summary.headLabels.map((label) => ({
        label: label.replace(/^HEAD\s*->\s*/, ''),
        tone: 'good' as const
      }));
    for (const label of summary.branchLabels) {
      if (summary.headLabels.some((head) => head.includes(label))) continue;
      pills.push({ label, tone: 'neutral' as const });
    }
    for (const label of summary.tagLabels) {
      pills.push({ label: label.replace(/^tag:\s*/, ''), tone: 'attention' as const });
    }
    return pills;
  }

  async function toggle(sha: string, parentCount: number): Promise<void> {
    commitFiles.activate(panel.root);
    await commitFiles.toggleCommit(sha, parentCount > 1);
  }

  async function pickFile(sha: string, file: GitCommitFileChange): Promise<void> {
    commitFiles.activate(panel.root);
    const read = commitFiles.selectCommitFile(sha, file);
    showCenterTab('diff');
    await read;
  }

  function openCommitMenu(
    sha: string,
    parentCount: number,
    expanded: boolean,
    event: MouseEvent
  ): void {
    event.preventDefault();
    contextMenu = snapshotSourceControlCommitMenu({
      sha,
      isMerge: parentCount > 1,
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
      if (action === 'toggle-commit') await toggle(menu.target.sha, menu.target.isMerge ? 2 : 1);
      else if (action === 'copy-hash') await copyText(menu.target.sha);
      return;
    }

    if (action === 'open-commit-diff') await pickFile(menu.target.sha, menu.target.file);
    else if (action === 'open-current-file' && panel.root) {
      openFileInEditor({
        path: absolutePathWithin(panel.root, menu.target.file.relativePath),
        projectRoot: panel.root,
        readOnly
      });
    } else if (action === 'copy-commit-path') {
      await copyText(nativeWorkspacePath(
        panel.root
          ? absolutePathWithin(panel.root, menu.target.file.relativePath)
          : menu.target.file.relativePath
      ));
    }
  }

  /** Everything the app told us about this commit, one fact per line. */
  function commitDetail(commit: {
    subject: string;
    author: string;
    committedAt: string;
    shortSha: string;
    refs: string;
  }): string[] {
    return [
      commit.subject,
      `${commit.author} · ${whenCommitted(commit.committedAt)}`,
      commit.refs.trim() === '' ? `Commit ${commit.shortSha}` : `Commit ${commit.shortSha} · ${commit.refs}`
    ];
  }
</script>

<section class="flex min-h-0 flex-col gap-1 px-1 pb-2" aria-label="Commits">
  <div class="flex items-center gap-2 px-2 pt-2">
    <h3 class="text-sm font-medium tracking-wide text-muted-foreground uppercase">Commits</h3>
    {#if countLabel !== ''}
      <Chip tone="count">{countLabel}</Chip>
    {/if}
    <span class="ml-auto">
      <IconButton
        label="Read the commit history again"
        size="sm"
        side="left"
        disabled={panel.historyLoading || panel.historyLoadingMore}
        data-testid="source-control-refresh-history"
        onclick={() => void service.refreshHistory()}
      >
        <RefreshCw class="size-3.5" aria-hidden="true" />
      </IconButton>
    </span>
  </div>

  {#if panel.historyLoading}
    <p class="px-2 py-1 text-sm text-muted-foreground"><WorkingSpinner size={14} /> Reading the history…</p>
  {:else if panel.historyError !== ''}
    <p class="px-2 py-1 text-sm text-[var(--color-bad)]">{panel.historyError}</p>
  {:else if panel.history.length === 0}
    <p class="px-2 py-1 text-sm text-muted-foreground">This repository has no commits yet.</p>
  {:else}
    <!-- An ordered list, but not a numbered one: the browser's own markers and
         their 40px indent were pushing every commit row off to the right. -->
    <ol class="flex min-w-0 list-none flex-col p-0">
      {#each panel.history as commit (commit.sha)}
        {@const expanded = isCommitExpanded(commitFilesState, commit.sha)}
        {@const entry = commitFilesEntry(commitFilesState, commit.sha)}
        {@const sentence = describeCommitFiles(entry, commit.parentCount > 1)}
        <li class="commit-row min-w-0">
          <Tooltip.Root delayDuration={0}>
            <Tooltip.Trigger>
              {#snippet child({ props })}
                <div {...props} class="min-w-0">
                  <ListRow
                    onclick={() => void toggle(commit.sha, commit.parentCount)}
                    oncontextmenu={(event) =>
                      openCommitMenu(commit.sha, commit.parentCount, expanded, event)}
                    data-testid={`source-control-commit-${commit.shortSha}`}
                  >
                    <ChevronRight
                      class={`chevron size-3.5 shrink-0 text-muted-foreground ${
                        expanded ? 'is-open' : ''
                      }`}
                      aria-hidden="true"
                    />
                    {#each refPills(commit.refs) as pill (pill.label)}
                      <Chip tone={pill.tone} class="max-w-[40%] overflow-hidden text-ellipsis">
                        {pill.label}
                      </Chip>
                    {/each}
                    <span class="min-w-0 flex-1 truncate">{commit.subject}</span>
                    <span class="shrink-0 text-sm text-muted-foreground">
                      {commit.author} · {whenCommitted(commit.committedAt)}
                    </span>
                  </ListRow>
                </div>
              {/snippet}
            </Tooltip.Trigger>
            <Tooltip.Content side="left" sideOffset={6} class="max-w-[320px]">
              <span class="flex flex-col gap-1 text-left">
                {#each commitDetail(commit) as line, index (index)}
                  <span class={index === 0 ? 'text-[13px] leading-snug' : 'text-sm text-muted-foreground'}>
                    {line}
                  </span>
                {/each}
              </span>
            </Tooltip.Content>
          </Tooltip.Root>

          {#if expanded}
            <div class="flex flex-col pb-1 pl-5">
              {#if sentence !== ''}
                <p class="px-2 py-1 text-sm text-muted-foreground">{sentence}</p>
              {:else}
                <p class="px-2 py-1 text-sm text-muted-foreground">
                  {summarizeCommitFiles(entry.files)}
                </p>
                {#each entry.files as file (file.relativePath)}
                  {@const parts = splitRepositoryPath(file.relativePath)}
                  <ListRow
                    onclick={() => void pickFile(commit.sha, file)}
                    oncontextmenu={(event) => openCommitFileMenu(commit.sha, file, event)}
                    selected={interaction.selectedCommitSha === commit.sha &&
                      interaction.selectedRelativePath === file.relativePath}
                    data-testid={`source-control-commit-file-${file.relativePath}`}
                  >
                    <span
                      class="w-3 shrink-0 text-center font-mono text-sm leading-none text-muted-foreground"
                      aria-hidden="true">{file.badge}</span
                    >
                    <FileIcon fileName={parts.name} size={14} />
                    <span class="min-w-0 flex-1 truncate" title={file.relativePath}>
                      {parts.name}
                      {#if parts.folder !== ''}
                        <span class="text-sm text-muted-foreground">{parts.folder}</span>
                      {/if}
                    </span>
                  </ListRow>
                {/each}
              {/if}
            </div>
          {/if}
        </li>
      {/each}
    </ol>

    {#if footer !== ''}
      <p class="px-2 pt-1 text-sm text-muted-foreground">{footer}</p>
    {/if}

    {#if canLoadMore}
      <div class="px-2 pt-1">
        <Button
          variant="ghost"
          size="sm"
          onclick={() => void service.loadMoreHistory()}
          disabled={panel.historyLoadingMore}
        >
          {#if panel.historyLoadingMore}<WorkingSpinner size={12} />Reading older commits…{:else}Load more{/if}
        </Button>
      </div>
    {/if}
  {/if}

  {#if contextMenu}
    <SourceControlContextMenu
      anchor={contextMenu.anchor}
      items={contextMenu.items}
      onSelect={(action) => void runContextAction(action)}
      onClose={() => (contextMenu = null)}
    />
  {/if}
</section>

<style>
  /* "Load more" appends another page of commits and never drops the ones
     already there, so this list only grows. Skipping layout and paint for the
     rows off screen is what keeps a deep history cheap. 28px is the collapsed
     row height (ListRow's min-h-7); `auto` means the browser uses the real
     height it last measured, so an opened row with its files listed still
     reserves the right space. */
  .commit-row {
    content-visibility: auto;
    contain-intrinsic-size: auto 28px;
  }

  /* The chevron turns to point down while its commit is open, and stops there. */
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
