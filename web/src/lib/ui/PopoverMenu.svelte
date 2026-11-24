<script lang="ts">
  /**
   * Reusable floating glass popover with outside-click + Escape dismiss and `data-occludes-chat`
   * so macOS `PassthroughSurface` always keeps pointer events inside the menu above live GPUI Chat.
   */
  import type { Snippet } from 'svelte';

  interface Props {
    open: boolean;
    align?: 'left' | 'right';
    minWidth?: number;
    onClose: () => void;
    children: Snippet;
  }

  let { open, align = 'left', minWidth = 180, onClose, children }: Props = $props();
  let panelEl = $state<HTMLDivElement | null>(null);
</script>

<svelte:window
  onpointerdown={(event) => {
    if (!open || !panelEl) return;
    if (event.target instanceof Node && !panelEl.contains(event.target)) {
      onClose();
    }
  }}
  onkeydown={(event) => {
    if (open && event.key === 'Escape') {
      event.stopPropagation();
      onClose();
    }
  }}
/>

{#if open}
  <div
    bind:this={panelEl}
    class="popover glass-popover"
    class:align-right={align === 'right'}
    style:min-width={`${minWidth}px`}
    role="menu"
    tabindex="-1"
    data-occludes-chat
    onpointerdown={(event) => event.stopPropagation()}
  >
    {@render children()}
  </div>
{/if}

<style>
  .popover {
    position: absolute;
    top: calc(100% + 6px);
    left: 0;
    z-index: 90;
    padding: 6px;
    border-radius: var(--radius-md);
    display: flex;
    flex-direction: column;
    gap: 2px;
    color: var(--color-text);
  }

  .popover.align-right {
    left: auto;
    right: 0;
  }
</style>
