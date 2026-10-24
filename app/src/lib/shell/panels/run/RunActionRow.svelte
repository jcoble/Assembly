<!--
  RunActionRow.svelte — one saved action in the Run panel's list.

  The row says what the action is called, the shortcut that starts it, where it
  stands right now, and the command it runs. Its run button is always on screen
  rather than hidden until hover: starting things is what this panel is for, and
  an action nobody can see how to start is an action nobody runs. The quieter
  controls — change it, open its page, forget it — are in the standard hover
  cluster, so the row reads the same as every other row in the app.
-->
<script lang="ts">
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import Pencil from '@lucide/svelte/icons/pencil';
  import Play from '@lucide/svelte/icons/play';
  import Square from '@lucide/svelte/icons/square';
  import Trash2 from '@lucide/svelte/icons/trash-2';

  import { Chip, type ChipTone } from '$lib/components/ui/chip/index.js';
  import { HoverActionButton } from '$lib/components/ui/hover-actions/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import type { StackRow } from '$lib/shell/stacks/stackStore.svelte';

  interface Props {
    row: StackRow;
    /** A start or stop for this action is still in flight. */
    busy?: boolean;
    canStart?: boolean;
    onRun(): void;
    onStop(): void;
    onEdit(): void;
    onRemove(): void;
    onOpenPreview(): void;
  }
  let { row, busy = false, canStart = true, onRun, onStop, onEdit, onRemove, onOpenPreview }: Props = $props();

  const live = $derived(row.state === 'running' || row.state === 'starting');

  /** What each state means, said as a tone rather than a color. */
  const tone = $derived<ChipTone>(
    row.state === 'running'
      ? 'live'
      : row.state === 'starting'
        ? 'attention'
        : row.state === 'failed'
          ? 'bad'
          : 'neutral'
  );
</script>

<div class="flex items-center gap-1">
  <ListRow class="min-w-0 flex-1" actionsLabel={`Settings for ${row.definition.name}`}>
    <span class="flex min-w-0 flex-1 flex-col gap-0.5 py-0.5">
      <span class="flex min-w-0 items-center gap-2">
        <span class="min-w-0 truncate">{row.definition.name}</span>
        {#if row.definition.keybinding}
          <Chip class="font-mono">{row.definition.keybinding}</Chip>
        {/if}
        <Chip {tone}>{row.statusLabel}</Chip>
      </span>
      <span class="min-w-0 truncate font-mono text-sm text-muted-foreground">
        {row.definition.script}
      </span>
    </span>

    {#snippet actions()}
      {#if row.definition.previewUrl}
        <HoverActionButton label="Open its page in the browser" tone="info" onclick={onOpenPreview}>
          <ExternalLink />
        </HoverActionButton>
      {/if}
      <HoverActionButton label={`Change ${row.definition.name}`} onclick={onEdit}>
        <Pencil />
      </HoverActionButton>
      <HoverActionButton label={`Forget ${row.definition.name}`} onclick={onRemove}>
        <Trash2 />
      </HoverActionButton>
    {/snippet}
  </ListRow>

  {#if live}
    <IconButton label={`Stop ${row.definition.name}`} disabled={busy} onclick={onStop}>
      <Square />
    </IconButton>
  {:else}
    <IconButton label={`Run ${row.definition.name}`} disabled={busy || !canStart} onclick={onRun}>
      <Play />
    </IconButton>
  {/if}
</div>
