import type { ConversationDisplayItem } from './conversationTimeline.ts';

export function conversationItemHasVisibleContent(item: ConversationDisplayItem): boolean {
  if (item.kind === 'user' || item.kind === 'assistant' || item.kind === 'reasoning' || item.kind === 'unknown') {
    return item.text.trim().length > 0 || (item.kind === 'user' && (item.attachments?.length ?? 0) > 0);
  }
  return true;
}
