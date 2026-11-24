<script lang="ts">
  /**
   * Resizable vertical divider between rounded workbench cards (1:1 VS Code / dockview sash feel).
   * Lights up emerald green (`--color-accent`) on hover/drag and mounts a full-window transparent
   * `data-occludes-chat` drag shield while dragging so macOS hit-testing never drops pointer capture
   * when sweeping across the live native GPUIKit Chat hole.
   */
  import { getContext } from 'svelte';
  import type { NativeSlotBridge } from '$lib/infrastructure/nativeBridge';

  interface Props {
    ariaLabel: string;
    onDragStart?: () => void;
    onDragDelta: (deltaX: number) => void;
    onDragEnd?: () => void;
  }

  let { ariaLabel, onDragStart, onDragDelta, onDragEnd }: Props = $props();

  const nativeBridge = getContext<NativeSlotBridge | undefined>('native-slot-bridge');
  let dragging = $state(false);
  let startX = 0;

  function handlePointerDown(event: PointerEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    startX = event.clientX;
    dragging = true;
    onDragStart?.();
    // Synchronously notify macOS PassthroughSurface that our drag shield now occludes Chat.
    queueMicrotask(() => nativeBridge?.syncNow());
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  }

  function handlePointerMove(event: PointerEvent): void {
    if (!dragging) return;
    onDragDelta(event.clientX - startX);
  }

  function finishDrag(event: PointerEvent): void {
    if (!dragging) return;
    dragging = false;
    onDragEnd?.();
    if ((event.currentTarget as HTMLElement).hasPointerCapture(event.pointerId)) {
      (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
    }
    queueMicrotask(() => nativeBridge?.syncNow());
  }
</script>

<div
  class="split-sash"
  class:dragging
  role="separator"
  aria-orientation="vertical"
  aria-label={ariaLabel}
  onpointerdown={handlePointerDown}
  onpointermove={handlePointerMove}
  onpointerup={finishDrag}
  onpointercancel={finishDrag}
>
  <div class="sash-line"></div>
</div>

{#if dragging}
  <div class="resize-shield" data-occludes-chat aria-hidden="true"></div>
{/if}

<style>
  .split-sash {
    position: relative;
    width: 8px;
    flex: 0 0 8px;
    cursor: col-resize;
    display: flex;
    align-items: stretch;
    justify-content: center;
    user-select: none;
    touch-action: none;
    z-index: 20;
  }

  .sash-line {
    width: 2px;
    margin: 4px 0;
    border-radius: 999px;
    background: transparent;
    transition: background 80ms ease, width 80ms ease, box-shadow 80ms ease;
  }

  .split-sash:hover .sash-line,
  .split-sash.dragging .sash-line {
    width: 3px;
    background: var(--color-accent);
    box-shadow: 0 0 8px rgba(30, 215, 96, 0.45);
  }

  .resize-shield {
    position: fixed;
    inset: 0;
    z-index: 1000;
    cursor: col-resize;
    background: transparent;
  }
</style>
