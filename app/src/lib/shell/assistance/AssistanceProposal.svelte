<script lang="ts">
  import { formatFullTime } from '$lib/shell/dateFormat.ts';
  import type { AssistanceProposal } from './assistanceTypes.ts';

  interface Props {
    proposal: AssistanceProposal;
    onApply: (selectedPatchIds: string[]) => void;
    onDismiss: () => void;
    onRetry: () => void;
    onContinue: () => void;
  }

  let { proposal, onApply, onDismiss, onRetry, onContinue }: Props = $props();
  let selectedPatchIds = $state<Set<string>>(new Set());

  $effect(() => {
    selectedPatchIds = new Set(proposal.selectedPatchIds);
  });

  function toggle(patchId: string): void {
    const next = new Set(selectedPatchIds);
    if (next.has(patchId)) next.delete(patchId);
    else next.add(patchId);
    selectedPatchIds = next;
  }

  function apply(): void {
    onApply([...selectedPatchIds]);
  }
</script>

<section class="assistance-proposal" data-testid="assistance-proposal" aria-label="Assistance proposal">
  <header>
    <div>
      <p class="eyebrow">Assistance proposal</p>
      <h2>{proposal.recipeId}</h2>
    </div>
    <span class="status">{proposal.status}</span>
  </header>

  <dl class="metadata">
    <div><dt>Provenance</dt><dd data-testid="assistance-provenance">{proposal.provenance}</dd></div>
    <div><dt>Confidence</dt><dd data-testid="assistance-confidence">{Math.round(proposal.confidence * 100)}%</dd></div>
    <div><dt>Expires</dt><dd>{formatFullTime(new Date(proposal.expiresAt))}</dd></div>
  </dl>

  {#if proposal.patches.length}
    <fieldset>
      <legend>Suggested fields</legend>
      {#each proposal.patches as patch (patch.id)}
        <label class="patch-row">
          <input
            type="checkbox"
            checked={selectedPatchIds.has(patch.id)}
            onchange={() => toggle(patch.id)}
          />
          <span><strong>{patch.field}</strong><small>{String(patch.value)}</small></span>
        </label>
      {/each}
    </fieldset>
  {:else}
    <p class="advisory">This is an explanation only; it proposes no field changes.</p>
  {/if}

  <p class="safety-note">Consequential changes stay behind preview, confirmation, revalidation, and audit.</p>
  <div class="actions">
    <button type="button" data-testid="assistance-apply-selected" onclick={apply} disabled={!selectedPatchIds.size}>Apply selected</button>
    <button type="button" data-testid="assistance-dismiss" onclick={onDismiss}>Dismiss</button>
    <button type="button" data-testid="assistance-retry" onclick={onRetry}>Retry</button>
    <button type="button" data-testid="assistance-continue" onclick={onContinue}>Continue without AI</button>
  </div>
</section>

<style>
  .assistance-proposal {
    width: min(420px, calc(100vw - 32px));
    max-height: min(620px, calc(100vh - 32px));
    overflow: auto;
    border: 1px solid var(--color-border);
    border-radius: 10px;
    padding: 1rem;
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-md);
  }

  header, .actions, .patch-row, .metadata div { display: flex; align-items: center; }
  header { justify-content: space-between; gap: 1rem; }
  .eyebrow { margin: 0; color: var(--color-text-2); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.08em; }
  h2 { margin: 0.2rem 0 0; font-size: 1rem; }
  .status { border-radius: 999px; padding: 0.2rem 0.45rem; background: color-mix(in srgb, var(--color-accent) 18%, transparent); font-size: 0.75rem; }
  .metadata { display: grid; grid-template-columns: repeat(3, 1fr); gap: 0.5rem; margin: 1rem 0; }
  .metadata div { flex-direction: column; align-items: flex-start; gap: 0.1rem; }
  dt { color: var(--color-text-2); font-size: 0.75rem; }
  dd { margin: 0; font-size: 0.8rem; }
  fieldset { margin: 0; border: 1px solid var(--color-border); border-radius: 6px; padding: 0.5rem; }
  legend { padding: 0 0.25rem; color: var(--color-text-2); font-size: 0.75rem; }
  .patch-row { gap: 0.5rem; padding: 0.4rem 0; cursor: pointer; }
  .patch-row span { display: grid; gap: 0.1rem; }
  small { color: var(--color-text-2); overflow-wrap: anywhere; }
  .advisory, .safety-note { color: var(--color-text-2); font-size: 0.8rem; }
  .safety-note { border-left: 2px solid var(--color-accent); padding-left: 0.5rem; }
  .actions { flex-wrap: wrap; gap: 0.4rem; }
  .actions button { border: 0; border-radius: 5px; padding: 0.35rem 0.5rem; background: var(--color-elevated); color: inherit; cursor: pointer; font: inherit; }
  .actions button:hover:not(:disabled) { background: var(--color-hover); }
  .actions button:focus-visible { outline: none; box-shadow: var(--focus-ring); }
  .actions button:first-child { background: var(--color-accent); color: var(--color-on-accent); }
  .actions button:disabled { cursor: not-allowed; opacity: 0.5; }
</style>
