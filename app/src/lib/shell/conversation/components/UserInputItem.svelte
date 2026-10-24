<script lang="ts">
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';

  interface Props {
    item: Extract<ConversationDisplayItem, { kind: 'input' }>;
  }

  let { item }: Props = $props();
</script>

<section class="input-event" data-testid="timeline-user-input-item">
  <strong class="input-title">{item.title}</strong>
  {#if item.description}<p>{item.description}</p>{/if}
  {#each item.fields as field (field.id)}
    <div class="input-field">
      <strong>{field.label}</strong>
      {#if field.description}<small>{field.description}</small>{/if}
      {#if field.choices?.length}
        <ul>{#each field.choices as choice (JSON.stringify(choice.value))}<li>{choice.label}{#if choice.description}<small>{choice.description}</small>{/if}</li>{/each}</ul>
      {/if}
    </div>
  {/each}
</section>

<style>
  .input-event{padding:12px;border:1px solid color-mix(in srgb,var(--color-accent) 30%,var(--color-border));border-radius:10px;background:color-mix(in srgb,var(--color-accent) 6%,transparent)}
  .input-title{font-size:13px;font-weight:620}
  p{margin:4px 0 0;color:var(--color-text-2);font-size:12px}
  .input-field{display:grid;gap:4px;margin-top:12px;font-size:12px}
  small{display:block;color:var(--color-text-3);font-size:12px}
  ul{margin:0;padding-left:18px;color:var(--color-text-2)}
</style>
