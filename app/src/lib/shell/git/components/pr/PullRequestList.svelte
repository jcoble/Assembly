<script lang="ts">
  /**
   * PullRequestList.svelte — the pull requests open on this repository, with
   * what their checks are doing.
   *
   * It reads `gh pr list` through the desktop app. `gh` not being installed is
   * not treated as an error the person made: it gets its own state, says so in
   * one sentence, and gives the two commands that fix it. Everything else keeps
   * `gh`'s own words, because those usually say exactly what is wrong (not
   * signed in, no remote, no repository on GitHub).
   *
   * Nothing loads on mount. The section is collapsed until it is opened, and
   * opening it is what runs the read — so a source-control panel sitting in the
   * background never shells out to `gh`.
   */
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';

  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { READ_ONLY_IN_BROWSER_MESSAGE } from '$lib/shell/git/gitBackendExtra';
  import { listOpenPullRequestsFromTauri } from '$lib/tauriSource';
  import { cn } from '$lib/utils';

  import {
    beginPullRequestListLoad,
    checkTone,
    createPullRequestListModel,
    describeChecks,
    describePullRequestListSummary,
    failPullRequestList,
    finishPullRequestListLoad,
    NO_GH_HINT,
    type PullRequestListModel
  } from './prList';

  interface Props {
    root: string | null;
  }
  let { root }: Props = $props();

  let open = $state(false);
  let model = $state<PullRequestListModel>(createPullRequestListModel());

  const summary = $derived(describePullRequestListSummary(model));

  async function load(): Promise<void> {
    if (!root) return;
    model = beginPullRequestListLoad(model);
    try {
      const list = await listOpenPullRequestsFromTauri(root);
      if (!list) throw new Error(READ_ONLY_IN_BROWSER_MESSAGE);
      model = finishPullRequestListLoad(model, list);
    } catch (error) {
      model = failPullRequestList(model, error);
    }
  }

  function toggle(): void {
    open = !open;
    if (open && model.state === 'idle') void load();
  }

  const TONE: Record<string, string> = {
    good: 'text-[var(--color-good)]',
    bad: 'text-[var(--color-bad)]',
    attention: 'text-[var(--color-attention)]',
    quiet: 'text-[var(--color-text-3)]'
  };
</script>

<section class="shrink-0 border-b border-[var(--color-border)]" data-testid="pr-list">
  <div class="flex items-center gap-1 px-2 py-1">
    <button
      type="button"
      class="flex min-w-0 flex-1 items-center gap-1 text-left text-[12px]
             tracking-[0.06em] text-[var(--color-text-2)] uppercase transition-colors
             hover:text-[var(--color-text)] focus-visible:ring-3 focus-visible:ring-ring/50
             outline-none"
      aria-expanded={open}
      onclick={toggle}
      data-testid="pr-list-toggle"
    >
      {#if open}
        <ChevronDown class="size-3 shrink-0" aria-hidden="true" />
      {:else}
        <ChevronRight class="size-3 shrink-0" aria-hidden="true" />
      {/if}
      <span>Open pull requests</span>
    </button>
    {#if open}
      <IconButton
        label="Read the open pull requests again"
        size="sm"
        side="bottom"
        class="shrink-0 text-[var(--color-text-2)] hover:bg-[var(--color-elevated)] hover:text-[var(--color-text)]"
        disabled={model.state === 'loading' || !root}
        onclick={() => void load()}
      >
        {#if model.state === 'loading'}<WorkingSpinner size={14} />{:else}<RefreshCw class="size-3.5" aria-hidden="true" />{/if}
      </IconButton>
    {/if}
  </div>

  {#if open}
    <div class="px-2 pb-2">
      {#if summary}
        <p
          class={cn(
            'text-[12px] leading-[16px]',
            model.state === 'failed' || model.state === 'no-gh'
              ? 'text-[var(--color-bad)]'
              : 'text-[var(--color-text-3)]'
          )}
          data-testid="pr-list-summary"
        >
          {summary}
        </p>
      {/if}
      {#if model.state === 'no-gh'}
        <p class="mt-0.5 text-[12px] leading-[16px] text-[var(--color-text-2)]">{NO_GH_HINT}</p>
      {/if}

      {#each model.pullRequests as pullRequest (pullRequest.number)}
        <a
          class="mt-1 flex items-center gap-1.5 rounded-[4px] px-1 py-1 transition-colors
                 hover:bg-[var(--color-elevated)] focus-visible:ring-3 focus-visible:ring-ring/50
                 outline-none"
          href={pullRequest.url}
          target="_blank"
          rel="noreferrer"
          title={pullRequest.url}
        >
          <span class="shrink-0 text-[12px] text-[var(--color-text-3)]">#{pullRequest.number}</span>
          <span class="min-w-0 flex-1">
            <span class="block truncate text-[13px] leading-[18px]">{pullRequest.title}</span>
            <span class="block truncate text-[12px] leading-[16px] text-[var(--color-text-3)]">
              {pullRequest.isDraft ? 'Draft · ' : ''}{pullRequest.headBranch}
            </span>
          </span>
          <span
            class={cn('shrink-0 text-[12px] leading-[16px]', TONE[checkTone(pullRequest)])}
            title={describeChecks(pullRequest)}
          >
            {describeChecks(pullRequest)}
          </span>
          <ExternalLink class="size-3 shrink-0 text-[var(--color-text-3)]" aria-hidden="true" />
        </a>
      {/each}
    </div>
  {/if}
</section>
