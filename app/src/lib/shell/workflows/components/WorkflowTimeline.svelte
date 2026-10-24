<!--
  WorkflowTimeline.svelte — what happened in a run, oldest at the top and the
  newest at the bottom, collapsed to the latest few until "Show all". A row
  that belongs to a step opens that step; the PR row opens the pull request.
  Proof images on this machine show as thumbnails, elsewhere by file name.
-->
<script lang="ts">
  import CircleCheck from '@lucide/svelte/icons/circle-check';
  import CircleX from '@lucide/svelte/icons/circle-x';
  import Bot from '@lucide/svelte/icons/bot';
  import Flag from '@lucide/svelte/icons/flag';
  import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
  import Hand from '@lucide/svelte/icons/hand';
  import ImageIcon from '@lucide/svelte/icons/image';
  import ListChecks from '@lucide/svelte/icons/list-checks';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import Play from '@lucide/svelte/icons/play';
  import Repeat from '@lucide/svelte/icons/repeat';
  import SquareTerminal from '@lucide/svelte/icons/square-terminal';
  import TimerOff from '@lucide/svelte/icons/timer-off';
  import UserRound from '@lucide/svelte/icons/user-round';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip, type ChipTone } from '$lib/components/ui/chip/index.js';
  import { openUrlInBrowser } from '$lib/shell/workbenchNavigation.ts';
  import type { WorkflowTimelineItem, WorkflowTimelineKind } from '$lib/shell/workflows/workflowTimeline.ts';
  import { readSourceImageFromTauri } from '$lib/tauriSource.ts';

  interface Props {
    items: WorkflowTimelineItem[];
    /** Still reading the run's history and messages. */
    loading: boolean;
    error: string;
    /** The run is on this machine, so its proof images can be read. */
    local: boolean;
    onOpenStep: (nodeId: string) => void;
  }

  let { items, loading, error, local, onOpenStep }: Props = $props();

  const COLLAPSED = 8;
  let expanded = $state(false);
  const shown = $derived(expanded ? items : items.slice(-COLLAPSED));

  const ICONS: Record<WorkflowTimelineKind, typeof Play> = {
    run: Flag, start: Play, finish: CircleCheck, checks: SquareTerminal, review: ListChecks, failed: CircleX,
    timeout: TimerOff, waiting: Hand, owner: UserRound, orchestrator: Bot, redo: Repeat, message: MessageSquare, pr: GitPullRequest, proof: ImageIcon
  };
  const TONE_TEXT: Record<NonNullable<WorkflowTimelineItem['tone']>, string> = {
    good: 'text-[var(--color-good)]', bad: 'text-[var(--color-bad)]',
    attention: 'text-[var(--color-attention)]', live: 'text-[var(--color-live)]'
  };
  const RECEIPT_TONE: Record<NonNullable<WorkflowTimelineItem['receipt']>, ChipTone> = {
    queued: 'live', delivered: 'good', failed: 'bad', expired: 'neutral'
  };

  const today = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });
  const earlier = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  const when = (ms: number): string =>
    (new Date(ms).toDateString() === new Date().toDateString() ? today : earlier).format(ms);

  function meta(item: WorkflowTimelineItem): string {
    const attempt = item.attempt && item.attempt > 1 && item.kind !== 'review' && item.kind !== 'redo' ? `attempt ${item.attempt}` : '';
    return [when(item.atMs), attempt, item.kind === 'message' ? '' : item.detail].filter(Boolean).join(' · ');
  }

  function open(item: WorkflowTimelineItem): void {
    if (item.url) void openUrlInBrowser({ url: item.url });
    else if (item.nodeId) onOpenStep(item.nodeId);
  }

  const fileName = (path: string): string => path.split('/').at(-1) ?? path;
  const IMAGE = /\.(png|jpe?g|gif|webp)$/i;

  // Thumbnails for the proof images in view; their object URLs are let go with them.
  let thumbnails = $state<Record<string, string>>({});
  // Joined into one string so a reload with the same files does not read them again.
  const proofImages = $derived(local ? shown.flatMap((item) => item.files).filter((file) => IMAGE.test(file)).join('\n') : '');

  $effect(() => {
    const files = proofImages ? proofImages.split('\n') : [];
    if (!files.length) return;
    const controller = new AbortController();
    const urls: string[] = [];
    void loadThumbnails(files, urls, controller.signal);
    return () => {
      controller.abort();
      urls.forEach((url) => URL.revokeObjectURL(url));
      thumbnails = {};
    };
  });

  async function loadThumbnails(files: string[], urls: string[], signal: AbortSignal): Promise<void> {
    for (const file of files) {
      try {
        const bytes = await readSourceImageFromTauri(file, signal);
        if (signal.aborted) return;
        const url = URL.createObjectURL(new Blob([bytes as BlobPart]));
        urls.push(url);
        thumbnails = { ...thumbnails, [file]: url };
      } catch {
        // A file that cannot be read keeps its name; the row still says it is there.
        if (signal.aborted) return;
      }
    }
  }
