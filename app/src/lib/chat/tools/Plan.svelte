<script lang="ts">
  import type { PlanItem } from '../../rows/store';
  let { steps, running }: { steps: PlanItem[] | null; running: boolean } = $props();

  // One plan above the composer: the newest plan row, whichever turn wrote it.
  // The step the agent is on (1-based): the one in progress, else the first
  // still pending, else the last.
  const step = $derived.by(() => {
    if (!steps?.length) return 0;
    const active = steps.findIndex((s) => s.status === 'in_progress');
    if (active >= 0) return active + 1;
    const pending = steps.findIndex((s) => s.status === 'pending');
    return pending >= 0 ? pending + 1 : steps.length;
  });
  let expanded = $state(false);

  // The plan is what the turn is doing, so its panel shuts when the turn ends.
  let wasRunning = false;
  $effect(() => {
    if (running) wasRunning = true;
    else if (wasRunning) {
      wasRunning = false;
      expanded = false;
    }
  });
</script>

<svelte:window onkeydown={(event) => { if (event.key === 'Escape') expanded = false; }} />

{#if steps?.length}
  <div class="plan">
    {#if expanded}
      <ol class="panel">
        {#each steps as s, i (i)}
          <li data-state={s.status}><span class="glyph" class:spin={running} data-state={s.status}></span>{s.text}</li>
        {/each}
      </ol>
    {/if}
    <button class="pill" aria-expanded={expanded} onclick={() => (expanded = !expanded)}>
      <span class="glyph" class:spin={running} data-state={steps[step - 1].status}></span>
      Step {step} / {steps.length}
    </button>
  </div>
{/if}

<style>
  .plan {
    position: relative;
    display: flex;
    justify-content: center;
    margin-bottom: 10px;
  }
  .pill {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 6px 14px;
    border-radius: 999px;
    border: 1px solid var(--line);
    background: var(--panel);
    color: inherit;
    font-size: 13px;
  }
  /* The panel opens upward out of the pill without pushing the composer down. */
  .panel {
    position: absolute;
    bottom: calc(100% + 8px);
    left: 50%;
    transform: translateX(-50%);
    z-index: 1;
    min-width: 240px;
    max-width: min(620px, 100%);
    margin: 0;
    padding: 12px 16px;
    list-style: none;
    display: grid;
    gap: 10px;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: var(--panel);
    box-shadow: 0 6px 24px rgb(0 0 0 / 0.15);
  }
  li {
    display: flex;
    gap: 10px;
    align-items: flex-start;
    font-size: 13px;
    color: var(--muted);
  }
  li[data-state='in_progress'] {
    color: inherit;
    font-weight: 600;
  }
  li[data-state='completed'] {
    text-decoration: line-through;
  }
  /* An open ring waiting, a turning arc while running, a filled disc once done. */
  .glyph {
    flex: none;
    width: 10px;
    height: 10px;
    margin-top: 2px;
    border-radius: 50%;
    border: 1.5px solid var(--muted);
  }
  .glyph[data-state='in_progress'] {
    border-color: var(--line);
    border-top-color: var(--accent);
  }
  .glyph.spin[data-state='in_progress'] {
    animation: turn 0.9s linear infinite;
  }
  .glyph[data-state='completed'] {
    background: var(--accent);
    border-color: var(--accent);
  }
  @keyframes turn {
    to {
      transform: rotate(360deg);
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .glyph.spin[data-state='in_progress'] {
      animation: none;
    }
  }
</style>
