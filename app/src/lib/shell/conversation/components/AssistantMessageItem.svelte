<script lang="ts">
  import ConversationMessage from './ConversationMessage.svelte';
  import TurnMetadata from './TurnMetadata.svelte';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  let { item, onFileLink }: { item: Extract<ConversationDisplayItem, { kind: 'assistant' }>; onFileLink?(path: string): void } = $props();
</script>

<div class="message-wrap" data-testid="timeline-assistant-message">
  <ConversationMessage text={item.text} blocks={item.blocks} role="assistant" itemId={item.itemId} completed={item.completed} {onFileLink} />
  <TurnMetadata text={item.text} timestampMs={item.timestampMs} />
</div>

<style>.message-wrap{position:relative}</style>
