<script lang="ts">
  import { conversationDisclosureContext, type ConversationDisclosureContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import { getContext } from 'svelte';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ConversationMessage from './ConversationMessage.svelte';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';

  const disclosure = getContext<ConversationDisclosureContext>(conversationDisclosureContext);

  let { item, onFileLink }: {
    item: Extract<ConversationDisplayItem, { kind: 'reasoning' }>;
    onFileLink?(path: string): void;
  } = $props();

  const open = $derived(disclosure?.get(`${item.itemId}:reasoning`) ?? false);
  const preview = $derived(item.text.replace(/\s+/g, ' ').trim().slice(0, 140) || 'Working through the request');
</script>

<details {open} ontoggle={(event) => { if (event.currentTarget.open !== open) disclosure?.set(`${item.itemId}:reasoning`, event.currentTarget.open); }} class:streaming={!item.completed} class="reasoning" data-testid="timeline-reasoning-item">
  <summary>
    <span class="chevron" aria-hidden="true"><ChevronRight size={14} strokeWidth={1.8} /></span>
    <strong>{item.completed ? 'Thinking' : 'Thinking…'}</strong>
    <span class="preview">{preview}</span>
  </summary>
  <div class="reasoning-body">
    <ConversationMessage text={item.text} role="assistant" itemId={item.itemId} completed={item.completed} {onFileLink} />
  </div>
</details>

<style>
  .reasoning{color:var(--color-text-2);border-radius:10px;padding:2px 0}
  .reasoning:hover{background:color-mix(in srgb,var(--color-surface) 40%,transparent)}
  summary{display:flex;align-items:center;gap:10px;min-height:36px;padding:6px 10px;border-radius:10px;cursor:pointer;list-style:none;font-size:13px}
  summary::-webkit-details-marker{display:none}
  summary:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:-2px}
  .chevron{display:grid;place-items:center;flex:none;color:var(--color-text-3)}
  details[open] .chevron{transform:rotate(90deg)}
  summary strong{flex:none;color:var(--color-text);font-weight:620;white-space:nowrap;font-size:13px}
  .preview{min-width:0;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--color-text-3);font-size:13px}
  .reasoning-body{margin:8px 0 8px 24px;padding:6px 0 6px 14px;border-left:1px solid color-mix(in srgb,var(--color-accent) 28%,var(--color-border));max-height:260px;overflow-y:auto;overflow-x:hidden;scrollbar-width:thin;scrollbar-color:var(--scrollbar-thumb) transparent}
  @media (prefers-reduced-motion:no-preference){
    .chevron{transition:transform .14s ease}
    .reasoning{transition:background .14s ease}
  }
</style>
