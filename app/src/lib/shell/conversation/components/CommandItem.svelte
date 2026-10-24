<script lang="ts">
  import ConversationMessage from './ConversationMessage.svelte';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  let { item, onFileLink }: { item: Extract<ConversationDisplayItem, { kind: 'command' }>; onFileLink?(path: string): void } = $props();
</script>

<!-- Command output is machine prose, so it reads in the same plain plane as a
     reply. A caption is all the identity it needs. -->
<aside class="command-event" data-testid="timeline-command-item">
  <span class="event-label">Command</span>
  <ConversationMessage text={item.text} role="assistant" itemId={item.itemId} completed={item.completed} {onFileLink} />
</aside>

<style>
  .command-event{padding-left:12px;border-left:2px solid color-mix(in srgb,var(--color-border) 80%,transparent)}
  .event-label{display:block;margin-bottom:8px;color:var(--color-text-3);font-size:12px;letter-spacing:.06em;text-transform:uppercase}
</style>
