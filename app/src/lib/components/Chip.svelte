<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { Component } from 'svelte';
  import { X } from '@lucide/svelte';
  import { toneStyle, type Tone } from './tone.js';

  interface Props {
    tone?: Tone;
    size?: 'xs' | 'sm';
    /** Optional Lucide icon component rendered before the label. */
    leadingIcon?: Component<{ size?: number | string; class?: string }>;
    /** When provided, renders a dismiss (×) button. */
    ondismiss?: () => void;
    /** Accessible label for the dismiss button (e.g. "Remove Live"). Defaults to "Remove". */
    dismissLabel?: string;
    children?: Snippet;
  }

  let {
    tone = 'neutral',
    size = 'sm',
    leadingIcon: LeadingIcon,
    ondismiss,
    dismissLabel = 'Remove',
    children,
  }: Props = $props();

  const iconSize = $derived(size === 'xs' ? 10 : 12);
</script>

<span
  class="chip chip--{size}"
  data-tone={tone}
  style={toneStyle(tone)}
  role={ondismiss ? 'group' : undefined}
>
  {#if LeadingIcon}
    <span class="chip__icon" aria-hidden="true">
      <LeadingIcon size={iconSize} />
    </span>
  {/if}

  <span class="chip__label">
    {@render children?.()}
  </span>

  {#if ondismiss}
    <button
      type="button"
      class="chip__dismiss"
      aria-label={dismissLabel}
      onclick={ondismiss}
    >
      <X size={iconSize} />
    </button>
  {/if}
</span>

<style>
  /* ── Chip shell ─────────────────────────────────────────────────── */
  .chip {
    display: inline-flex;
    align-items: center;
    gap: var(--space-1);
    border-radius: var(--radius-pill);
    font-weight: var(--weight-medium);
    white-space: nowrap;
    line-height: 1;

    /* tone-driven via CSS custom props set by toneStyle() */
    color:            var(--_tone-color);
    background-color: var(--_tone-bg);
  }

  /* ── Sizes ──────────────────────────────────────────────────────── */
  .chip--sm {
    font-size:      var(--text-sm);
    padding:        var(--space-1) var(--space-2);
    gap:            var(--space-1);
  }

  .chip--xs {
    font-size:      var(--text-xs);
    /* 2px vertical / gap are intentionally below the --space scale floor
       (4px): the xs chip needs a tighter inset than --space-1 allows. */
    padding:        2px var(--space-1);
    gap:            2px;
  }

  /* ── Parts ──────────────────────────────────────────────────────── */
  .chip__icon {
    display:     flex;
    align-items: center;
    flex-shrink: 0;
    opacity:     0.85;
  }

  .chip__label {
    display: flex;
    align-items: center;
  }

  /* ── Dismiss button ─────────────────────────────────────────────── */
  .chip__dismiss {
    all:         unset;
    display:     flex;
    align-items: center;
    cursor:      pointer;
    border-radius: var(--radius-pill);
    opacity:     0.65;
    transition:  opacity 130ms ease;
    /* tighten touch target without affecting layout */
    margin-inline-start: 1px;
  }

  .chip__dismiss:hover {
    opacity: 1;
  }

  .chip__dismiss:focus-visible {
    outline:    none;
    box-shadow: var(--focus-ring);
  }
</style>
