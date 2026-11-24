<script lang="ts">
  /**
   * Top header of the Left Sessions Rail:
   * `Sessions` title + Search toggle + View Options glass popover (Group by / Sort / Direction) + `+` New Session.
   */
  import { Check, ListFilter, Plus, Search, X } from '@lucide/svelte';
  import PopoverMenu from '$lib/ui/PopoverMenu.svelte';
  import { naturalDirForSort, sortDirectionLabel } from './format';
  import type { RailDir, RailSort, RailView } from './types';

  interface Props {
    view: RailView;
    filterText: string;
    searchOpen: boolean;
    onToggleSearch: () => void;
    onFilterChange: (text: string) => void;
    onCloseSearch: () => void;
    onViewChange: (next: RailView) => void;
    onNewSession: () => void;
  }

  let {
    view,
    filterText,
    searchOpen,
    onToggleSearch,
    onFilterChange,
    onCloseSearch,
    onViewChange,
    onNewSession
  }: Props = $props();

  let optionsOpen = $state(false);
  let searchInputEl = $state<HTMLInputElement | null>(null);

  $effect(() => {
    if (searchOpen && searchInputEl) {
      searchInputEl.focus();
    }
  });

  const groupNone = $derived(!view.groupBy.status && !view.groupBy.project);
  const directionChoices = $derived<{ dir: RailDir; label: string }[]>(
    view.sort === 'name'
      ? [
          { dir: 'asc', label: 'A to Z' },
          { dir: 'desc', label: 'Z to A' }
        ]
      : [
          { dir: 'desc', label: 'Newest first' },
          { dir: 'asc', label: 'Oldest first' }
        ]
  );

  function setSort(sort: RailSort): void {
    onViewChange({
      ...view,
      sort,
      dir: naturalDirForSort(sort)
    });
  }
</script>

