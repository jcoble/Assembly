<script lang="ts">
  import type { AgentPlanStep, ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  import WorkingSpinner from '../../components/feedback/WorkingSpinner.svelte';

  /** How many files the running turn touched, and by how many lines. A turn
   * that changed files without a diff to read has no line counts, and the chip
   * leaves that half of the line out when both are zero. */
  export interface PlanFileChanges {
    files: number;
    added: number;
    removed: number;
  }

  let { plan, fileChanges = null, expanded, running, onToggle }: {
    plan: Extract<ConversationDisplayItem, { kind: 'plan' }> | null;
    fileChanges?: PlanFileChanges | null;
    expanded: boolean;
    /** Whether the session is working right now. A turn cancelled with a step
     * still marked in progress leaves that mark behind, and an idle app spun
     * the glyph over it for as long as the plan stayed on screen. */
    running: boolean;
    onToggle(): void;
  } = $props();

  const panelId = $props.id();

  /* Which step the session is on. The one in progress is the answer when there
     is one; before anything has started that is the first step still pending,
     and once every step is done the plan sits on its last one. */
  const currentStep = $derived.by(() => {
    if (!plan) return 0;
    const running = plan.steps.findIndex((step) => step.state === 'in-progress');
    if (running >= 0) return running + 1;
    const pending = plan.steps.findIndex((step) => step.state === 'pending');
    if (pending >= 0) return pending + 1;
    return plan.steps.length;
  });
  const currentState = $derived<AgentPlanStep['state']>(
    plan?.steps[currentStep - 1]?.state ?? 'pending'
  );
  const fileLabel = $derived(
    fileChanges && fileChanges.files > 0
      ? `${fileChanges.files} ${fileChanges.files === 1 ? 'file' : 'files'} changed`
      : ''
  );
  const showLineCounts = $derived(!!fileChanges && fileChanges.added + fileChanges.removed > 0);
</script>

<!-- A running step shows the working spinner; the shut panel's steps never
     mount one, since `visibility: hidden` does not stop it the way scrolling off screen does. -->
{#snippet stateGlyph(state: AgentPlanStep['state'], spin: boolean)}
  <span class="glyph" data-state={state} aria-hidden="true">
    {#if spin && state === 'in-progress'}<WorkingSpinner size={14} />
    {:else}
      <svg viewBox="0 0 16 16">
        <circle class="glyph-track" cx="8" cy="8" r="6" />
        <circle class="glyph-arc" cx="8" cy="8" r="6" pathLength="100" />
      </svg>
    {/if}
  </span>
{/snippet}

<div class="plan-chip" data-testid="conversation-plan-chip">
  {#if plan}<div class="plan-panel" class:open={expanded} id={panelId} data-testid="conversation-plan-panel">
    <ol>
      {#each plan.steps as step (step.id)}
        <li data-state={step.state}>
          {@render stateGlyph(step.state, running && expanded)}
          <span>{step.title}</span>
        </li>
      {/each}
    </ol>
  </div>{/if}
  <button
    class="plan-pill"
    type="button"
    data-testid="conversation-plan-pill"
    aria-expanded={plan ? expanded : undefined}
    aria-controls={plan ? panelId : undefined}
    onclick={() => { if (plan) onToggle(); }}
  >
    {#if plan}
      {@render stateGlyph(currentState, running)}
      <span class="plan-step">Step {currentStep} / {plan.steps.length}</span>
    {/if}
    {#if fileLabel}
      {#if plan}<span class="plan-dot" aria-hidden="true">·</span>{/if}
      <span class="plan-files">{fileLabel}</span>
      {#if showLineCounts && fileChanges}
        <span class="plan-added">+{fileChanges.added}</span>
        <span class="plan-removed">−{fileChanges.removed}</span>
      {/if}
    {/if}
  </button>
</div>

<style>
  /* The chip is one centred pill with a panel that opens upward out of it. The
     panel is taken out of the flow on purpose: opening it must not push the
     composer down or shorten the transcript above it. */
  .plan-chip { position: relative; display: flex; justify-content: center; margin-bottom: 10px; }

  .plan-pill {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    max-width: min(560px, calc(100% - 44px));
    padding: 8px 16px;
    border: 0;
    border-radius: var(--radius-pill);
    /* The card surface and its shadow are what lift the pill off the page.
       No outline at rest — the composer capsule below it has none either. */
    background: var(--color-elevated);
    box-shadow: var(--shadow-sm);
    color: var(--color-text);
    font: 14px/1.2 inherit;
    cursor: pointer;
    transition: background 140ms cubic-bezier(0.2, 0, 0, 1);
  }
  .plan-pill:hover { background: var(--color-hover); }
  .plan-pill:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 2px; }
  .plan-step { white-space: nowrap; }
  .plan-dot { color: var(--color-text-3); }
  .plan-files { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .plan-added { color: var(--color-good); }
  .plan-removed { color: var(--color-bad); }

  .plan-panel {
    position: absolute;
    bottom: calc(100% + 10px);
    left: 50%;
    z-index: 1;
    min-width: 240px;
    max-width: min(620px, calc(100% - 44px));
    padding: 13px 17px;
    border-radius: var(--radius-lg);
    background: var(--color-elevated);
    box-shadow: var(--shadow-lg);
    text-align: left;
    /* Shut, it is a shade smaller and a few pixels lower, so opening reads as
       the panel coming up out of the pill rather than appearing over it.
       Only transform and opacity move, and `visibility` is what takes the
       shut panel out of the tab order and off the pointer. */
    transform: translateX(-50%) translateY(6px) scale(0.985);
    transform-origin: 50% 100%;
    opacity: 0;
    visibility: hidden;
    transition:
      opacity 200ms cubic-bezier(0.2, 0, 0, 1),
      transform 200ms cubic-bezier(0.2, 0, 0, 1),
      visibility 200ms;
  }
  .plan-panel.open { transform: translateX(-50%); opacity: 1; visibility: visible; }
  .plan-panel ol { display: grid; gap: 11px; margin: 0; padding: 0; list-style: none; }
  .plan-panel li { display: flex; align-items: flex-start; gap: 11px; color: var(--color-text-2); font-size: 14px; line-height: 1.35; }
  .plan-panel li[data-state='in-progress'] { color: var(--color-text); font-weight: 560; }
  .plan-panel li[data-state='completed'] { color: var(--color-text-3); }
  .plan-panel .glyph { margin-top: 1px; }

  /* One mark for every step state, in the pill and in the panel alike:
     an open ring waiting, the working spinner while it runs, a filled disc once it
     has finished one way or the other. */
  .glyph { display: inline-flex; flex: none; }
  .glyph svg { width: 14px; height: 14px; }
  .glyph-track { fill: none; stroke: var(--color-text-3); stroke-width: 1.5; }
  .glyph-arc { fill: none; stroke: none; }
  .glyph[data-state='in-progress'] .glyph-track { stroke: color-mix(in srgb, var(--color-accent) 30%, transparent); }
  .glyph[data-state='in-progress'] .glyph-arc {
    stroke: var(--color-accent);
    stroke-width: 2;
    stroke-linecap: round;
    stroke-dasharray: 30 100;
  }
  .glyph[data-state='completed'] .glyph-track { fill: var(--color-accent); stroke: var(--color-accent); }
  .glyph[data-state='failed'] .glyph-track { fill: var(--color-bad); stroke: var(--color-bad); }
  .glyph[data-state='blocked'] .glyph-track { fill: var(--color-attention); stroke: var(--color-attention); }

  @media (prefers-reduced-motion: reduce) {
    .plan-pill, .plan-panel { transition: none; }
  }
</style>
