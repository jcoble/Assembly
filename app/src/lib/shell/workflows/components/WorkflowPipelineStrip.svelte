<!--
  WorkflowPipelineStrip.svelte — a run's steps as one strip of segments, in
  order. A segment's fill is the step's state; a step that ran more than once
  shows its attempt count. Hovering a segment names the step and its state;
  the step that matters now (broken, waiting, running or stopped, else the last
  one done) is named under its segment. Each segment opens that step's details.
-->
<script lang="ts">
  import type { ChipTone } from '$lib/components/ui/chip/index.js';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';

  interface Segment {
    key: string;
    title: string;
    tone: ChipTone;
    /** The step's state in words, for the tooltip and screen readers. */
    label: string;
    /** The state in one or two words, for the caption under the strip. */
    state: string;
    attempt: number;
  }

  let { segments, onSelect }: { segments: Segment[]; onSelect: (key: string) => void } = $props();

  // The chip's status fills, so a segment and its step's chip agree.
  const fill: Record<ChipTone, string> = {
    good: 'bg-[var(--color-good-bg)] text-[var(--color-good)]',
    bad: 'bg-[var(--color-bad-bg)] text-[var(--color-bad)]',
    live: 'bg-[var(--color-live-bg)] text-[var(--color-live)]',
    attention: 'bg-[var(--color-attention-bg)] text-[var(--color-attention)]',
    neutral: 'bg-muted-foreground/30 text-foreground',
    count: 'bg-secondary text-muted-foreground'
  };

  const focus = $derived.by(() => {
    const now = segments.findIndex((segment) => ['bad', 'attention', 'live', 'neutral'].includes(segment.tone));
    return now >= 0 ? now : segments.findLastIndex((segment) => segment.tone === 'good');
  });

  const describe = (segment: Segment): string =>
    `${segment.title}: ${segment.label}${segment.attempt > 1 ? `, ${segment.attempt} attempts` : ''}`;
</script>

<div class="flex min-w-0 flex-col gap-1">
  <ol class="m-0 flex min-w-0 list-none gap-1 p-0" aria-label="Steps">
    {#each segments as segment, index (segment.key)}
      <li class="flex min-w-0 flex-1">
        <Tooltip.Root>
          <Tooltip.Trigger>
            {#snippet child({ props })}
              <button
                {...props}
                type="button"
                class="flex h-6 min-w-0 flex-1 items-center justify-center rounded-md text-sm leading-none font-medium tabular-nums outline-none transition-[filter] hover:brightness-125 focus-visible:ring-3 focus-visible:ring-ring/50 {fill[segment.tone]}"
                aria-label={describe(segment)}
                onclick={() => onSelect(segment.key)}
              >
                {segment.attempt > 1 ? `×${segment.attempt}` : ''}
              </button>
            {/snippet}
          </Tooltip.Trigger>
          <Tooltip.Content side="top">
            <span class="font-medium">{index + 1}. {segment.title}</span>
            <span class="text-muted-foreground">{segment.label}{segment.attempt > 1 ? ` · ${segment.attempt} attempts` : ''}</span>
          </Tooltip.Content>
        </Tooltip.Root>
      </li>
    {/each}
  </ol>
  {#if focus >= 0}
    {@const segment = segments[focus]}
    <!-- Anchored under its segment: from its left edge in the first half, to its right edge in the second. -->
    <p
      class="min-w-0 truncate text-sm leading-tight"
      class:text-right={focus >= segments.length / 2}
      style={focus < segments.length / 2
        ? `padding-left: calc(${focus} / ${segments.length} * 100%)`
        : `padding-right: calc(${segments.length - 1 - focus} / ${segments.length} * 100%)`}
      aria-hidden="true"
    >
      <span class="font-medium text-foreground">{segment.title}</span>
      <span class="text-muted-foreground"> · {segment.state}</span>
    </p>
  {/if}
</div>
