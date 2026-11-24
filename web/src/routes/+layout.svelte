<script lang="ts">
  import '$lib/styles/theme.css';
  import { setContext } from 'svelte';
  import { clockStore } from '$lib/application/clock.svelte';
  import { connectionStore } from '$lib/application/connection.svelte';
  import { machinesStore } from '$lib/application/machines.svelte';
  import { preferencesStore } from '$lib/application/preferences.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { createNativeSlotBridge, type NativeSlotBridge } from '$lib/infrastructure/nativeBridge';
  import * as Tooltip from '$lib/ui/tooltip/index.js';

  let { children } = $props();

  const nativeBridge: NativeSlotBridge = createNativeSlotBridge();
  setContext<NativeSlotBridge>('native-slot-bridge', nativeBridge);

  $effect(() => {
    const stopConn = connectionStore.start();
    const stopClock = clockStore.acquire();
    const stopPrefs = preferencesStore.acquire();
    const stopSessions = sessionsStore.acquire();
    const stopMachines = machinesStore.acquire();

    return () => {
      stopMachines();
      stopSessions();
      stopPrefs();
      stopClock();
      stopConn();
      nativeBridge.dispose();
    };
  });

  $effect(() => {
    if (typeof document !== 'undefined') {
      document.documentElement.dataset.theme = preferencesStore.theme;
    }
  });
</script>

<Tooltip.Provider delayDuration={0}>
  {@render children()}
</Tooltip.Provider>
