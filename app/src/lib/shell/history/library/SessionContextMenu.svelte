<script lang="ts">
  import { onMount } from 'svelte';

  import type {
    SessionContextMenuAction,
    SessionContextMenuAnchor,
    SessionContextMenuItem
  } from './sessionLibraryContextMenu';
  import { placeSessionContextMenu } from './sessionLibraryContextMenu';

  interface Props {
    anchor: SessionContextMenuAnchor;
    items: SessionContextMenuItem[];
    onSelect(action: SessionContextMenuAction): void;
    onClose(): void;
  }

  let { anchor, items, onSelect, onClose }: Props = $props();

  let menuElement: HTMLDivElement | null = null;
  let measuredWidth = $state(0);
  let measuredHeight = $state(0);
  let viewportWidth = $state(typeof window === 'undefined' ? 0 : window.innerWidth);
  let viewportHeight = $state(typeof window === 'undefined' ? 0 : window.innerHeight);

  const placement = $derived(placeSessionContextMenu(
    anchor,
    { width: measuredWidth, height: measuredHeight },
    { width: viewportWidth, height: viewportHeight }
  ));

  onMount(() => {
    viewportWidth = window.innerWidth;
    viewportHeight = window.innerHeight;
    const rect = menuElement?.getBoundingClientRect();
    if (!rect) return;
    measuredWidth = rect.width;
    measuredHeight = rect.height;
  });
</script>

<div
  bind:this={menuElement}
  data-testid="session-context-menu"
  class="session-context-menu"
  role="menu"
  tabindex="-1"
  aria-label="Session actions"
  style={`left: ${placement.left}px; top: ${placement.top}px; visibility: ${measuredWidth > 0 && measuredHeight > 0 ? 'visible' : 'hidden'}`}
  onclick={(event) => event.stopPropagation()}
  oncontextmenu={(event) => event.preventDefault()}
  onkeydown={(event) => {
    if (event.key === 'Escape') onClose();
  }}
>
  {#each items as item (item.id)}
    <button
      data-testid={`session-context-menu-${item.id}`}
      type="button"
      role="menuitem"
      class="menu-item"
      class:disabled={!item.enabled}
      disabled={!item.enabled}
      aria-disabled={!item.enabled}
      title={item.enabled ? item.label : item.disabledReason ?? item.label}
      onclick={() => {
        if (item.enabled) onSelect(item.id);
      }}
    >{item.label}</button>
  {/each}
</div>

<style>
  .session-context-menu { position: fixed; z-index: 1000; display: flex; min-width: 216px; flex-direction: column; gap: 2px; border: 1px solid var(--color-border); border-radius: var(--radius-md); padding: 5px; background: var(--color-bg); box-shadow: var(--shadow-lg); }
  .menu-item { width: 100%; border-radius: var(--radius-sm); padding: 8px 9px; color: var(--color-text-2); font-size: 13px; font-weight: var(--weight-medium); text-align: left; }
  .menu-item:hover:not(:disabled), .menu-item:focus-visible:not(:disabled) { background: var(--color-surface); color: var(--color-text); outline: none; }
  .menu-item.disabled { cursor: not-allowed; color: var(--color-disabled-text); }
</style>
