<script lang="ts">
  /**
   * ChangesPane.svelte — what has changed in the working copy, and the box that
   * turns it into a commit.
   *
   * The rows are deliberately tight, the way an editor's source-control list
   * is: file name first, the folder it lives in dimmed beside it, and git's own
   * letter for what happened to it on the right. The tightness comes from
   * padding, never from shrinking the text — 13px for a row's name and 12px for
   * anything secondary is the floor everywhere in this shell.
   *
   * ⌘Enter in the message box commits, which is the shortcut the box's own
   * placeholder promises. Ctrl+Enter does the same for anyone on a keyboard
   * without a command key.
   *
   * Presentational: it reads the state it is handed and calls the service it is
   * handed. Nothing starts here.
   */
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import FileDiff from '@lucide/svelte/icons/file-diff';
  import Minus from '@lucide/svelte/icons/minus';
  import Plus from '@lucide/svelte/icons/plus';
  import SquareArrowOutUpRight from '@lucide/svelte/icons/square-arrow-out-up-right';
  import Sparkles from '@lucide/svelte/icons/sparkles';

  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import {
    buildGitStatusFileGroups,
    describeGitStatusGroups,
    gitFileTitle,
    gitStatusGroupActionLabel,
    hasStagedChanges,
    type GitPanelState,
    type GitStatusFileGroup
  } from '$lib/shell/git/gitPanelStore.svelte';
  import { splitRepositoryPath } from '$lib/shell/git/gitCommitFilesStore.svelte';
  import { absolutePathWithin, type GitService } from '$lib/shell/git/gitService';
  import { requestOpenFile } from '$lib/shell/openFileBus';
  import { cn } from '$lib/utils';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import type { ProjectGitFileStatus } from '$lib/tauriSource';

  interface Props {
    panel: GitPanelState;
    service: GitService;
    /** False in a browser: it can read the repository but not change it. */
    canWrite: boolean;
    readOnlyReason: string;
    /** A changed file was picked, so the diff should be brought to the front. */
    onShowDiff?: () => void;
    /** Ask the active agent for a commit subject, without opening the chat UI. */
    onGenerateCommitMessage?: () => Promise<string>;
    /** Whether the current active session can service an agent action. */
    agentAvailable?: boolean;
    /** Plain explanation for a disabled generation action. */
    agentUnavailableReason?: string;
  }
  let {
    panel,
    service,
    canWrite,
    readOnlyReason,
    onShowDiff,
    onGenerateCommitMessage,
    agentAvailable = false,
    agentUnavailableReason = 'No active agent session is running. Start an agent conversation to generate this message.'
  }: Props = $props();

  /** Pick a file's changes, and ask for wherever they are drawn to come forward. */
  function pickFile(file: ProjectGitFileStatus): void {
    void service.selectFile(file);
    onShowDiff?.();
  }

  let open = $state(true);
  let generatingCommitMessage = $state(false);
  let generationError = $state('');

  const groups = $derived(buildGitStatusFileGroups(panel.status?.files ?? []));
  const summary = $derived(describeGitStatusGroups(groups));
  const busy = $derived(panel.actionBusy !== '');
  const branch = $derived(panel.status?.branch ?? 'this branch');
  const files = $derived(panel.status?.files ?? []);
  const canCommit = $derived(
    canWrite && !busy && panel.commitMessage.trim() !== '' && hasStagedChanges(panel.status)
  );

  /** Why the Commit button is off, in the words that fit this moment. */
  const commitHint = $derived(
    !canWrite
      ? readOnlyReason
      : !hasStagedChanges(panel.status)
        ? 'Stage a file first — a commit records the staged files.'
        : panel.commitMessage.trim() === ''
          ? 'Say what you changed, then commit.'
          : `Commit the staged files on ${branch}`
  );

  /** The colour a status letter gets, so the eye can sort the list without reading it. */
  function badgeTone(badge: string): string {
    if (badge === 'A' || badge === '?') return 'text-[var(--color-good)]';
    if (badge === 'D') return 'text-[var(--color-bad)]';
    if (badge === 'R' || badge === 'C') return 'text-[var(--color-accent)]';
    return 'text-[var(--color-attention)]';
  }

  function runGroupAction(group: GitStatusFileGroup): void {
    const paths = group.files.map((file) => file.relativePath);
    if (group.action === 'stage') void service.stagePaths(paths);
    else void service.unstagePaths(paths);
  }

  function runFileAction(group: GitStatusFileGroup, file: ProjectGitFileStatus): void {
    if (group.action === 'stage') void service.stagePaths([file.relativePath]);
    else void service.unstagePaths([file.relativePath]);
  }

  /** Ask the editor to open this file. The editor lane listens on the same bus. */
  function openInEditor(file: ProjectGitFileStatus): void {
    if (!panel.root) return;
    requestOpenFile({ path: absolutePathWithin(panel.root, file.relativePath) });
  }

  function openAllChanges(): void {
    if (!panel.root) return;
    for (const file of files) {
      requestOpenFile({ path: absolutePathWithin(panel.root, file.relativePath) });
    }
  }

  function commitOnShortcut(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || !(event.metaKey || event.ctrlKey)) return;
    event.preventDefault();
    if (canCommit) void service.commit();
  }

  async function generateCommitMessage(): Promise<void> {
    if (!onGenerateCommitMessage || !agentAvailable || generatingCommitMessage) return;
    generatingCommitMessage = true;
    generationError = '';
    try {
      const message = await onGenerateCommitMessage();
      panel.commitMessage = message.trim();
    } catch (error) {
      generationError = error instanceof Error ? error.message : String(error);
    } finally {
      generatingCommitMessage = false;
    }
  }

  const ROW_ACTION =
    'flex size-5 shrink-0 items-center justify-center rounded-[4px] text-[var(--color-text-2)] ' +
    'opacity-0 transition-colors group-hover:opacity-100 focus-visible:opacity-100 ' +
    'hover:bg-[var(--color-elevated)] hover:text-[var(--color-text)] ' +
    'focus-visible:ring-3 focus-visible:ring-ring/50 outline-none disabled:opacity-40';
