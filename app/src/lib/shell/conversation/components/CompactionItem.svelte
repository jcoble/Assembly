<script lang="ts">
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  import { formatContextTokens } from '$lib/shell/conversation/composerSlashCommands.ts';

  let { item }: { item: Extract<ConversationDisplayItem, { kind: 'compaction' }> } = $props();

  // What it cost, when the agent said. Codex reports only that it happened, so
  // the row reads as a plain boundary rather than inventing a number for it.
  const label = $derived(
    item.preTokens !== undefined && item.postTokens !== undefined
      ? `Context compacted · ${formatContextTokens(item.preTokens)} → ${formatContextTokens(item.postTokens)}`
      : 'Context compacted'
  );
</script>

<div class="compaction" data-testid="timeline-compaction-item">
  <span class="rule"></span>
  <span class="label">{label}</span>
  <span class="rule"></span>
</div>

<style>
  /* A boundary, not a card: the reader needs to know the older part of the
     conversation is gone, and nothing more than that. */
  .compaction{display:flex;align-items:center;gap:10px;padding:2px 0}
  .rule{flex:1;height:1px;background:var(--color-border)}
  .label{color:var(--color-text-3);font-size:12px;letter-spacing:.04em;white-space:nowrap;font-variant-numeric:tabular-nums}
</style>
