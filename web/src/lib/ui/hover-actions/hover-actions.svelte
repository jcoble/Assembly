<!--
  hover-actions.svelte — the row-hover action cluster.

  The app-wide way to put a few icon actions on a list row: bare buttons, no
  pill or panel behind them, right-aligned over the row's own metadata. They
  are always in the page and only fade in, so revealing them never rebuilds a
  subtree and never moves the row's text.

  The host row supplies the trigger: give it `class="group"` and position this
  cluster over the part of the row that may be covered.

  Revealing is a 150ms fade and settle — finite, opacity and transform only, and
  skipped entirely for a reader who asked for reduced motion.

  Usage:
    <li class="group relative">
      …row content…
      <HoverActions class="absolute top-1/2 right-2 -translate-y-1/2">
        <HoverActionButton label="Open editor" tone="info" onclick={open}>
          <FileCode2 />
        </HoverActionButton>
      </HoverActions>
    </li>
-->
<script lang="ts">
  import type { Snippet } from 'svelte';

  import { cn } from '$lib/utils.js';

  interface Props {
    children: Snippet;
    /** Names the group of actions for a screen reader, e.g. "Session actions". */
    label?: string;
    class?: string;
    'data-testid'?: string;
  }

  let { children, label, class: className, 'data-testid': dataTestId }: Props = $props();
</script>

<span
  data-slot="hover-actions"
  data-testid={dataTestId}
  aria-label={label}
  class={cn(
    'inline-flex items-center gap-1 opacity-0 pointer-events-none',
    'group-hover:opacity-100 group-hover:pointer-events-auto',
    'group-focus-within:opacity-100 group-focus-within:pointer-events-auto',
    // Fades and settles in from the right, once, in 150ms. Opacity and
    // transform only, so it costs the compositor nothing and nothing reflows.
    'motion-safe:translate-x-1 motion-safe:group-hover:translate-x-0',
    'motion-safe:group-focus-within:translate-x-0',
    'motion-safe:transition-[opacity,transform] motion-safe:duration-150 motion-safe:ease-out',
    className
  )}
>
  {@render children()}
</span>
