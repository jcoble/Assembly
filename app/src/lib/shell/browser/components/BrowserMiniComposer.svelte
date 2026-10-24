<script lang="ts">
  /**
   * BrowserMiniComposer.svelte — the card that floats over the page being
   * marked up.
   *
   * It sits over the foot of the page, not under it, because what it says is
   * about the picture directly above it: this many places marked, and here is
   * the sentence that goes with them. In the panel's chrome it read as another
   * row of controls, and it pushed the page down every time marking started.
   *
   * Send is a send. The words and the marked-up picture go to the session as
   * one turn, and the session comes forward — the reader watches it go rather
   * than finding a staged draft somewhere else and pressing Enter again.
   *
   * The chip is the list. Closed it counts; open it shows every annotation
   * with a crop of the place it points at and what the page calls that place,
   * so a note that ended up on the wrong thing can be taken out before the turn
   * leaves. The cross on the chip throws all of it away and gives the live page
   * back.
   */
  import ArrowUp from '@lucide/svelte/icons/arrow-up';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import X from '@lucide/svelte/icons/x';

  import { Chip } from '$lib/components/ui/chip/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';

  import { annotationCountLabel, annotationKind, type NumberedAnnotation } from './annotationList.ts';

  interface Props {
    description: string;
    annotations: readonly NumberedAnnotation[];
    /** Freehand marks on the still. They have no number and no row in the
     * list, but they are drawn into what is sent, so they count. */
    inkCount?: number;
    /** The still the annotations were made on, for the list's crops. */
    backdrop: string | null;
    /** The marking surface's size, the coordinates the boxes are in. */
    surface: { width: number; height: number };
    /** True while the picture is being composed and sent. */
    busy: boolean;
    /** Nothing to send: no session, or no page. */
    disabled: boolean;
    listOpen: boolean;
    onDescriptionChange(value: string): void;
    onToggleList(): void;
    onRemove(id: string): void;
    onDiscard(): void;
    onSend(): void;
  }

  let {
    description,
    annotations,
    inkCount = 0,
    backdrop,
    surface,
    busy,
    disabled,
    listOpen,
    onDescriptionChange,
    onToggleList,
    onRemove,
    onDiscard,
    onSend
  }: Props = $props();

  /** The crop tile in the list, in CSS pixels. */
  const THUMB_WIDTH = 30;
  const THUMB_HEIGHT = 20;

  /* A page marked only with the marker used to leave Send disabled — the
     strokes were never counted — and, because the row holding Discard was
     gated on the same count, left no way out of the still at all. */
  const marked = $derived(annotations.length > 0 || inkCount > 0);
  const canSend = $derived(!disabled && !busy && (description.trim().length > 0 || marked));

  /**
   * The still, scaled and shifted so one annotation's box fills the tile. A
   * background rather than a second canvas: the crop is a picture of a
   * rectangle of another picture, which is what background-size and
   * background-position already say.
   */
  function crop(item: NumberedAnnotation): string {
    const width = Math.max(1, Math.abs(item.box.width));
    const height = Math.max(1, Math.abs(item.box.height));
    const scale = Math.min(THUMB_WIDTH / width, THUMB_HEIGHT / height);
    const x = Math.min(item.box.x, item.box.x + item.box.width) * scale;
    const y = Math.min(item.box.y, item.box.y + item.box.height) * scale;
    const offsetX = (THUMB_WIDTH - width * scale) / 2;
    const offsetY = (THUMB_HEIGHT - height * scale) / 2;
    return [
      `background-size:${surface.width * scale}px ${surface.height * scale}px`,
      `background-position:${offsetX - x}px ${offsetY - y}px`
    ].join(';');
  }

  function submit(event: SubmitEvent): void {
    event.preventDefault();
    if (canSend) onSend();
  }
</script>

