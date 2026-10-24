<script lang="ts">
  import { onMount } from 'svelte';

  import {
    placeSourceControlContextMenu,
    type SourceControlContextMenuAction,
    type SourceControlContextMenuAnchor,
    type SourceControlContextMenuItem
  } from './sourceControlContextMenu.ts';

  interface Props {
    anchor: SourceControlContextMenuAnchor;
    items: SourceControlContextMenuItem<SourceControlContextMenuAction, string>[];
    onSelect(action: SourceControlContextMenuAction): void;
    onClose(): void;
  }

  let { anchor, items, onSelect, onClose }: Props = $props();
  let menuElement: HTMLDivElement | null = null;
  let placement = $state({ left: 0, top: 0 });
  let measured = $state(false);

  onMount(() => {
    const rect = menuElement?.getBoundingClientRect();
    if (rect) {
      placement = placeSourceControlContextMenu(anchor, {
        width: rect.width,
        height: rect.height
      });
      measured = true;
      menuElement?.focus();
    }

    const closeOutside = (event: PointerEvent): void => {
      if (event.target instanceof Node && menuElement?.contains(event.target)) return;
      onClose();
    };
    document.addEventListener('pointerdown', closeOutside, true);
    return () => document.removeEventListener('pointerdown', closeOutside, true);
  });
</script>

<div
  bind:this={menuElement}
  data-testid="source-control-context-menu"
  class="source-control-context-menu"
  role="menu"
  tabindex="-1"
  aria-label="Source control actions"
  style={`left: ${placement.left}px; top: ${placement.top}px; visibility: ${measured ? 'visible' : 'hidden'}`}
  onclick={(event) => event.stopPropagation()}
  oncontextmenu={(event) => event.preventDefault()}
  onkeydown={(event) => {
    if (event.key === 'Escape') onClose();
  }}
>
  {#each items as item (item.id)}
    {#if item.id === 'copy-path' || item.id === 'copy-commit-path'}
      <div class="separator" role="separator"></div>
    {/if}
    <button
      type="button"
      role="menuitem"
      disabled={!item.enabled}
      onclick={() => item.enabled && onSelect(item.id)}
    >{item.label}</button>
  {/each}
</div>

<style>
  .source-control-context-menu { position: fixed; z-index: 1000; min-width: 128px; overflow: hidden; border: 1px solid var(--color-border); border-radius: var(--radius-md); padding: 4px; background: var(--color-surface); color: var(--color-text); box-shadow: var(--shadow-md); }
  button { display: flex; width: 100%; min-height: 24px; align-items: center; border: 0; border-radius: var(--radius-sm); padding: 4px 8px; background: transparent; color: inherit; font: inherit; font-size: 13px; text-align: left; cursor: default; }
  button:hover:not(:disabled), button:focus-visible:not(:disabled) { background: var(--color-elevated); outline: none; }
  button:disabled { opacity: 0.5; }
  .separator { height: 1px; margin: 4px -4px; background: var(--color-border); }
</style>
