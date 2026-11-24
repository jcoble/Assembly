<!--
  chip.svelte — the small pill a panel puts beside a title or on a row.

  A count in a section header, "needs you" on a session, "running" on a
  process, "failed" on a build. `tone` says what the chip means, not which
  color to paint it: `count` for a number, `attention` for the needs-you amber,
  and `live` / `good` / `bad` for the shell's status colors.

  Usage:
    <Chip tone="count">{files.length}</Chip>
    <Chip tone="attention">Needs you</Chip>
-->
<script lang="ts">
  import type { Snippet } from 'svelte';

  import { cn } from '$lib/utils.js';

  import { chipVariants, type ChipTone } from './variants.js';

  interface Props {
    children: Snippet;
    /** `attention` is the needs-you amber; `live`/`good`/`bad` are the shell status colors. */
    tone?: ChipTone;
    class?: string;
    'data-testid'?: string;
  }

  let { children, tone = 'neutral', class: className, 'data-testid': dataTestId }: Props =
    $props();
</script>

<span data-slot="chip" data-testid={dataTestId} class={cn(chipVariants({ tone }), className)}>
  {@render children()}
</span>
