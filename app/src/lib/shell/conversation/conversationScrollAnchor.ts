import type { ConversationDisplayItem } from './conversationTimeline.ts';

export const USER_SEND_ANCHOR_OFFSET_PX = 12;

export interface ConversationSendAnchorRequest {
  requestId: number;
  conversationId: string;
  userItemId: string;
}

/** A turn counts as running from a local send until the backend's turn ends.
 * A new session's first message has only the backend's turn. */
export function sendTurnRunning(localTurnActive: boolean, activeTurnId: string | null): boolean {
  return localTurnActive || !!activeTurnId;
}

/** A row's height before it is measured, from what the row already holds: a tool call or run is
 * one line, a message grows with its text, and a sent message stops at its ten-line fold. A close
 * guess keeps the reader's place from jumping when the row is measured. */
export function estimateRowHeight(row: { run?: unknown; item?: ConversationDisplayItem }): number {
  if (row.run || row.item?.kind === 'tool') return 36;
  if (row.item?.kind !== 'user' && row.item?.kind !== 'assistant') return 90;
  return Math.min(row.item.kind === 'user' ? 300 : 2000, Math.max(48, Math.round(row.item.text.length / 3)));
}
