<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Tooltip } from 'bits-ui';

  interface Props {
    /** Plain text label — use this for simple tooltips */
    text?: string;
    /** Rich content snippet — takes precedence over `text` */
    content?: Snippet;
    /** Side to prefer. Default: 'bottom' */
    side?: 'top' | 'bottom' | 'left' | 'right';
    /** Delay before showing (ms). Default: 600 */
    delayDuration?: number;
    /** The element that receives the tooltip */
    children: Snippet;
  }

  let {
    text,
    content,
    side = 'bottom',
    delayDuration = 0,
    children,
  }: Props = $props();
</script>

<!--
  NOTE: this wrapper mounts its OWN Tooltip.Provider so it works standalone.
  As a result `skipDelayDuration` (skip the open-delay when moving between
  adjacent tooltips) would be INERT here — it only takes effect when several
  Tooltip.Root share ONE Provider. So we deliberately do NOT expose/pass it.
  For app-wide skip-delay behaviour, mount a single <Tooltip.Provider
  skipDelayDuration={…}> at the app root and have these render only Root/Trigger/
  Content beneath it. `delayDuration` IS honoured per-provider and is kept.
-->
<Tooltip.Provider {delayDuration}>
  <Tooltip.Root>
    <Tooltip.Trigger class="mcb-tooltip-trigger">
      {@render children()}
    </Tooltip.Trigger>

    <Tooltip.Portal>
      <Tooltip.Content class="mcb-tooltip-content" {side} sideOffset={6}>
        {#if content}
          {@render content()}
        {:else if text}
          {text}
        {/if}
        <Tooltip.Arrow class="mcb-tooltip-arrow" />
      </Tooltip.Content>
    </Tooltip.Portal>
  </Tooltip.Root>
</Tooltip.Provider>

<style>
  /* ── Trigger: transparent-ish wrapper ──────────────────────────────
     inline-flex (not display:contents) so the trigger keeps a box — a
     focus-visible ring has something to paint on and the hover/pointer
     geometry stays intact. The trigger itself carries no visual chrome. */
  :global(.mcb-tooltip-trigger) {
    display: inline-flex;
    align-items: center;
    background: transparent;
    border: none;
    padding: 0;
    margin: 0;
    font: inherit;
    color: inherit;
    cursor: inherit;
    border-radius: var(--radius-sm);
    outline: none;
  }

  :global(.mcb-tooltip-trigger:focus-visible) {
    box-shadow: var(--focus-ring);
  }

  /* ── Content bubble ────────────────────────────────────────────── */
  :global(.mcb-tooltip-content) {
    padding: var(--space-1) var(--space-2);
    background-color: var(--color-elevated);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    box-shadow: var(--shadow-sm);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
    color: var(--color-text-2);
    white-space: nowrap;
    pointer-events: none;
    outline: none;
    z-index: 9100;
    max-width: 260px;
    white-space: normal;

    animation: tooltip-in 100ms cubic-bezier(0.16, 1, 0.3, 1);
  }

  @keyframes tooltip-in {
    from {
      opacity: 0;
      transform: scale(0.95);
    }
    to {
      opacity: 1;
      transform: scale(1);
    }
  }

  /* ── Arrow ─────────────────────────────────────────────────────── */
  :global(.mcb-tooltip-arrow) {
    fill: var(--color-elevated);
  }
</style>
