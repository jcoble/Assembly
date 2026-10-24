<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { DropdownMenu } from 'bits-ui';

  export interface MenuItem {
    id: string;
    label: string;
    /** Lucide icon component */
    icon?: Component<{ size?: number | string; class?: string }>;
    onselect?: () => void;
    danger?: boolean;
    separatorBefore?: boolean;
    disabled?: boolean;
  }

  interface Props {
    /** Array-driven API: pass flat item list */
    items?: MenuItem[];
    /** Snippet-based API: render your own content inside the panel */
    content?: Snippet;
    /** The element that opens the menu */
    trigger: Snippet;
    /** Alignment of the content panel relative to the trigger */
    align?: 'start' | 'center' | 'end';
    sideOffset?: number;
  }

  let {
    items,
    content,
    trigger,
    align = 'start',
    sideOffset = 6,
  }: Props = $props();
</script>

<DropdownMenu.Root>
  <DropdownMenu.Trigger>
    {@render trigger()}
  </DropdownMenu.Trigger>

  <DropdownMenu.Portal>
    <DropdownMenu.Content
      class="mcb-menu-content"
      {align}
      {sideOffset}
    >
      {#if content}
        {@render content()}
      {:else if items}
        {#each items as item (item.id)}
          {#if item.separatorBefore}
            <DropdownMenu.Separator class="mcb-menu-separator" />
          {/if}
          <DropdownMenu.Item
            class="mcb-menu-item{item.danger ? ' mcb-menu-item--danger' : ''}"
            disabled={item.disabled}
            onSelect={item.onselect}
          >
            {#if item.icon}
              <span class="mcb-menu-item__icon" aria-hidden="true">
                <item.icon size={14} />
              </span>
            {/if}
            <span class="mcb-menu-item__label">{item.label}</span>
          </DropdownMenu.Item>
        {/each}
      {/if}
    </DropdownMenu.Content>
  </DropdownMenu.Portal>
</DropdownMenu.Root>

<style>
  /* ── Content panel ─────────────────────────────────────────────── */
  :global(.mcb-menu-content) {
    min-width: 180px;
    max-width: 280px;
    padding: var(--space-1);
    background-color: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-md);
    outline: none;
    z-index: 9000;

    /* Subtle entrance */
    animation: menu-in 130ms cubic-bezier(0.16, 1, 0.3, 1);
  }

  @keyframes menu-in {
    from {
      opacity: 0;
      transform: translateY(-4px) scale(0.97);
    }
    to {
      opacity: 1;
      transform: translateY(0) scale(1);
    }
  }

  /* ── Separator ─────────────────────────────────────────────────── */
  :global(.mcb-menu-separator) {
    display: block;
    height: 1px;
    margin: var(--space-1) calc(-1 * var(--space-1));
    background-color: var(--color-border);
  }

  /* ── Item ──────────────────────────────────────────────────────── */
  :global(.mcb-menu-item) {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    width: 100%;
    padding: var(--space-2) var(--space-2);
    border-radius: var(--radius-sm);
    font-size: var(--text-sm);
    font-weight: var(--weight-medium);
    color: var(--color-text-2);
    cursor: default;
    user-select: none;
    outline: none;
    border: none;
    background: transparent;
    transition:
      background-color 130ms ease,
      color 130ms ease;
  }

  :global(.mcb-menu-item[data-highlighted]) {
    background-color: var(--color-surface);
    color: var(--color-text);
  }

  :global(.mcb-menu-item[data-disabled]) {
    opacity: 0.4;
    cursor: not-allowed;
    pointer-events: none;
  }

  :global(.mcb-menu-item--danger) {
    color: var(--color-bad);
  }

  :global(.mcb-menu-item--danger[data-highlighted]) {
    background-color: var(--color-bad-bg);
    color: var(--color-bad);
  }

  /* ── Item parts ────────────────────────────────────────────────── */
  :global(.mcb-menu-item__icon) {
    display: inline-flex;
    align-items: center;
    flex-shrink: 0;
    opacity: 0.75;
  }

  :global(.mcb-menu-item[data-highlighted] .mcb-menu-item__icon) {
    opacity: 1;
  }

  :global(.mcb-menu-item__label) {
    flex: 1;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
</style>
