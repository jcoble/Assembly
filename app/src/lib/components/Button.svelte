<script lang="ts">
  import type { Component, Snippet } from 'svelte';

  interface Props {
    variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
    size?: 'sm' | 'md';
    disabled?: boolean;
    type?: 'button' | 'submit' | 'reset';
    onclick?: (event: MouseEvent) => void;
    leftIcon?: Component;
    children?: Snippet;
  }

  let {
    variant = 'secondary',
    size = 'md',
    disabled = false,
    type = 'button',
    onclick,
    leftIcon: LeftIcon,
    children,
  }: Props = $props();
</script>

<button
  {type}
  {disabled}
  class="btn btn--{variant} btn--{size}"
  onclick={onclick}
>
  {#if LeftIcon}
    <span class="btn__icon" aria-hidden="true">
      <LeftIcon />
    </span>
  {/if}
  {#if children}
    <span class="btn__label">{@render children()}</span>
  {/if}
</button>

<style>
  .btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: var(--space-2);
    border: none;
    border-radius: var(--radius-md);
    font-family: inherit;
    font-weight: var(--weight-medium);
    letter-spacing: 0.01em;
    cursor: pointer;
    white-space: nowrap;
    transition:
      background-color 130ms ease,
      opacity 130ms ease,
      box-shadow 130ms ease;
    outline: none;
    text-decoration: none;
    user-select: none;
  }

  /* ── Sizes ─────────────────────────────────────────── */
  .btn--sm {
    padding: var(--space-1) var(--space-3);
    font-size: var(--text-sm);
    min-height: 28px;
  }

  .btn--md {
    padding: var(--space-2) var(--space-4);
    font-size: var(--text-md);
    min-height: 36px;
  }

  /* ── Variants ───────────────────────────────────────── */
  .btn--primary {
    background-color: var(--color-accent);
    color: var(--color-on-accent);
  }
  .btn--primary:hover:not(:disabled) {
    filter: brightness(1.08);
  }

  .btn--secondary {
    background-color: var(--color-surface);
    color: var(--color-text);
  }
  .btn--secondary:hover:not(:disabled) {
    background-color: var(--color-elevated);
  }

  .btn--ghost {
    background-color: transparent;
    color: var(--color-text-2);
  }
  .btn--ghost:hover:not(:disabled) {
    background-color: var(--color-surface);
    color: var(--color-text);
  }

  .btn--danger {
    background-color: var(--color-bad-bg);
    color: var(--color-bad);
  }
  .btn--danger:hover:not(:disabled) {
    background-color: var(--color-bad-bg-strong);
  }

  /* ── Focus ──────────────────────────────────────────── */
  .btn:focus-visible {
    box-shadow: var(--focus-ring);
  }

  /* ── Disabled ───────────────────────────────────────── */
  .btn:disabled {
    opacity: 0.4;
    cursor: not-allowed;
    pointer-events: none;
  }

  /* ── Icon slot ──────────────────────────────────────── */
  .btn__icon {
    display: inline-flex;
    align-items: center;
    flex-shrink: 0;
  }

  /* Lucide icons size to font context */
  .btn--sm .btn__icon :global(svg) {
    width: 14px;
    height: 14px;
  }
  .btn--md .btn__icon :global(svg) {
    width: 16px;
    height: 16px;
  }
</style>
