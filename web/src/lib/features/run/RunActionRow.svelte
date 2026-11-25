<!--
  RunActionRow.svelte — one saved action in the Run panel's list.

  1:1 port from mac-command-bar:
  Shows action name, optional shortcut chip, live status chip, command subtitle,
  hover action cluster (preview, edit, forget), and always-visible Run / Stop button.
-->
<script lang="ts">
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import Pencil from '@lucide/svelte/icons/pencil';
  import Play from '@lucide/svelte/icons/play';
  import Square from '@lucide/svelte/icons/square';
  import Trash2 from '@lucide/svelte/icons/trash-2';

  import { Chip, type ChipTone } from '$lib/ui/chip/index.js';
  import { HoverActionButton } from '$lib/ui/hover-actions/index.js';
  import { IconButton } from '$lib/ui/icon-button/index.js';
  import { ListRow } from '$lib/ui/list-row/index.js';

  import type { RunRow } from './types';

  interface Props {
    row: RunRow;
    /** A start or stop for this action is still in flight. */
    busy?: boolean;
    canStart?: boolean;
    onRun(): void;
    onStop(): void;
    onEdit(): void;
    onRemove(): void;
    onOpenPreview(): void;
  }
  let {
    row,
    busy = false,
    canStart = true,
    onRun,
    onStop,
    onEdit,
    onRemove,
    onOpenPreview
  }: Props = $props();

  const live = $derived(row.state === 'running' || row.state === 'starting');

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
      <span class="flex min-w-0 flex-wrap items-center gap-1.5">
        <span class="min-w-0 truncate font-medium">{row.definition.name}</span>
        {#if row.definition.keybinding}
          <Chip class="font-mono text-xs">{row.definition.keybinding}</Chip>
        {/if}
        <Chip {tone} class="text-xs">{row.statusLabel}</Chip>
      </span>
      <span class="min-w-0 truncate font-mono text-xs text-muted-foreground">
        {row.definition.script}
      </span>
    </span>

    {#snippet actions()}
      <span class="inline-flex items-center gap-0.5 rounded-full bg-card/95 pl-1 shadow-xs">
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
      </span>
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
