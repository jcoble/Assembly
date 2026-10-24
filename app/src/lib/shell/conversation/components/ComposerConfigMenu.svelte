<!--
  ComposerConfigMenu.svelte — the strip along the bottom of the message box
  where the agent's settings live.

  It replaces the old row of dropdowns above the composer. The settings are
  the same three the agent reports (model, reasoning effort, approval policy)
  and they are written through the same call; what changed is that they are
  now stated as sentences rather than raw ids, and take one line instead of
  four.

  Two controls, because the two questions are different:
    approvals — how much the agent may do without asking. A decision about this
                conversation's safety.
    model     — which model, and how hard it is thinking. Read as one phrase
                ("5.6 Sol Extra High"), opened for the rare change.

  Both are the same pill, side by side at the right of the composer's control
  row, and each takes a highlighted state while its own menu is on screen so
  you can see which chip the sheet belongs to.

  Every label comes from `agentConfigLabels.ts`; nothing here invents wording.
-->
<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import Check from '@lucide/svelte/icons/check';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ShieldCheck from '@lucide/svelte/icons/shield-check';

  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import {
    approvalDescription,
    approvalLabel,
    effortLabel,
    modelEffortLabel,
    modelLabel
  } from '$lib/shell/conversation/agentConfigLabels.ts';
  import {
    hasAgentConversationConfig,
    emptyAgentConversationConfigState,
    snapshotAgentConversationConfig,
    type AgentConversationConfigField,
    type AgentConversationConfigState
  } from '$lib/shell/conversation/conversationConfig.ts';

  interface Props {
    /** The agent this conversation is with, used to name the settings menu. */
    provider: string;
    state: AgentConversationConfigState;
    /** Fields whose new value is still being written. Those controls are held. */
    pending: Partial<Record<AgentConversationConfigField, string>>;
    error?: string | null;
    onChange?(field: AgentConversationConfigField, value: string): void;
  }

  let { provider, state: configState, pending, error = null, onChange }: Props = $props();
  let menuProvider = $state('');
  let menuState = $state<AgentConversationConfigState>(emptyAgentConversationConfigState());
  let menuPending = $state<Partial<Record<AgentConversationConfigField, string>>>({});

  const approvalBusy = $derived('approvalPolicy' in pending);
  const saving = $derived(Object.keys(pending).length > 0);

  /** Which individual controls have choices the agent can accept. */
  const canChooseEffort = $derived(configState.availableEfforts.length > 0);
  const canChooseApproval = $derived(configState.availableApprovalPolicies.length > 0);
  const hasAgentSettings = $derived(hasAgentConversationConfig(configState));

  /**
   * The options to show, with whatever the agent currently reports included
   * even when it is missing from the list — otherwise the menu would show a
   * check mark against nothing.
   */
  function optionsFor(available: readonly string[], current: string | null): string[] {
    if (current && !available.includes(current)) return [current, ...available];
    return [...available];
  }

  const modelOptions = $derived(optionsFor(configState.availableModels, configState.model));
  const menuModelOptions = $derived(optionsFor(menuState.availableModels, menuState.model));
  const menuEffortOptions = $derived(
    optionsFor(menuState.availableEfforts, menuState.reasoningEffort)
  );
  const menuApprovalOptions = $derived(
    optionsFor(menuState.availableApprovalPolicies, menuState.approvalPolicy)
  );

  function snapshotOnOpen(open: boolean): void {
    if (!open) return;
    menuProvider = provider;
    menuState = snapshotAgentConversationConfig(configState);
    menuPending = { ...pending };
  }
</script>

