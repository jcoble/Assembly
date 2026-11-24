<script lang="ts">
  /**
   * Left Sessions Rail — 1:1 port of the old SvelteKit app's rail backed by `.NET` `sessions/rail`.
   * Renders SQL-ordered sections, project groups, nested child rows, filters, view options,
   * infinite pagination, right-click context menu, and local+remote session switching.
   */
  import { ChevronDown, ChevronRight, X } from '@lucide/svelte';
  import { connectionStore } from '$lib/application/connection.svelte';
  import { engineStore } from '$lib/application/engine.svelte';
  import { machinesStore } from '$lib/application/machines.svelte';
  import { preferencesStore } from '$lib/application/preferences.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { workspaceStore } from '$lib/application/workspace.svelte';
  import { openNativeNewSession } from '$lib/infrastructure/nativeBridge';
  import { projectInitials, projectLabel } from './format';
  import RailFilters from './RailFilters.svelte';
  import RailHeader from './RailHeader.svelte';
  import SessionContextMenu from './SessionContextMenu.svelte';
  import SessionRow from './SessionRow.svelte';
  import { RailController } from './store.svelte';
  import type { RailEntry } from './types';

  const rail = new RailController();

  let searchOpen = $state(false);
  let contextMenu = $state<{ entry: RailEntry; x: number; y: number } | null>(null);

  $effect(() => rail.start());

  // Restore persisted `rail.filters` + `rail.view` as soon as backend preferences arrive.
  $effect(() => {
    if (preferencesStore.loaded) {
      rail.syncSavedPreferences();
    }
  });

  // Re-query rail whenever live Engine state advances (e.g. `needs_me` / `runs`).
  $effect(() => {
    const rev = engineStore.revision;
    if (rev > 0) {
      rail.notifyExternalChange();
    }
  });

  const offlineMachines = $derived.by(() => {
    const set = new Set<string>();
    for (const m of machinesStore.list) {
      if (m.local) continue;
      if (m.connected === false || m.status === 'offline' || m.status === 'disconnected') {
        set.add(m.name);
      }
    }
    return set;
  });

  function isEntryOffline(entry: RailEntry): boolean {
    return Boolean(entry.machine && offlineMachines.has(entry.machine));
  }

  function handleSelect(entry: RailEntry): void {
    sessionsStore.select(entry.sessionId, entry.machine ?? null, entry.title ?? entry.group ?? null);
    workspaceStore.openHomeOverlay('none');
    if (searchOpen && rail.filterText) {
      rail.clearFilterText();
      searchOpen = false;
    }
  }

  function handleNewSession(): void {
    workspaceStore.openHomeOverlay('none');
    if (workspaceStore.secondaryExpanded) {
      workspaceStore.toggleSecondaryExpanded();
    }
    openNativeNewSession();
  }

  function handleScroll(event: Event): void {
    const el = event.currentTarget as HTMLElement;
    const remaining = el.scrollHeight - el.scrollTop - el.clientHeight;
    if (remaining < 180 && rail.hasMore && !rail.loadingMore) {
      rail.loadMore();
    }
  }
</script>

