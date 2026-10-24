<script lang="ts">
  /**
   * BrowserToolbar.svelte — the browser tab's one row, laid out like Codex's.
   *
   * Left, one pill holding back, forward and reload; then Annotate, which arms
   * the element marker (icon only when the pane is narrow, by a container
   * query). The address fills the middle. Right, one pill holding the list of
   * marks made so far and ⋯ for the other two marking tools.
   *
   * The ⋯ menu is a native popup menu: the page beside this row is a native
   * view, and anything drawn in the DOM would sit underneath it. No tooltips
   * either — Bits UI's tooltip state loops when mounted beside the native
   * view — so every control names itself with an aria-label.
   */
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import ArrowRight from '@lucide/svelte/icons/arrow-right';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import RotateCw from '@lucide/svelte/icons/rotate-cw';
  import SquareDashedMousePointer from '@lucide/svelte/icons/square-dashed-mouse-pointer';
  import type { Resource } from '@tauri-apps/api/core';
  import { CheckMenuItem, Menu } from '@tauri-apps/api/menu';
  import { onDestroy } from 'svelte';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { cn } from '$lib/utils.js';

  // `browse` is none armed.
  import type { BrowserPanelTool } from '$lib/shell/browser/browserTypes.ts';

  interface Props {
    address: string;
    tool: BrowserPanelTool;
    canGoBack: boolean;
    canGoForward: boolean;
    /** Marks placed on the page so far; the list button needs at least one. */
    annotationCount: number;
    listOpen: boolean;
    onAddressInput(value: string): void;
    onNavigate(): void;
    onBack(): void;
    onForward(): void;
    onReload(): void;
    onToolChange(tool: BrowserPanelTool): void;
    onToggleList(): void;
  }

  let {
    address,
    tool,
    canGoBack,
    canGoForward,
    annotationCount,
    listOpen,
    onAddressInput,
    onNavigate,
    onBack,
    onForward,
    onReload,
    onToolChange,
    onToggleList
  }: Props = $props();

  const ROUND = 'size-[28px] rounded-full p-0 [&_svg]:size-[16px]';

  /** The last native menu, closed when the next one opens so they never pile up. */
  let lastMenu: Resource[] = [];
  let destroyed = false;

  async function closeAll(resources: Resource[]): Promise<void> {
    await Promise.all(resources.map((item) => item.close()));
  }

  function choose(next: BrowserPanelTool): void {
    onToolChange(next === tool ? 'browse' : next);
  }

  async function openMore(): Promise<void> {
    const previous = lastMenu;
    lastMenu = [];
    await closeAll(previous);
    const region = await CheckMenuItem.new({
      text: 'Draw a region',
      checked: tool === 'region',
      action: () => choose('region')
    });
    const marker = await CheckMenuItem.new({
      text: 'Draw with a marker',
      checked: tool === 'drawing',
      action: () => choose('drawing')
    });
    const menu = await Menu.new({ items: [region, marker] });
    // The toolbar may have gone while the menu was being built.
    if (destroyed) {
      await closeAll([menu, region, marker]);
      return;
    }
    lastMenu = [menu, region, marker];
    await menu.popup();
  }

  onDestroy(() => {
    destroyed = true;
    void closeAll(lastMenu);
    lastMenu = [];
  });
</script>

<div class="@container flex h-[44px] items-center gap-2 px-2" data-testid="browser-toolbar">
  <div class="flex flex-none items-center rounded-full bg-muted p-[2px]">
    <Button variant="ghost" class={ROUND} aria-label="Go back" disabled={!canGoBack} data-testid="browser-back" onclick={onBack}>
      <ArrowLeft aria-hidden="true" />
    </Button>
    <Button
      variant="ghost"
      class={ROUND}
      aria-label="Go forward"
      disabled={!canGoForward}
      data-testid="browser-forward"
      onclick={onForward}
    >
      <ArrowRight aria-hidden="true" />
    </Button>
    <span class="mx-[2px] h-[16px] w-px bg-border" aria-hidden="true"></span>
    <Button variant="ghost" class={ROUND} aria-label="Reload the page" data-testid="browser-reload" onclick={onReload}>
      <RotateCw aria-hidden="true" />
    </Button>
  </div>

  <Button
    variant="ghost"
    class={cn(
      'h-[32px] flex-none gap-1.5 rounded-full bg-muted px-3 text-[13px] @max-[640px]:px-2 [&_svg]:size-[16px]',
      tool === 'element' && 'bg-foreground/16 text-foreground'
    )}
    aria-label={tool === 'element' ? 'Stop marking elements' : 'Annotate: mark an element on the page'}
    aria-pressed={tool === 'element'}
    data-testid="browser-tool-element"
    onclick={() => choose('element')}
  >
    <SquareDashedMousePointer aria-hidden="true" />
    <span class="@max-[640px]:hidden">Annotate</span>
  </Button>

  <form
    class="min-w-0 flex-1"
    onsubmit={(event) => {
      event.preventDefault();
      onNavigate();
    }}
  >
    <Input
      aria-label="Address"
      placeholder="Enter an http, https, or file address"
      value={address}
      class="h-[32px] rounded-full border-0 bg-muted px-3 text-center text-[13px] focus:text-left focus-visible:ring-2 focus-visible:ring-ring/50 dark:bg-muted"
      data-testid="browser-address"
      oninput={(event) => onAddressInput(event.currentTarget.value)}
    />
  </form>

  <div class="flex flex-none items-center rounded-full bg-muted p-[2px]">
    <Button
      variant="ghost"
      class={ROUND}
      aria-label={listOpen ? 'Hide the list of marks' : 'Show the list of marks'}
      aria-pressed={listOpen}
      disabled={annotationCount === 0}
      data-testid="browser-annotation-list-toggle"
      onclick={onToggleList}
    >
      <MessageSquare aria-hidden="true" />
    </Button>
    <Button variant="ghost" class={ROUND} aria-label="More marking tools" data-testid="browser-more" onclick={openMore}>
      <Ellipsis aria-hidden="true" />
    </Button>
  </div>
</div>
