<script lang="ts">
  import { onMount } from 'svelte';
  import { RefreshCw } from '@lucide/svelte';
  import { refreshCurrentUsage, refreshUsageHistory, usageState } from './usageStore.svelte.ts';
  import {
    readUsageDisplayMode,
    usageDisplayLabel,
    usageDisplayPercent,
    usageProviderLabel,
    usageQuotaWindowLabel,
    usageResetLabel,
    writeUsageDisplayMode,
    type UsageDisplayMode
  } from './usageCurrent.ts';
  import UsageWorkspace from './UsageWorkspace.svelte';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import type { ProviderUsageSnapshot } from './usageTypes.ts';

  interface Props { provider?: string | null; instanceId?: string | null; }
  let { provider = null, instanceId = null }: Props = $props();
  // Only Codex/Claude have usage readers: usage_sources.rs::discover_local_usage_sources and usage_current.rs::read_current_provider_usage.
  const providerNames = ['codex', 'claude'] as const;
  let open = $state(false);
  let fullOpen = $state(false);
  let triggerButton = $state<HTMLButtonElement>();
  let modalSurface = $state<HTMLDivElement>();
  let menuSnapshots = $state<Record<string, ProviderUsageSnapshot>>({});
  let menuLoading = $state(false);
  /** Read the quotas as how much has gone, or as how much is left. */
  let displayMode = $state<UsageDisplayMode>('used');
  let displayModeVersion = 0;

  onMount(() => {
    const owner = { active: true };
    const restoreVersion = displayModeVersion;
    void restoreDisplayMode(owner, restoreVersion);
    return () => {
      owner.active = false;
    };
  });

  async function restoreDisplayMode(owner: { active: boolean }, restoreVersion: number): Promise<void> {
    try {
      const stored = await readUsageDisplayMode();
      if (owner.active && displayModeVersion === restoreVersion) displayMode = stored;
    } catch (_error) {
      // Keep the default display mode when persisted preference is unavailable.
    }
  }

  function toggleDisplayMode(): void {
    displayModeVersion += 1;
    displayMode = displayMode === 'used' ? 'remaining' : 'used';
    void writeUsageDisplayMode(displayMode);
  }

  function snapshotFor(providerName: string): ProviderUsageSnapshot | null {
    return menuSnapshots[providerName] ?? null;
  }

  function captureCurrentUsage(): void {
    menuSnapshots = Object.fromEntries(
      Object.entries(usageState.currentByProvider).map(([name, snapshot]) => [
        name,
        { ...snapshot, windows: snapshot.windows.map((window) => ({ ...window })) }
      ])
    );
  }

  async function refreshMenu(): Promise<void> {
    menuLoading = true;
    const forProvider = provider;
    const forInstance = instanceId;
    try {
      await refreshCurrentUsage(forProvider, forInstance);
      if (provider === forProvider && instanceId === forInstance) captureCurrentUsage();
    } catch (_error) {
      // The usage store owns unavailable-state text; the popover only mirrors it.
    } finally {
      if (provider === forProvider && instanceId === forInstance) menuLoading = false;
    }
  }

  async function refreshUsageWorkspace(providerName: string | null, instance: string | null): Promise<void> {
    try {
      await Promise.all([
        refreshCurrentUsage(providerName, instance),
        refreshUsageHistory()
      ]);
    } catch (_error) {
      // Manual refresh can fail without closing the stats surface.
    }
  }

  function unavailableMessage(snapshot: ProviderUsageSnapshot | null): string {
    return snapshot?.unavailableReason ?? (snapshot?.provider === 'claude'
      ? 'Live Claude quota is unavailable.'
      : 'Live quota is unavailable locally.');
  }

  function toggleOpen(): void {
    open = !open;
    if (open) {
      captureCurrentUsage();
      void refreshMenu();
    }
  }

  async function refresh(): Promise<void> {
    await refreshMenu();
  }

  async function openStats(): Promise<void> {
    open = false;
    fullOpen = true;
    void refreshUsageWorkspace(provider, instanceId);
    modalSurface?.focus();
  }

  function closeStats(): void {
    fullOpen = false;
    triggerButton?.focus();
  }

  function handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && fullOpen) {
      event.preventDefault();
      closeStats();
    }
  }

  function handleBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) closeStats();
  }
