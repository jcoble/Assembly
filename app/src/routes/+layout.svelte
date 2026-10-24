<script lang="ts">
  import '../app.css';
  import { onMount } from 'svelte';
  import { Badge } from '$lib/components/ui/badge/index.js';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';
  import { isNativeTauriRuntime } from '$lib/tauriSource.ts';

  let { children } = $props();

  // In proof mode the app shows the planted value and fixture label it read on start (plan 3.11).
  let proofLabel = $state<string | null>(null);
  onMount(async () => {
    if (!isNativeTauriRuntime()) return;
    const { invoke } = await import('@tauri-apps/api/core');
    proofLabel = await invoke<string | null>('read_proof_mode_label');
  });
</script>

<Tooltip.Provider delayDuration={0}>
  {@render children()}
</Tooltip.Provider>

{#if proofLabel}
  <Badge variant="outline" class="fixed top-2.5 left-1/2 z-50 -translate-x-1/2 bg-background">{proofLabel}</Badge>
{/if}
