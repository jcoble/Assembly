<script lang="ts">
  /**
   * AnnotationBadges.svelte — the numbers on the page.
   *
   * Every place the reader pointed at gets a numbered circle where they
   * pointed — the spot they clicked, not the corner of whatever the page said
   * was under it — and the number matches the row in the card's list. Without
   * them a request naming "the second thing" points at nothing: the picture
   * that gets sent shows three blue boxes and no way to tell which is which.
   *
   * The circles sit over the still, not over the live page — nothing in this
   * document can be drawn over a native view, which is why marking works on a
   * captured picture in the first place. Coordinates are the marking surface's
   * own pixels, the same ones the canvas underneath paints in.
   *
   * The one being written about is open for a comment: a box beside its circle
   * that takes what the reader says about that spot. Enter or the check keeps
   * it. Once kept, only the circle stays on the page — what was said is a row
   * in the list, and a page covered in pills is a page nobody can look at.
   * Clicking a circle opens it again.
   */
  import Check from '@lucide/svelte/icons/check';

  import { IconButton } from '$lib/components/ui/icon-button/index.js';

  import type { NumberedAnnotation } from './annotationList.ts';

  interface Props {
    annotations: readonly NumberedAnnotation[];
    /** The annotation whose comment is being typed, if any. */
    editingId: string | null;
    /** The marking surface's size, so a box near an edge can be pulled inside. */
    surface: { width: number; height: number };
    onLabel(id: string, label: string): void;
    onEdit(id: string): void;
    onDoneEditing(): void;
  }

  let { annotations, editingId, surface, onLabel, onEdit, onDoneEditing }: Props = $props();

  /** How wide the comment box is, and how far it may reach past its circle. */
  const BOX_WIDTH = 230;
  const BOX_HEIGHT = 34;
  /** The gap between the circle and the box beside it. */
  const BOX_GAP = 16;

  /** The comment box sits to the right of the circle, pulled inside at the edges. */
  function boxLeft(item: NumberedAnnotation): number {
    const room = surface.width - BOX_WIDTH - 8;
    return Math.max(8, Math.min(item.pin.x + BOX_GAP, room > 8 ? room : 8));
  }

  function boxTop(item: NumberedAnnotation): number {
    const centred = item.pin.y - BOX_HEIGHT / 2;
    const floor = surface.height > 0 ? Math.max(8, surface.height - BOX_HEIGHT - 8) : centred;
    return Math.max(8, Math.min(centred, floor));
  }

  function keys(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === 'Escape') {
      event.preventDefault();
      onDoneEditing();
    }
  }
</script>

<div class="badges" data-testid="browser-annotation-badges">
  {#each annotations as item (item.id)}
    <button
      type="button"
      class="badge"
      style:left={`${item.pin.x}px`}
      style:top={`${item.pin.y}px`}
      aria-label={`Comment on annotation ${item.number}`}
      data-testid="browser-annotation-badge"
      onclick={() => onEdit(item.id)}
    >{item.number}</button>

    {#if editingId === item.id}
      <div
        class="comment"
        style:left={`${boxLeft(item)}px`}
        style:top={`${boxTop(item)}px`}
        style:width={`${BOX_WIDTH}px`}
        data-testid="browser-annotation-label-editor"
      >
        <!-- svelte-ignore a11y_autofocus -->
        <input
          class="comment-input"
          autofocus
          aria-label={`What to change about this ${item.tag}`}
          placeholder="Add a comment..."
          value={item.label}
          data-testid="browser-annotation-label-input"
          oninput={(event) => onLabel(item.id, event.currentTarget.value)}
          onkeydown={keys}
        />
        <IconButton
          label="Keep this note"
          size="xs"
          variant="default"
          data-testid="browser-annotation-label-done"
          onclick={onDoneEditing}
        >
          <Check aria-hidden="true" />
        </IconButton>
      </div>
    {/if}
  {/each}
</div>

<style>
  .badges {
    position: absolute;
    inset: 0;
    /* The canvas underneath takes the pointer for drawing and erasing; only a
       circle and the comment box beside it take it back. */
    pointer-events: none;
  }

  .badge {
    position: absolute;
    display: inline-flex;
    height: 22px;
    min-width: 22px;
    transform: translate(-50%, -50%);
    align-items: center;
    justify-content: center;
    border: 1.5px solid rgba(255, 255, 255, 0.92);
    border-radius: 999px;
    background: rgb(37, 99, 235);
    padding: 0 6px;
    font-size: 12px;
    font-weight: 600;
    line-height: 1;
    color: rgb(255, 255, 255);
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.35);
    cursor: pointer;
    pointer-events: auto;
    transition: transform 120ms ease;
  }

  .badge:hover {
    transform: translate(-50%, -50%) scale(1.1);
  }

  .comment {
    position: absolute;
    display: flex;
    height: 34px;
    align-items: center;
    gap: 6px;
    border: 1px solid var(--color-border);
    border-radius: 999px;
    background: var(--color-elevated);
    padding: 2px 4px 2px 14px;
    box-shadow: 0 6px 20px rgba(0, 0, 0, 0.35);
    pointer-events: auto;
  }

  .comment-input {
    min-width: 0;
    flex: 1;
    border: 0;
    background: transparent;
    font-size: 13px;
    color: var(--color-text);
    outline: none;
  }

  .comment-input::placeholder {
    color: var(--color-text-2);
  }
</style>
