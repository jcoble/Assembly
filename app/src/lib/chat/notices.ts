// Run errors and compaction boundaries, as Assembly words them
// (ErrorItem.svelte and CompactionItem.svelte there).

export type ErrorNotice = { label: string; message: string; hint: string | null };

// Provider errors arrive as the JSON they were sent in; lift out the sentence.
function unwrap(raw: string): string {
  let message = raw;
  for (;;) {
    try {
      const parsed = JSON.parse(message) as { error?: { message?: unknown }; message?: unknown } | null;
      const next = typeof parsed?.error?.message === 'string' ? parsed.error.message : parsed?.message;
      if (typeof next !== 'string') return message;
      message = next;
    } catch {
      return message;
    }
  }
}

// What a failed run shows. A cancelled run is the person's own doing, not an error.
export function errorNotice(error: { message?: string; code?: unknown } | undefined): ErrorNotice | null {
  if (error?.code === 'cancelled') return null;
  const message = unwrap(error?.message ?? '').trim().replace(/^Internal error:\s*/i, '');
  if (/prompt is too long/i.test(message)) {
    return {
      label: 'Conversation is full',
      message: "This conversation has outgrown the model's context window, so nothing more can be sent to it.",
      hint: 'Send /compact if this agent offers it, or start a new session and bring what you need with you.',
    };
  }
  return {
    label: 'Conversation error',
    message: message || 'The agent reported an error without saying what went wrong.',
    hint: null,
  };
}

function tokens(value: number): string {
  if (value < 1_000) return String(Math.round(value));
  if (value < 10_000) return `${Math.floor(value / 100) / 10}k`;
  if (value < 1_000_000) return `${Math.round(value / 1_000)}k`;
  return `${Math.floor(value / 100_000) / 10}m`;
}

// The compaction line: the token counts when the agent gave both.
export function compactionLabel(value: { before?: unknown; after?: unknown }): string {
  const { before, after } = value;
  return typeof before === 'number' && typeof after === 'number'
    ? `Context compacted · ${tokens(before)} → ${tokens(after)}`
    : 'Context compacted';
}
