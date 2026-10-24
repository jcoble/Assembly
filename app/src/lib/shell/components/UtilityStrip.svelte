<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * UtilityStrip.svelte — the slim always-visible strip along the bottom of the
   * right panel.
   *
   * Three things live here: Resources, which carries the machine's live memory,
   * CPU and process count and opens the Resource Manager, and Usage, which
   * opens the quota card. Neither is a tab — they are always on screen, under
   * whichever panel is open.
   *
   * The surfaces themselves are mounted at the page root (see
   * `ShellOverlays.svelte`), so this component only says which one was asked
   * for and hands over the rectangle of the button that was pressed.
   *
   * The one piece of IO here is the lightweight resource totals read: it runs
   * on mount, every five seconds, and whenever the window comes back to the
   * front.
   */
  import { onMount } from 'svelte';
  import Cpu from '@lucide/svelte/icons/cpu';
  import Settings from '@lucide/svelte/icons/settings';

  import {
    formatResourceBytes,
    formatResourceCpu
  } from '$lib/shell/resources/resourceSampleViewModel';
  import {
    refreshResourceTotals,
    resourceSampleState
  } from '$lib/shell/resources/resourceSampleStore.svelte';
  import { resourceDiagnostics } from '$lib/shell/resourceDiagnostics.svelte';

  import { utilityAnchorFor, type UtilityId } from './utilityStrip';

  interface Props {
    /** Which of the two surfaces is open right now, so its button reads as on. */
    openUtility?: UtilityId | null;
    /** Open one of the two surfaces, anchored to the button that was pressed. */
    onOpenUtility(id: UtilityId, anchor: ReturnType<typeof utilityAnchorFor>): void;
    /** Open the settings dialog. The gear sits at the left end of this bar. */
    onOpenSettings(): void;
    /** Where the active session runs ("Local Mac" or "Remote"); none when no session is open. */
    location?: string | null;
  }
  let { openUtility = null, onOpenUtility, onOpenSettings, location = null }: Props = $props();

  function open(id: UtilityId, event: MouseEvent): void {
    if (!(event.currentTarget instanceof HTMLElement)) return;
    onOpenUtility(id, utilityAnchorFor(event.currentTarget.getBoundingClientRect()));
  }

  /* Each figure is a quiet label with its value one step brighter, so the
     bar reads as a row of named numbers rather than one run-on string. */
  const values = $derived.by((): [string, string][] => {
    const totals = resourceSampleState.totals ?? resourceSampleState.snapshot?.native.totals;
    if (!totals) return [];
    return [
      ['CPU', formatResourceCpu(totals.cpuPercent)],
      ['Memory', formatResourceBytes(totals.physicalFootprintBytes)],
      ['RSS', formatResourceBytes(totals.rssBytes)]
    ];
  });

  const status = $derived(
    resourceSampleState.loading
      ? 'Reading process usage…'
      : (resourceSampleState.error ?? 'Process usage is not available')
  );

  const ownershipValues = $derived.by((): [string, string][] => {
    if (!import.meta.env.DEV) return [];
    return [
      ['Chat', `${resourceDiagnostics.loadedConversationProjections}/${resourceDiagnostics.loadedConversationEventCount}`],
      ['Snap', `${resourceDiagnostics.conversationSnapshotReadsInFlight}/${resourceDiagnostics.conversationSnapshotReadsInvalidated} stale`],
      ['Turns', `${resourceDiagnostics.conversationRenderedRows}/${resourceDiagnostics.conversationVirtualRows}`],
      ['Cache', `${resourceDiagnostics.conversationMeasuredElementCacheEntries}/${resourceDiagnostics.conversationMeasuredSizeCacheEntries}`],
      ['CM', `${resourceDiagnostics.codeMirrorEditorStates}/${formatResourceBytes(resourceDiagnostics.openTabDocumentBytes)}`],
      ['Source', `${resourceDiagnostics.editorSourceReadsInFlight}/${resourceDiagnostics.editorSourceReadsInvalidated} stale/${formatResourceBytes(resourceDiagnostics.editorSourceReadBytesInFlight)}`],
      ['Dirs', `${resourceSampleState.totals?.activeSourceDirectoryReads ?? 0}`]
    ];
  });

  onMount(() => {
    void refreshResourceTotals();
    const refreshInterval = window.setInterval(() => {
      void refreshResourceTotals();
    }, 5_000);
    const refreshWhenVisible = (): void => {
      if (document.visibilityState === 'visible') void refreshResourceTotals();
    };
    document.addEventListener('visibilitychange', refreshWhenVisible);
    return () => {
      window.clearInterval(refreshInterval);
      document.removeEventListener('visibilitychange', refreshWhenVisible);
    };
  });
