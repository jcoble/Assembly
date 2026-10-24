<script lang="ts">
  // One sent image in history: its thumbnail, fetched with the conversation's
  // signal; a click fetches and shows the original. Both URLs are revoked on unmount.
  import { getContext, onDestroy } from 'svelte';
  import { ATTACHMENT_FETCH, baseName, type AttachmentRef } from './attachments';
  import FullImage from './FullImage.svelte';

  let { attachment }: { attachment: AttachmentRef } = $props();
  const read = getContext<(name: string) => Promise<Blob>>(ATTACHMENT_FETCH);
  let thumb = $state('');
  let full = $state('');
  let gone = false;

  async function load(path: string): Promise<string> {
    const blob = await read(baseName(path));
    return gone ? '' : URL.createObjectURL(blob);
  }

  async function showThumb() {
    try {
      if (attachment.thumbnail) thumb = await load(attachment.thumbnail);
    } catch {
      // Left as the name-only chip.
    }
  }
  void showThumb();

  async function open() {
    try {
      full = await load(attachment.path);
    } catch {
      // Nothing to open; the thumbnail stays.
    }
  }

  function close() {
    URL.revokeObjectURL(full);
    full = '';
  }

  onDestroy(() => {
    gone = true;
    if (thumb) URL.revokeObjectURL(thumb);
    if (full) URL.revokeObjectURL(full);
  });
</script>

<figure>
  {#if thumb}
    <button class="thumb" onclick={() => void open()} aria-label={`Open ${attachment.name}`}><img src={thumb} alt="" /></button>
  {/if}
  <figcaption title={attachment.path}>{attachment.name}</figcaption>
</figure>
{#if full}<FullImage src={full} name={attachment.name} onclose={close} />{/if}

<style>
  figure {
    display: grid;
    gap: 4px;
    max-width: 220px;
    margin: 0;
  }
  .thumb {
    padding: 0;
    border: 1px solid var(--line);
    border-radius: 8px;
    overflow: hidden;
    background: var(--panel);
    cursor: zoom-in;
  }
  img {
    display: block;
    max-width: 220px;
    max-height: 180px;
  }
  figcaption {
    overflow: hidden;
    color: var(--muted);
    font-size: 12px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
