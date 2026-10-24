<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';

  import { Button } from '$lib/components/ui/button/index.js';
  import RestartProvidersButton from '$lib/shell/components/RestartProvidersButton.svelte';
  import {
    checkForProviderUpdates,
    installProviderUpdates,
    providerUpdateState,
    providerVersionLine,
    type ProviderUpdateState
  } from '$lib/shell/providers/providerUpdateService.svelte';

  let { profileId, machineName }: { profileId?: string; machineName?: string } = $props();
  const remoteState = $state<ProviderUpdateState>({ phase: 'idle', generation: 0, installingProvider: null, message: '', status: null });
  const updateState = $derived(profileId ? remoteState : providerUpdateState);
  const owner = new AbortController();
  onMount(() => { if (profileId) void checkForProviderUpdates(owner.signal, updateState, profileId); });
  onDestroy(() => owner.abort());
</script>

<div class="flex flex-col items-end gap-1.5">
  {#if profileId}<span class="text-[12px]">Provider adapters · {machineName}</span>{/if}
  <div class="flex items-center gap-2">
    <Button
      variant="secondary"
      size="sm"
      disabled={updateState.phase === 'checking' || updateState.phase === 'installing'}
      onclick={() => void checkForProviderUpdates(owner.signal, updateState, profileId)}
    >
      {#if updateState.phase === 'checking'}<WorkingSpinner size={12} />Checking…{:else}Check now{/if}
    </Button>
    <RestartProvidersButton {updateState} {profileId} />
  </div>
  {#if updateState.status}
    {#each updateState.status.providers as provider (provider.provider)}
      <div class="flex items-center gap-2 text-[12px]">
        {#if updateState.installingProvider === provider.provider}<span class="inline-flex" role="status" aria-label={`Downloading ${provider.provider} adapter`}><WorkingSpinner /></span>{/if}
        <span>{providerVersionLine(provider)}</span>
        {#if provider.updateAvailable}
          <Button variant="secondary" size="sm" disabled={updateState.phase === 'installing'} onclick={() => void installProviderUpdates(provider.provider, owner.signal, updateState, profileId)}>
            {updateState.installingProvider === provider.provider ? 'Installing…' : provider.currentVersion ? 'Update' : 'Install'}
          </Button>
        {/if}
      </div>
    {/each}
  {/if}
  {#if updateState.message}
    <p class="max-w-[360px] text-right text-[12px] leading-[1.4] text-[var(--color-text-2)]">
      {updateState.message}
    </p>
  {/if}
</div>