<div class="composer" data-testid="browser-mini-composer">
  {#if listOpen && annotations.length > 0}
    <ul class="list" data-testid="browser-annotation-list">
      {#each annotations as item (item.id)}
        <li class="row">
          <span class="what">
            <span class="number">{item.number}</span>
            {#if backdrop}
              <span
                class="thumb"
                style={`background-image:url(${backdrop});${crop(item)}`}
                aria-hidden="true"
              ></span>
            {/if}
            <Chip tone="neutral">{annotationKind(item)}</Chip>
            <span class="spacer"></span>
            <IconButton
              label={`Remove annotation ${item.number}`}
              size="xs"
              variant="ghost"
              data-testid="browser-annotation-remove"
              onclick={() => onRemove(item.id)}
            >
              <X aria-hidden="true" />
            </IconButton>
          </span>
          <span class="said">{item.label || 'No note'}</span>
        </li>
      {/each}
    </ul>
  {/if}

  <form class="card" onsubmit={submit}>
    {#if marked}
      <div class="chip-row">
        <button
          type="button"
          class="count"
          aria-expanded={listOpen}
          data-testid="browser-annotation-count"
          onclick={onToggleList}
        >
          <MessageSquare aria-hidden="true" />
          {annotationCountLabel(annotations.length)}
        </button>
        <IconButton
          label="Discard these annotations"
          size="xs"
          variant="ghost"
          data-testid="browser-annotating-discard"
          onclick={onDiscard}
        >
          <X aria-hidden="true" />
        </IconButton>
      </div>
    {/if}

    <input
      class="say"
      aria-label="Describe the change"
      placeholder="Describe the change…"
      value={description}
      disabled={busy}
      data-testid="browser-mini-composer-input"
      oninput={(event) => onDescriptionChange(event.currentTarget.value)}
    />

    <div class="actions">
      <IconButton
        label={busy ? 'Sending' : 'Send to the session'}
        size="sm"
        variant="default"
        disabled={!canSend}
        data-testid="browser-annotation-send"
        onclick={() => onSend()}
      >
        <ArrowUp aria-hidden="true" />
      </IconButton>
    </div>
  </form>
</div>

<style>
  .composer {
    display: flex;
    width: min(720px, calc(100% - 32px));
    flex-direction: column;
    align-items: flex-start;
    gap: 8px;
  }

  .card {
    display: flex;
    width: 100%;
    flex-direction: column;
    gap: 10px;
    border: 1px solid var(--color-border);
    border-radius: 18px;
    background: var(--color-elevated);
    padding: 12px 14px;
    box-shadow: 0 16px 40px rgba(0, 0, 0, 0.38);
  }

  .list {
    display: flex;
    width: min(400px, 100%);
    max-height: 200px;
    flex-direction: column;
    overflow-y: auto;
    margin: 0;
    border: 1px solid var(--color-border);
    border-radius: 14px;
    background: var(--color-elevated);
    padding: 0;
    box-shadow: 0 16px 40px rgba(0, 0, 0, 0.38);
    list-style: none;
  }

  .row {
    display: flex;
    flex-direction: column;
    gap: 6px;
    border-bottom: 1px solid var(--color-border);
    padding: 10px 12px;
  }

  .row:last-child {
    border-bottom: 0;
  }

  .what {
    display: flex;
    min-width: 0;
    align-items: center;
    gap: 8px;
  }

  .spacer {
    flex: 1;
  }

  .thumb {
    display: block;
    height: 20px;
    width: 30px;
    flex: none;
    border: 1px solid var(--color-border);
    border-radius: 4px;
    background-color: var(--color-surface);
    background-repeat: no-repeat;
  }

  .number {
    display: inline-flex;
    height: 17px;
    min-width: 17px;
    flex: none;
    align-items: center;
    justify-content: center;
    border-radius: 999px;
    background: rgb(37, 99, 235);
    font-size: 12px;
    font-weight: 600;
    line-height: 1;
    color: rgb(255, 255, 255);
  }

  .said {
    overflow: hidden;
    font-size: 13px;
    color: var(--color-text);
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .chip-row {
    display: flex;
    align-items: center;
    gap: 2px;
    align-self: flex-start;
    border: 1px solid var(--color-border);
    border-radius: 999px;
    background: var(--color-surface);
    padding: 2px 4px 2px 2px;
  }

  .count {
    display: inline-flex;
    height: 22px;
    align-items: center;
    gap: 6px;
    border: 0;
    border-radius: 999px;
    background: transparent;
    padding: 0 6px;
    font-size: 12px;
    color: var(--color-text);
    cursor: pointer;
  }

  .count :global(svg) {
    height: 12px;
    width: 12px;
  }

  .say {
    width: 100%;
    min-width: 0;
    border: 0;
    background: transparent;
    padding: 2px 2px 8px;
    font-size: 14px;
    color: var(--color-text);
    outline: none;
  }

  .say::placeholder {
    color: var(--color-text-2);
  }

  .actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 8px;
  }
</style>
