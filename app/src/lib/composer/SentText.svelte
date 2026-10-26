<script lang="ts">
  // A prompt as sent: the composer's attachment lines shown as images above
  // the typed text, which is shown as typed. Text past MESSAGE_FOLD_LINES folds
  // to that many lines with a fade; the chevron shows the rest (kept.open).
  import { Kept } from '../rows/kept.svelte';
  import { MESSAGE_FOLD_LINES } from '../session/fold';
  import { splitAttachments } from './attachments';
  import SentImage from './SentImage.svelte';

  let { text, images = true, kept = new Kept() }: { text: string; images?: boolean; kept?: Kept } = $props();
  const split = $derived(splitAttachments(text));

  let p = $state<HTMLParagraphElement>();
  let height = $state(0);
  // Over by a pixel is a rounded-off line, not an extra one.
  const overflows = $derived.by(() => {
    if (!p || !height) return false;
    const line = parseFloat(getComputedStyle(p).lineHeight);
    return line > 0 && height > MESSAGE_FOLD_LINES * line + 1;
  });
</script>

{#if images && split.attachments.length}
  <div class="sent">
    {#each split.attachments as attachment, i (i)}
      <SentImage {attachment} />
    {/each}
  </div>
{/if}
{#if split.text}
  <div class="clip" class:folded={overflows && !kept.open} style:--lines={MESSAGE_FOLD_LINES}>
    <p bind:this={p} bind:offsetHeight={height}>{split.text}</p>
  </div>
  {#if overflows}
    <button class="more" class:open={kept.open} aria-expanded={kept.open} aria-label={kept.open ? 'Show less' : 'Show all'} onclick={() => (kept.open = !kept.open)}>
      <svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true"><path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" /></svg>
    </button>
  {/if}
{/if}

<style>
  .sent {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 8px;
  }
  p {
    margin: 0;
    line-height: 1.55;
    white-space: pre-wrap;
  }
  .clip.folded {
    max-height: calc(var(--lines) * 1.55em);
    overflow: hidden;
    mask-image: linear-gradient(to bottom, #000 calc(100% - 3em), transparent);
  }
  .more {
    display: grid;
    place-items: center;
    width: 100%;
    height: 20px;
    margin-top: 2px;
    padding: 0;
    border: 0;
    background: none;
    color: var(--muted);
    cursor: pointer;
  }
  .more:hover {
    color: inherit;
  }
  .more.open svg {
    transform: rotate(180deg);
  }
</style>
