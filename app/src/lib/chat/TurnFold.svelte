<script lang="ts">
  import { formatWorkedFor } from '../thread/turns';
  import type { Fold } from '../thread/fold';
  // A finished turn's work, folded to one line above its reply: how long it
  // took and what it did. `open` is the reader's click, kept per turn by the conversation.
  let { fold, open, ontoggle }: { fold: Fold; open: boolean; ontoggle: () => void } = $props();
</script>

<button class="fold" class:open aria-expanded={open} onclick={ontoggle}>
  {#if fold.durationMs != null}<span class="time">{formatWorkedFor(fold.durationMs)}</span>{/if}
  <span class="summary">{fold.summary}</span>
  <svg class="chevron" viewBox="0 0 16 16" width="12" height="12" aria-hidden="true"><path d="M6 4l4 4-4 4" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" /></svg>
</button>

<style>
  .fold {
    display: flex;
    align-items: center;
    gap: 8px;
    max-width: 100%;
    margin: 0 0 8px;
    padding: 2px 0;
    border: 0;
    background: none;
    color: var(--muted);
    font: inherit;
    font-size: 13px;
    cursor: pointer;
    text-align: left;
  }
  .fold:hover {
    color: inherit;
  }
  .time {
    font-variant-numeric: tabular-nums;
    flex: none;
  }
  .summary {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .chevron {
    flex: none;
    transition: transform 0.12s ease;
  }
  .open .chevron {
    transform: rotate(90deg);
  }
</style>
