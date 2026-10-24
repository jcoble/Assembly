<script lang="ts">
  /**
   * AssistanceHost.svelte — where an assistance proposal is shown, and nothing
   * else.
   *
   * This used to carry a floating chip with a menu of every recipe in the app.
   * It was a section of its own in the corner of the window, disconnected from
   * whatever a person was actually doing, and it is gone: a recipe now belongs
   * to the surface it helps with, offered as a small button in that surface's
   * own header (the commit-message buttons in the source-control panel are the
   * first of those). This file keeps the one thing that has nowhere else to
   * live — the proposal card, which floats above the shell because it can be
   * raised from any surface — and the store and service behind it are
   * unchanged.
   */
  import AssistanceProposal from './AssistanceProposal.svelte';
  import {
    clearAssistance,
    assistanceState,
    setAssistanceError
  } from './assistanceStore.svelte.ts';
  import type { AssistanceProposal as AssistanceProposalType } from './assistanceTypes.ts';

  interface Props {
    proposal?: AssistanceProposalType | null;
    onApply?: (selectedPatchIds: string[]) => void;
    onRetry?: () => void;
  }

  let { proposal = null, onApply, onRetry }: Props = $props();

  function applySelected(selectedPatchIds: string[]): void {
    if (onApply) onApply(selectedPatchIds);
    else setAssistanceError('No typed surface adapter is connected yet');
  }
</script>

{#if proposal || assistanceState.proposal}
  <aside class="assistance-host" data-testid="assistance-host" aria-label="Assistance">
    <AssistanceProposal
      proposal={proposal ?? assistanceState.proposal!}
      onApply={applySelected}
      onDismiss={clearAssistance}
      onRetry={() => onRetry?.()}
      onContinue={clearAssistance}
    />
  </aside>
{/if}

<style>
  /* Keep the proposal one full rail-width left of the 44px surface rail and
     below the rail's stacking level. The 28px bottom resource summary is
     cleared too, so neither control can claim the other's hit area. */
  .assistance-host { position: fixed; right: calc(44px + 16px); bottom: calc(28px + 16px); z-index: 48; display: grid; justify-items: end; gap: 0.5rem; pointer-events: none; }
  .assistance-host :global(button), .assistance-host :global(section) { pointer-events: auto; }
</style>
