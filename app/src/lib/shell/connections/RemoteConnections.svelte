<script lang="ts">
  import { onMount } from 'svelte';
  import PairPhonePanel from '../components/PairPhonePanel.svelte';
  import ProviderCliUpdateControl from '../providers/ProviderCliUpdateControl.svelte';
  import ProviderUpdateControl from '../providers/ProviderUpdateControl.svelte';
  import WorkingSpinner from '../components/feedback/WorkingSpinner.svelte';
  import { Button } from '$lib/components/ui/button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { hydrateOwned, rail, replaceConnectedRemoteRows } from '$lib/shell/stores/sessionRailStore.svelte';
  import { ownedSessionFromBackend } from '$lib/shell/ownedSessions';
  import {
    connectRemoteAssemblyFromTauri, deployRemoteBackendDev, disconnectRemoteAssemblyFromTauri,
    installRemoteAssemblyFromTauri, readRemoteAssemblyEnvironmentFromTauri,
    readRemoteBackendStatusesFromTauri, removeRemoteAssemblyProfileFromTauri,
    uninstallRemoteAssemblyFromTauri,
    type RemoteAssemblyEnvironment, type RemoteAssemblyProfile, type RemoteBackendProfileStatus,
    type RemoteInstallUpdate
  } from '$lib/tauriSource';

  type Transfer = Exclude<RemoteInstallUpdate, string> & { machine: string };

  let { initialProfile = null, onConnected, onChange, onClose }: {
    initialProfile?: RemoteAssemblyProfile | null;
    onConnected?: (profile: RemoteAssemblyProfile) => void;
    onChange?: (environment: RemoteAssemblyEnvironment) => void;
    onClose?: () => void;
  } = $props();
  const emptyProfile = (): RemoteAssemblyProfile => ({
    id: crypto.randomUUID(), name: '', sshTarget: '', sourceRoot: '', defaultCwd: ''
  });
  let profile = $state<RemoteAssemblyProfile>(emptyProfile());
  let editing = $state<RemoteAssemblyProfile | null>(null);
  let environment = $state<RemoteAssemblyEnvironment>({ profiles: [], readyProfileIds: [] });
  let busy = $state(false);
  let status = $state('');
  let error = $state('');
  let operationArea = $state<'saved' | 'install'>('saved');
  let owner = $state<AbortController | null>(null);
  let backends = $state<Record<string, RemoteBackendProfileStatus>>({});
  let confirmingUninstallId = $state<string | null>(null);
  let transfer = $state<Transfer | null>(null);
  let pairingPhone = $state<RemoteAssemblyProfile | null>(null);
  let mounted = false;

  const megabytes = (bytes: number) => `${Math.round(bytes / 1_000_000).toLocaleString()} MB`;

  function transferDetail(current: Transfer): string {
    const amount = `${megabytes(current.bytes)} of ${megabytes(current.total)}`;
    if (current.bytesPerSecond === null || current.etaSeconds === null) return `${amount} · Estimating time remaining`;
    const eta = current.etaSeconds < 60 ? `${current.etaSeconds} seconds` : `${Math.ceil(current.etaSeconds / 60)} minutes`;
    return `${amount} · ${(current.bytesPerSecond / 1_000_000).toFixed(1)} MB/s · About ${eta} remaining`;
  }

  function apply(next: RemoteAssemblyEnvironment) {
    environment = next;
    rail.remoteProfiles = next.profiles;
    onChange?.(next);
  }

  async function readBackends(profiles: RemoteAssemblyProfile[]) {
    try {
      const statuses = await readRemoteBackendStatusesFromTauri(profiles);
      if (mounted) backends = Object.fromEntries(statuses.map((entry) => [entry.profileId, entry]));
    } catch (reason) {
      if (mounted) {
        backends = {};
        error = `Could not check backend versions: ${reason instanceof Error ? reason.message : typeof reason === "object" && reason !== null && "message" in reason ? String(reason.message) : String(reason)}`;
      }
    }
  }

  function backendLabel(profileId: string): string {
    const backend = backends[profileId];
    if (!backend) return 'Backend compatibility not verified';
    if (!backend.installed) return 'Backend not installed';
    if (backend.backendBuildKind === 'development') return `Development build · ${backend.backendCommit ?? 'unknown commit'}`;
    const installed = backend.installedVersion ?? 'unknown version';
    if (backend.updateAvailable === null) return `Backend ${installed} · compatibility not verified`;
    return backend.updateAvailable
      ? `Backend ${installed} · update to ${backend.latestVersion} available`
      : `Backend ${installed} · latest compatible release`;
  }

  onMount(() => {
    mounted = true;
    if (initialProfile) editing = { ...initialProfile };
    const controller = new AbortController();
    void loadEnvironment(controller.signal);
    return () => { mounted = false; controller.abort(); owner?.abort(); };
  });

  async function loadEnvironment(signal: AbortSignal): Promise<void> {
    try {
      const next = await readRemoteAssemblyEnvironmentFromTauri();
      if (signal.aborted) return;
      apply(next);
      await readBackends(next.profiles);
    } catch (reason) {
      if (!signal.aborted) error = String(reason);
    }
  }

  async function runConnection(selected: RemoteAssemblyProfile, install = false, area: 'saved' | 'install' = 'saved') {
    if (busy) return;
    const attempt = new AbortController();
    owner = attempt;
    busy = true;
    operationArea = area;
    error = '';
    transfer = null;
    status = install ? 'Preparing installation…' : 'Connecting…';
    try {
      const operation = install ? installRemoteAssemblyFromTauri : connectRemoteAssemblyFromTauri;
      const result = await operation(selected, attempt.signal, (message) => {
        if (!mounted || attempt.signal.aborted) return;
        if (typeof message === 'string') { transfer = null; status = message; }
        else if (message.bytes < message.total) transfer = { ...message, machine: selected.name };
        else { transfer = null; status = `Download complete. Verifying and unpacking the backend on ${selected.name}…`; }
      });
      if (!mounted || attempt.signal.aborted) return;
      replaceConnectedRemoteRows([result.profile.id, result.replacedProfileId], result.sessions.map(ownedSessionFromBackend));
      const next = await readRemoteAssemblyEnvironmentFromTauri();
      if (!mounted || attempt.signal.aborted) return;
      apply(next);
      await readBackends(next.profiles);
      if (editing?.id === result.profile.id) editing = null;
      if (profile.id === result.profile.id) profile = emptyProfile();
      status = install
        ? 'Installed and connected. Your conversations are available in Sessions.'
        : 'Connected. Your conversations are available in Sessions.';
      onConnected?.(result.profile);
    } catch (reason) {
      if (import.meta.env.DEV && install && !attempt.signal.aborted) {
        try {
          const next = await readRemoteAssemblyEnvironmentFromTauri();
          if (mounted && !attempt.signal.aborted) apply(next);
        } catch (refreshError) { console.warn('Could not refresh saved machines', refreshError); }
      }
      if (!attempt.signal.aborted) await readBackends(environment.profiles);
      if (mounted) {
        status = '';
        error = attempt.signal.aborted ? `${install ? 'Installation' : 'Connection'} cancelled.` : reason instanceof Error ? reason.message : typeof reason === 'object' && reason !== null && 'message' in reason ? String(reason.message) : String(reason);
      }
    } finally {
      if (owner === attempt) { owner = null; busy = false; transfer = null; }
    }
  }

  async function changeConnection(selected: RemoteAssemblyProfile, remove = false) {
    if (busy) return;
    busy = true;
    operationArea = 'saved';
    status = '';
    error = '';
    try {
      const next = remove
        ? await removeRemoteAssemblyProfileFromTauri(selected.id)
        : await disconnectRemoteAssemblyFromTauri(selected.id);
      if (!mounted) return;
      apply(next);
      status = remove ? 'Saved connection removed. The backend is still installed.' : 'Disconnected. The connection is saved for next time.';
      if (remove && editing?.id === selected.id) editing = null;
    } catch (reason) { if (mounted) error = String(reason); }
    finally { if (mounted) busy = false; }
  }

  async function deployDevBuild(selected: RemoteAssemblyProfile) {
    if (busy) return;
    busy = true;
    operationArea = 'saved';
    status = `Deploying this checkout to ${selected.name}…`;
    error = '';
    const timings: string[] = [];
    try {
      await deployRemoteBackendDev(selected.sshTarget, (line) => {
        if (!mounted) return;
        if (/: \d+(\.\d+)?s$/.test(line)) timings.push(line);
        else status = line;
      });
      if (!mounted) return;
      await readBackends(environment.profiles);
      status = `Development build deployed to ${selected.name}. ${timings.join(' · ')}`;
    } catch (reason) { if (mounted) { status = ''; error = reason instanceof Error ? reason.message : String(reason); } }
    finally { if (mounted) busy = false; }
  }

  async function uninstallBackend(selected: RemoteAssemblyProfile, deleteData: boolean) {
    if (busy) return;
    busy = true;
    operationArea = 'saved';
    status = deleteData ? 'Uninstalling and deleting data…' : 'Uninstalling…';
    error = '';
    try {
      const result = await uninstallRemoteAssemblyFromTauri(selected, deleteData);
      if (!mounted) return;
      if (deleteData) {
        const replacedProfileIds = new Set([selected.id, result.replacedProfileId]);
        hydrateOwned(rail.owned.filter((session) => !replacedProfileIds.has(session.remoteProfileId ?? '')));
      }
      apply(result.environment);
      confirmingUninstallId = null;
      status = deleteData
        ? `Backend uninstalled from ${selected.name} and its stored data deleted. The saved connection is kept, and project files were left alone.`
        : `Backend uninstalled from ${selected.name}. Its stored data was kept, and the saved connection is kept.`;
      await readBackends(result.environment.profiles);
    } catch (reason) { if (mounted) { status = ''; error = String(reason); } }
    finally { if (mounted) busy = false; }
  }
