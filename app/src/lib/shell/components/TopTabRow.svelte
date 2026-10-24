<script lang="ts" module>
  import type { TopTabKind } from '$lib/shell/layout/topTabsOps';

  /** One tab as the row draws it. Built by the page from the owning stores. */
  export interface TopTabView {
    key: string;
    kind: TopTabKind;
    label: string;
    /** Hover text, one fact per line; drawn by the OS above the native page. */
    detail: string;
    /** Editor tabs only: the file's absolute path. */
    path?: string;
    dirty?: boolean;
    /** Editor tabs only: a preview file, replaced by the next one opened. */
    preview?: boolean;
    /** Changes tab only: working-tree lines added and removed. */
    additions?: number;
    deletions?: number;
  }

  /** What the `+` menu can open. */
  export type NewTabKind = 'browser' | 'diff' | 'git-history' | 'pull-requests' | 'file';
</script>

<script lang="ts">
  /**
   * TopTabRow.svelte — the window chrome's tab row (Codex style).
   *
   * Custom because shadcn has no closable, scrollable row of mixed tabs; every
   * control inside it is stock shadcn. The Chat tab is always first. While the
   * pane sits beside the chat it is the title over the chat column; while the
   * pane is expanded it is the first tab, and clicking it collapses the pane.
   * After it: one tab per open file, browser page, Changes, History and Pull
   * requests, then `+` (a menu: browser page, Changes, History, Pull requests
   * or a file from the Files drawer), expand, and the drawer toggle.
   *
   * Tabs keep their natural width up to 180px; once they no longer fit, the
   * row scrolls rather than squeezing them. Hover details are the native `title`
   * tooltip, the one popup the OS draws above a live browser page.
   *
   * PRESENTATIONAL ONLY: the tabs and the active key are handed in, and every
   * click is handed back out. The one exception is the Board button, a
   * self-contained control that reads its own count.
   */
  import { untrack } from 'svelte';
  import FileSearch from '@lucide/svelte/icons/file-search';
  import GitBranch from '@lucide/svelte/icons/git-branch';
  import GitCommitHorizontal from '@lucide/svelte/icons/git-commit-horizontal';
  import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
  import Globe from '@lucide/svelte/icons/globe';
  import Maximize2 from '@lucide/svelte/icons/maximize-2';
  import MessageCircle from '@lucide/svelte/icons/message-circle';
  import Minimize2 from '@lucide/svelte/icons/minimize-2';
  import PanelRight from '@lucide/svelte/icons/panel-right';
  import Plus from '@lucide/svelte/icons/plus';
  import X from '@lucide/svelte/icons/x';

  import { Button, buttonVariants } from '$lib/components/ui/button/index.js';
  import * as ContextMenu from '$lib/components/ui/context-menu/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { Separator } from '$lib/components/ui/separator/index.js';
  import BoardButton from '$lib/shell/board/BoardButton.svelte';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';
  import { cn } from '$lib/utils.js';
  import FileIcon from './explorer/FileIcon.svelte';
  import { revealTab, scrollTabStrip } from './tabScrolling';

  interface Props {
    tabs: TopTabView[];
    activeKey: string | null;
    expanded: boolean;
    /** The tab pane is in the frame (at least one tab is open). */
    paneOpen: boolean;
    chatTitle: string;
    drawerOpen: boolean;
    canOpenBrowser: boolean;
    onSelect(key: string): void;
    onClose(key: string): void;
    onSelectChat(): void;
    onOpen(kind: NewTabKind): void;
    onToggleExpanded(): void;
    onToggleDrawer(): void;
    editorActions: {
      pin(path: string): void;
      closeOthers(path: string): void;
      closeSaved(): void;
      timeline(path: string): void;
    };
  }
  let {
    tabs,
    activeKey,
    expanded,
    paneOpen,
    chatTitle,
    drawerOpen,
    canOpenBrowser,
    onSelect,
    onClose,
    onSelectChat,
    onOpen,
    onToggleExpanded,
    onToggleDrawer,
    editorActions
  }: Props = $props();

  const ICONS = {
    browser: Globe,
    diff: GitBranch,
    'git-history': GitCommitHorizontal,
    'pull-requests': GitPullRequest
  } as const;

  const NEW_TAB_ITEMS = [
    { kind: 'browser', label: 'New browser tab', icon: Globe },
    { kind: 'diff', label: 'Changes', icon: GitBranch },
    { kind: 'git-history', label: 'Source control history', icon: GitCommitHorizontal },
    { kind: 'pull-requests', label: 'Pull requests', icon: GitPullRequest },
    { kind: 'file', label: 'Open file…', icon: FileSearch }
  ] as const;

  // Picking an item opens a new surface (or focuses the Files filter), so the
  // menu must not hand focus back to `+` as it closes; Escape still does.
  let newTabPicked = false;

  let track = $state<HTMLElement | null>(null);

  // Collapsed, the chat column's title sits over the chat and the tabs over
  // the pane (and over the drawer beside it, when open); expanded, the Chat
  // tab leads the one row.
  const columns = $derived(
    expanded
      ? 'auto minmax(0, 1fr)'
      : `minmax(0, 1fr) ${paneOpen ? 'calc(var(--tools-rail-width) + var(--drawer-width, 0px))' : 'auto'}`
  );

  $effect(() => {
    activeKey;
    untrack(() => revealTab(track, track?.querySelector('[data-active="true"]') ?? null));
  });

  // The row also narrows without a new selection (layout settling at launch,
  // the drawer opening), which can push the active tab out of view.
  $effect(() => {
    const strip = track;
    if (!strip) return;
    const observer = new ResizeObserver(() => revealTab(strip, strip.querySelector('[data-active="true"]')));
    observer.observe(strip);
    return () => observer.disconnect();
  });
