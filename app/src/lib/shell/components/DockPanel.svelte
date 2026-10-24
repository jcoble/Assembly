<script lang="ts">
  /**
   * The bottom dock region for Problems.
   *
   * This region is on screen from the moment the shell opens, so the panel it
   * hosts must load nothing when it mounts — `ProblemsPanel` reads the language
   * server only when the user presses its Refresh button. See
   * `panelActivation.ts` for the rule and the route that is waiting for a
   * gesture to fire it.
   *
   * Moving the list is a SETTING, not a piece of state this component keeps: the
   * buttons write `settings.panels.problemsLocation` and say so, and the shell
   * reacts to the setting. That way the settings dialog and these buttons cannot
   * disagree, and the choice survives a restart.
   */
  import X from '@lucide/svelte/icons/x';

  import { settings, type ProblemsLocation } from '$lib/shell/settings/settingsStore.svelte';

  import ProblemsPanel from './problems/ProblemsPanel.svelte';

  interface Props {
    root: string;
    onReset(): void;
    /**
     * The user asked for the Problems list somewhere else. The setting is
     * already written by the time this runs — this is only so the shell can
     * close the bottom strip and open the list in its new home straight away,
     * rather than on the next launch.
     */
    onProblemsLocationChange?(location: ProblemsLocation): void;
  }
  let { onReset, onProblemsLocationChange }: Props = $props();

  function moveProblems(location: ProblemsLocation): void {
    settings.panels.problemsLocation = location;
    onProblemsLocationChange?.(location);
  }
</script>

{#snippet actions()}
  <button
    class="dock-action"
    type="button"
    title="Hide the bottom dock. Settings or the command palette brings it back."
    onclick={() => moveProblems('hidden')}
  >
    <X size={13} strokeWidth={1.6} />
    Hide
  </button>
  <button class="dock-action" type="button" onclick={onReset}>Reset layout</button>
{/snippet}

<div class="dock-slot">
  <ProblemsPanel headerEnd={actions} />
</div>

<style>
  .dock-slot {
    position: relative;
    height: 100%;
  }

  /* On the end of the panel's own header row. These used to float in that
     corner, which put them straight on top of the panel's Refresh button. */
  .dock-action {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    flex-shrink: 0;
    /* The same 28px the panel's own Refresh button is, so the header row reads
       as one row of controls rather than two sizes of button. */
    height: 28px;
    background: transparent;
    border: 0;
    border-radius: 6px;
    color: var(--color-text-2);
    font-family: inherit;
    font-size: 12px;
    padding: 0 8px;
    cursor: pointer;
    transition: color 120ms, background-color 120ms;
  }

  .dock-action:hover {
    color: var(--color-text);
    background: var(--color-elevated);
  }

  .dock-action:focus-visible {
    outline: none;
    box-shadow: var(--focus-ring);
  }
</style>
