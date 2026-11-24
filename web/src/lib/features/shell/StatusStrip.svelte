<script lang="ts">
  /**
   * Bottom utility bar matching the old SvelteKit app's `UtilityStrip.svelte` 1:1:
   * Settings gear · Resources CPU & live stats · Machine location dot · Usage button.
   */
  import { Cpu, Settings, X } from '@lucide/svelte';
  import { connectionStore } from '$lib/application/connection.svelte';
  import { engineStore } from '$lib/application/engine.svelte';
  import { machinesStore } from '$lib/application/machines.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { workspaceStore } from '$lib/application/workspace.svelte';

  const activeLocation = $derived.by(() => {
    const selected = sessionsStore.selected;
    if (selected?.machine) {
      const machine = machinesStore.list.find((m) => m.name === selected.machine);
      return machine?.name ? `Remote · ${machine.name}` : `Remote · ${selected.machine}`;
    }
    return 'Local Mac';
  });

  const isConnected = $derived(connectionStore.status === 'connected');
  const isRemote = $derived(Boolean(sessionsStore.selected?.machine));
</script>

<footer class="utility-strip" aria-label="Settings, resources and usage">
  <button
    type="button"
    class="utility settings"
    class:open={workspaceStore.homeOverlay === 'settings'}
    aria-label="Settings"
    title="Settings"
    onclick={() =>
      workspaceStore.openHomeOverlay(
        workspaceStore.homeOverlay === 'settings' ? 'none' : 'settings'
      )}
  >
    <Settings size={13} strokeWidth={1.6} aria-hidden="true" />
  </button>

  <button
    type="button"
    class="utility resources"
    class:open={workspaceStore.openUtility === 'resources'}
    aria-expanded={workspaceStore.openUtility === 'resources'}
    onclick={() => workspaceStore.toggleUtility('resources')}
  >
    <Cpu size={13} strokeWidth={1.6} aria-hidden="true" />
    <span class="values">
      <span class="value">
        Backend <b>{isConnected ? `:${connectionStore.snapshot.port}` : connectionStore.status}</b>
      </span>
      <span class="sep" aria-hidden="true">·</span>
      <span class="value">Sessions <b>{sessionsStore.list.length}</b></span>
      <span class="sep" aria-hidden="true">·</span>
      <span class="value">Machines <b>{machinesStore.list.length}</b></span>
      {#if engineStore.needCount > 0}
        <span class="sep" aria-hidden="true">·</span>
        <span class="value">Needs <b>{engineStore.needCount}</b></span>
      {/if}
    </span>
  </button>

  <span class="location" title={isRemote ? 'Remote session machine' : 'Running on this Mac'}>
    <span class="location-dot" class:ok={isConnected} aria-hidden="true"></span>
    <span>{activeLocation}</span>
  </span>

  <button
    type="button"
    class="utility usage"
    class:open={workspaceStore.openUtility === 'usage'}
    aria-expanded={workspaceStore.openUtility === 'usage'}
    onclick={() => workspaceStore.toggleUtility('usage')}
  >
    <span class="label">Usage</span>
  </button>

  {#if workspaceStore.openUtility === 'resources'}
    <div class="utility-popover popover-left glass-popover" role="dialog" data-occludes-chat>
      <div class="popover-head">
        <span>Resource Manager</span>
        <button type="button" class="close-btn" onclick={() => workspaceStore.closeUtility()}>
          <X size={13} />
        </button>
      </div>
      <div class="popover-body">
        <div class="stat-row">
          <span>Socket status</span>
          <strong>{connectionStore.status}</strong>
        </div>
        <div class="stat-row">
          <span>Backend port</span>
          <strong>{connectionStore.snapshot.port ?? '—'}</strong>
        </div>
        <div class="stat-row">
          <span>Loaded sessions</span>
          <strong>{sessionsStore.list.length}</strong>
        </div>
        <div class="stat-row">
          <span>Connected machines</span>
          <strong>{machinesStore.list.length}</strong>
        </div>
        <p class="popover-note">
          Live CPU / physical memory / RSS telemetry wires in Wave 1 Resources & Settings lane.
        </p>
      </div>
    </div>
  {:else if workspaceStore.openUtility === 'usage'}
    <div class="utility-popover popover-right glass-popover" role="dialog" data-occludes-chat>
      <div class="popover-head">
        <span>Usage &amp; Limits</span>
        <button type="button" class="close-btn" onclick={() => workspaceStore.closeUtility()}>
          <X size={13} />
        </button>
      </div>
      <div class="popover-body">
        <p class="popover-note">
          Provider usage &amp; subscription limits card wires in Wave 1 Settings &amp; Usage lane.
        </p>
      </div>
    </div>
  {/if}
</footer>

<style>
  .utility-strip {
    position: relative;
    display: flex;
    flex: 0 0 28px;
    width: 100%;
    min-width: 0;
    height: 28px;
    align-items: center;
    gap: 6px;
    padding: 0 10px;
    background: var(--color-canvas);
    color: var(--color-text-3);
    font-size: 11.5px;
    user-select: none;
  }

  .utility {
    display: inline-flex;
    min-width: 0;
    height: 20px;
    align-items: center;
    gap: 6px;
    padding: 0 7px;
    border: 0;
    border-radius: 999px;
    background: transparent;
    color: inherit;
    font: inherit;
    line-height: 1;
    cursor: pointer;
  }

  .settings {
    flex: 0 0 auto;
    width: 22px;
    padding: 0;
    justify-content: center;
  }

  .resources {
    flex: 0 1 auto;
    margin-right: auto;
    overflow: hidden;
  }

  .usage {
    flex: 0 0 auto;
  }

  .utility:hover,
  .utility.open {
    background: var(--color-elevated);
    color: var(--color-text);
  }

  .values {
    display: flex;
    min-width: 0;
    align-items: center;
    gap: 7px;
    overflow: hidden;
    white-space: nowrap;
  }

  .value {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
  }

  .value b {
    color: var(--color-text-2);
    font-weight: 500;
  }

  .sep {
    flex: 0 0 auto;
    opacity: 0.45;
  }

  .location {
    display: inline-flex;
    flex: 0 0 auto;
    align-items: center;
    gap: 6px;
    color: var(--color-text-2);
    white-space: nowrap;
    padding: 0 4px;
  }

  .location-dot {
    width: 6px;
    height: 6px;
    border-radius: 999px;
    background: var(--color-text-3);
  }

  .location-dot.ok {
    background: var(--color-accent);
  }

  .utility-popover {
    position: absolute;
    bottom: calc(100% + 6px);
    width: 280px;
    border-radius: var(--radius-md);
    padding: 10px 12px;
    z-index: 80;
    color: var(--color-text);
  }

  .popover-left {
    left: 34px;
  }

  .popover-right {
    right: 10px;
  }

  .popover-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    font-size: 12px;
    font-weight: 600;
    margin-bottom: 8px;
  }

  .close-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 20px;
    height: 20px;
    border: none;
    border-radius: 5px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
  }

  .close-btn:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .popover-body {
    display: flex;
    flex-direction: column;
    gap: 5px;
  }

  .stat-row {
    display: flex;
    justify-content: space-between;
    font-size: 11.5px;
    color: var(--color-text-2);
  }

  .stat-row strong {
    color: var(--color-text);
    font-weight: 500;
  }

  .popover-note {
    margin: 6px 0 0;
    font-size: 11px;
    color: var(--color-text-3);
    line-height: 1.4;
  }
</style>
