<!--
  RunningProcessRow.svelte — one action that is running right now, with the
  last of what it printed.

  This is a peek, not a terminal: the terminal is the session in the rail, and
  the row's title goes there. What it is for is the question you ask a dev
  server every few minutes — is it up, which port, and did it just print a
  stack trace — answered without leaving the panel you are in.

  The output box holds at most `RUN_OUTPUT_TAIL_LINES` lines and scrolls to the
  newest, so a busy process cannot grow it.
-->
<script lang="ts">
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import Square from '@lucide/svelte/icons/square';
  import Terminal from '@lucide/svelte/icons/terminal';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip, type ChipTone } from '$lib/components/ui/chip/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import type { StackRow } from '$lib/shell/stacks/stackStore.svelte';

  interface Props {
    row: StackRow;
    /** The last lines this run printed, oldest first. */
    tail?: readonly string[];
    /** A start or stop for this action is still in flight. */
    busy?: boolean;
    canRestart?: boolean;
    onStop(): void;
    onRestart(): void;
    onOpenSession(): void;
  }
  let { row, tail = [], busy = false, canRestart = true, onStop, onRestart, onOpenSession }: Props = $props();

  const tone = $derived<ChipTone>(row.state === 'running' ? 'live' : 'attention');

  /** Blank lines at the end are the line still being written; do not draw them. */
  const lines = $derived.by(() => {
    const shown = [...tail];
    while (shown.length > 0 && shown[shown.length - 1].trim() === '') shown.pop();
    return shown;
  });
</script>

<article class="flex flex-col gap-2 rounded-xl border bg-card px-3 py-2">
  <header class="flex min-w-0 items-center gap-2">
    <Button
      variant="ghost"
      size="sm"
      class="min-w-0 flex-1 justify-start truncate px-1 text-[13px] font-normal"
      onclick={onOpenSession}
    >
      {row.definition.name}
    </Button>
    <Chip {tone}>{row.statusLabel}</Chip>
    <IconButton label={`Restart ${row.definition.name}`} size="xs" disabled={busy || !canRestart} onclick={onRestart}>
      <RotateCcw />
    </IconButton>
    <IconButton label={`Stop ${row.definition.name}`} size="xs" disabled={busy} onclick={onStop}>
      <Square />
    </IconButton>
  </header>

  {#if row.processes.length > 0}
    <p class="flex flex-wrap gap-1 text-sm leading-tight text-muted-foreground">
      {#each row.processes as process (process.pid)}
        <span class="font-mono">port {process.port} · process {process.pid}</span>
      {/each}
    </p>
  {/if}

  {#if lines.length > 0}
    <pre
      class="max-h-40 overflow-auto rounded-md bg-secondary px-2 py-1.5 font-mono text-sm
             leading-[1.5] whitespace-pre-wrap text-muted-foreground"
      aria-label={`Recent output from ${row.definition.name}`}>{lines.join('\n')}</pre>
  {:else}
    <p class="flex items-center gap-1.5 text-sm leading-tight text-muted-foreground">
      <Terminal class="size-3.5 shrink-0" aria-hidden="true" />
      Nothing printed yet.
    </p>
  {/if}
</article>