</script>

{#snippet operationStatus()}
  {#if transfer}
    <div class="transfer" role="status">
      <span class="busy-line"><WorkingSpinner size={12} />Downloading backend on {transfer.machine}</span>
      <progress value={transfer.bytes} max={transfer.total}></progress>
      <small>{transferDetail(transfer)}</small>
    </div>
  {:else if status}<p class="busy-line" role="status">{#if busy}<WorkingSpinner size={12} />{/if}{status}</p>{/if}
  {#if error}<p role="alert">{error}</p>{/if}
{/snippet}

<div class="remote-connections">
  <section>
    <div class="section-heading"><strong>Saved machines</strong><small>Connect to an installed backend or update it from the latest signed release.</small></div>
    <div class="connection-actions"><Button variant="ghost" size="xs" disabled={busy} onclick={async () => {
      busy = true; error = ''; operationArea = 'saved';
      try { await readBackends(environment.profiles); }
      finally { if (mounted) busy = false; }
    }}>Check for backend updates</Button></div>
    {#if environment.profiles.length === 0}<p>No remote machines saved yet.</p>{/if}
    {#each environment.profiles as saved (saved.id)}
      {@const backend = backends[saved.id]}
      <div class="connection-row">
        <span><strong>{saved.name}</strong><small><span class="status-dot" class:connected={environment.readyProfileIds.includes(saved.id)} aria-hidden="true"></span>{saved.sshTarget} · {environment.readyProfileIds.includes(saved.id) ? 'Connected' : 'Disconnected'} · {backendLabel(saved.id)}</small></span>
        <div class="connection-actions">
          {#if environment.readyProfileIds.includes(saved.id)}
            <Button variant="secondary" size="sm" disabled={busy} onclick={() => void changeConnection(saved)}>Disconnect</Button>
            <Button variant="secondary" size="sm" disabled={busy} onclick={() => pairingPhone = saved}>Pair phone</Button>
          {:else}
            <Button variant="secondary" size="sm" disabled={busy} onclick={() => void runConnection(saved)}>Connect</Button>
          {/if}
          {#if backend && !backend.installed}
            <Button variant="ghost" size="xs" disabled={busy} onclick={() => void runConnection(saved, true, 'saved')}>Install</Button>
          {:else if backend?.backendBuildKind === 'development'}
            <Button variant="ghost" size="xs" disabled={busy} onclick={() => void runConnection(saved, true, 'saved')}>Install signed {backend.latestVersion ?? 'release'}</Button>
          {:else if backend?.updateAvailable === true}
            <Button variant="ghost" size="xs" disabled={busy} onclick={() => void runConnection(saved, true, 'saved')}>Update</Button>
          {/if}
          {#if import.meta.env.DEV}
            <Button variant="ghost" size="xs" disabled={busy} onclick={() => void deployDevBuild(saved)}>Deploy from this checkout</Button>
          {/if}
          <Button variant="ghost" size="xs" disabled={busy} onclick={() => { editing = { ...saved }; status = ''; error = ''; }}>Edit</Button>
          <Button variant="ghost" size="xs" disabled={busy} onclick={() => { confirmingUninstallId = saved.id; status = ''; error = ''; }}>Uninstall</Button>
          <Button variant="ghost" size="xs" disabled={busy} onclick={() => void changeConnection(saved, true)}>Remove</Button>
        </div>
      </div>
      {#if environment.readyProfileIds.includes(saved.id)}
        <ProviderUpdateControl profileId={saved.id} machineName={saved.name} />
      {/if}
      <ProviderCliUpdateControl profileId={saved.id} machineName={saved.name} />
      {#if confirmingUninstallId === saved.id}
        <div class="uninstall-confirm">
          <strong>Uninstall the backend from {saved.name}?</strong>
          <p>This stops and removes the backend service and its programs on {saved.name}. Any remote work running there right now is interrupted immediately.</p>
          <p>Keeping data leaves the backend's own stored data on the machine — its conversation history, sessions and settings — so reinstalling later picks up where you left off. Deleting that data erases it permanently and cannot be undone. Your project files and code checkouts on the machine are never deleted either way, and the saved connection stays in this list.</p>
          <div class="connection-actions">
            <Button variant="secondary" size="sm" disabled={busy} onclick={() => void uninstallBackend(saved, false)}>Uninstall and keep data</Button>
            <Button variant="destructive" size="sm" disabled={busy} onclick={() => void uninstallBackend(saved, true)}>Uninstall and delete data</Button>
            <Button variant="ghost" size="sm" disabled={busy} onclick={() => confirmingUninstallId = null}>Cancel</Button>
          </div>
        </div>
      {/if}
    {/each}
    {#if editing}
      <div class="edit-form">
        <div class="connection-heading"><strong>Edit saved machine</strong><Button variant="ghost" size="xs" disabled={busy} onclick={() => editing = null}>Cancel</Button></div>
        <label>Machine name<Input disabled={busy} bind:value={editing.name} autocomplete="off" /></label>
        <label>SSH destination<Input disabled={busy} bind:value={editing.sshTarget} autocomplete="off" /></label>
        <div class="connection-actions"><Button size="sm" disabled={busy || !editing.name.trim() || !editing.sshTarget.trim()} onclick={() => editing && void runConnection(editing)}>Save and connect</Button></div>
      </div>
    {/if}
    {#if operationArea === 'saved'}{@render operationStatus()}{/if}
  </section>
  <section class="install-section">
    <div class="section-heading"><strong>Install a remote machine</strong><small>Downloads the latest signed Linux x86-64 backend directly onto the selected remote machine over public HTTPS, verifies and installs it over SSH, then connects.</small></div>
    <label>Machine name<Input disabled={busy} bind:value={profile.name} placeholder="Agent Workbox" autocomplete="off" /></label>
    <label>SSH destination<Input disabled={busy} bind:value={profile.sshTarget} placeholder="user@hostname or SSH alias" autocomplete="off" /></label>
    <div class="connection-actions"><Button size="sm" disabled={busy || !profile.name.trim() || !profile.sshTarget.trim()} onclick={() => void runConnection(profile, true, 'install')}>{busy && operationArea === 'install' ? 'Please wait…' : 'Install and connect'}</Button></div>
    {#if operationArea === 'install'}{@render operationStatus()}{/if}
  </section>
  {#if pairingPhone}<PairPhonePanel profile={pairingPhone} onClose={() => pairingPhone = null} />{/if}
  <div class="connection-actions">
    {#if owner}<Button variant="ghost" size="sm" onclick={() => owner?.abort()}>Cancel current operation</Button>
    {:else if onClose}<Button variant="ghost" size="sm" onclick={onClose}>Done</Button>{/if}
  </div>
</div>

<style>
  .remote-connections { display: flex; flex-direction: column; gap: 18px; padding: 20px; text-align: left; color: var(--color-text); }
  section { display: flex; flex-direction: column; gap: 12px; }
  .install-section { padding-top: 18px; border-top: 1px solid var(--color-border); }
  .section-heading { display: flex; flex-direction: column; gap: 3px; }
  p, small { margin: 0; color: var(--color-text-3); font-size: 12px; }
  .connection-row, .connection-heading, .connection-actions { display: flex; align-items: center; gap: 8px; }
  .connection-row { flex-wrap: wrap; padding: 12px 0; border-bottom: 1px solid var(--color-border); }
  .connection-row > span { display: flex; flex: 1 1 240px; min-width: 0; flex-direction: column; }
  .connection-row > .connection-actions { flex-wrap: wrap; margin-left: auto; }
  small { overflow-wrap: anywhere; }
  .status-dot { display: inline-block; width: 7px; height: 7px; margin-right: 6px; border-radius: 50%; background: var(--color-text-3); }
  .status-dot.connected { background: var(--color-accent); }
  .connection-heading { justify-content: space-between; }
  .uninstall-confirm { display: flex; flex-direction: column; gap: 8px; margin-bottom: 12px; padding: 12px; border: 1px solid var(--color-border); border-radius: 8px; }
  .uninstall-confirm strong { font-size: 13px; }
  .edit-form { display: flex; flex-direction: column; gap: 10px; padding: 12px 0 4px; }
  label { display: flex; flex-direction: column; gap: 6px; font-size: 12px; color: var(--color-text-3); }
  .connection-actions { justify-content: flex-end; }
  .transfer { display: flex; flex-direction: column; gap: 6px; font-size: 12px; }
  .transfer progress { width: 100%; height: 6px; accent-color: var(--color-accent); }
  .busy-line { display: flex; align-items: center; gap: 6px; }
</style>
