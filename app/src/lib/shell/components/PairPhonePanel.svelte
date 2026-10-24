<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import * as Dialog from '$lib/components/ui/dialog/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import SmartphoneIcon from '@lucide/svelte/icons/smartphone';
  import WorkingSpinner from './feedback/WorkingSpinner.svelte';
  import { remoteWorkflowFromTauri, type RemoteAssemblyProfile } from '$lib/tauriSource';
  import { loadPhoneDevices, loadPhonePanel, type PhonePanel } from './phonePairing';

  let { profile, onClose }: { profile: RemoteAssemblyProfile; onClose: () => void } = $props();
  let panel = $state<PhonePanel | null>(null);
  let error = $state('');
  let owner = new AbortController();
  // Each call is cancelled with the panel's current owner, so closing or a new code stops the SSH request.
  const run = (operation: string, args: object) => remoteWorkflowFromTauri(profile.id, operation, args, owner.signal);
  const time = (ms: number) => new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  const day = (ms: number) => new Date(ms).toLocaleDateString([], { month: 'short', day: 'numeric' });
  const message = (reason: unknown) => reason instanceof Error ? reason.message : String(reason);

  async function load(signal: AbortSignal) {
    try {
      const next = await loadPhonePanel(run, profile.name, signal);
      if (next) panel = next;
    } catch (reason) {
      if (!signal.aborted) error = message(reason);
    }
  }

  function newCode() {
    owner.abort();
    owner = new AbortController();
    error = '';
    // The backend has already dropped the old code, so stop showing it while the new one loads.
    panel = null;
    void load(owner.signal);
  }

  async function reloadDevices(signal: AbortSignal) {
    try {
      const devices = await loadPhoneDevices(run);
      if (!signal.aborted && panel) panel = { ...panel, devices };
    } catch (reason) {
      if (!signal.aborted) error = message(reason);
    }
  }

  async function remove(deviceId: string, signal: AbortSignal) {
    try {
      await run('phoneRevoke', { deviceId });
    } catch (reason) {
      if (!signal.aborted) error = message(reason);
      return;
    }
    if (!signal.aborted) await reloadDevices(signal);
  }

  onMount(() => void load(owner.signal));
  onDestroy(() => owner.abort());
</script>

<Dialog.Root open onOpenChange={(next) => { if (!next) onClose(); }}>
  <Dialog.Content class="sm:max-w-[400px] gap-5 p-6">
    <Dialog.Header>
      <Dialog.Title>Pair a phone with {profile.name}</Dialog.Title>
      <Dialog.Description>In Assembly on your phone, choose Pair a machine and point the camera at this code.</Dialog.Description>
    </Dialog.Header>

    {#if panel}
      <div class="flex flex-col items-center gap-3">
        <img src={panel.qr} alt="Pairing QR code for {profile.name}" class="size-[220px] rounded-lg bg-white shadow-sm" />
        <p class="font-mono text-[26px] font-semibold tracking-[0.12em] text-foreground" aria-label="Pairing code {panel.pairing.code}">
          <span>{panel.pairing.code.slice(0, 4)}</span><span class="ml-3">{panel.pairing.code.slice(4, 8)}</span><span class="ml-3">{panel.pairing.code.slice(8)}</span>
        </p>
        <p class="max-w-full truncate font-mono text-[13px] text-muted-foreground" title={panel.pairing.url}>{panel.pairing.url}</p>
        <div class="flex items-center gap-2 text-[13px] text-muted-foreground">
          <span>Single use · expires at {time(panel.pairing.expiresAt)}</span>
          <Button variant="ghost" size="xs" onclick={newCode}>New code</Button>
        </div>
      </div>
    {:else if error}
      <div class="flex flex-col items-start gap-3 rounded-lg bg-secondary p-4">
        <p class="text-[13px] text-foreground">{error}</p>
        <Button variant="ghost" size="xs" onclick={newCode}>Try again</Button>
      </div>
    {:else}
      <div class="flex h-[220px] items-center justify-center gap-2 text-[13px] text-muted-foreground" role="status">
        <WorkingSpinner size={12} />Making a pairing code…
      </div>
    {/if}

    {#if panel}
      <section class="flex flex-col gap-2 border-t pt-4">
        <div class="flex items-center justify-between">
          <h3 class="text-xs font-medium tracking-wide text-muted-foreground uppercase">Paired phones</h3>
          <Button variant="ghost" size="xs" onclick={() => { error = ''; void reloadDevices(owner.signal); }}>Refresh</Button>
        </div>
        {#each panel.devices as device (device.id)}
          <div class="flex items-center gap-3">
            <span class="flex size-8 shrink-0 items-center justify-center rounded-md bg-secondary text-muted-foreground"><SmartphoneIcon class="size-4" /></span>
            <span class="flex min-w-0 flex-1 flex-col">
              <span class="truncate text-[13px] text-foreground">{device.name}</span>
              <span class="truncate text-sm text-muted-foreground">{device.lastSeenAt ? `Last seen ${day(device.lastSeenAt)}, ${time(device.lastSeenAt)}` : `Paired ${day(device.createdAt)} · not connected yet`}</span>
            </span>
            <Button variant="ghost" size="xs" onclick={() => void remove(device.id, owner.signal)}>Remove</Button>
          </div>
        {:else}
          <p class="text-[13px] text-muted-foreground">No phones paired yet.</p>
        {/each}
        {#if error}<p class="text-[13px] text-destructive" role="alert">{error}</p>{/if}
      </section>
    {/if}
  </Dialog.Content>
</Dialog.Root>