<div class="sessions-rail">
  <RailHeader
    view={rail.view}
    filterText={rail.filterText}
    {searchOpen}
    onToggleSearch={() => {
      if (searchOpen && !rail.filterText) {
        searchOpen = false;
      } else {
        searchOpen = true;
      }
    }}
    onFilterChange={(text) => rail.setFilterText(text)}
    onCloseSearch={() => {
      rail.clearFilterText();
      searchOpen = false;
    }}
    onViewChange={(view) => rail.setView(view)}
    onNewSession={handleNewSession}
  />

  <RailFilters
    agents={rail.agents}
    filters={rail.filters}
    onChange={(filters) => rail.setFilters(filters)}
    onClear={() => rail.clearFilters()}
  />

  {#if rail.error || rail.rowError}
    <div class="rail-error" role="alert">
      <span class="error-text">{rail.rowError ?? rail.error}</span>
      {#if rail.rowError}
        <button type="button" class="dismiss-btn" onclick={() => rail.clearRowError()}>
          <X size={12} />
        </button>
      {/if}
    </div>
  {/if}

  <div class="rail-scroll" onscroll={handleScroll}>
    {#if !rail.loaded && connectionStore.status !== 'connected'}
      <div class="empty-state">Connecting to .NET backend...</div>
    {:else if !rail.loaded && rail.loading}
      <div class="empty-state">Loading sessions...</div>
    {:else if rail.listItems.length === 0}
      <div class="empty-state">
        {rail.filterText ? 'No matching sessions.' : 'No sessions yet.'}
      </div>
    {:else}
      {#each rail.listItems as item (item.key)}
        {#if item.kind === 'section'}
          <button
            type="button"
            class="section-header"
            class:needs={item.foldKey === 'needs'}
            onclick={() => rail.toggleFold(item.foldKey, item.key)}
          >
            {#if item.folded}
              <ChevronRight size={12} />
            {:else}
              <ChevronDown size={12} />
            {/if}
            <span class="section-label">{item.label}</span>
            <span class="header-count">{item.count}</span>
          </button>
        {:else if item.kind === 'project'}
          {@const initials = projectInitials(item.label)}
          <button
            type="button"
            class="project-header"
            onclick={() => rail.toggleFold(item.foldKey, item.key)}
          >
            {#if item.folded}
              <ChevronRight size={12} />
            {:else}
              <ChevronDown size={12} />
            {/if}
            {#if initials}
              <span class="proj-initials">{initials}</span>
            {/if}
            <span class="project-label">{projectLabel(item.label)}</span>
            {#if item.machines.length > 0}
              <span class="machine-chip">{item.machines[0]}</span>
            {/if}
            <span class="header-count">{item.count}</span>
          </button>
        {:else}
          <SessionRow
            entry={item.entry}
            selected={sessionsStore.selectedId === item.entry.sessionId}
            offline={isEntryOffline(item.entry)}
            onSelect={handleSelect}
            onContextMenu={(entry, event) =>
              (contextMenu = { entry, x: event.clientX, y: event.clientY })}
          />
        {/if}
      {/each}

      {#if rail.hasMore}
        <button
          type="button"
          class="load-more"
          disabled={rail.loadingMore}
          onclick={() => rail.loadMore()}
        >
          {rail.loadingMore ? 'Loading more...' : 'Load older sessions'}
        </button>
      {/if}
    {/if}
  </div>
</div>

{#if contextMenu}
  <SessionContextMenu
    entry={contextMenu.entry}
    x={contextMenu.x}
    y={contextMenu.y}
    onMark={(sessionId, state) => void rail.mark(sessionId, state)}
    onClose={() => (contextMenu = null)}
  />
{/if}

<style>
  .sessions-rail {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    user-select: none;
  }

  .rail-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
    margin: 0 10px 6px;
    padding: 6px 8px;
    border-radius: var(--radius-sm);
    background: var(--color-bad-bg);
    border: 1px solid rgba(255, 109, 145, 0.32);
    color: var(--color-bad);
    font-size: 11.5px;
    flex-shrink: 0;
  }

  .error-text {
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .dismiss-btn {
    border: none;
    background: transparent;
    color: inherit;
    cursor: pointer;
    padding: 2px;
    display: inline-flex;
  }

  .rail-scroll {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 2px 6px 10px;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .section-header {
    display: flex;
    align-items: center;
    gap: 5px;
    width: 100%;
    padding: 7px 6px 4px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text-3);
    font-size: 10.5px;
    font-weight: 700;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    cursor: pointer;
    text-align: left;
  }

  .section-header:hover {
    color: var(--color-text-2);
  }

  .section-header.needs {
    color: var(--color-attention);
  }

  .section-label {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .project-header {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    padding: 7px 6px 4px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text-2);
    font-size: 12px;
    font-weight: 600;
    cursor: pointer;
    text-align: left;
  }

  .project-header:hover {
    color: var(--color-text);
  }

  .proj-initials {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 10px;
    font-weight: 700;
    color: var(--color-text-3);
    letter-spacing: 0.02em;
    flex-shrink: 0;
  }

  .project-label {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .machine-chip {
    flex-shrink: 0;
    padding: 1px 6px;
    border-radius: 5px;
    background: rgba(255, 255, 255, 0.07);
    color: var(--color-text-2);
    font-size: 10px;
    font-weight: 500;
  }

  .header-count {
    margin-left: auto;
    flex-shrink: 0;
    font-size: 11px;
    font-weight: 500;
    color: var(--color-text-3);
    font-variant-numeric: tabular-nums;
  }

  .empty-state {
    padding: 16px 10px;
    color: var(--color-text-3);
    font-size: 12px;
  }

  .load-more {
    margin: 8px 6px 4px;
    height: 28px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-border);
    background: var(--color-elevated);
    color: var(--color-text-2);
    font-size: 11.5px;
    cursor: pointer;
  }

  .load-more:hover:not(:disabled) {
    background: var(--color-selected);
    color: var(--color-text);
  }

  .load-more:disabled {
    opacity: 0.6;
    cursor: default;
  }
</style>
