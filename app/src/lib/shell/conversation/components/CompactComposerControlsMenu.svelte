<script lang="ts">
  import Check from '@lucide/svelte/icons/check';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { approvalLabel, effortLabel, modelLabel } from '$lib/shell/conversation/agentConfigLabels.ts';
  import type { AgentConversationConfigField, AgentConversationConfigState } from '$lib/shell/conversation/conversationConfig.ts';
  import { emptyAgentConversationConfigState, snapshotAgentConversationConfig } from '$lib/shell/conversation/conversationConfig.ts';

  interface Props {
    provider: string;
    state: AgentConversationConfigState;
    pending: Partial<Record<AgentConversationConfigField, string>>;
    onChange?(field: AgentConversationConfigField, value: string): void;
  }

  let { provider, state: configState, pending, onChange }: Props = $props();
  const optionsFor = (available: readonly string[], current: string | null): string[] => current && !available.includes(current) ? [current, ...available] : [...available];
  let menuProvider = $state('');
  let menuState = $state<AgentConversationConfigState>(emptyAgentConversationConfigState());
  let menuPending = $state<Partial<Record<AgentConversationConfigField, string>>>({});

  function snapshotOnOpen(open: boolean): void {
    if (!open) return;
    menuProvider = provider;
    menuState = snapshotAgentConversationConfig(configState);
    menuPending = { ...pending };
  }
</script>

<DropdownMenu.Root onOpenChange={snapshotOnOpen}>
  <DropdownMenu.Trigger>
    {#snippet child({ props })}
      <button {...props} class="composer-chip" type="button" aria-label="More composer controls">
        <Ellipsis size={16} aria-hidden="true" />
        <ChevronDown size={12} class="chip-chevron" aria-hidden="true" />
      </button>
    {/snippet}
  </DropdownMenu.Trigger>
  <DropdownMenu.Content side="top" align="start" sideOffset={8} class="compact-menu">
    <DropdownMenu.Label>Composer controls</DropdownMenu.Label>
    {#if menuState.availableModels.length || menuState.model}
      <DropdownMenu.Label>Model</DropdownMenu.Label>
      {#each optionsFor(menuState.availableModels, menuState.model) as model (model)}
        {@const modelAvailable = menuState.availableModels.includes(model)}
        <DropdownMenu.Item
          disabled={'model' in menuPending || !modelAvailable}
          title={modelAvailable ? undefined : 'Unavailable for this session.'}
          class="model-option"
          onSelect={() => onChange?.('model', model)}
        >
          <span class="check-slot">{#if model === menuState.model}<Check size={13} aria-hidden="true" />{/if}</span>
          <span class="menu-row-copy">
            <span class="model-option-name">{modelLabel(model, menuState.modelLabels)}</span>
            <span class="menu-row-description">{menuProvider}</span>
          </span>
        </DropdownMenu.Item>
      {/each}
    {/if}
    {#if menuState.availableEfforts.length || menuState.reasoningEffort}
      <DropdownMenu.Separator />
      <DropdownMenu.Label>Thinking effort</DropdownMenu.Label>
      {#each optionsFor(menuState.availableEfforts, menuState.reasoningEffort) as effort (effort)}
        <DropdownMenu.Item disabled={'reasoningEffort' in menuPending} onSelect={() => onChange?.('reasoningEffort', effort)}>
          <span class="check-slot">{#if effort === menuState.reasoningEffort}<Check size={13} aria-hidden="true" />{/if}</span>{effortLabel(effort)}
        </DropdownMenu.Item>
      {/each}
    {/if}
    {#if menuState.availableApprovalPolicies.length || menuState.approvalPolicy}
      <DropdownMenu.Separator />
      <DropdownMenu.Label>Access for {menuProvider}</DropdownMenu.Label>
      {#each optionsFor(menuState.availableApprovalPolicies, menuState.approvalPolicy) as policy (policy)}
        <DropdownMenu.Item disabled={'approvalPolicy' in menuPending} onSelect={() => onChange?.('approvalPolicy', policy)}>
          <span class="check-slot">{#if policy === menuState.approvalPolicy}<Check size={13} aria-hidden="true" />{/if}</span>{approvalLabel(policy)}
        </DropdownMenu.Item>
      {/each}
    {/if}
  </DropdownMenu.Content>
</DropdownMenu.Root>

<style>
  /* The same pill as the two chips it stands in for at narrow widths. */
  .composer-chip { display: inline-flex; align-items: center; gap: var(--composer-pill-gap); padding: var(--composer-pill-inset); border: 0; border-radius: var(--radius-pill); background: var(--composer-pill-surface); color: var(--composer-pill-text); font: 400 13px/1.2 inherit; cursor: pointer; }
  .composer-chip:hover:not(:disabled) { background: var(--composer-pill-surface-hover); color: var(--composer-pill-text-open); }
  .composer-chip[data-state='open'] { background: var(--composer-pill-surface-open); color: var(--composer-pill-text-open); }
  .composer-chip:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 2px; }
  :global(.chip-chevron) { opacity: .7; }
  :global(.compact-menu) { min-width: var(--menu-sheet-min-width); }
  .check-slot { display: inline-grid; place-items: center; flex: none; width: 15px; color: var(--color-accent); }
  :global(.model-option[data-disabled]) { cursor: not-allowed; }
  .menu-row-copy { display: flex; min-width: 0; flex-direction: column; gap: var(--menu-row-description-gap); }
  .menu-row-description { color: var(--secondary-label); font-size: 13px; }
  .model-option-name { overflow: hidden; color: var(--color-text); font-size: 13px; font-weight: 550; text-overflow: ellipsis; white-space: nowrap; }
  @media (prefers-reduced-motion: no-preference) {
    .composer-chip { transition: background-color .14s ease, color .14s ease; }
  }
</style>
