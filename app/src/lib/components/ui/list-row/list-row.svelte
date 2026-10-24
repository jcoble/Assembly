<!--
  list-row.svelte — the standard row in any panel list.

  A row is content on the left and, on hover, a few icon actions on the right.
  This component owns both halves of that: it carries the `group` class the
  hover cluster's reveal depends on, positions the cluster over the row's own
  metadata, and sizes the cluster's glyphs at 18px. That last rule used to be
  copied into every consuming stylesheet, which is exactly why rows drifted
  apart; it belongs to the kit now.

  The clickable part is a button that fills the row, and the action cluster sits
  over it as a sibling rather than inside it, so a row can be clicked and still
  offer its own buttons.

  Usage:
    <ListRow onclick={open} selected={isOpen} actionsLabel="File actions">
      <span class="truncate">{file.name}</span>
      {#snippet actions()}
        <HoverActionButton label="Open editor" tone="info" onclick={openEditor}>
          <FileCode2 />
        </HoverActionButton>
      {/snippet}
    </ListRow>
-->
<script lang="ts">
  import type { Snippet } from 'svelte';

  import { HoverActions } from '$lib/components/ui/hover-actions/index.js';
  import { cn } from '$lib/utils.js';

  interface Props {
    /** The row's content. */
    children: Snippet;
    /** HoverActionButtons. Rendered inside a HoverActions cluster, revealed on hover. */
    actions?: Snippet;
    /** Names the action group for a screen reader, e.g. "Worktree actions". */
    actionsLabel?: string;
    selected?: boolean;
    disabled?: boolean;
    onclick?: (event: MouseEvent) => void;
    oncontextmenu?: (event: MouseEvent) => void;
    class?: string;
    'data-testid'?: string;
  }

  let {
    children,
    actions,
    actionsLabel,
    selected = false,
    disabled = false,
    onclick,
    oncontextmenu,
    class: className,
    'data-testid': dataTestId
  }: Props = $props();
</script>

<div
  data-slot="list-row"
  data-testid={dataTestId}
  class={cn(
    'row group relative flex min-h-7 w-full min-w-0 items-center rounded-lg',
    selected ? 'bg-secondary text-foreground' : 'text-foreground',
    disabled && 'opacity-50',
    className
  )}
>
  {#if onclick}
    <button
      type="button"
      {disabled}
      aria-current={selected ? 'true' : undefined}
      class="flex min-h-7 min-w-0 flex-1 items-center gap-2 rounded-lg px-2 py-1 text-left
             text-[13px] leading-tight outline-none transition-colors
             hover:bg-accent/60 hover:text-foreground
             focus-visible:ring-3 focus-visible:ring-ring/50
             disabled:pointer-events-none disabled:opacity-50"
      onclick={(event) => onclick(event)}
      {oncontextmenu}
    >
      {@render children()}
    </button>
  {:else}
    <div class="flex min-h-7 min-w-0 flex-1 items-center gap-2 px-2 py-1 text-[13px] leading-tight">
      {@render children()}
    </div>
  {/if}

  {#if actions}
    <HoverActions label={actionsLabel} class="absolute top-1/2 right-2 -translate-y-1/2">
      {@render actions()}
    </HoverActions>
  {/if}
</div>

<style>
  /* The kit owns the row cluster's glyph size, so every panel's rows match
     without each one saying it again. */
  .row :global([data-slot='hover-actions'] svg) {
    width: 18px;
    height: 18px;
  }
</style>
