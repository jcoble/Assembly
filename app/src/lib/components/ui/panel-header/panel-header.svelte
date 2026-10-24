<!--
  panel-header.svelte — the top line of a drawer panel.

  Every panel in the right drawer opens the same way, following the Codex file
  pane: one row with the panel's own control or note on the left, how many
  things are in it, and round 28px icon actions on the right (sized here, so
  every panel's header matches the Files one). The drawer's tab row
  already names the panel, so the name is the header's accessible label only.
  A panel with nothing to show here gets just the top padding.

  Usage:
    <PanelHeader title="Worktrees" count={rows.length}>
      {#snippet actions()}
        <IconButton label="Refresh" onclick={refresh}><RefreshCw /></IconButton>
      {/snippet}
      {branchName}
    </PanelHeader>
-->
<script lang="ts">
  import type { Snippet } from 'svelte';

  import { Chip } from '$lib/components/ui/chip/index.js';
  import { cn } from '$lib/utils.js';

  interface Props {
    title: string;
    /** Renders a `count`-toned Chip before the actions when not null. */
    count?: number | null;
    /** Right-aligned action slot — put IconButtons or a DropdownMenu trigger here. */
    actions?: Snippet;
    /** Left side of the row, for a branch menu, a name or a path. */
    children?: Snippet;
    class?: string;
    'data-testid'?: string;
  }

  let {
    title,
    count = null,
    actions,
    children,
    class: className,
    'data-testid': dataTestId
  }: Props = $props();
</script>

{#if children || count !== null || actions}
  <header
    data-slot="panel-header"
    data-testid={dataTestId}
    aria-label={title}
    class={cn('flex min-h-[32px] min-w-0 flex-none items-center gap-1 px-2 pt-1.5 pb-2', className)}
  >
    <div class="min-w-0 flex-1 truncate text-[13px] leading-tight text-muted-foreground">
      {@render children?.()}
    </div>
    {#if count !== null}
      <Chip tone="count">{count}</Chip>
    {/if}
    {#if actions}
      <div class="flex shrink-0 items-center gap-1 [&_button]:size-7 [&_button]:rounded-full [&_svg]:size-4">
        {@render actions()}
      </div>
    {/if}
  </header>
{:else}
  <div data-slot="panel-header" data-testid={dataTestId} class={cn('flex-none pt-1.5', className)}></div>
{/if}
