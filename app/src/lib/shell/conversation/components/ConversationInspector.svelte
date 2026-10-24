<script lang="ts">
  import { conversationRecentEvents } from '$lib/shell/conversation/conversationStore.svelte.ts';

  interface Props {
    ownedId: string;
  }

  let { ownedId }: Props = $props();
  const events = $derived(conversationRecentEvents(ownedId));
</script>

<aside class="inspector" data-testid="conversation-inspector" role="log" aria-label="Recent conversation events">
  <div class="inspector-heading">
    <strong>Inspector</strong>
    <span>Recent events</span>
  </div>
  <div class="event-list">
    {#if events.length === 0}
      <p class="empty">No events received yet.</p>
    {:else}
      {#each events as event (`${event.sequence}:${event.timestampMs}`)}
        <div class="event-row">[{event.sequence}] {event.kind}: {event.summary}</div>
      {/each}
    {/if}
  </div>
</aside>

<style>
  .inspector {
    position: absolute;
    inset: 47px 0 0 auto;
    z-index: 3;
    display: flex;
    flex-direction: column;
    width: min(430px, 100%);
    border-left: 1px solid var(--color-border);
    background: var(--color-bg);
    color: var(--color-text);
    box-shadow: var(--shadow-lg);
    font: 12px/1.45 var(--font-mono);
  }

  .inspector-heading {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 12px;
    padding: 12px 15px;
    border-bottom: 1px solid var(--color-border);
  }

  .inspector-heading span,
  .empty {
    color: var(--color-text-2);
  }

  .event-list {
    min-height: 0;
    overflow: auto;
    padding: 8px 0;
  }

  .event-row {
    padding: 5px 15px;
    overflow-wrap: anywhere;
    border-bottom: 1px solid color-mix(in srgb, var(--color-border) 45%, transparent);
  }

  .empty {
    margin: 12px 15px;
  }
</style>
