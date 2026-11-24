<!--
  empty-state.svelte — what a panel shows when it has nothing to show.

  An empty panel should say what will appear there and when, in plain
  sentences, rather than leaving a blank rectangle. One icon, a short title, one
  or two sentences, and an optional thing to press.

  Usage:
    <EmptyState
      title="No agents yet"
      body="When this session spawns agents or runs a workflow, they show up here with live status and activity."
    >
      {#snippet icon()}<Bot />{/snippet}
    </EmptyState>
-->
<script lang="ts">
  import type { Snippet } from 'svelte';

  import { cn } from '$lib/utils.js';

  interface Props {
    title: string;
    /** One or two plain sentences saying what will appear here and when. */
    body?: string;
    /** A single lucide icon, rendered at 24px above the title. */
    icon?: Snippet;
    /** Optional call to action. */
    actions?: Snippet;
    class?: string;
    'data-testid'?: string;
  }

  let {
    title,
    body,
    icon,
    actions,
    class: className,
    'data-testid': dataTestId
  }: Props = $props();
</script>

<div
  data-slot="empty-state"
  data-testid={dataTestId}
  class={cn(
    'flex flex-col items-center justify-center gap-2 px-4 py-8 text-center',
    className
  )}
>
  {#if icon}
    <span class="empty-icon flex text-muted-foreground" aria-hidden="true">
      {@render icon()}
    </span>
  {/if}
  <p class="max-w-[36ch] text-[13px] leading-tight font-medium text-foreground">{title}</p>
  {#if body}
    <p class="max-w-[42ch] text-sm leading-snug text-muted-foreground">{body}</p>
  {/if}
  {#if actions}
    <div class="flex items-center gap-2 pt-1">
      {@render actions()}
    </div>
  {/if}
</div>

<style>
  /* One size for the icon whatever the caller passes in, so every empty state
     in the app reads the same. */
  .empty-icon :global(svg) {
    width: 24px;
    height: 24px;
  }
</style>
