<script lang="ts">
  /**
   * GitDiffView.svelte — the Changes tab.
   *
   * Shows every uncommitted change in the session's working copy — or, in
   * the Branch scope, everything since the branch left the default branch —
   * file under file, with the changed-file tree beside it (`MultiFileDiff`). A file
   * picked in Source Control is the same view, scrolled to that file. A file
   * opened from a commit or a pull request is shown on its own.
   *
   * Reads go through `gitService`; the tab is only in the page while it is the
   * tab in front, so everything read here is let go of when it is not.
   */
  import { onDestroy, untrack } from 'svelte';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { SegmentedControl } from '$lib/components/ui/segmented-control/index.js';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import MultiFileDiff from '$lib/shell/git/components/MultiFileDiff.svelte';
  import { diffTextOf, parsedDiffOf, splitDiffByFile } from '$lib/shell/git/diffRows';
  import { readBranchDiff } from '$lib/shell/git/gitBackendExtra';
  import { gitCommitFiles, gitCommitFilesView } from '$lib/shell/git/gitCommitFilesStore.svelte';
  import { gitPanel } from '$lib/shell/git/gitPanelStore.svelte';
  import { gitService } from '$lib/shell/git/gitService';
  import { requestOpenFile } from '$lib/shell/openFileBus';
  import { isDiffMode, type DiffMode } from '$lib/shell/sessionWorkspaces';
  import { canonicalPath } from '$lib/shell/files/explorer/explorerStore.svelte';
  import { sessionWorkingIn } from '$lib/shell/projects/projects.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  import type { ProjectGitStatus, SourceGitDiff } from '$lib/tauriSource';

  interface Props {
    rootAvailable?: boolean;
    /** The session's folder, used when Source Control has not pointed the panel anywhere yet. */
    sessionRoot?: string;
    mode?: DiffMode;
    onModeChange?: (mode: DiffMode) => void;
    /** Told the +/- totals this view shows, so the tab title can show the same. */
    onTotals?: (totals: { added: number; removed: number }) => void;
  }
  let { rootAvailable = true, sessionRoot = '', mode = 'unified', onModeChange, onTotals }: Props = $props();

  /** Files read at once. Each read is a few short git processes. */
  const READS_AT_ONCE = 4;
  const MODES = [
    { value: 'unified', label: 'Unified' },
    { value: 'side-by-side', label: 'Side by side' }
  ];

  const commitSha = $derived(
    gitPanel.diffOwner ? gitCommitFilesView(gitCommitFiles, gitPanel.diffOwner).selectedCommitSha : ''
  );
  /** A commit's file is shown on its own, not as part of the working copy. */
  const singleMode = $derived(commitSha !== '');
  const single = $derived<SourceGitDiff | null>(commitSha ? gitPanel.selectedDiff : null);

  /** Uncommitted: the working copy against HEAD. Branch: everything since the
   * merge base with the default branch, committed or not. */
  let scope = $state<'uncommitted' | 'branch'>('uncommitted');
  let branchBase = $state('');
  let branchError = $state('');
  let workingIsBranch = false;

  let working = $state.raw<SourceGitDiff[]>([]);
  let unreadable = $state.raw<string[]>([]);
  let pending = $state(0);
  /** Owns the current round of reads; a new round or closing the tab aborts it. */
  let reads = new AbortController();
  let view = $state<MultiFileDiff | null>(null);

  const files = $derived(singleMode ? (single ? [single] : []) : working);
  const totals = $derived(
    files.reduce(
      (sum, file) => {
        const parsed = parsedDiffOf(file);
        return { added: sum.added + parsed.addedCount, removed: sum.removed + parsed.removedCount };
      },
      { added: 0, removed: 0 }
    )
  );
  $effect(() => onTotals?.(totals));
  const singleLabel = $derived(`Commit ${commitSha.slice(0, 7)}`);

  function put(diff: SourceGitDiff): void {
    working = [...working.filter((file) => file.relativePath !== diff.relativePath), diff];
  }

  /** Read every changed file's diff, a few at a time, the picked one first.
   * Full file texts are dropped once the diff text is taken from them. In the
   * Branch scope one read brings every tracked file; only files git does not
   * track yet are read one by one. */
  async function readAll(status: ProjectGitStatus, branch: boolean): Promise<void> {
    reads.abort();
    reads = new AbortController();
    const { signal } = reads;
    view?.reset();
    let paths = status.files.map((file) => file.relativePath);
    const wanted = new Set(paths);
    working = branch || workingIsBranch ? [] : working.filter((file) => wanted.has(file.relativePath));
    workingIsBranch = branch;
    branchError = '';
    if (branch) {
      pending = 1;
      try {
        const answer = gitPanel.root ? await readBranchDiff(gitPanel.root) : null;
        if (signal.aborted) return;
        if (answer) {
          branchBase = answer.base;
          working = splitDiffByFile(answer.diff);
        } else {
          branchError = 'Comparing with the default branch needs the desktop app.';
        }
      } catch (error) {
        if (signal.aborted) return;
        branchError = error instanceof Error ? error.message : String(error);
      }
      paths = status.files.filter((file) => file.status === 'untracked').map((file) => file.relativePath);
    }
    unreadable = [];
    const queue = paths;
    const first = queue.indexOf(gitPanel.selectedPath);
    if (first > 0) queue.unshift(...queue.splice(first, 1));
    pending = queue.length;

    const worker = async (): Promise<void> => {
      for (let path = queue.shift(); path !== undefined; path = queue.shift()) {
        try {
          const diff = await gitService.readWorkingDiff(path, signal);
          if (signal.aborted) return;
          if (diff) put({ ...diff, diff: diffTextOf(diff), originalContent: null, modifiedContent: null });
          else unreadable = [...unreadable, path];
        } catch {
          if (signal.aborted) return;
          unreadable = [...unreadable, path];
        } finally {
          if (!signal.aborted) pending -= 1;
        }
      }
    };
    await Promise.all(Array.from({ length: READS_AT_ONCE }, worker));
  }

  /** Read one file's current text, so a collapsed run of lines can open. The
   * diff already on screen stays; only the text is added to it. */
  async function loadFullText(path: string): Promise<void> {
    const { signal } = reads;
    try {
      const read = await gitService.readWorkingDiff(path, signal);
      const shown = working.find((file) => file.relativePath === path);
      if (read && shown && !signal.aborted) put({ ...shown, modifiedContent: read.modifiedContent });
    } catch {
      // The collapsed lines stay collapsed; the diff on screen is still right.
    }
  }

  // Closing the tab stops the readers and the reads they are waiting on.
  onDestroy(() => reads.abort());

  // The working copy follows the repository status: a new status (after a
  // commit, a stage, a refresh) means the diffs are read again.
  $effect(() => {
    if (singleMode || !rootAvailable) return;
    const root = gitPanel.root;
    const status = gitPanel.status;
    const branch = scope === 'branch';
    untrack(() => {
      if (!root) {
        if (sessionRoot) gitService.activate(sessionRoot);
      } else if (!status) {
        if (!gitPanel.statusLoading && !gitPanel.statusError && !gitPanel.desktopOnly) void gitService.refreshStatus();
      } else {
        void readAll(status, branch);
      }
    });
  });

  function openAt(relativePath: string, line: number | null): void {
    const root = gitPanel.root;
    if (!rootAvailable || !root) return;
    requestOpenFile({
      path: `${root.replace(/\/+$/, '')}/${relativePath.replace(/^\/+/, '')}`,
      projectRoot: root,
      line: line && line > 0 ? line : undefined,
      // Another project's diff, where another session is working, opens read-only.
      readOnly: canonicalPath(root) !== canonicalPath(sessionRoot) && sessionWorkingIn(root, rail.owned) !== null
    });
  }
