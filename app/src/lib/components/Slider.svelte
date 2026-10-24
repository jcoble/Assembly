<!--
  Slider.svelte — bits-ui v2 Slider wrapper (type="single")
  Props:
    value    — $bindable number
    min      — number (default 0)
    max      — number (default 100)
    step     — number (default 1)
    label    — optional string label above the slider
    disabled — optional boolean

  Track: --color-elevated background, --color-accent range fill.
  Thumb: clean circle, --focus-ring on focus.
  Value display: --text-xs, --color-text-2, shown to the right of the label row.
-->
<script lang="ts">
  import { Slider } from 'bits-ui';

  interface Props {
    value?: number;
    min?: number;
    max?: number;
    step?: number;
    label?: string;
    disabled?: boolean;
  }

  let {
    value = $bindable(0),
    min = 0,
    max = 100,
    step = 1,
    label,
    disabled = false,
  }: Props = $props();

  // Stable id so the visible label can name the role="slider" thumb.
  const labelId = $props.id();
</script>

<div class="mcb-slider-wrapper" class:mcb-slider-wrapper--disabled={disabled}>
  {#if label}
    <div class="mcb-slider-header">
      <span class="mcb-slider-label" id={labelId}>{label}</span>
      <span class="mcb-slider-value">{value}</span>
    </div>
  {:else}
    <!-- No label: show value inline above thumb track -->
    <div class="mcb-slider-header mcb-slider-header--no-label">
      <span class="mcb-slider-value">{value}</span>
    </div>
  {/if}

  <Slider.Root
    type="single"
    bind:value
    {min}
    {max}
    {step}
    {disabled}
    class="mcb-slider-root"
  >
    {#snippet children({ thumbItems })}
      <!-- Track -->
      <span class="mcb-slider-track">
        <Slider.Range class="mcb-slider-range" />
      </span>
      <!-- Thumb(s) — single type always has exactly one.
           aria-labelledby/-label lands on the role="slider" element (bits
           merges restProps onto the thumb span), giving it an accessible name. -->
      {#each thumbItems as { index } (index)}
        <Slider.Thumb
          {index}
          class="mcb-slider-thumb"
          aria-labelledby={label ? labelId : undefined}
          aria-label={label ? undefined : 'Value'}
        />
      {/each}
    {/snippet}
  </Slider.Root>
</div>

<style>
  .mcb-slider-wrapper {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    width: 100%;
  }

  .mcb-slider-wrapper--disabled {
    opacity: 0.4;
    pointer-events: none;
  }

  /* ── Label / value row ───────────────────────────────── */
  .mcb-slider-header {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: var(--space-2);
  }

  .mcb-slider-header--no-label {
    justify-content: flex-end;
  }

  .mcb-slider-label {
    font-size: var(--text-sm);
    font-weight: var(--weight-medium);
    color: var(--color-text-2);
    user-select: none;
  }

  .mcb-slider-value {
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
    color: var(--color-text-2);
    min-width: 2.5ch;
    text-align: right;
    user-select: none;
    font-variant-numeric: tabular-nums;
  }

  /* NOTE: .mcb-slider-root / .mcb-slider-range / .mcb-slider-thumb are forwarded to
     bits-ui-rendered elements, so they must use :global(...). Only
     .mcb-slider-track / header / label / value are local elements here. */

  /* ── Slider root — flex row, vertically centers track + thumb ── */
  :global(.mcb-slider-root) {
    position: relative;
    display: flex;
    width: 100%;
    touch-action: none;
    user-select: none;
    align-items: center;
    cursor: pointer;
    /* Enough height for the thumb + focus ring */
    min-height: 20px;
  }

  /* ── Track ───────────────────────────────────────────── */
  .mcb-slider-track {
    position: relative;
    height: 4px;
    width: 100%;
    flex-grow: 1;
    overflow: hidden;
    border-radius: var(--radius-pill);
    background-color: var(--color-elevated);
  }

  /* ── Range (filled portion) ──────────────────────────── */
  :global(.mcb-slider-range) {
    position: absolute;
    height: 100%;
    background-color: var(--color-accent);
    border-radius: var(--radius-pill);
  }

  /* ── Thumb ───────────────────────────────────────────── */
  :global(.mcb-slider-thumb) {
    display: block;
    width: 16px;
    height: 16px;
    border-radius: var(--radius-pill);
    background-color: var(--color-text);
    border: 2px solid var(--color-accent);
    box-shadow: var(--shadow-thumb);
    cursor: grab;
    outline: none;
    transition:
      box-shadow 130ms ease,
      transform 130ms ease;
    /* Vertically centered by flex parent; pull it out of the track overflow */
    position: absolute;
    transform: translateX(-50%);
  }

  :global(.mcb-slider-thumb:hover) {
    transform: translateX(-50%) scale(1.1);
  }

  /* data-active is set by bits-ui while dragging */
  :global(.mcb-slider-thumb[data-active]) {
    cursor: grabbing;
    transform: translateX(-50%) scale(0.97);
  }

  :global(.mcb-slider-thumb:focus-visible) {
    box-shadow: var(--focus-ring);
  }
</style>
