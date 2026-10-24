<script lang="ts">
  import { copyText } from '../host/clipboard';
  import { messageTime } from '../thread/fold';
  // Under a sent message or a reply: copy its text, and when it was written.
  // Hidden until the pointer is over its row or focus is inside it (the row's
  // own :hover and :focus-within, in Conversation.svelte). The fork button
  // joins the reply's line with fork and rewind.
  let { text, at, now, align = 'start' }: { text: string; at: number; now: number; align?: 'start' | 'end' } = $props();

  let copied = $state(false);
  async function copy() {
    await copyText(text);
    copied = true;
    setTimeout(() => (copied = false), 1500);
  }
</script>

<div class="message-meta" class:end={align === 'end'}>
  <button aria-label={copied ? 'Copied' : 'Copy message'} title={copied ? 'Copied' : 'Copy message'} onclick={() => void copy()}>
    {#if copied}
      <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true"><path d="M3.5 8.5l3 3 6-7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" /></svg>
    {:else}
      <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true"><rect x="5.5" y="5.5" width="8" height="8" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.3" /><path d="M10.5 3.5v-.5a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h.5" fill="none" stroke="currentColor" stroke-width="1.3" /></svg>
    {/if}
  </button>
  {#if at > 0}<span class="time">{messageTime(at, now)}</span>{/if}
</div>

<style>
  .message-meta {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 24px;
    margin: 4px 0 0;
    color: var(--muted);
    font-size: 12px;
    opacity: 0;
    pointer-events: none;
    transition: opacity 0.12s ease;
  }
  .message-meta.end {
    justify-content: flex-end;
  }
  button {
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    padding: 0;
    border: 0;
    border-radius: 6px;
    background: none;
    color: inherit;
    cursor: pointer;
  }
  button:hover {
    background: var(--code-bg);
    color: var(--text, inherit);
  }
  .time {
    font-variant-numeric: tabular-nums;
  }
</style>
