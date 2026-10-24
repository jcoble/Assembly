<script lang="ts">
  /**
   * RepoPane.svelte — the top strip of the source-control view: which
   * repository, which branch, how far ahead or behind it is, and the three
   * buttons that talk to the remote.
   *
   * Presentational. It reads the state it is handed and calls the service it is
   * handed; it starts nothing on its own, so mounting it costs no git call.
   *
   * The remote buttons are disabled when this page cannot change the repository
   * — a browser can read the repository through the dev server, but fetching,
   * pulling and pushing belong to the desktop app. The button says why in its
   * hover text rather than failing when pressed.
   *
   * Hover text is a plain `title` attribute rather than the tooltip component
   * on purpose: a disabled button does not raise the events that component
   * listens for, so the one explanation that matters most — why the button is
   * off — would be the one nobody could read.
   */
  import CloudDownload from '@lucide/svelte/icons/cloud-download';
  import CloudUpload from '@lucide/svelte/icons/cloud-upload';
  import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
  import GitBranch from '@lucide/svelte/icons/git-branch';
  import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
  import RefreshCcwDot from '@lucide/svelte/icons/refresh-ccw-dot';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';

  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import {
    describeGitBranchTitle,
    repositoryLabel,
    type GitPanelState
  } from '$lib/shell/git/gitPanelStore.svelte';
  import type { GitService } from '$lib/shell/git/gitService';
  import { cn } from '$lib/utils';

  interface Props {
    panel: GitPanelState;
    service: GitService;
    /** False in a browser: it can read the repository but not change it. */
    canWrite: boolean;
    /** Said in the hover text of every button this page cannot use. */
    readOnlyReason: string;
    /** Open the agent-backed pull request panel. */
    onOpenPullRequest?: () => void;
  }
  let { panel, service, canWrite, readOnlyReason, onOpenPullRequest }: Props = $props();

  const busy = $derived(panel.actionBusy !== '');
  const name = $derived(repositoryLabel(panel.root) || 'Source control');
  const branch = $derived(panel.status?.branch ?? '');
  const ahead = $derived(panel.status?.ahead ?? 0);
  const behind = $derived(panel.status?.behind ?? 0);
  const hasUpstream = $derived(panel.status?.hasUpstream ?? false);
  const branchTitle = $derived(describeGitBranchTitle(panel.status));

  const remoteActions = [
    {
      id: 'fetch',
      label: 'Fetch',
      busyLabel: 'Fetching…',
      icon: RefreshCcwDot,
      tip: 'Ask the remote what it has, without changing anything here'
    },
    {
      id: 'pull',
      label: 'Pull',
      busyLabel: 'Pulling…',
      icon: CloudDownload,
      tip: 'Bring the remote commits into this branch'
    },
    {
      id: 'push',
      label: 'Push',
      busyLabel: 'Pushing…',
      icon: CloudUpload,
      tip: 'Send this branch to the remote'
    }
  ] as const;

  const CHIP =
    'inline-flex shrink-0 items-center rounded-[4px] px-1 py-px text-[12px] leading-[16px]';
</script>

<header class="shrink-0 border-b border-[var(--color-border)] px-2 py-1.5">
  <div class="flex items-center gap-1.5">
    <FolderGit2 class="size-3.5 shrink-0 text-[var(--color-text-2)]" aria-hidden="true" />
    <h2
      class="min-w-0 flex-1 truncate text-[13px] leading-[18px] font-semibold"
      title={panel.root ?? ''}
    >
      {name}
    </h2>
    <button
      type="button"
      class="flex size-6 shrink-0 items-center justify-center rounded-[4px]
             text-[var(--color-text-2)] transition-colors hover:bg-[var(--color-elevated)]
             hover:text-[var(--color-text)] focus-visible:ring-3 focus-visible:ring-ring/50
             outline-none disabled:opacity-45"
      aria-label="Read this repository again"
      title="Read the changed files and the history again"
      disabled={!panel.activated || panel.statusLoading}
      onclick={() => void service.refresh()}
    >
      {#if panel.statusLoading}<WorkingSpinner size={14} />{:else}<RefreshCw class="size-3.5" aria-hidden="true" />{/if}
    </button>
  </div>

  <!-- The branch name wraps rather than being cut off: a name shortened to
       `codex/outbound-rule-generat…` cannot be told apart from the next one like
       it. The hover text carries the full name and spells the counts out, for
       the case where even wrapping runs out of room. -->
  <div
    class="mt-0.5 flex min-w-0 flex-wrap items-center gap-x-1 gap-y-0.5 pl-5
           text-[12px] leading-[16px] text-[var(--color-text-2)]"
    title={branchTitle}
  >
    <GitBranch class="size-3 shrink-0" aria-hidden="true" />
    <span class="min-w-0 break-words">{branch || 'no branch checked out'}</span>
    {#if !hasUpstream}
      <span class="{CHIP} text-[var(--color-text-3)]">no remote branch</span>
    {:else if ahead === 0 && behind === 0}
      <span class="{CHIP} text-[var(--color-text-3)]">up to date</span>
    {:else}
      {#if ahead > 0}
        <span
          class="{CHIP} bg-[var(--color-live-bg)] text-[var(--color-live)]"
          title="{ahead} commit{ahead === 1 ? '' : 's'} of yours the remote does not have"
        >
          ↑{ahead}
        </span>
      {/if}
      {#if behind > 0}
        <span
          class="{CHIP} bg-[var(--color-attention-bg)] text-[var(--color-attention)]"
          title="{behind} commit{behind === 1 ? '' : 's'} on the remote you do not have"
        >
          ↓{behind}
        </span>
      {/if}
    {/if}
  </div>

  <div class="mt-1.5 flex items-center gap-1">
    {#each remoteActions as action (action.id)}
      {@const Icon = action.icon}
      <button
        type="button"
        class={cn(
          buttonVariants({ variant: 'secondary', size: 'xs' }),
          'flex-1 gap-1 px-1.5 text-[12px] font-normal'
        )}
        disabled={!canWrite || busy || !panel.activated}
        title={canWrite ? action.tip : readOnlyReason}
        onclick={() => void service.runRemoteAction(action.id)}
      >
        {#if panel.actionBusy === action.id}<WorkingSpinner size={12} />{action.busyLabel}{:else}<Icon class="size-3" aria-hidden="true" />{action.label}{/if}
      </button>
    {/each}
  </div>

  <button
    type="button"
    class={cn(
      buttonVariants({ variant: 'ghost', size: 'xs' }),
      'mt-1.5 w-full justify-start gap-1.5 px-1.5 text-[12px] text-[var(--color-text-2)]'
    )}
    disabled={!panel.activated || !canWrite}
    title={canWrite ? 'Generate the pull request title and description, then push and create it' : readOnlyReason}
    onclick={() => onOpenPullRequest?.()}
    data-testid="open-pull-request"
  >
    <GitPullRequest class="size-3.5" aria-hidden="true" />
    New pull request
  </button>

  {#if panel.actionError}
    <p class="mt-1 text-[12px] leading-[16px] text-[var(--color-bad)]">{panel.actionError}</p>
  {:else if panel.actionStatus}
    <p class="mt-1 text-[12px] leading-[16px] text-[var(--color-text-2)]">{panel.actionStatus}</p>
  {/if}
  {#if panel.statusError}
    <p class="mt-1 text-[12px] leading-[16px] text-[var(--color-bad)]">{panel.statusError}</p>
  {/if}
</header>
