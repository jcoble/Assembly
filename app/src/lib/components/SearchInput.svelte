<script lang="ts">
  import { Search, X } from '@lucide/svelte';

  interface Props {
    value?: string;
    placeholder?: string;
    onsubmit?: (v: string) => void;
    onclear?: () => void;
  }

  let {
    value = $bindable(''),
    placeholder = 'Search',
    onsubmit,
    onclear,
  }: Props = $props();

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter') {
      onsubmit?.(value);
    }
    if (e.key === 'Escape') {
      // Standard search affordance: Escape dismisses focus, it does NOT
      // erase the user's query. Use the explicit clear button to wipe it.
      (e.currentTarget as HTMLInputElement | null)?.blur();
    }
  }

  function clear() {
    value = '';
    onclear?.();
  }
</script>

<div class="search-input">
  <span class="icon-lead" aria-hidden="true">
    <Search size={14} />
  </span>

  <input
    type="search"
    bind:value
    {placeholder}
    onkeydown={handleKeydown}
    class="input"
    aria-label="Search"
    autocomplete="off"
    spellcheck="false"
  />

  {#if value}
    <button
      class="clear-btn"
      onclick={clear}
      type="button"
      aria-label="Clear search"
      tabindex={0}
    >
      <X size={12} />
    </button>
  {/if}
</div>

<style>
  .search-input {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    background: var(--color-surface);
    border-radius: var(--radius-md);
    padding: var(--space-2) var(--space-3);
    position: relative;
  }

  .search-input:focus-within {
    box-shadow: var(--focus-ring);
  }

  .icon-lead {
    display: flex;
    align-items: center;
    color: var(--color-text-3);
    flex-shrink: 0;
    pointer-events: none;
  }

  .input {
    flex: 1;
    background: transparent;
    border: none;
    outline: none;
    color: var(--color-text);
    font-size: var(--text-sm);
    font-weight: var(--weight-normal);
    line-height: 1.5;
    padding: 0;
    min-width: 0;
  }

  .input::placeholder {
    color: var(--color-text-3);
  }

  /* This component renders its own clear button; hide the native one that
     type="search" adds in WebKit/Blink so there isn't a duplicate. */
  .input::-webkit-search-cancel-button {
    -webkit-appearance: none;
    appearance: none;
  }

  .clear-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    background: transparent;
    border: none;
    outline: none;
    cursor: pointer;
    color: var(--color-text-3);
    border-radius: var(--radius-sm);
    padding: var(--space-1);
    flex-shrink: 0;
    transition: color 130ms ease, background 130ms ease;
  }

  .clear-btn:hover {
    color: var(--color-text);
    background: var(--color-elevated);
  }

  .clear-btn:focus-visible {
    box-shadow: var(--focus-ring);
  }
</style>
