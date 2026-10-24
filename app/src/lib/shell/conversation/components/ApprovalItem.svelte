<script lang="ts">
  import ShieldAlert from '@lucide/svelte/icons/shield-alert';
  import { Button } from '$lib/components/ui/button/index.js';
  import type { AgentPermissionOption } from '$lib/shell/conversation/conversationTypes.ts';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';

  interface Props {
    item: Extract<ConversationDisplayItem, { kind: 'approval' }>;
    onDecision?(requestId: string, optionId: string): void;
  }

  let { item, onDecision }: Props = $props();

  function optionLabel(option: AgentPermissionOption): string {
    const kind = option.kind?.toLowerCase() ?? '';
    if (kind.includes('allow_always') || kind.includes('always_allow')) return 'Always Allow';
    if (kind.includes('allow')) return 'Allow';
    if (kind.includes('reject') || kind.includes('deny')) return 'Deny';
    return option.name;
  }
</script>

<aside class="approval-event" data-testid="timeline-approval-item">
  <div class="approval-heading"><span class="approval-icon"><ShieldAlert size={16} strokeWidth={1.8} aria-hidden="true" /></span><strong>{item.title}</strong></div>
  <div class="tool-title">{item.toolTitle}</div>
  {#if item.summary && item.summary !== item.toolTitle}<p>{item.summary}</p>{/if}
  {#if item.state === 'requested'}
    <div class="approval-actions">
      {#each item.options as option (option.optionId)}
        {@const label = optionLabel(option)}
        <Button
          variant={label === 'Deny' ? 'ghost' : 'default'}
          size="sm"
          data-testid={`approval-${option.optionId}-button`}
          onclick={() => onDecision?.(item.requestId, option.optionId)}
        >{label}</Button>
      {/each}
    </div>
  {:else}<small>{item.state}</small>{/if}
</aside>

<style>
  .approval-event{padding:12px;border:1px solid color-mix(in srgb,var(--color-accent) 34%,var(--color-border));border-radius:10px;background:color-mix(in srgb,var(--color-accent) 7%,transparent)}
  .approval-heading{display:flex;align-items:center;gap:8px;font-size:13px}
  .approval-icon{display:grid;place-items:center;color:var(--color-accent)}
  .tool-title{margin-top:8px;font:500 12px/1.55 var(--font-mono);color:var(--color-text)}
  p{margin:4px 0 0;color:var(--color-text-2);font-size:12px}
  small{display:block;margin-top:8px;color:var(--color-text-3);font-size:12px}
  .approval-actions{display:flex;flex-wrap:wrap;gap:8px;margin-top:12px}
</style>