</script>

<svelte:window onkeydown={handleKeydown} />

<aside class="usage-popover" data-testid="usage-popover" aria-label="Provider usage">
  <button bind:this={triggerButton} type="button" class="trigger" aria-expanded={open} onclick={toggleOpen}>Usage</button>
  {#if open}
    <section class="card" data-testid="usage-live-quota">
      <header class="card-header">
        <div>
          <p class="eyebrow">Live quota</p>
          <h2>Usage</h2>
        </div>
        <button class="refresh-button" type="button" onclick={() => void refresh()} disabled={menuLoading}>
          {#if menuLoading}<WorkingSpinner size={13} />Refreshing…{:else}<RefreshCw size={13} aria-hidden="true" />Refresh{/if}
        </button>
      </header>

      <div class="provider-list">
        {#each providerNames as providerName (providerName)}
          {@const snapshot = snapshotFor(providerName)}
          <article class="provider-card">
            <div class="provider-heading">
              <div>
                <strong>{usageProviderLabel(providerName)}</strong>
                <small>{snapshot?.account ?? 'Local account'}</small>
              </div>
              <span class:available={snapshot?.state === 'available'}>
                {snapshot?.state === 'available' ? 'Live' : 'Not available'}
              </span>
            </div>
            {#if snapshot?.state === 'available' && snapshot.windows.length > 0}
              <div class="quota-list">
                {#each snapshot.windows as window, index (`${window.label}-${window.windowMinutes ?? index}`)}
                  <div class="quota-row">
                    <div class="quota-copy">
                      <span>{usageQuotaWindowLabel(window)}</span>
                      <small>{usageResetLabel(window.resetsAt)}</small>
                    </div>
                    <button
                      type="button"
                      class="quota-figure"
                      data-testid="usage-display-toggle"
                      title={displayMode === 'used' ? 'Show how much is left' : 'Show how much has been used'}
                      onclick={toggleDisplayMode}
                    >
                      {usageDisplayLabel(window, displayMode)}
                    </button>
                    <div class="quota-bar" aria-label={`${usageQuotaWindowLabel(window)}, ${usageDisplayLabel(window, displayMode)}`}>
                      <i style={`width: ${usageDisplayPercent(window, displayMode)}%`}></i>
                    </div>
                  </div>
                {/each}
              </div>
            {:else}
              <p class="unavailable">{unavailableMessage(snapshot)}</p>
            {/if}
          </article>
        {/each}
      </div>

      <button type="button" class="details" onclick={() => void openStats()}>Open Stats &amp; Usage</button>
    </section>
  {/if}
</aside>

{#if fullOpen}
  <div class="modal-backdrop" role="presentation" onclick={handleBackdropClick}>
    <div
      bind:this={modalSurface}
      class="modal-surface"
      role="dialog"
      aria-modal="true"
      aria-label="Stats and Usage"
      tabindex="-1"
      data-testid="usage-modal"
    >
      <UsageWorkspace {displayMode} onToggleDisplayMode={toggleDisplayMode} onClose={() => void closeStats()} />
    </div>
  </div>
{/if}

<style>
  .usage-popover { position: relative; z-index: 10; }
  button { border: 0; font: inherit; cursor: pointer; }
  .trigger, .refresh-button, .details {
    border-radius: var(--radius-sm, 7px);
    color: var(--color-text);
    background: var(--color-elevated);
  }
  .trigger { padding: 0.5rem 0.75rem; white-space: nowrap; }
  .trigger:hover:not(:disabled), .refresh-button:hover:not(:disabled), .details:hover:not(:disabled) { background: var(--color-hover); }
  button:focus-visible, .modal-surface:focus-visible { outline: none; box-shadow: var(--focus-ring); }
  button:disabled { cursor: not-allowed; opacity: 0.55; }
  .card {
    position: absolute;
    top: calc(100% + 9px);
    right: 0;
    display: grid;
    gap: 13px;
    width: min(400px, calc(100vw - 28px));
    max-height: min(76vh, 620px);
    overflow-y: auto;
    padding: 14px;
    border: 1px solid color-mix(in srgb, var(--color-border) 60%, transparent);
    border-radius: var(--radius-lg, 12px);
    color: var(--color-text);
    background: var(--color-surface);
    box-shadow: var(--shadow-lg);
  }
  h2, p { margin: 0; }
  h2 { font-size: 1.1rem; line-height: 1.25; letter-spacing: -0.015em; }
  .eyebrow { margin-bottom: 2px; color: var(--color-text-3); font-size: 0.75rem; font-weight: 650; letter-spacing: 0.08em; text-transform: uppercase; }
  .card-header, .provider-heading, .quota-copy { display: flex; justify-content: space-between; gap: 10px; }
  .card-header { align-items: flex-start; }
  .refresh-button { display: inline-flex; align-items: center; gap: 6px; min-height: 30px; padding: 5px 9px; color: var(--color-text-2); font-size: 0.78rem; }
  .provider-list, .quota-list { display: grid; }
  .provider-list { gap: 8px; }
  .provider-card {
    display: grid;
    gap: 12px;
    padding: 12px;
    border: 1px solid color-mix(in srgb, var(--color-border) 38%, transparent);
    border-radius: var(--radius-md, 10px);
    background: color-mix(in srgb, var(--color-elevated) 58%, var(--color-surface));
  }
  .provider-heading { align-items: flex-start; }
  .provider-heading > div { display: grid; gap: 2px; }
  .provider-heading strong { font-size: 0.86rem; }
  .provider-heading small, .quota-copy small { color: var(--color-text-3); font-size: 0.75rem; }
  .provider-heading > span { color: var(--color-text-3); font-size: 0.75rem; }
  .provider-heading > span.available { color: var(--color-good); }
  .quota-list { gap: 12px; }
  .quota-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 6px 10px; align-items: end; }
  .quota-copy { min-width: 0; flex-direction: column; gap: 2px; }
  .quota-copy span { font-size: 0.78rem; font-weight: 570; }
  .quota-figure { padding: 0; border-radius: 5px; background: transparent; color: var(--color-text); font-size: 0.76rem; font-weight: 650; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .quota-figure:hover { color: var(--color-accent); }
  .quota-bar { grid-column: 1 / -1; height: 6px; overflow: hidden; border-radius: 999px; background: color-mix(in srgb, var(--color-border) 45%, transparent); }
  .quota-bar i { display: block; height: 100%; border-radius: inherit; background: var(--color-accent); }
  .unavailable { color: var(--color-text-2); font-size: 0.76rem; line-height: 1.45; }
  .details { width: 100%; min-height: 34px; padding: 7px 10px; color: var(--color-text); font-size: 0.8rem; }
  .modal-backdrop {
    position: fixed;
    inset: 0;
    z-index: 1000;
    display: grid;
    place-items: center;
    padding: 24px;
    background: color-mix(in srgb, var(--color-bg) 74%, transparent);
    backdrop-filter: blur(5px);
  }
  .modal-surface {
    width: min(1120px, calc(100vw - 48px));
    min-width: min(900px, calc(100vw - 48px));
    max-height: min(90vh, 920px);
    overflow: hidden;
    border: 1px solid color-mix(in srgb, var(--color-border) 65%, transparent);
    border-radius: var(--radius-lg, 14px);
    background: var(--color-surface);
    box-shadow: var(--shadow-lg);
  }
  @media (max-width: 700px) {
    .modal-backdrop { padding: 10px; }
    .modal-surface { width: calc(100vw - 20px); min-width: 0; max-height: calc(100vh - 20px); }
  }
</style>
