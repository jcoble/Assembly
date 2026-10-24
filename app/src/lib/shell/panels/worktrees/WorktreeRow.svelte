<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * WorktreeRow.svelte — one worktree in the panel's list.
   *
   * Draws only. Every value it shows was worked out by
   * `worktrees/worktreeManagerRows.ts` and every button hands straight back to
   * the panel; nothing here reads the machine or decides anything.
   *
   * A CLOSED row is one line and stays one line: the branch, the short status
   * words, and the actions. The folder, the age, the remote standing and the
   * sessions live in the open body, where there is room to label them — on the
   * closed row they used to wrap into an unreadable stack in a narrow panel.
   *
   * The row is a kit `ListRow`, so its hover cluster and its focus ring are the
   * same ones every other panel's rows have. Its inset and its text size are
   * set at the foot of this file: the list carries a branch name, its status
   * words and its actions on one line, so it takes the shell's body size and a
   * card's inset rather than the dense sizing a plain file list wants. The
   * actions are the same three the open body offers as words; a person who
   * already knows which one they want should not have to open the row to reach
   * it.
   */
  import Archive from '@lucide/svelte/icons/archive';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import Eraser from '@lucide/svelte/icons/eraser';
  import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
  import ScanSearch from '@lucide/svelte/icons/scan-search';
  import Trash2 from '@lucide/svelte/icons/trash-2';
  import type { Component, Snippet } from 'svelte';

  import { Chip } from '$lib/components/ui/chip/index.js';
  import { HoverActionButton } from '$lib/components/ui/hover-actions/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import type {
    WorktreeChipTone,
    WorktreeManagerRow
  } from '$lib/shell/worktrees/worktreeManagerRows';
  import { cn } from '$lib/utils';

  import type {
    WorktreeAgentAction,
    WorktreeAgentActionId
  } from './worktreeAgentPrompts.ts';

  interface Props {
    row: WorktreeManagerRow;
    /** The three agent actions for this row, already worked out. */
    agentActions: WorktreeAgentAction[];
    /** This row's details are open below it. */
    expanded: boolean;
    /** A session is being started for this row. */
    busy: boolean;
    /** Something is happening somewhere in the panel; every button waits. */
    panelBusy: boolean;
    onToggle(): void;
    /** Start the session for one of the three actions. */
    onAction(id: WorktreeAgentActionId): void;
    /** Ask the "clear git's record" question, for a row whose folder is gone. */
    onAskClear(): void;
    /** The details, drawn inside this row while it is open. */
    detail?: Snippet;
  }
  let {
    row,
    agentActions,
    expanded,
    busy,
    panelBusy,
    onToggle,
    onAction,
    onAskClear,
    detail
  }: Props = $props();

  /**
   * Every chip on a row, at the size the rest of the shell reads at. The kit's
   * own `text-sm` lands at 10.5px here, because this app sets the root font to
   * 14px and the kit's sizes are in rem — which is most of why these rows read
   * as smaller than they look in the source.
   */
  const CHIP = 'h-6 shrink-0 px-2 text-(length:--text-quiet) font-normal';

  /**
   * A chip that names the folder rather than warning about it. It reads as
   * quiet metadata beside the branch, which leaves the coloured chips as the
   * only thing on the row asking for attention.
   */
  const QUIET_CHIP = `${CHIP} bg-foreground/8 text-muted-foreground`;

  /** A chip says how loudly it means it; the kit says which colour that is. */
  const CHIP_TONE: Record<WorktreeChipTone, 'bad' | 'attention' | 'neutral'> = {
    danger: 'bad',
    warn: 'attention',
    info: 'neutral'
  };

  const ICONS: Record<WorktreeAgentActionId, Component> = {
    inspect: ScanSearch,
    'archive-and-remove': Archive,
    remove: Trash2
  };

  /**
   * What a button says on hover: what it will do, or why it will not. A
   * switched-off button never fires the events a tooltip needs, so the reason
   * has to be part of the label itself rather than a separate hover card.
   */
  function actionLabel(action: WorktreeAgentAction): string {
    if (!action.enabled) return `${action.label} — ${action.disabledReason ?? 'not available'}`;
    if (action.id === 'inspect') {
      return 'Inspect: asks the utility model about this worktree and shows the answer under the row';
    }
    if (action.id === 'archive-and-remove') {
      return 'Archive and remove: starts a session that copies the work somewhere safe, then removes the worktree';
    }
    return 'Remove: starts a session that checks this worktree, warns about anything risky, then removes it';
  }
</script>

<li class={cn('worktree-row rounded-xl transition-colors', expanded && 'bg-foreground/8')}>
  <ListRow selected={expanded} actionsLabel="Worktree actions" onclick={onToggle}>
    <ChevronRight
      class={cn(
        'size-4 shrink-0 text-muted-foreground transition-transform',
        expanded && 'rotate-90'
      )}
      aria-hidden="true"
    />
    <FolderGit2 class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
    <!-- The branch name keeps about eight characters; past that the chip
         group gives way and clips the chips that no longer fit. -->
    <span class="min-w-[8ch] flex-1 truncate font-medium" title={row.path}>{row.branch}</span>
    <span class="flex min-w-0 items-center gap-1.5 overflow-hidden">
      {#if row.isPrimary}
        <Chip class={QUIET_CHIP}>main checkout</Chip>
      {/if}
      {#if row.taskId}
        <Chip class={QUIET_CHIP}>{row.taskId}</Chip>
      {/if}
      {#each row.chips as chip (chip.id)}
        <Chip tone={CHIP_TONE[chip.tone]} class={CHIP}>{chip.short}</Chip>
      {/each}
    </span>
    {#if busy}
      <span class="shrink-0 text-(length:--text-quiet) leading-tight text-muted-foreground">
        <WorkingSpinner size={12} /> Starting…
      </span>
    {/if}

    {#snippet actions()}
      {#if row.folderGone}
        <HoverActionButton
          label="Clear git’s record of this worktree. The folder is already gone, so nothing on disk is touched. You are asked first."
          disabled={panelBusy}
          onclick={onAskClear}
        >
          <Eraser aria-hidden="true" />
        </HoverActionButton>
      {/if}
      {#each agentActions as action (action.id)}
        {@const Icon = ICONS[action.id]}
        <HoverActionButton
          label={actionLabel(action)}
          tone={action.id === 'inspect' ? 'info' : 'default'}
          disabled={!action.enabled || panelBusy}
          onclick={() => onAction(action.id)}
        >
          <Icon aria-hidden="true" />
        </HoverActionButton>
      {/each}
    {/snippet}
  </ListRow>

  {#if expanded && detail}
    <div class="border-t border-border px-(--space-4) pt-(--space-3) pb-(--space-4)">
      {@render detail()}
    </div>
  {/if}
</li>

<style>
  /* The kit's row is sized for a dense file list. A worktree row is the thing
     a decision is made about, so it takes the shell's body size and the same
     12 by 16 inset the cards use, and its hover cluster moves in to match. */
  .worktree-row :global([data-slot='list-row'] > button) {
    gap: var(--space-2);
    padding: var(--space-3) var(--space-4);
    font-size: var(--text-body);
  }
  .worktree-row :global([data-slot='hover-actions']) {
    right: var(--space-3);
  }
</style>