</script>

<Card.Root size="sm" class="gap-1.5 bg-foreground/8">
  <Card.Header>
    <div class="flex min-w-0 items-center gap-2">
      <h3 class="min-w-0 flex-1 truncate text-[13px] leading-tight font-semibold text-foreground">Timeline</h3>
      {#if items.length > COLLAPSED}
        <Button variant="ghost" size="xs" class="-my-1" onclick={() => (expanded = !expanded)}>
          {expanded ? 'Show latest' : `Show all ${items.length}`}
        </Button>
      {/if}
    </div>
  </Card.Header>
  <Card.Content class="px-1">
    {#if error}
      <p class="px-2 py-1.5 text-sm text-destructive [overflow-wrap:anywhere]">{error}</p>
    {:else if items.length === 0}
      <p class="px-2 py-1.5 text-sm text-muted-foreground">{loading ? 'Loading the timeline…' : 'Nothing has happened in this run yet.'}</p>
    {:else}
      <ol class="m-0 flex min-w-0 list-none flex-col p-0">
        {#each shown as item, index (item.key)}
          {@const Icon = ICONS[item.kind]}
          {@const clickable = Boolean(item.url || item.nodeId)}
          <li class="relative min-w-0">
            {#if index < shown.length - 1}
              <!-- The thread joining one row's icon to the next. -->
              <span class="absolute top-7 -bottom-1 left-[17.5px] w-px bg-border" aria-hidden="true"></span>
            {/if}
            {#if clickable}
              <button
                type="button"
                class="flex w-full min-w-0 items-start gap-3 rounded-lg px-2 py-1.5 text-left outline-none transition-colors hover:bg-accent/60 focus-visible:ring-3 focus-visible:ring-ring/50"
                title={item.url ?? undefined}
                onclick={() => open(item)}
              >{@render row(item, Icon)}</button>
            {:else}
              <div class="flex w-full min-w-0 items-start gap-3 px-2 py-1.5">{@render row(item, Icon)}</div>
            {/if}
          </li>
        {/each}
      </ol>
    {/if}
  </Card.Content>
</Card.Root>

{#snippet row(item: WorkflowTimelineItem, Icon: typeof Play)}
  <span class="flex size-5 shrink-0 items-center justify-center [&>svg]:size-3.5 {item.tone ? TONE_TEXT[item.tone] : 'text-muted-foreground'}" aria-hidden="true">
    <Icon />
  </span>
  <span class="flex min-w-0 flex-1 flex-col gap-0.5">
    <span class="truncate text-[13px] leading-snug font-medium">{item.title}</span>
    <span class="truncate text-sm text-muted-foreground tabular-nums">{meta(item)}</span>
    {#if item.kind === 'message' && item.detail}
      <span class="line-clamp-2 text-[13px] leading-snug text-muted-foreground [overflow-wrap:anywhere]">{item.detail}</span>
    {/if}
    {#if item.files.length}
      <span class="mt-1 flex min-w-0 flex-wrap items-start gap-1.5">
        {#each item.files as file (file)}
          {#if thumbnails[file]}
            <img src={thumbnails[file]} alt={fileName(file)} title={fileName(file)} class="h-16 w-28 rounded-md bg-background/60 object-cover object-top ring-1 ring-border" />
          {:else}
            <span class="max-w-full truncate rounded-md bg-background/60 px-2 py-1 font-mono text-sm text-muted-foreground" title={file}>{fileName(file)}</span>
          {/if}
        {/each}
      </span>
    {/if}
  </span>
  {#if item.receipt}<Chip tone={RECEIPT_TONE[item.receipt]}>{item.receipt}</Chip>{/if}
{/snippet}
