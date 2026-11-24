<script lang="ts">
  import { getContext } from 'svelte';
  import { Filter, Plus, Search } from '@lucide/svelte';
  import { connectionStore } from '$lib/application/connection.svelte';
  import { engineStore } from '$lib/application/engine.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { workspaceStore } from '$lib/application/workspace.svelte';
  import RightPanel from '$lib/features/shell/RightPanel.svelte';
  import SplitSash from '$lib/features/shell/SplitSash.svelte';
  import StatusStrip from '$lib/features/shell/StatusStrip.svelte';
  import TopBar from '$lib/features/shell/TopBar.svelte';
  import type { NativeSlotBridge } from '$lib/infrastructure/nativeBridge';

  const nativeBridge = getContext<NativeSlotBridge>('native-slot-bridge');
  let chatSlotEl = $state<HTMLDivElement | null>(null);
  let centerAreaEl = $state<HTMLElement | null>(null);

  // Drag baselines captured on pointerdown for smooth zero-drift resizing.
  let dragStartRailWidth = 0;
  let dragStartRightWidth = 0;
  let dragStartChatRatio = 0.46;

  const chatVisible = $derived(
    workspaceStore.homeOverlay === 'none' && !workspaceStore.secondaryExpanded
  );

  // Lease the local Engine's `runs` and `needs_me` scopes for live Top Bar counters.
  $effect(() => {
    const machine = connectionStore.localEngineMachine;
    if (!machine) return;
    const stopRuns = engineStore.watchScope({ machine, topic: 'runs', key: null });
    const stopNeeds = engineStore.watchScope({ machine, topic: 'needs_me', key: null });
    return () => {
      stopNeeds();
      stopRuns();
    };
  });

  // Bind `#native-chat-slot` whenever Chat is visible (including side-by-side with split chrome tabs!).
  $effect(() => {
    if (chatVisible && chatSlotEl) {
      nativeBridge.setChatSlotElement(chatSlotEl);
    } else {
      nativeBridge.setChatSlotElement(null);
    }
  });
</script>

