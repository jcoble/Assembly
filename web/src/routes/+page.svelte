<script lang="ts">
  import { getContext } from 'svelte';
  import { connectionStore } from '$lib/application/connection.svelte';
  import { engineStore } from '$lib/application/engine.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { workspaceStore } from '$lib/application/workspace.svelte';
  import SessionsRail from '$lib/features/rail/SessionsRail.svelte';
  import RunPanel from '$lib/features/run/RunPanel.svelte';
  import { formatPreviewTabTitle } from '$lib/features/run/model';
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

  const activeProjectRoot = $derived(
    sessionsStore.selected?.root?.trim() ||
      sessionsStore.list.find((session) => !session.machine && session.root)?.root?.trim() ||
      ''
  );

  const activeProjectName = $derived(sessionsStore.selected?.project?.trim() ?? '');

  function openRunPreview(url: string): void {
    const trimmed = url.trim();
    if (!trimmed) return;
    if (!/^https?:\/\//i.test(trimmed)) return;
    workspaceStore.openSecondaryTab('browser', formatPreviewTabTitle(trimmed), trimmed);
  }

  // Lease the local Engine's `runs` and `needs_me` scopes for live Top Bar & Rail counters.
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
    <!-- Left Sessions Rail Card (SQL-backed via `.NET` `sessions/rail` + `sessionRail`) -->
    {#if workspaceStore.railOpen}
      <aside class="rail-card" style:width={`${workspaceStore.railWidth}px`}>
        <SessionsRail />
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
        <RightPanel>
          {#snippet run()}
            <RunPanel
              visible={workspaceStore.rightPanelOpen && workspaceStore.activeRightTab === 'run'}
              root={activeProjectRoot}
              projectName={activeProjectName}
              onOpenPreview={openRunPreview}
              onOpenTerminal={() => workspaceStore.selectRightTab('terminal')}
            />
          {/snippet}
        </RightPanel>
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
    border-radius: var(--radius-md);
    overflow: hidden;
    flex-shrink: 0;
    min-height: 0;
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
    pointer-events: none;
  }

  .secondary-card,
  .overlay-card {
    flex: 1;
    min-width: 0;
    min-height: 0;
    background: var(--color-surface-solid);
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

  .muted {
    color: var(--color-text-3);
    font-size: 11.5px;
  }
</style>