</script>

<section class={cn('flex min-h-0 flex-col', open ? 'flex-1' : 'shrink-0')}>
  <button
    type="button"
    class="flex w-full shrink-0 items-center gap-1 px-2 py-1 text-left
           text-[12px] tracking-[0.06em] text-[var(--color-text-2)] uppercase
           transition-colors hover:text-[var(--color-text)]
           focus-visible:ring-3 focus-visible:ring-ring/50 outline-none"
    aria-expanded={open}
    onclick={() => (open = !open)}
  >
    {#if open}
      <ChevronDown class="size-3 shrink-0" aria-hidden="true" />
    {:else}
      <ChevronRight class="size-3 shrink-0" aria-hidden="true" />
    {/if}
    <span>Changes</span>
    <span class="ml-auto normal-case text-[var(--color-text-3)]">{summary}</span>
  </button>

  {#if open}
    <div class="flex min-h-0 flex-1 flex-col gap-1.5 overflow-y-auto px-2 pt-0.5 pb-2">
      <div class="flex shrink-0 flex-col gap-1">
        <textarea
          class="w-full resize-y rounded-[6px] border border-[var(--color-border)]
                 bg-[var(--color-surface)] px-2 py-1.5 text-[13px] leading-[18px]
                 text-[var(--color-text)] placeholder:text-[var(--color-text-3)]
                 focus-visible:border-[var(--color-accent)] focus-visible:ring-3
                 focus-visible:ring-ring/50 outline-none disabled:opacity-60"
          rows="2"
          placeholder="Message (⌘Enter to commit on '{branch}')"
          aria-label="Commit message"
          disabled={!canWrite}
          title={canWrite ? '' : readOnlyReason}
          bind:value={panel.commitMessage}
          onkeydown={commitOnShortcut}
        ></textarea>
        <button
          type="button"
          class={cn(
            buttonVariants({ variant: 'ghost', size: 'xs' }),
            'w-fit gap-1 px-1.5 text-[12px] text-[var(--color-text-2)]'
          )}
          disabled={!canWrite || !agentAvailable || generatingCommitMessage}
          title={!canWrite ? readOnlyReason : agentAvailable ? 'Ask the active agent to write a commit message from this diff' : agentUnavailableReason}
          onclick={() => void generateCommitMessage()}
          data-testid="generate-commit-message"
        >
          {#if generatingCommitMessage}
            <WorkingSpinner size={12} />
            Generating…
          {:else}
            <Sparkles class="size-3" aria-hidden="true" />
            Generate commit message
          {/if}
        </button>
        {#if !agentAvailable && onGenerateCommitMessage}
          <p class="text-[12px] leading-[16px] text-[var(--color-text-3)]" data-testid="commit-agent-unavailable">
            {agentUnavailableReason}
          </p>
        {/if}
        {#if generationError}
          <p class="text-[12px] leading-[16px] text-[var(--color-bad)]" role="alert">{generationError}</p>
        {/if}
      </div>

      <button
        type="button"
        class={cn(buttonVariants({ variant: 'default', size: 'xs' }), 'w-full shrink-0 text-[12px]')}
        disabled={!canCommit}
        title={commitHint}
        onclick={() => void service.commit()}
      >
        {panel.actionBusy === 'commit' ? 'Committing…' : 'Commit'}
      </button>

      <button
        type="button"
        class={cn(
          buttonVariants({ variant: 'secondary', size: 'xs' }),
          'w-full shrink-0 text-[12px] font-normal'
        )}
        disabled={files.length === 0}
        title={files.length > 0 ? 'Open every changed file in the editor' : 'There are no changes to open'}
        onclick={openAllChanges}
        data-testid="open-all-changes"
      >
        Open All Changes
      </button>

      {#if groups.length === 0}
        <p class="px-1 py-1 text-[13px] leading-[18px] text-[var(--color-text-2)]">
          {#if panel.statusLoading}<WorkingSpinner size={12} /> Reading the repository…{:else}Nothing has changed yet.{/if}
        </p>
      {/if}

      {#each groups as group (group.id)}
        <div class="flex flex-col">
          <div class="flex items-center gap-1 px-1 pt-1 pb-0.5">
            <span class="text-[12px] leading-[16px] text-[var(--color-text-2)]">
              {group.label}
            </span>
            <span class="text-[12px] leading-[16px] text-[var(--color-text-3)]">
              {group.files.length}
            </span>
            <button
              type="button"
              class="ml-auto rounded-[4px] px-1 py-px text-[12px] leading-[16px]
                     text-[var(--color-text-2)] transition-colors
                     hover:bg-[var(--color-elevated)] hover:text-[var(--color-text)]
                     focus-visible:ring-3 focus-visible:ring-ring/50 outline-none
                     disabled:opacity-40"
              disabled={!canWrite || busy}
              title={canWrite ? '' : readOnlyReason}
              onclick={() => runGroupAction(group)}
            >
              {gitStatusGroupActionLabel(group)}
            </button>
          </div>

          {#each group.files as file (group.id + file.relativePath)}
            {@const parts = splitRepositoryPath(file.relativePath)}
            <div
              class={cn(
                'group flex items-center gap-1 rounded-[4px] pr-1 transition-colors',
                'hover:bg-[var(--color-elevated)]',
                panel.selectedPath === file.relativePath && 'bg-[var(--color-elevated)]'
              )}
            >
              <button
                type="button"
                class="flex min-w-0 flex-1 items-center gap-1.5 rounded-[4px] py-[3px] pl-1.5
                       text-left focus-visible:ring-3 focus-visible:ring-ring/50 outline-none"
                title={gitFileTitle(file)}
                onclick={() => pickFile(file)}
              >
                <FileDiff
                  class="size-3.5 shrink-0 text-[var(--color-text-3)]"
                  aria-hidden="true"
                />
                <span class="shrink-0 truncate text-[13px] leading-[18px]">{parts.name}</span>
                {#if parts.folder}
                  <span class="min-w-0 truncate text-[12px] leading-[16px] text-[var(--color-text-3)]">
                    {parts.folder}
                  </span>
                {/if}
              </button>

              <button
                type="button"
                class={ROW_ACTION}
                aria-label="Open {parts.name}"
                title="Open this file in the editor"
                onclick={() => openInEditor(file)}
              >
                <SquareArrowOutUpRight class="size-3" aria-hidden="true" />
              </button>
              <button
                type="button"
                class={ROW_ACTION}
                disabled={!canWrite || busy}
                aria-label="{group.action === 'stage' ? 'Stage' : 'Unstage'} {parts.name}"
                title={canWrite
                  ? group.action === 'stage'
                    ? 'Stage this file'
                    : 'Unstage this file'
                  : readOnlyReason}
                onclick={() => runFileAction(group, file)}
              >
                {#if group.action === 'stage'}
                  <Plus class="size-3" aria-hidden="true" />
                {:else}
                  <Minus class="size-3" aria-hidden="true" />
                {/if}
              </button>

              <span
                class={cn(
                  'w-3 shrink-0 text-center text-[12px] leading-[16px] font-medium',
                  badgeTone(file.badge)
                )}
                title={file.status}
              >
                {file.badge || '·'}
              </span>
            </div>
          {/each}
        </div>
      {/each}
    </div>
  {/if}
</section>