</script>

{#snippet tabChip(tab: TopTabView, props: Record<string, unknown>)}
  {@const active = tab.key === activeKey}
  <div
    {...props}
    title={tab.detail}
    class={cn(
      'group relative flex h-[30px] max-w-[180px] shrink-0 items-center gap-2 rounded-[6px] pl-3 text-[13px] transition-colors',
      active || tab.dirty ? 'pr-1.5' : 'pr-3',
      active ? 'bg-secondary text-foreground' : 'text-foreground/70 hover:bg-accent hover:text-foreground'
    )}
    data-active={active}
  >
    <button
      type="button"
      role="tab"
      aria-selected={active}
      class="flex h-full min-w-0 flex-1 items-center gap-2 text-left outline-none"
      onclick={() => onSelect(tab.key)}
    >
      {#if tab.kind === 'editor'}
        <FileIcon fileName={tab.label} size={16} />
      {:else}
        {@const Icon = ICONS[tab.kind]}
        <Icon class="size-[16px] shrink-0" aria-hidden="true" />
      {/if}
      <span class={cn('min-w-0 truncate', tab.preview && 'italic')}>{tab.label}</span>
      {#if tab.additions || tab.deletions}
        <span class="inline-flex shrink-0 gap-1 text-[11px] tabular-nums">
          <span class="text-(--color-good)">+{tab.additions}</span>
          <span class="text-(--color-bad)">−{tab.deletions}</span>
        </span>
      {/if}
    </button>
    <Button
      variant="ghost"
      aria-label={`Close ${tab.label}${tab.dirty ? ' (unsaved)' : ''}`}
      class={cn(
        'size-[20px] shrink-0 rounded-full p-0',
        // A resting tab's hidden × takes no room, so its label keeps the full
        // width; on hover the × sits over the label's end.
        active || tab.dirty
          ? 'opacity-100'
          : 'absolute right-1 bg-accent opacity-0 group-hover:opacity-100 focus-visible:opacity-100'
      )}
      onclick={() => onClose(tab.key)}
    >
      {#if tab.dirty}
        <span class="group-hover:hidden" aria-hidden="true">•</span>
        <X class="hidden size-[14px] group-hover:block" aria-hidden="true" />
      {:else}
        <X class="size-[14px]" aria-hidden="true" />
      {/if}
    </Button>
  </div>
{/snippet}

<div
  class="grid h-full min-w-0 items-center"
  style:grid-template-columns={columns}
  data-tauri-drag-region
>
  <div class={cn('flex h-full min-w-0 items-center gap-1', expanded ? 'pl-2' : 'px-2')} data-tauri-drag-region>
    <!-- Collapsed, chat is on screen beside the pane, so its tab reads as active. -->
    <button
      type="button"
      class={cn(
        'flex h-[30px] min-w-0 items-center gap-2 rounded-[6px] px-3 text-[13px] transition-colors',
        expanded ? 'max-w-[224px] text-foreground hover:bg-accent' : 'bg-secondary text-foreground'
      )}
      title={chatTitle}
      aria-label={expanded ? `Chat: ${chatTitle}. Collapse the pane` : `Chat: ${chatTitle}`}
      onclick={onSelectChat}
    >
      <MessageCircle class="size-[16px] shrink-0" aria-hidden="true" />
      <span class="truncate">{chatTitle}</span>
    </button>
    {#if expanded}<Separator orientation="vertical" class="h-4" />{/if}
  </div>

  <!-- The tabs and + sit together on the left; the window controls hold the
       right end, and the free space between them drags the window. -->
  <div class="flex h-full min-w-0 items-center gap-1 px-2" data-tauri-drag-region>
    <div
      bind:this={track}
      class="flex min-w-0 items-center gap-1 overflow-x-auto [scrollbar-width:none]"
      role="tablist"
      aria-label="Open tabs"
      use:scrollTabStrip
    >
      {#each tabs as tab (tab.key)}
        {#if tab.kind === 'editor' && tab.path}
          {@const path = tab.path}
          <!-- Opening the menu activates its tab first, so the menu never
               floats over a live browser page (a native view no DOM can cover). -->
          <ContextMenu.Root onOpenChange={(open) => { if (open) onSelect(tab.key); }}>
            <ContextMenu.Trigger>
              {#snippet child({ props })}
                {@render tabChip(tab, props)}
              {/snippet}
            </ContextMenu.Trigger>
            <ContextMenu.Content class="w-[220px]" aria-label={`Actions for ${tab.label}`}>
              {#if tab.preview}
                <ContextMenu.Item onSelect={() => editorActions.pin(path)}>Pin Tab</ContextMenu.Item>
              {/if}
              <ContextMenu.Item onSelect={() => onClose(tab.key)}>Close</ContextMenu.Item>
              <ContextMenu.Item
                disabled={!tabs.some((other) => other.kind === 'editor' && other.key !== tab.key && !other.dirty)}
                onSelect={() => editorActions.closeOthers(path)}
              >Close other clean files</ContextMenu.Item>
              <ContextMenu.Item
                disabled={!tabs.some((other) => other.kind === 'editor' && !other.dirty)}
                onSelect={editorActions.closeSaved}
              >Close saved files</ContextMenu.Item>
              <ContextMenu.Item onSelect={() => editorActions.timeline(path)}>File Timeline</ContextMenu.Item>
            </ContextMenu.Content>
          </ContextMenu.Root>
        {:else}
          {@render tabChip(tab, {})}
        {/if}
      {/each}
    </div>

    <!-- A menu (role="menu"), so the live browser page steps aside while it is open. -->
    <DropdownMenu.Root>
      <DropdownMenu.Trigger class={cn(buttonVariants({ variant: 'ghost', size: 'icon-sm' }), 'flex-none')} aria-label="Open a tab">
        <Plus aria-hidden="true" />
      </DropdownMenu.Trigger>
      <DropdownMenu.Content
        side="bottom"
        align="start"
        class="w-56"
        onCloseAutoFocus={(event) => {
          if (newTabPicked) event.preventDefault();
          newTabPicked = false;
        }}
      >
        <DropdownMenu.Group>
          {#each NEW_TAB_ITEMS as item (item.kind)}
            <DropdownMenu.Item
              disabled={item.kind === 'browser' && !canOpenBrowser}
              onSelect={() => {
                newTabPicked = true;
                onOpen(item.kind);
              }}
            >
              <item.icon aria-hidden="true" />
              {item.label}
            </DropdownMenu.Item>
          {/each}
        </DropdownMenu.Group>
      </DropdownMenu.Content>
    </DropdownMenu.Root>

    <div class="ml-auto flex flex-none items-center gap-1">
      <BoardButton />
      <Separator orientation="vertical" class="mx-1 h-[14px]" />
      {#if paneOpen}
        <Tooltip.Root>
          <Tooltip.Trigger
            class={buttonVariants({ variant: 'ghost', size: 'icon-sm' })}
            aria-label={expanded ? 'Collapse the pane' : 'Expand the pane'}
            onclick={onToggleExpanded}
          >
            {#if expanded}<Minimize2 aria-hidden="true" />{:else}<Maximize2 aria-hidden="true" />{/if}
          </Tooltip.Trigger>
          <Tooltip.Content side="bottom">{expanded ? 'Collapse' : 'Expand'}</Tooltip.Content>
        </Tooltip.Root>
      {/if}
      <Tooltip.Root>
        <Tooltip.Trigger
          class={buttonVariants({ variant: 'ghost', size: 'icon-sm' })}
          aria-label={drawerOpen ? 'Close side panel' : 'Open side panel'}
          aria-pressed={drawerOpen}
          onclick={onToggleDrawer}
        >
          <PanelRight aria-hidden="true" />
        </Tooltip.Trigger>
        <Tooltip.Content side="bottom">{drawerOpen ? 'Close side panel' : 'Open side panel'}</Tooltip.Content>
      </Tooltip.Root>
    </div>
  </div>
</div>
