<script lang="ts">
  import { getContext, setContext, type Snippet } from 'svelte';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  import { UIMessage } from '@tanstack/ai-svelte/ui';
  import {
    conversationMessagesContext, conversationToolContext,
    type ConversationMessagesContext, type ConversationToolContext
  } from '$lib/shell/conversation/conversationChatUI.ts';

  let { itemId, item, onFileLink, children }: {
    itemId: string;
    item: Extract<ConversationDisplayItem, { kind: 'tool' }>;
    onFileLink?(path: string): void;
    children?: Snippet<[Extract<ConversationDisplayItem, { kind: 'tool' }>]>;
  } = $props();
  const host = getContext<ConversationMessagesContext>(conversationMessagesContext);
  const message = $derived(host.messages.get(itemId));
  setContext<ConversationToolContext>(conversationToolContext, {
    get item() { return item; },
    get renderTool() { return children; },
    openFile(path) { onFileLink?.(path); }
  });
</script>

{#if message}
  <UIMessage ui={host.ui} {message} />
{/if}