</script>

<div class="diff-view">
  <header class="bar">
    {#if singleMode}
      <span class="scope">{singleLabel}</span>
    {:else}
      <DropdownMenu.Root>
        <DropdownMenu.Trigger
          class={buttonVariants({ variant: 'ghost', size: 'sm' })}
          aria-label="Which changes to show"
        >
          {scope === 'branch' ? (branchBase && !branchError ? `Branch vs ${branchBase}` : 'Branch') : 'Uncommitted'}
          <ChevronDown aria-hidden="true" />
        </DropdownMenu.Trigger>
        <DropdownMenu.Content align="start">
          <DropdownMenu.RadioGroup
            value={scope}
            onValueChange={(value) => {
              if (value === 'uncommitted' || value === 'branch') scope = value;
            }}
          >
            <DropdownMenu.RadioItem value="uncommitted">Uncommitted</DropdownMenu.RadioItem>
            <DropdownMenu.RadioItem value="branch">Branch</DropdownMenu.RadioItem>
          </DropdownMenu.RadioGroup>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    {/if}
    <span class="counts"><em>+{totals.added}</em> <del>-{totals.removed}</del></span>
    {#if !singleMode && pending > 0}<span class="quiet"><WorkingSpinner size={12} /> Reading {pending} more {pending === 1 ? 'file' : 'files'}…</span>{/if}
    <span class="spacer"></span>
    <SegmentedControl
      size="sm"
      items={MODES}
      value={mode}
      aria-label="Diff layout"
      onValueChange={(value) => {
        if (isDiffMode(value)) onModeChange?.(value);
      }}
    />
    {#if !singleMode}
      <IconButton label="Refresh changes" size="xs" disabled={gitPanel.statusLoading} onclick={() => void gitService.refreshStatus()}>
        <RefreshCw size={14} />
      </IconButton>
    {/if}
  </header>

  {#if !rootAvailable}
    <p class="notice">Checkout/Worktree deleted.</p>
  {:else if singleMode && gitPanel.diffLoading}
    <p class="notice"><WorkingSpinner size={12} /> Reading the changes…</p>
  {:else if singleMode && gitPanel.diffError}
    <p class="notice error">{gitPanel.diffError}</p>
  {:else if !singleMode && gitPanel.statusError}
    <p class="notice error">{gitPanel.statusError}</p>
  {:else if !singleMode && gitPanel.desktopOnly}
    <p class="notice">Reading the working copy needs the desktop app.</p>
  {:else if !singleMode && !gitPanel.root && !sessionRoot}
    <p class="notice">Pick a session with a project folder to see its changes.</p>
  {:else if !singleMode && !gitPanel.status}
    <p class="notice"><WorkingSpinner size={12} /> Reading the working copy…</p>
  {:else if !singleMode && files.length === 0 && pending > 0}
    <p class="notice"><WorkingSpinner size={12} /> Reading the changes…</p>
  {:else}
    {#if !singleMode && scope === 'branch' && branchError}
      <p class="notice error">{branchError}</p>
    {/if}
    {#if unreadable.length > 0}
      <p class="notice">Not shown here: {unreadable.join(', ')}</p>
    {/if}
    <MultiFileDiff
      bind:this={view}
      {files}
      {mode}
      focusPath={singleMode || pending > 0 ? '' : gitPanel.selectedPath}
      onOpenLine={openAt}
      onLoadFullText={singleMode ? undefined : (path) => void loadFullText(path)}
    />
  {/if}
</div>

<style>
  .diff-view {
    display: flex;
    flex-direction: column;
    height: 100%;
    width: 100%;
    min-height: 0;
    background: var(--color-bg);
    color: var(--color-text-2);
    font-size: 13px;
  }

  .bar {
    display: flex;
    flex: 0 0 auto;
    align-items: center;
    gap: 12px;
    min-height: 44px;
    padding: 6px 8px 6px 12px;
    border-bottom: 1px solid var(--color-border);
  }

  .scope {
    color: var(--color-text);
    white-space: nowrap;
  }

  .counts {
    font: 12px var(--font-mono);
    white-space: nowrap;
  }

  .counts em {
    color: var(--color-good);
    font-style: normal;
  }

  .counts del {
    color: var(--color-bad);
    text-decoration: none;
  }

  .quiet {
    color: var(--color-text-3);
    white-space: nowrap;
  }

  .spacer {
    flex: 1 1 auto;
  }

  .notice {
    margin: 0;
    padding: 10px 12px;
    color: var(--color-text-3);
  }

  .notice.error {
    color: var(--color-bad);
  }
</style>