</script>

<footer class="utility-strip" aria-label="Settings, resources and usage">
  <button
    type="button"
    class="utility settings"
    data-testid="settings-gear"
    aria-label="Settings"
    onclick={() => onOpenSettings()}
  >
    <Settings class="glyph" strokeWidth={1.6} aria-hidden="true" />
  </button>
  <button
    type="button"
    class="utility resources"
    class:open={openUtility === 'resources'}
    aria-expanded={openUtility === 'resources'}
    data-testid="utility-resources"
    onclick={(event) => open('resources', event)}
  >
    <Cpu class="glyph" strokeWidth={1.6} aria-hidden="true" />
    {#if values.length}
      <span class="values">
        {#each values as [label, value], index (label)}
          {#if index}<span class="sep" aria-hidden="true">·</span>{/if}
          <span class="value">{label} <b>{value}</b></span>
        {/each}
      </span>
    {:else}
      <span class="label">Resources</span>
      <span class="status-text">{#if resourceSampleState.loading}<WorkingSpinner size={12} /> {/if}{status}</span>
    {/if}
    {#if ownershipValues.length}
      <span class="values ownership-summary">
        {#each ownershipValues as [label, value] (label)}
          <span class="sep" aria-hidden="true">·</span>
          <span class="value">{label} <b>{value}</b></span>
        {/each}
      </span>
    {/if}
  </button>
  {#if location}
    <span class="location" data-testid="utility-location">
      <span class="location-dot" aria-hidden="true"></span>{location}
    </span>
  {/if}
  <button
    type="button"
    class="utility usage"
    class:open={openUtility === 'usage'}
    aria-expanded={openUtility === 'usage'}
    aria-label="Usage and limits"
    data-testid="utility-usage"
    onclick={(event) => open('usage', event)}
  >
    <span class="label">Usage</span>
  </button>
</footer>

<style>
  .utility-strip {
    display: flex;
    flex: 0 0 28px;
    width: 100%;
    min-width: 0;
    height: 28px;
    align-items: center;
    gap: var(--space-1);
    padding: 0 var(--space-2);
    /* The status bar that hosts this strip paints its own surface and top
       edge; drawing them again here doubled the rule. Labels sit a step below
       the muted text so the bar stays quiet; values take the muted colour. */
    color: color-mix(in srgb, var(--muted-foreground) 62%, var(--card));
    font-size: 12px;
    container-type: inline-size;
    user-select: none;
  }

  .utility {
    display: flex;
    min-width: 0;
    height: 20px;
    align-items: center;
    gap: 6px;
    padding: 0 var(--space-2);
    border: 0;
    border-radius: var(--radius-pill);
    background: transparent;
    color: inherit;
    font: inherit;
    line-height: 1;
    cursor: pointer;
  }

  /* No label, so it needs no room for one. */
  .settings {
    flex: 0 0 auto;
    padding: 0;
    width: 20px;
    justify-content: center;
  }

  /* Hugs its own text rather than filling the bar. This strip used to sit in
     the narrow tools column, where stretching was right; across the whole
     window it made one button the width of the screen. The auto margin is what
     keeps Usage against the right edge. */
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
    background: var(--accent);
    color: var(--foreground);
  }

  .utility:focus-visible {
    outline: none;
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--ring) 50%, transparent);
  }

  .utility :global(.glyph) {
    width: 13px;
    height: 13px;
    flex: 0 0 auto;
  }

  .label {
    flex: 0 0 auto;
  }

  .location {
    display: inline-flex;
    flex: 0 0 auto;
    align-items: center;
    gap: 6px;
    color: var(--muted-foreground);
    white-space: nowrap;
  }

  .location-dot {
    width: 6px;
    height: 6px;
    border-radius: 999px;
    background: var(--primary);
  }

  .values {
    display: flex;
    min-width: 0;
    align-items: center;
    gap: var(--space-2);
    overflow: hidden;
    white-space: nowrap;
  }

  .status-text {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .value {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
  }

  .value b {
    color: var(--muted-foreground);
    font-weight: 500;
  }

  .sep {
    flex: 0 0 auto;
    color: var(--secondary);
  }

  .ownership-summary {
    flex: 0 1 auto;
  }

  /* In a narrow column the numbers go before the name does. */
  @container (max-width: 260px) {
    .values,
    .status-text {
      display: none;
    }
  }
</style>