<div class="rail-header">
  <span class="rail-title">Sessions</span>

  <div class="rail-actions">
    <button
      type="button"
      class="icon-btn"
      class:active={searchOpen || filterText.length > 0}
      title="Search sessions"
      aria-label="Search sessions"
      onclick={onToggleSearch}
    >
      <Search size={14} />
    </button>

    <div class="menu-anchor">
      <button
        type="button"
        class="icon-btn"
        class:active={optionsOpen}
        title="View options"
        aria-label="View options"
        onpointerdown={(event) => event.stopPropagation()}
        onclick={() => (optionsOpen = !optionsOpen)}
      >
        <ListFilter size={14} />
      </button>

      <PopoverMenu open={optionsOpen} align="right" minWidth={236} onClose={() => (optionsOpen = false)}>
        <div class="opt-section">
          <div class="opt-label">Group by</div>
          <div class="seg-row">
            <button
              type="button"
              class="seg-btn"
              class:selected={groupNone}
              onclick={() =>
                onViewChange({ ...view, groupBy: { status: false, project: false } })}
            >
              {#if groupNone}<Check size={12} />{/if}
              <span>None</span>
            </button>
            <button
              type="button"
              class="seg-btn"
              class:selected={view.groupBy.status}
              onclick={() =>
                onViewChange({
                  ...view,
                  groupBy: { ...view.groupBy, status: !view.groupBy.status }
                })}
            >
              {#if view.groupBy.status}<Check size={12} />{/if}
              <span>Status</span>
            </button>
            <button
              type="button"
              class="seg-btn"
              class:selected={view.groupBy.project}
              onclick={() =>
                onViewChange({
                  ...view,
                  groupBy: { ...view.groupBy, project: !view.groupBy.project }
                })}
            >
              {#if view.groupBy.project}<Check size={12} />{/if}
              <span>Project</span>
            </button>
          </div>
        </div>

        <div class="opt-divider"></div>

        <div class="opt-section">
          <div class="opt-label">Sort</div>
          <button
            type="button"
            class="opt-row"
            class:selected={view.sort === 'recent'}
            onclick={() => setSort('recent')}
          >
            <span>Recent activity</span>
            {#if view.sort === 'recent'}<Check size={13} />{/if}
          </button>
          <button
            type="button"
            class="opt-row"
            class:selected={view.sort === 'name'}
            onclick={() => setSort('name')}
          >
            <span>Name</span>
            {#if view.sort === 'name'}<Check size={13} />{/if}
          </button>
        </div>

        <div class="opt-divider"></div>

        <div class="opt-section">
          <div class="opt-label">Direction · {sortDirectionLabel(view.sort, view.dir)}</div>
          <div class="seg-row">
            {#each directionChoices as choice (choice.dir)}
              <button
                type="button"
                class="seg-btn"
                class:selected={view.dir === choice.dir}
                onclick={() => onViewChange({ ...view, dir: choice.dir })}
              >
                {#if view.dir === choice.dir}<Check size={12} />{/if}
                <span>{choice.label}</span>
              </button>
            {/each}
          </div>
        </div>
      </PopoverMenu>
    </div>

    <button
      type="button"
      class="icon-btn"
      title="New session"
      aria-label="New session"
      onclick={onNewSession}
    >
      <Plus size={15} />
    </button>
  </div>
</div>

{#if searchOpen}
  <div class="search-strip">
    <Search size={13} class="search-glyph" />
    <input
      bind:this={searchInputEl}
      type="text"
      class="search-input"
      placeholder="Search sessions or projects..."
      value={filterText}
      oninput={(event) => onFilterChange((event.currentTarget as HTMLInputElement).value)}
      onkeydown={(event) => {
        if (event.key === 'Escape') {
          event.stopPropagation();
          if (filterText) {
            onFilterChange('');
          } else {
            onCloseSearch();
          }
        }
      }}
    />
    <button
      type="button"
      class="clear-btn"
      title={filterText ? 'Clear search' : 'Close search'}
      onclick={() => (filterText ? onFilterChange('') : onCloseSearch())}
    >
      <X size={12} />
    </button>
  </div>
{/if}

<style>
  .rail-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 10px 10px 6px 12px;
    flex-shrink: 0;
  }

  .rail-title {
    font-size: 15px;
    font-weight: 700;
    letter-spacing: -0.01em;
    color: var(--color-text);
  }

  .rail-actions {
    display: flex;
    align-items: center;
    gap: 2px;
  }

  .menu-anchor {
    position: relative;
  }

  .icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    border: none;
    border-radius: 6px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
  }

  .icon-btn:hover,
  .icon-btn.active {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .search-strip {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: 2px 10px 6px;
    padding: 0 8px;
    height: 28px;
    border-radius: var(--radius-sm);
    background: var(--color-elevated);
    border: 1px solid var(--color-field-border);
    color: var(--color-text-3);
    flex-shrink: 0;
  }

  .search-strip:focus-within {
    border-color: var(--color-focus);
  }

  .search-input {
    flex: 1;
    min-width: 0;
    border: none;
    background: transparent;
    color: var(--color-text);
    font-size: 12px;
    outline: none;
  }

  .search-input::placeholder {
    color: var(--color-text-3);
  }

  .clear-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 18px;
    height: 18px;
    border: none;
    border-radius: 4px;
    background: transparent;
    color: var(--color-text-3);
    cursor: pointer;
  }

  .clear-btn:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .opt-section {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 3px 2px;
  }

  .opt-label {
    padding: 2px 6px;
    font-size: 10.5px;
    font-weight: 600;
    letter-spacing: 0.03em;
    text-transform: uppercase;
    color: var(--color-text-3);
  }

  .opt-divider {
    height: 1px;
    margin: 4px 2px;
    background: var(--color-border);
  }

  .seg-row {
    display: flex;
    gap: 4px;
    padding: 0 2px;
  }

  .seg-btn {
    flex: 1;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 4px;
    height: 26px;
    padding: 0 8px;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: rgba(255, 255, 255, 0.04);
    color: var(--color-text-2);
    font-size: 11.5px;
    cursor: pointer;
    white-space: nowrap;
  }

  .seg-btn:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .seg-btn.selected {
    background: var(--color-good-bg);
    border-color: rgba(30, 215, 96, 0.35);
    color: var(--color-accent);
  }

  .opt-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    width: 100%;
    height: 28px;
    padding: 0 8px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text-2);
    font-size: 12px;
    cursor: pointer;
    text-align: left;
  }

  .opt-row:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .opt-row.selected {
    color: var(--color-accent);
  }
</style>
