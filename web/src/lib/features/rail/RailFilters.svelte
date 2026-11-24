<script lang="ts">
  /**
   * 1:1 `Provider` | `Status` | `Location` filter pills bar with multi-select glass popovers.
   */
  import { Check, X } from '@lucide/svelte';
  import PopoverMenu from '$lib/ui/PopoverMenu.svelte';
  import { hasActiveFilters, providerDisplayName } from './format';
  import type { RailFilters, SessionMarkState } from './types';

  interface Props {
    agents: string[];
    filters: RailFilters;
    onChange: (next: RailFilters) => void;
    onClear: () => void;
  }

  let { agents, filters, onChange, onClear }: Props = $props();

  type GroupId = 'provider' | 'status' | 'location';
  let openGroup = $state<GroupId | null>(null);

  const providerOptions = $derived.by(() => {
    const ids = new Set<string>([...agents, ...filters.provider]);
    return [...ids].map((value) => ({ value, label: providerDisplayName(value) }));
  });

  const statusOptions: { value: SessionMarkState; label: string }[] = [
    { value: 'working', label: 'Working' },
    { value: 'done', label: 'Done' },
    { value: 'settled', label: 'Settled' }
  ];

  const locationOptions: { value: 'local' | 'remote'; label: string }[] = [
    { value: 'local', label: 'Local' },
    { value: 'remote', label: 'Remote' }
  ];

  function toggleProvider(value: string): void {
    const next = filters.provider.includes(value)
      ? filters.provider.filter((v) => v !== value)
      : [...filters.provider, value];
    onChange({ ...filters, provider: next });
  }

  function toggleStatus(value: SessionMarkState): void {
    const next = filters.status.includes(value)
      ? filters.status.filter((v) => v !== value)
      : [...filters.status, value];
    onChange({ ...filters, status: next });
  }

  function toggleLocation(value: 'local' | 'remote'): void {
    const next = filters.location.includes(value)
      ? filters.location.filter((v) => v !== value)
      : [...filters.location, value];
    onChange({ ...filters, location: next });
  }

  const anyActive = $derived(hasActiveFilters(filters));
</script>

<div class="filter-pills">
  <!-- Provider pill -->
  <div class="pill-anchor">
    <button
      type="button"
      class="pill"
      class:active={filters.provider.length > 0 || openGroup === 'provider'}
      onpointerdown={(event) => event.stopPropagation()}
      onclick={() => (openGroup = openGroup === 'provider' ? null : 'provider')}
    >
      <span>Provider</span>
      {#if filters.provider.length > 0}
        <span class="pill-count">{filters.provider.length}</span>
      {/if}
    </button>

    <PopoverMenu open={openGroup === 'provider'} minWidth={168} onClose={() => (openGroup = null)}>
      {#if providerOptions.length === 0}
        <div class="empty-opt">No providers yet</div>
      {:else}
        {#each providerOptions as opt (opt.value)}
          {@const checked = filters.provider.includes(opt.value)}
          <button
            type="button"
            class="check-item"
            class:checked
            onclick={() => toggleProvider(opt.value)}
          >
            <span>{opt.label}</span>
            {#if checked}<Check size={13} />{/if}
          </button>
        {/each}
      {/if}
    </PopoverMenu>
  </div>

  <!-- Status pill -->
  <div class="pill-anchor">
    <button
      type="button"
      class="pill"
      class:active={filters.status.length > 0 || openGroup === 'status'}
      onpointerdown={(event) => event.stopPropagation()}
      onclick={() => (openGroup = openGroup === 'status' ? null : 'status')}
    >
      <span>Status</span>
      {#if filters.status.length > 0}
        <span class="pill-count">{filters.status.length}</span>
      {/if}
    </button>

    <PopoverMenu open={openGroup === 'status'} minWidth={154} onClose={() => (openGroup = null)}>
      {#each statusOptions as opt (opt.value)}
        {@const checked = filters.status.includes(opt.value)}
        <button
          type="button"
          class="check-item"
          class:checked
          onclick={() => toggleStatus(opt.value)}
        >
          <span>{opt.label}</span>
          {#if checked}<Check size={13} />{/if}
        </button>
      {/each}
    </PopoverMenu>
  </div>

  <!-- Location pill -->
  <div class="pill-anchor">
    <button
      type="button"
      class="pill"
      class:active={filters.location.length > 0 || openGroup === 'location'}
      onpointerdown={(event) => event.stopPropagation()}
      onclick={() => (openGroup = openGroup === 'location' ? null : 'location')}
    >
      <span>Location</span>
      {#if filters.location.length > 0}
        <span class="pill-count">{filters.location.length}</span>
      {/if}
    </button>

    <PopoverMenu open={openGroup === 'location'} minWidth={148} onClose={() => (openGroup = null)}>
      {#each locationOptions as opt (opt.value)}
        {@const checked = filters.location.includes(opt.value)}
        <button
          type="button"
          class="check-item"
          class:checked
          onclick={() => toggleLocation(opt.value)}
        >
          <span>{opt.label}</span>
          {#if checked}<Check size={13} />{/if}
        </button>
      {/each}
    </PopoverMenu>
  </div>

  {#if anyActive}
    <button type="button" class="clear-pill" title="Clear filters" onclick={onClear}>
      <X size={11} />
    </button>
  {/if}
</div>

<style>
  .filter-pills {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 6px;
    padding: 4px 10px 8px;
    flex-shrink: 0;
  }

  .pill-anchor {
    position: relative;
  }

  .pill {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    height: 25px;
    padding: 0 10px;
    border-radius: 999px;
    border: 1px solid var(--color-border);
    background: var(--color-elevated);
    color: var(--color-text-2);
    font-size: 11.5px;
    font-weight: 500;
    cursor: pointer;
    transition: background 100ms ease, color 100ms ease, border-color 100ms ease;
  }

  .pill:hover {
    color: var(--color-text);
    background: var(--color-selected);
  }

  .pill.active {
    background: var(--color-good-bg);
    border-color: rgba(30, 215, 96, 0.36);
    color: var(--color-accent);
  }

  .pill-count {
    font-size: 10.5px;
    font-weight: 600;
  }

  .clear-pill {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 22px;
    height: 22px;
    border-radius: 999px;
    border: 1px solid var(--color-border);
    background: transparent;
    color: var(--color-text-3);
    cursor: pointer;
  }

  .clear-pill:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .check-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    width: 100%;
    height: 28px;
    padding: 0 9px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text-2);
    font-size: 12px;
    cursor: pointer;
    text-align: left;
  }

  .check-item:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .check-item.checked {
    color: var(--color-accent);
  }

  .empty-opt {
    padding: 6px 9px;
    font-size: 11.5px;
    color: var(--color-text-3);
  }
</style>
