<script lang="ts">
  /**
   * RightPanelTabs.svelte — the tab row across the top of the right drawer.
   *
   * One rounded island of icon tabs, Codex style: four permanent ones (Files,
   * Source control, Agents, Tasks), then the one ⋯-menu tab last picked
   * (Worktrees, Terminal, Run, Context or History; the next pick replaces it); outside
   * the island, the ⋯ menu and the drawer's close button. Tabs are
   * not closable; only the drawer closes.
   * PRESENTATIONAL ONLY: the selected tab is handed in and every click is
   * handed back out.
   */
  import Bot from '@lucide/svelte/icons/bot';
  import CirclePlay from '@lucide/svelte/icons/circle-play';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import Files from '@lucide/svelte/icons/files';
  import Gauge from '@lucide/svelte/icons/gauge';
  import GitBranch from '@lucide/svelte/icons/git-branch';
  import GitFork from '@lucide/svelte/icons/git-fork';
  import History from '@lucide/svelte/icons/history';
  import ListChecks from '@lucide/svelte/icons/list-checks';
  import SquareTerminal from '@lucide/svelte/icons/square-terminal';
  import X from '@lucide/svelte/icons/x';

  import { Badge } from '$lib/components/ui/badge/index.js';
  import { Button, buttonVariants } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import * as Tabs from '$lib/components/ui/tabs/index.js';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';
  import type { RightExtraTabId } from '$lib/shell/controllers/workbenchController.svelte';
  import type { RightTabId } from '$lib/shell/workbenchNavigation';

  interface Props {
    activeId: RightTabId;
    extraId: RightExtraTabId | null;
    onSelect(id: RightTabId): void;
    onClose(): void;
    /** New messages for the selected session, shown on the Agents tab. */
    agentsUnread: number;
  }
  let { activeId, extraId, onSelect, onClose, agentsUnread }: Props = $props();

  const PERMANENT = [
    { id: 'files', label: 'Files', icon: Files },
    { id: 'source-control', label: 'Source control', icon: GitBranch },
    { id: 'agents', label: 'Agents', icon: Bot },
    { id: 'tasks', label: 'Tasks', icon: ListChecks }
  ] as const;

  const EXTRA = [
    { id: 'worktrees', label: 'Worktrees', icon: GitFork },
    { id: 'terminal', label: 'Terminal', icon: SquareTerminal },
    { id: 'run', label: 'Run', icon: CirclePlay },
    { id: 'context', label: 'Context', icon: Gauge },
    { id: 'history', label: 'History', icon: History }
  ] as const;

  // A 24px pill with a 14px glyph in the --muted track; the active one is a
  // white-16% fill with no border, shadow or underline.
  const PILL =
    'h-6 w-7 flex-none rounded-full border-transparent p-0 text-muted-foreground hover:bg-foreground/6 [&_svg]:size-[14px] group-data-[variant=default]/tabs-list:data-active:shadow-none data-active:bg-foreground/16 dark:data-active:border-transparent dark:data-active:bg-foreground/16';

  const shown = $derived([...PERMANENT, ...EXTRA.filter((tab) => tab.id === extraId)]);
</script>

<!-- 44px: the header band the rail and editor headers share. -->
<div class="flex h-11 min-w-0 flex-none items-center gap-1 px-2">
  <!-- A function binding, so the row never keeps its own copy of the active tab. -->
  <Tabs.Root bind:value={() => activeId, (id) => onSelect(id as RightTabId)} class="min-w-0 flex-1">
    <Tabs.List class="gap-0.5 rounded-full bg-muted p-[3px] group-data-[orientation=horizontal]/tabs:h-auto" aria-label="Side panel">
      {#each shown as tab (tab.id)}
        <Tooltip.Root>
          <Tooltip.Trigger>
            {#snippet child({ props })}
              <Tabs.Trigger {...props} value={tab.id} aria-label={tab.label} class={PILL}>
                <tab.icon class="size-[14px]" strokeWidth={2} aria-hidden="true" />
                {#if tab.id === 'agents' && agentsUnread > 0}
                  <Badge class="pointer-events-none absolute -top-1 -right-1 h-4 min-w-4 px-1" aria-label={`${agentsUnread} unread messages`}>
                    {agentsUnread}
                  </Badge>
                {/if}
              </Tabs.Trigger>
            {/snippet}
          </Tooltip.Trigger>
          <Tooltip.Content side="bottom" align="start">{tab.label}</Tooltip.Content>
        </Tooltip.Root>
      {/each}
    </Tabs.List>
  </Tabs.Root>

  <!-- The menu opens down and to the left, inside the drawer: anything drawn
       past its edge would sit under the live browser page beside it. -->
  <DropdownMenu.Root>
    <DropdownMenu.Trigger class={buttonVariants({ variant: 'ghost', size: 'icon-sm' })} aria-label="More panels">
      <Ellipsis class="size-[14px]" strokeWidth={2} aria-hidden="true" />
    </DropdownMenu.Trigger>
    <DropdownMenu.Content side="bottom" align="end" class="w-44">
      {#each EXTRA as tab (tab.id)}
        <DropdownMenu.Item onSelect={() => onSelect(tab.id)}>
          <tab.icon strokeWidth={1.75} aria-hidden="true" />
          {tab.label}
        </DropdownMenu.Item>
      {/each}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
  <Button variant="ghost" size="icon-sm" aria-label="Close side panel" onclick={onClose}>
    <X class="size-[14px]" strokeWidth={2} aria-hidden="true" />
  </Button>
</div>
