<!-- Settings → Plugins and Providers: this machine's providers, then each connected remote machine's. -->
<script lang="ts">
  import { onDestroy } from 'svelte';
  import { readRemoteAssemblyEnvironmentFromTauri, type RemoteAssemblyProfile } from '$lib/tauriSource';
  import MachineProviders from './MachineProviders.svelte';

  let remotes = $state<RemoteAssemblyProfile[]>([]);
  let stopped = false;
  onDestroy(() => (stopped = true));

  void load();

  async function load(): Promise<void> {
    try {
      const environment = await readRemoteAssemblyEnvironmentFromTauri();
      if (!stopped) remotes = environment.profiles.filter((profile) => environment.readyProfileIds.includes(profile.id));
    } catch {
      // Without the saved machines this machine's providers still show.
    }
  }
</script>

<div class="flex flex-col gap-4 p-3" data-testid="provider-list">
  <MachineProviders />
  {#each remotes as profile (profile.id)}
    <MachineProviders {profile} />
  {/each}
  <p class="text-muted-foreground px-1 text-[13px]">
    May write, may prove and may judge appear once the provider test matrix records them. A provider that asks for
    sign-in only on its first message shows Ready until then.
  </p>
</div>
