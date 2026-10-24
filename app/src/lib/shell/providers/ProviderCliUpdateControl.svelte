<script lang="ts">
  import { onMount } from 'svelte';
  import { Button } from '$lib/components/ui/button/index.js';
  import WorkingSpinner from '../components/feedback/WorkingSpinner.svelte';
  import { checkProviderClis, updateProviderCli, type CliUpdateState } from '$lib/shell/providers/providerCliUpdateService.svelte';

  let { profileId, machineName = 'This Mac' }: { profileId?: string; machineName?: string } = $props();
  const state = $state<CliUpdateState>({ checking: false, updating: null, statuses: [], message: '' });
  const owner = new AbortController();
  onMount(() => { void checkProviderClis(state, owner.signal, profileId); return () => owner.abort(); });
</script>

<div class="flex flex-col items-end gap-1.5 py-2">
  <span class="text-[12px]">Provider CLIs · {machineName}</span>
  <Button variant="secondary" size="sm" disabled={state.checking || state.updating !== null} onclick={() => void checkProviderClis(state, owner.signal, profileId)}>
    {#if state.checking}<WorkingSpinner size={12} />Reading versions…{:else}Read CLI versions{/if}
  </Button>
  {#each state.statuses as cli (cli.provider)}
    <div class="flex items-center gap-2 text-[12px]">
      <span>{cli.provider === 'codex' ? 'Codex' : 'Claude'}: {cli.version ?? 'Unavailable'}</span>
      <Button variant="secondary" size="sm" disabled={state.checking || state.updating !== null || !cli.version} onclick={() => void updateProviderCli(cli.provider, state, owner.signal, profileId)}>
        {#if state.updating === cli.provider}<WorkingSpinner size={12} />Updating…{:else}Update CLI{/if}
      </Button>
    </div>
    {#if cli.error}<p class="max-w-[360px] text-right text-[12px]" role="alert">{cli.error}</p>{/if}
  {/each}
  <p class="max-w-[360px] text-right text-[12px] text-[var(--color-text-2)]">CLI updates are separate from ACP adapters and Assembly. Antigravity's runtime updates with its adapter.</p>
  {#if state.message}<p class="max-w-[360px] text-right text-[12px]" role="status">{state.message}</p>{/if}
</div>
