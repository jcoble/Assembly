<script lang="ts">
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  import { providerErrorMessage } from '$lib/shell/errorPresentation.ts';
  let { item }: { item: Extract<ConversationDisplayItem, { kind: 'error' }> } = $props();

  /**
   * What the error says, in words. An adapter hands its errors over as the
   * JSON-RPC object it received — `{"code":-32603,"message":"Internal error:
   * Prompt is too long"}` — which is the wire, not a sentence. The message is
   * lifted out of it, and the one error a reader can do something about gets
   * told what that is.
   */
  const shown = $derived.by(() => {
    let message = providerErrorMessage(item.text).trim();
    message = message.replace(/^Internal error:\s*/i, '');
    if (/prompt is too long/i.test(message)) {
      return {
        label: 'Conversation is full',
        message: 'This conversation has outgrown the model\'s context window, so nothing more can be sent to it.',
        hint: 'Send /compact if this agent offers it, or start a new session and bring what you need with you.'
      };
    }
    return { label: 'Conversation error', message, hint: null };
  });
</script>

<aside class="error-event" data-testid="timeline-error-item">
  <span class="event-label">{shown.label}</span>
  <p>{shown.message}</p>
  {#if shown.hint}<p class="hint">{shown.hint}</p>{/if}
</aside>

<style>
  .error-event{padding:12px;border:1px solid color-mix(in srgb,var(--color-bad) 34%,var(--color-border));border-radius:8px;background:var(--color-bad-bg)}
  .event-label{display:block;color:var(--color-bad);font-size:12px;letter-spacing:.06em;text-transform:uppercase}
  p{margin:8px 0 0;color:var(--color-text-2);font-size:13px;line-height:1.5;white-space:pre-wrap}
  .hint{color:var(--color-text);margin-top:6px}
</style>
