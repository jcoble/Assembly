<script lang="ts">
  import { getContext } from 'svelte';
  import { Filter, Plus, Search } from '@lucide/svelte';
  import { connectionStore } from '$lib/application/connection.svelte';
  import { engineStore } from '$lib/application/engine.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { workspaceStore } from '$lib/application/workspace.svelte';
  import RightPanel from '$lib/features/shell/RightPanel.svelte';
  import TopBar from '$lib/features/shell/TopBar.svelte';
  import type { NativeSlotBridge } from '$lib/infrastructure/nativeBridge';

  const nativeBridge = getContext<NativeSlotBridge>('native-slot-bridge');
  let chatSlotEl = $state<HTMLDivElement | null>(null);

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
    <!-- Left Sessions Rail (1:1 structure: header + Provider/Status/Location pills + sessions list) -->
    {#if workspaceStore.railOpen}
      <aside class="rail" style:width={`${workspaceStore.railWidth}px`}>
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
    {/if}

    <!-- Center Workspace: Live GPUIKit Chat + Optional Side-by-Side Split Secondary Chrome Pane -->
    <main class="center-area">
      {#if workspaceStore.homeOverlay !== 'none'}
        <section class="overlay-surface">
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
            bind:this={chatSlotEl}
          ></div>
        {/if}

        {#if workspaceStore.hasSecondaryPane && workspaceStore.activeSecondaryTab}
          <section class="secondary-pane">
            <div class="secondary-placeholder">
              <h3>{workspaceStore.activeSecondaryTab.title}</h3>
              <p class="muted">
                Side-by-side split pane (`{workspaceStore.activeSecondaryTab.kind}`) — Wave 1 lane mounts here while native GPUIKit Chat stays live on the left.
              </p>
            </div>
          </section>
        {/if}
      {/if}
    </main>

    <!-- Right Panel (4 hanging core icons + `...` overflow + `×`) -->
    {#if workspaceStore.rightPanelOpen}
      <RightPanel />
    {/if}
  </div>

  <!-- Bottom Utility Strip (1:1 with old app footer) -->
  <footer class="status-strip">
    <div class="strip-left">
      <span class="status-dot" class:ok={connectionStore.status === 'connected'}></span>
      <span>
        {connectionStore.status === 'connected'
          ? `Connected :${connectionStore.snapshot.port}`
          : connectionStore.status}
      </span>
      <span class="muted">· Sessions {sessionsStore.list.length}</span>
    </div>
    <div class="strip-right muted">
      <span>Remote · Usage</span>
    </div>
  </footer>
</div>

<style>
  .workbench {
    display: flex;
    flex-direction: column;
    width: 100vw;
    height: 100vh;
    background: transparent;
    overflow: hidden;
  }

  .body-row {
    display: flex;
    flex: 1;
    min-height: 0;
    background: transparent;
  }

  .rail {
    display: flex;
    flex-direction: column;
    background: var(--color-rail);
    border-right: 1px solid var(--color-border);
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
    flex: 1;
    min-width: 280px;
    min-height: 0;
    background: transparent;
    pointer-events: none;
  }

  .native-chat-slot.split {
    flex: 0 0 42%;
    border-right: 1px solid var(--color-border);
  }

  .secondary-pane,
  .overlay-surface {
    flex: 1;
    min-width: 0;
    min-height: 0;
    background: var(--color-surface-solid);
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

  .status-strip {
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: 24px;
    padding: 0 10px;
    background: var(--color-bg);
    border-top: 1px solid var(--color-border);
    font-size: 11px;
    color: var(--color-text-2);
    flex-shrink: 0;
  }

  .strip-left,
  .strip-right {
    display: flex;
    align-items: center;
    gap: 6px;
  }

  .status-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--color-text-3);
  }

  .status-dot.ok {
    background: var(--color-good);
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
