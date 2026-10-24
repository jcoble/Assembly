<script lang="ts">
  import { onDestroy } from 'svelte';
  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import {
    checkForProviderUpdates,
    restartProviders,
    restartReconnectStep,
    sessionsEndedByRestart,
    startRestartCountdown,
    stepRestartCountdown,
    type ProviderUpdateState,
    type RestartCountdown,
    type RestartReconnect
  } from '$lib/shell/providers/providerUpdateService.svelte';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';

  // Stays mounted while its owner shows provider updates, so the re-check
  // after a restart still runs once the Restart button itself has gone.
  let { updateState, profileId }: { updateState: ProviderUpdateState; profileId?: string } = $props();
  const owner = new AbortController();
  onDestroy(() => owner.abort());
  const machineName = $derived(rail.remoteProfiles.find((profile) => profile.id === profileId)?.name ?? 'the remote machine');

  // A remote restart ends the sessions running there, so it warns and counts down first.
  let countdown = $state<RestartCountdown | null>(null);
  const counting = $derived(countdown !== null);
  const endingSessions = $derived(profileId ? sessionsEndedByRestart(rail.owned, profileId) : []);

  function requestRestart() {
    if (!profileId || endingSessions.length === 0) void restartProviders(updateState, profileId);
    else countdown = startRestartCountdown();
  }

  function stepCountdown(event: 'tick' | 'cancel' | 'restart-now') {
    if (!countdown) return;
    const next = stepRestartCountdown(countdown, event);
    countdown = next.phase === 'counting' ? next : null;
    if (next.phase === 'finished') void restartProviders(updateState, profileId);
  }

  $effect(() => {
    if (!counting) return;
    // One one-second interval while the warning counts down; cancel, finishing and unmounting clear it.
    const timer = window.setInterval(() => stepCountdown('tick'), 1000);
    return () => window.clearInterval(timer);
  });

  // After a restart, re-check (as Check now does) once the connection drops and returns.
  let reconnect: RestartReconnect = 'waiting-for-drop';
  $effect(() => {
    if (!profileId || updateState.phase !== 'reconnecting') { reconnect = 'waiting-for-drop'; return; }
    reconnect = restartReconnectStep(reconnect, rail.remoteConnections[profileId]);
    if (reconnect === 'recheck') void checkForProviderUpdates(owner.signal, updateState, profileId);
  });
</script>

{#if updateState.status?.restartRequired}
  <Button variant="secondary" size="sm" disabled={updateState.phase === 'installing'} onclick={requestRestart}>
    {profileId ? 'Restart remote server' : 'Restart Assembly'}
  </Button>
{/if}

<AlertDialog.Root open={countdown !== null} onOpenChange={(open) => { if (!open) stepCountdown('cancel'); }}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Restart the remote server on {machineName}?</AlertDialog.Title>
      <AlertDialog.Description>
        Restarting ends these sessions running on {machineName}. Restarting in {countdown?.remaining ?? 0} seconds.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <ul class="m-0 flex list-disc flex-col gap-1 pl-5 text-[13px]">
      {#each endingSessions as title, index (index)}<li class="break-words">{title}</li>{/each}
    </ul>
    <AlertDialog.Footer>
      <AlertDialog.Cancel size="sm">Cancel</AlertDialog.Cancel>
      <AlertDialog.Action size="sm" variant="destructive" onclick={() => stepCountdown('restart-now')}>Restart now</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
