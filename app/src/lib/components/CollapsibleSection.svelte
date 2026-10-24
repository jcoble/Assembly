<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { ChevronRight } from '@lucide/svelte';

  interface Props {
    title: string;
    icon?: Component;
    badge?: string | number;
    expanded?: boolean;
    children?: Snippet;
  }

  let {
    title,
    icon: Icon,
    badge,
    expanded = $bindable(true),
    children,
  }: Props = $props();

  function toggle() {
    expanded = !expanded;
  }
</script>

<section class="collapsible-section">
  <button
    class="header"
    type="button"
    aria-expanded={expanded}
    onclick={toggle}
  >
    <span class="chevron" class:expanded aria-hidden="true">
      <ChevronRight size={14} />
    </span>

    {#if Icon}
      <span class="section-icon" aria-hidden="true">
        <Icon size={14} />
      </span>
    {/if}

    <span class="title">{title}</span>

    {#if badge !== undefined && badge !== null && badge !== ''}
      <span class="section-count" aria-label="{badge} items">
        {badge}
      </span>
    {/if}
  </button>

  {#if expanded}
    <div class="body" role="region" aria-label={title}>
      {@render children?.()}
    </div>
  {/if}
</section>

<style>
  .collapsible-section {
    display: flex;
    flex-direction: column;
  }

  .header {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    width: 100%;
    background: transparent;
    border: none;
    outline: none;
    cursor: pointer;
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    text-align: left;
    color: var(--color-text-2);
    transition: background 130ms ease, color 130ms ease;
  }

  .header:hover {
    background: var(--color-elevated);
    color: var(--color-text);
  }

  .header:focus-visible {
    box-shadow: var(--focus-ring);
  }

  .chevron {
    display: flex;
    align-items: center;
    color: var(--color-text-3);
    flex-shrink: 0;
    transform: rotate(0deg);
    transition: transform 130ms ease;
  }

  .chevron.expanded {
    transform: rotate(90deg);
  }

  .section-icon {
    display: flex;
    align-items: center;
    color: var(--color-text-3);
    flex-shrink: 0;
  }

  .title {
    flex: 1;
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    letter-spacing: 0.04em;
    text-transform: uppercase;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* Inline count pill — a header-tuned affordance, intentionally distinct
     from the standalone Badge component (own min-width/height for the row). */
  .section-count {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    background: var(--color-elevated);
    color: var(--color-text-3);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
    font-variant-numeric: tabular-nums;
    border-radius: var(--radius-pill);
    padding: 0 var(--space-2);
    min-width: 18px;
    height: 16px;
    line-height: 1;
    flex-shrink: 0;
  }

  .body {
    display: flex;
    flex-direction: column;
  }
</style>