<div class="workbench">
  <TopBar />

  <div class="body-row">
    <!-- Left Sessions Rail Card (Pilot Lane A replaces inner body next) -->
    {#if workspaceStore.railOpen}
      <aside class="rail-card" style:width={`${workspaceStore.railWidth}px`}>
        <div class="rail-header">
          <span class="rail-title">Sessions</span>
          <div class="rail-actions">
            <button type="button" class="rail-btn" title="Search"><Search size={13} /></button>
            <button type="button" class="rail-btn" title="Filter"><Filter size={13} /></button>
            <button type="button" class="rail-btn" title="New session"><Plus size={14} /></button>
          </div>
        </div>

        <div class="filter-pills">
          <button type="button" class="filter-pill">Provider</button>
          <button type="button" class="filter-pill">Status</button>
          <button type="button" class="filter-pill">Location</button>
        </div>

        <div class="rail-list">
          {#if sessionsStore.list.length === 0}
            <div class="empty-hint">
              {connectionStore.status === 'connected'
                ? 'No sessions yet (Pilot Lane A mounts here)'
                : 'Connecting to .NET backend...'}
            </div>
          {:else}
            {#each sessionsStore.list as session (session.id)}
              <button
                type="button"
                class="session-row"
                class:selected={sessionsStore.selectedId === session.id}
                onclick={() => {
                  sessionsStore.select(session.id);
                  workspaceStore.openHomeOverlay('none');
                }}
              >
                <span class="session-title">{session.title || session.id}</span>
                <span class="session-meta">
                  {session.machine ?? 'This Mac'}{session.provider ? ` · ${session.provider}` : ''}
                </span>
              </button>
            {/each}
          {/if}
        </div>
      </aside>

      <SplitSash
        ariaLabel="Resize sessions sidebar"
        onDragStart={() => (dragStartRailWidth = workspaceStore.railWidth)}
        onDragDelta={(dx) => workspaceStore.setRailWidth(dragStartRailWidth + dx)}
      />
    {/if}

    <!-- Center Workspace: Live GPUIKit Chat Card + Optional Side-by-Side Split Secondary Card -->
    <main class="center-area" bind:this={centerAreaEl}>
      {#if workspaceStore.homeOverlay !== 'none'}
        <section class="overlay-card">
          <h2>{workspaceStore.homeOverlay}</h2>
          <p class="muted">Wave 1 lane mounts here (1:1 port of old app).</p>
          <button
            type="button"
            class="return-btn"
            onclick={() => workspaceStore.openHomeOverlay('none')}
          >
            Back to Workspace
          </button>
        </section>
      {:else}
        {#if chatVisible}
          <div
            id="native-chat-slot"
            class="native-chat-slot"
            class:split={workspaceStore.hasSecondaryPane}
            style:flex={workspaceStore.hasSecondaryPane
              ? `0 0 ${(workspaceStore.chatSplitRatio * 100).toFixed(2)}%`
              : '1 1 0%'}
            bind:this={chatSlotEl}
          ></div>
        {/if}

        {#if chatVisible && workspaceStore.hasSecondaryPane && workspaceStore.activeSecondaryTab}
          <SplitSash
            ariaLabel="Resize chat and secondary pane"
            onDragStart={() => (dragStartChatRatio = workspaceStore.chatSplitRatio)}
            onDragDelta={(dx) => {
              const width = centerAreaEl?.clientWidth ?? 0;
              if (width > 120) {
                workspaceStore.setChatSplitRatio(dragStartChatRatio + dx / width);
              }
            }}
          />
        {/if}

        {#if workspaceStore.hasSecondaryPane && workspaceStore.activeSecondaryTab}
          <section class="secondary-card">
            <div class="secondary-placeholder">
              <h3>{workspaceStore.activeSecondaryTab.title}</h3>
              <p class="muted">
                Side-by-side split pane (`{workspaceStore.activeSecondaryTab.kind}`) — Wave 1 lane
                mounts here while native GPUIKit Chat stays live on the left.
              </p>
            </div>
          </section>
        {/if}
      {/if}
    </main>

    <!-- Right Panel Card (4 hanging core icons + `...` overflow + `×`) -->
    {#if workspaceStore.rightPanelOpen}
      <SplitSash
        ariaLabel="Resize right panel"
        onDragStart={() => (dragStartRightWidth = workspaceStore.rightPanelWidth)}
        onDragDelta={(dx) => workspaceStore.setRightPanelWidth(dragStartRightWidth - dx)}
      />

      <div class="right-column" style:width={`${workspaceStore.rightPanelWidth}px`}>
        <RightPanel />
      </div>
    {/if}
  </div>

  <!-- Exact 1:1 Bottom Utility Strip (Settings · Resources · Machine · Usage) -->
  <StatusStrip />
</div>

<style>
  .workbench {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100%;
    background: transparent;
    overflow: hidden;
  }

  .body-row {
    display: flex;
    flex: 1;
    min-height: 0;
    padding: 0 6px;
    background: transparent;
  }

  .rail-card {
    display: flex;
    flex-direction: column;
    background: var(--color-rail);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    overflow: hidden;
    flex-shrink: 0;
    min-height: 0;
  }

  .rail-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 10px 10px 6px 12px;
  }

  .rail-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--color-text);
  }

  .rail-actions {
    display: flex;
    align-items: center;
    gap: 2px;
  }

  .rail-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    border: none;
    border-radius: 6px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
  }

  .rail-btn:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .filter-pills {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 10px 8px;
  }

  .filter-pill {
    height: 25px;
    padding: 0 10px;
    border-radius: 999px;
    border: 1px solid var(--color-border);
    background: var(--color-elevated);
    color: var(--color-text-2);
    font-size: 11.5px;
    cursor: pointer;
  }

  .filter-pill:hover {
    color: var(--color-text);
  }

  .rail-list {
    flex: 1;
    overflow-y: auto;
    padding: 4px 6px;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .session-row {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 2px;
    width: 100%;
    padding: 6px 10px;
    border: 1px solid transparent;
    border-left: 2px solid transparent;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text);
    text-align: left;
    cursor: pointer;
  }

  .session-row:hover {
    background: var(--color-hover);
  }

  .session-row.selected {
    background: var(--color-selected);
    border-left-color: var(--color-accent);
  }

  .session-title {
    font-size: 12.5px;
    font-weight: 500;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .session-meta {
    font-size: 11px;
    color: var(--color-text-3);
  }

  .center-area {
    display: flex;
    flex: 1;
    min-width: 0;
    min-height: 0;
    background: transparent;
  }

  .native-chat-slot {
    min-width: 280px;
    min-height: 0;
    background: transparent;
    border-radius: var(--radius-md);
    border: 1px solid var(--color-border);
    pointer-events: none;
  }

  .secondary-card,
  .overlay-card {
    flex: 1;
    min-width: 0;
    min-height: 0;
    background: var(--color-surface-solid);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    overflow: hidden;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 20px;
    text-align: center;
  }

  .secondary-placeholder {
    max-width: 420px;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .right-column {
    flex-shrink: 0;
    min-height: 0;
    height: 100%;
  }

  .return-btn {
    height: 30px;
    padding: 0 12px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-field-border);
    background: var(--color-selected);
    color: var(--color-text);
    font-size: 12px;
    cursor: pointer;
  }

  .empty-hint {
    padding: 12px 8px;
    color: var(--color-text-3);
    font-size: 12px;
  }

  .muted {
    color: var(--color-text-3);
    font-size: 11.5px;
  }
</style>
