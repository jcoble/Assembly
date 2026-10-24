<script lang="ts">
  import type { Component } from 'svelte';

  interface Props {
    icon: Component;
    label: string;
    size?: 'sm' | 'md';
    variant?: 'default' | 'ghost' | 'danger';
    disabled?: boolean;
    onclick?: (event: MouseEvent) => void;
  }

  let {
    icon: Icon,
    label,
    size = 'md',
    variant = 'default',
    disabled = false,
    onclick,
  }: Props = $props();
</script>

<button
  type="button"
  {disabled}
  class="icon-btn icon-btn--{variant} icon-btn--{size}"
  aria-label={label}
  title={label}
  onclick={onclick}
>
  <span aria-hidden="true" class="icon-btn__icon">
    <Icon />
  </span>
</button>

<style>
  .icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border: none;
    border-radius: var(--radius-md);
    cursor: pointer;
    flex-shrink: 0;
    transition:
      background-color 130ms ease,
      opacity 130ms ease,
      box-shadow 130ms ease;
    outline: none;
    user-select: none;
  }

  /* ── Sizes (square) ─────────────────────────────────── */
  .icon-btn--sm {
    width: 28px;
    height: 28px;
  }
  .icon-btn--md {
    width: 36px;
    height: 36px;
  }

  /* ── Variants ───────────────────────────────────────── */
  .icon-btn--default {
    background-color: var(--color-surface);
    color: var(--color-text-2);
  }
  .icon-btn--default:hover:not(:disabled) {
    background-color: var(--color-elevated);
    color: var(--color-text);
  }

  .icon-btn--ghost {
    background-color: transparent;
    color: var(--color-text-3);
  }
  .icon-btn--ghost:hover:not(:disabled) {
    background-color: var(--color-surface);
    color: var(--color-text-2);
  }

  .icon-btn--danger {
    background-color: var(--color-bad-bg);
    color: var(--color-bad);
  }
  .icon-btn--danger:hover:not(:disabled) {
    background-color: var(--color-bad-bg-strong);
  }

  /* ── Focus ──────────────────────────────────────────── */
  .icon-btn:focus-visible {
    box-shadow: var(--focus-ring);
  }

  /* ── Disabled ───────────────────────────────────────── */
  .icon-btn:disabled {
    opacity: 0.4;
    cursor: not-allowed;
    pointer-events: none;
  }

  /* ── Icon sizing ────────────────────────────────────── */
  .icon-btn__icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
  }

  .icon-btn--sm .icon-btn__icon :global(svg) {
    width: 14px;
    height: 14px;
  }
  .icon-btn--md .icon-btn__icon :global(svg) {
    width: 16px;
    height: 16px;
  }
</style>
