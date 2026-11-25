<!--
  RunningProcessRow.svelte — one action that is running right now, with its live output tail.

  1:1 port from mac-command-bar:
  Keeps at most `RUN_OUTPUT_TAIL_LINES` lines and scrolls to the newest output automatically.
-->
<script lang="ts">
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import Square from '@lucide/svelte/icons/square';
  import Terminal from '@lucide/svelte/icons/terminal';

  import { Button } from '$lib/ui/button/index.js';
  import { Chip, type ChipTone } from '$lib/ui/chip/index.js';
  import { IconButton } from '$lib/ui/icon-button/index.js';

  import type { RunRow } from './types';

  interface Props {
    row: RunRow;
    /** The last lines this run printed, oldest first. */
    tail?: readonly string[];
    /** A start or stop for this action is still in flight. */
    busy?: boolean;
    canRestart?: boolean;
    onStop(): void;
    onRestart(): void;
    onOpenSession(): void;
  }
  let {
    row,
    tail = [],
    busy = false,
    canRestart = true,
    onStop,
    onRestart,
    onOpenSession
  }: Props = $props();

  let preEl = $state<HTMLPreElement | null>(null);

  const tone = $derived<ChipTone>(row.state === 'running' ? 'live' : 'attention');

  /** Blank lines at the end are the line still being written; omit them when rendering. */
  const lines = $derived.by(() => {
    const shown = [...tail];
    while (shown.length > 0 && shown[shown.length - 1].trim() === '') shown.pop();
    return shown;
  });

  const outputText = $derived(lines.join('\n'));

  $effect(() => {
    if (preEl && outputText.length > 0) {
      preEl.scrollTop = preEl.scrollHeight;
    }
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
    <Chip {tone} class="text-xs">{row.statusLabel}</Chip>
    <IconButton
      label={`Restart ${row.definition.name}`}
      size="xs"
      disabled={busy || !canRestart}
      onclick={onRestart}
    >
      <RotateCcw />
    </IconButton>
    <IconButton label={`Stop ${row.definition.name}`} size="xs" disabled={busy} onclick={onStop}>
      <Square />
    </IconButton>
  </header>

  {#if row.processes.length > 0}
    <p class="flex flex-wrap gap-1 text-xs leading-tight text-muted-foreground">
      {#each row.processes as process (process.pid)}
        <span class="font-mono">port {process.port} · process {process.pid}</span>
      {/each}
    </p>
  {:else if row.pid !== null}
    <p class="flex flex-wrap gap-1 text-xs leading-tight text-muted-foreground">
      <span class="font-mono">process {row.pid}</span>
    </p>
  {/if}

  {#if lines.length > 0}
    <pre
      bind:this={preEl}
      class="max-h-40 overflow-auto rounded-md bg-secondary px-2 py-1.5 font-mono text-xs
             leading-[1.5] whitespace-pre-wrap text-muted-foreground"
      aria-label={`Recent output from ${row.definition.name}`}>{outputText}</pre>
  {:else}
    <p class="flex items-center gap-1.5 text-xs leading-tight text-muted-foreground">
      <Terminal class="size-3.5 shrink-0" aria-hidden="true" />
      Nothing printed yet.
    </p>
  {/if}
</article>
