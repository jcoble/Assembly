<script lang="ts">
  import { conversationDisclosureContext, type ConversationDisclosureContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import { getContext } from 'svelte';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import CircleDot from '@lucide/svelte/icons/circle-dot';
  import Users from '@lucide/svelte/icons/users';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';

  const disclosure = getContext<ConversationDisclosureContext>(conversationDisclosureContext);

  let { item }: { item: Extract<ConversationDisplayItem, { kind: 'subagent' }> } = $props();

  const running = $derived(/^(working|running|active|started)$/i.test(item.state));
  const open = $derived(disclosure?.get(`${item.itemId}:subagent`) ?? running);
  const failed = $derived(/^(failed|error|cancelled)$/i.test(item.state));
  const done = $derived(/^(completed|done|finished)$/i.test(item.state));

  /**
   * Everything the child event carries beyond the fields already in the
   * heading. Only plain values are shown — a nested payload has no row shape.
   */
  const details = $derived(
    Object.entries(item.metadata ?? {})
      .filter(([key, value]) =>
        !['childId', 'parentId', 'label', 'state'].includes(key) &&
        (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') &&
        String(value).length > 0)
      .map(([key, value]) => [key, String(value)] as const)
  );
</script>

<details class="subagent" class:running class:failed class:done data-testid="timeline-subagent-item" {open} ontoggle={(event) => { if (event.currentTarget.open !== open) disclosure?.set(`${item.itemId}:subagent`, event.currentTarget.open); }}>
  <summary>
    <span class="chevron" aria-hidden="true"><ChevronRight size={14} strokeWidth={1.8} /></span>
    <span class="agent-icon" aria-hidden="true">
      {#if running}<CircleDot size={14} strokeWidth={2} />{:else}<Users size={14} strokeWidth={1.8} />{/if}
    </span>
    <strong>{item.label}</strong>
    <span class="state-chip" data-testid="timeline-subagent-state">{item.state}</span>
  </summary>
  <div class="subagent-body">
    <p class="note">Read-only view of a child agent's work.</p>
    <dl>
      <div><dt>Child</dt><dd>{item.childId}</dd></div>
      {#if item.parentId}<div><dt>Parent</dt><dd>{item.parentId}</dd></div>{/if}
      {#each details as [key, value] (key)}<div><dt>{key}</dt><dd>{value}</dd></div>{/each}
    </dl>
  </div>
</details>

<style>
  .subagent{border:1px solid transparent;border-left:2px solid color-mix(in srgb,var(--color-accent) 45%,var(--color-border));border-radius:10px}
  .subagent[open]{border-color:color-mix(in srgb,var(--color-border) 62%,transparent);border-left-color:color-mix(in srgb,var(--color-accent) 45%,var(--color-border));background:color-mix(in srgb,var(--color-surface) 45%,var(--color-bg) 55%)}
  summary{display:flex;align-items:center;gap:8px;min-height:28px;padding:2px 8px;border-radius:10px;cursor:pointer;list-style:none}
  summary::-webkit-details-marker{display:none}
  summary:hover{background:color-mix(in srgb,var(--color-hover) 55%,transparent)}
  summary:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:-2px}
  .chevron,.agent-icon{display:grid;place-items:center;flex:none;color:var(--color-text-3)}
  details[open] .chevron{transform:rotate(90deg)}
  strong{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:13px;font-weight:620}
  .state-chip{flex:none;color:var(--color-text-3);font-size:12px;text-transform:lowercase}
  .running{border-left-color:var(--color-accent)}
  .running .agent-icon,.running .state-chip{color:var(--color-accent)}
  .done .state-chip{color:var(--color-good)}
  .failed{border-left-color:var(--color-bad)}
  .failed .state-chip{color:var(--color-bad)}
  .subagent-body{display:grid;gap:8px;padding:0 12px 12px 38px}
  .note{margin:0;color:var(--color-text-2);font-size:13px}
  dl{display:grid;gap:4px;margin:0}
  dl div{display:grid;grid-template-columns:88px minmax(0,1fr);gap:8px}
  dt{color:var(--color-text-3);font-size:12px}
  dd{margin:0;min-width:0;overflow-wrap:anywhere;font:12px/1.55 var(--font-mono)}

  @media (prefers-reduced-motion:no-preference){
    .chevron{transition:transform .14s ease}
    summary{transition:background .14s ease}
  }
</style>
