<script lang="ts">
  import {
    refreshResourceSample,
    resourceManagerState,
    resourceSampleState,
    toggleResourceManager
  } from './resourceSampleStore.svelte';
  import { afterFloatingSurfacePaint } from '$lib/shell/floatingSurface.ts';

  function toggle(): void {
    toggleResourceManager();
    if (resourceManagerState.open) {
      afterFloatingSurfacePaint(() => void refreshResourceSample());
    }
  }
</script>

<aside class="resource-popover" data-testid="resource-popover" aria-label="Resources">
  <button
    type="button"
    class="trigger"
    aria-expanded={resourceManagerState.open}
    aria-controls="resource-manager-panel"
    onclick={toggle}
  >
    Resources
    {#if resourceSampleState.snapshot}<span>{resourceSampleState.snapshot.native.totals.processCount}</span>{/if}
  </button>
</aside>

<style>
  .resource-popover { position: relative; z-index: 10; }
  .trigger {
    border: 0;
    border-radius: 0.45rem;
    padding: 0.5rem 0.75rem;
    background: var(--color-elevated);
    color: var(--color-text);
    font: inherit;
    cursor: pointer;
  }
  .trigger:hover { background: var(--color-hover); }
  .trigger:focus-visible { outline: none; box-shadow: var(--focus-ring); }
  .trigger span { margin-left: 0.35rem; color: var(--color-text-2); }
</style>