<div class="config-row" data-testid="conversation-config-bar" aria-label="Agent settings">
  {#if !hasAgentSettings}
    <span class="unavailable" data-testid="conversation-config-unavailable">
      Agent settings unavailable
    </span>
  {:else}
    <!-- What the agent may do on its own. -->
    <DropdownMenu.Root onOpenChange={snapshotOnOpen}>
    <DropdownMenu.Trigger disabled={!canChooseApproval || approvalBusy}>
      {#snippet child({ props })}
        <button
          {...props}
          data-testid="conversation-config-approval-policy"
          class="composer-chip"
          type="button"
        >
          <ShieldCheck size={14} aria-hidden="true" />
          {approvalLabel(configState.approvalPolicy)}
          <ChevronDown size={13} class="chip-chevron" aria-hidden="true" />
        </button>
      {/snippet}
    </DropdownMenu.Trigger>
    <!-- Opens upward. This strip sits on the bottom edge of the window, so a
         menu dropped below the trigger has nowhere to go and is cut off by the
         edge; `avoidCollisions` then flips it back down only if the space
         above ever runs out too. -->
    <DropdownMenu.Content
      side="top"
      align="start"
      sideOffset={8}
      avoidCollisions
      collisionPadding={12}
      class="w-auto! max-w-[320px] min-w-[248px]"
    >
      <DropdownMenu.Label>When {menuProvider} needs permission</DropdownMenu.Label>
      {#each menuApprovalOptions as policy (policy)}
        <DropdownMenu.Item onSelect={() => onChange?.('approvalPolicy', policy)}>
          <span class="check-slot">
            {#if policy === menuState.approvalPolicy}<Check aria-hidden="true" class="size-3.5" />{/if}
          </span>
          <span class="menu-row-copy">
            <span>{approvalLabel(policy)}</span>
            {#if approvalDescription(policy)}
              <span class="menu-row-description">{approvalDescription(policy)}</span>
            {/if}
          </span>
        </DropdownMenu.Item>
      {/each}
    </DropdownMenu.Content>
    </DropdownMenu.Root>

    <!-- Which model, thinking how hard. -->
    <div class="config-right">
    {#if saving}
      <span class="text-primary text-[13px]" data-testid="conversation-config-saving"><WorkingSpinner size={12} /> Saving…</span>
    {/if}
    {#if error}
      <span
        class="text-destructive text-[13px]"
        data-testid="conversation-config-error"
        title={error}
      >
        Settings unavailable
      </span>
    {/if}
    <DropdownMenu.Root onOpenChange={snapshotOnOpen}>
      <DropdownMenu.Trigger disabled={modelOptions.length === 0 && !canChooseEffort}>
        {#snippet child({ props })}
          <button
            {...props}
            data-testid="conversation-config-pill"
            class="composer-chip"
            type="button"
          >
            {modelEffortLabel(configState.model, configState.reasoningEffort, configState.modelLabels)}
            <ChevronDown size={13} class="chip-chevron" aria-hidden="true" />
          </button>
        {/snippet}
      </DropdownMenu.Trigger>
      <!-- Same reason as the approval menu: upward, away from the window edge. -->
      <DropdownMenu.Content
        side="top"
        align="end"
        sideOffset={8}
        avoidCollisions
        collisionPadding={12}
        class="w-auto! min-w-[220px]"
      >
        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger
            data-testid="conversation-config-model"
            disabled={menuModelOptions.length === 0 || 'model' in menuPending}
          >
            Model
            <span class="text-muted-foreground ml-auto pl-4">{modelLabel(menuState.model, menuState.modelLabels)}</span>
          </DropdownMenu.SubTrigger>
          <!-- A submenu hangs off its row, near the bottom of the window: its
               bottom edge is pinned to the row's so it grows upward. -->
          <!-- Opens to the LEFT. This chip is right-aligned in the composer,
               and collision avoidance measures the window rather than the
               centre pane, so a submenu allowed to open rightward has room in
               the window and lands on top of the file tree. -->
          <DropdownMenu.SubContent
            side="left"
            align="end"
            avoidCollisions
            collisionPadding={12}
            class="min-w-[180px]"
          >
            {#each menuModelOptions as model (model)}
              {@const modelAvailable = menuState.availableModels.includes(model)}
              <DropdownMenu.Item
                disabled={'model' in menuPending || !modelAvailable}
                title={modelAvailable ? undefined : 'Unavailable for this session.'}
                class="model-option"
                onSelect={() => onChange?.('model', model)}
              >
                <span class="check-slot">
                  {#if model === menuState.model}<Check aria-hidden="true" class="size-3.5" />{/if}
                </span>
                <span class="menu-row-copy">
                  <span class="model-option-name">{modelLabel(model, menuState.modelLabels)}</span>
                  <span class="menu-row-description">{menuProvider}</span>
                </span>
              </DropdownMenu.Item>
            {/each}
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>

        <DropdownMenu.Separator />

        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger
            data-testid="conversation-config-reasoning-effort"
            disabled={menuState.availableEfforts.length === 0 || 'reasoningEffort' in menuPending}
          >
            Effort
            <span class="text-muted-foreground ml-auto pl-4">
              {effortLabel(menuState.reasoningEffort)}
            </span>
          </DropdownMenu.SubTrigger>
          <!-- A submenu hangs off its row, near the bottom of the window: its
               bottom edge is pinned to the row's so it grows upward. -->
          <!-- Opens to the LEFT. This chip is right-aligned in the composer,
               and collision avoidance measures the window rather than the
               centre pane, so a submenu allowed to open rightward has room in
               the window and lands on top of the file tree. -->
          <DropdownMenu.SubContent
            side="left"
            align="end"
            avoidCollisions
            collisionPadding={12}
            class="min-w-[180px]"
          >
            {#each menuEffortOptions as effort (effort)}
              <DropdownMenu.Item onSelect={() => onChange?.('reasoningEffort', effort)}>
                <span class="check-slot">
                  {#if effort === menuState.reasoningEffort}
                    <Check aria-hidden="true" class="size-3.5" />
                  {/if}
                </span>
                {effortLabel(effort)}
              </DropdownMenu.Item>
            {/each}
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
    </div>
  {/if}
</div>

<style>
  /* The two chips sit together at the right of the composer's control row. */
  .config-row {
    display: flex;
    align-items: center;
    gap: var(--composer-row-gap);
    min-width: 0;
  }
  .config-right {
    display: flex;
    align-items: center;
    gap: var(--composer-row-gap);
    min-width: 0;
  }
  .unavailable {
    color: var(--secondary-label);
    font-size: 13px;
  }

  /* One pill for both chips. The open state is what tells you which chip the
     menu on screen belongs to; bits-ui marks the trigger while its menu is up. */
  .composer-chip {
    display: inline-flex;
    min-width: 0;
    align-items: center;
    gap: var(--composer-pill-gap);
    padding: var(--composer-pill-inset);
    border: 0;
    border-radius: var(--radius-pill);
    background: var(--composer-pill-surface);
    color: var(--composer-pill-text);
    font: 400 13px/1.2 inherit;
    white-space: nowrap;
    cursor: pointer;
  }
  .composer-chip:hover:not(:disabled) {
    background: var(--composer-pill-surface-hover);
    color: var(--composer-pill-text-open);
  }
  .composer-chip[data-state='open'] {
    background: var(--composer-pill-surface-open);
    color: var(--composer-pill-text-open);
  }
  .composer-chip:disabled { cursor: default; opacity: .5; }
  .composer-chip:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 2px; }
  :global(.chip-chevron) { opacity: .7; }
  @media (prefers-reduced-motion: no-preference) {
    .composer-chip { transition: background-color .14s ease, color .14s ease; }
  }

  :global(.model-option[data-disabled]) { cursor: not-allowed; }
  .check-slot { display: inline-grid; place-items: center; flex: none; width: 15px; color: var(--color-accent); }
  .menu-row-copy { display: flex; min-width: 0; flex-direction: column; gap: var(--menu-row-description-gap); }
  .menu-row-description { color: var(--secondary-label); font-size: 13px; line-height: 1.4; white-space: normal; }
  .model-option-name { overflow: hidden; color: var(--color-text); font-size: 13px; font-weight: 550; text-overflow: ellipsis; white-space: nowrap; }
</style>
