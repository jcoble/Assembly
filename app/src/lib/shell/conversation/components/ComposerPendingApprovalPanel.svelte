<script lang="ts">
  import ShieldAlert from '@lucide/svelte/icons/shield-alert';
  import type { AgentPermissionOption, AgentPermissionRequest } from '$lib/shell/conversation/conversationTypes.ts';

  interface Props {
    approval: AgentPermissionRequest;
    pendingCount?: number;
    responding?: boolean;
    disabled?: boolean;
    onDecision?(requestId: string, optionId: string): void | Promise<void>;
  }

  let { approval, pendingCount = 1, responding = false, disabled = false, onDecision }: Props = $props();

  function optionLabel(option: AgentPermissionOption): string {
    const kind = option.kind?.toLowerCase() ?? '';
    if (kind.includes('always') || kind.includes('session')) return 'Always allow this session';
    if (kind.includes('allow') || kind.includes('accept')) return 'Approve once';
    if (kind.includes('reject') || kind.includes('deny') || kind.includes('decline')) return 'Decline';
    return option.name || 'Continue';
  }

  function isDeny(option: AgentPermissionOption): boolean {
    const label = optionLabel(option).toLowerCase();
    return label.includes('decline') || label.includes('deny') || label.includes('cancel');
  }
</script>

<section class="approval-panel" data-testid="conversation-pending-approval" aria-labelledby="pending-approval-title">
  <div class="approval-heading">
    <span class="approval-icon" aria-hidden="true"><ShieldAlert size={16} strokeWidth={1.8} /></span>
    <span class="approval-kicker">Pending approval</span>
    {#if pendingCount > 1}<span class="approval-count">1/{pendingCount}</span>{/if}
  </div>
  <h3 id="pending-approval-title">{approval.title || 'Permission requested'}</h3>
  <p class="approval-tool">{approval.toolTitle || 'Action requested'}</p>
  {#if approval.description}
    <div class="approval-detail">
      <span class="detail-label">Details</span>
      <pre>{approval.description}</pre>
    </div>
  {/if}
  <div class="approval-actions" aria-label="Approval actions">
    {#each approval.options as option (option.optionId)}
      <button
        class:deny={isDeny(option)}
        class:primary={!isDeny(option) && option === approval.options[approval.options.length - 1]}
        data-testid={`conversation-approval-${option.optionId}`}
        type="button"
        disabled={responding || disabled}
        onclick={() => void onDecision?.(approval.requestId, option.optionId)}
      >
        {optionLabel(option)}
      </button>
    {/each}
  </div>
</section>

<style>
  .approval-panel { padding: 16px 18px 14px; border-bottom: 1px solid color-mix(in srgb, var(--color-border) 68%, transparent); background: color-mix(in srgb, var(--color-attention-bg) 42%, var(--color-surface)); }
  .approval-heading { display: flex; align-items: center; gap: 8px; color: var(--color-attention); }
  .approval-icon { display: inline-grid; place-items: center; }
  .approval-kicker { font-size: 12px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; }
  .approval-count { margin-left: 2px; padding: 2px 6px; border-radius: 6px; background: color-mix(in srgb, var(--color-attention) 13%, transparent); color: var(--color-text-2); font-size: 12px; font-variant-numeric: tabular-nums; }
  h3 { margin: 9px 0 0; color: var(--color-text); font-size: 14px; font-weight: 650; line-height: 1.35; }
  .approval-tool { margin: 4px 0 0; color: var(--color-text-2); font: 12px/1.45 var(--font-mono); overflow-wrap: anywhere; }
  .approval-detail { margin-top: 11px; padding: 9px 10px; border: 1px solid color-mix(in srgb, var(--color-border) 72%, transparent); border-radius: 9px; background: color-mix(in srgb, var(--color-bg) 76%, transparent); }
  .detail-label { display: block; color: var(--color-text-2); font-size: 12px; font-weight: 650; }
  pre { max-height: 150px; overflow: auto; margin: 6px 0 0; color: var(--color-text); font: 12px/1.55 var(--font-mono); white-space: pre-wrap; overflow-wrap: anywhere; }
  .approval-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 7px; margin-top: 13px; }
  .approval-actions button { min-height: 28px; padding: 5px 10px; border: 1px solid color-mix(in srgb, var(--color-border) 75%, transparent); border-radius: 999px; background: var(--color-elevated); color: var(--color-text); font: 600 12px/1.2 inherit; cursor: pointer; }
  .approval-actions button:hover:not(:disabled) { background: var(--color-hover); }
  .approval-actions button.primary { border-color: color-mix(in srgb, var(--color-accent) 56%, var(--color-border)); background: var(--color-accent); color: var(--color-on-accent); }
  .approval-actions button.primary:hover:not(:disabled) { filter: brightness(1.06); }
  .approval-actions button.deny { color: var(--color-bad); }
  .approval-actions button:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 2px; }
  .approval-actions button:disabled { cursor: wait; opacity: .5; }
</style>
