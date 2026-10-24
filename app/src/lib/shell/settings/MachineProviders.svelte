<!-- One machine's providers: a row per provider descriptor, checked the way the new-session
     picker checks a provider (sign-in state and models). A remote machine's installed
     adapters come from its provider update status. -->
<script lang="ts">
  import { onDestroy } from 'svelte';
  import { invoke } from '@tauri-apps/api/core';
  import { homeDir } from '@tauri-apps/api/path';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { probeAgentProviderConfigFromTauri, type RemoteAssemblyProfile } from '$lib/tauriSource';
  import type { AgentConversationProvider } from '$lib/shell/conversation/conversationTypes';
  import type { ProviderUpdateStatus } from '$lib/shell/providers/providerUpdateService.svelte';
  import { providerRow, type ProviderCheck, type ProviderSummary } from './providerRows';

  let { profile }: { profile?: RemoteAssemblyProfile } = $props();

  const stop = new AbortController();
  onDestroy(() => stop.abort());

  let summaries = $state<ProviderSummary[]>([]);
  let checks = $state<Record<string, ProviderCheck>>({});
  let loadError = $state('');
  let versionError = $state('');
  const rows = $derived(summaries.map((summary) => providerRow(summary, checks[summary.id])));

  void load();

  async function load(): Promise<void> {
    try {
      let list = await invoke<ProviderSummary[]>('list_provider_descriptors');
      if (profile) list = await remoteVersions(list, profile.id);
      if (stop.signal.aborted) return;
      summaries = list;
      // A saved machine's default folder is usually blank; every remote backend is Linux, so /tmp exists.
      const cwd = profile ? profile.defaultCwd || '/tmp' : await homeDir();
      for (const summary of list) if (summary.adapterVersion !== null) void check(summary.id, cwd);
    } catch (error) {
      if (!stop.signal.aborted) loadError = message(error);
    }
  }

  // A remote machine's installed adapters come from its update status, which needs the release
  // server; when that can't be read the rows are still checked, without versions.
  async function remoteVersions(list: ProviderSummary[], profileId: string): Promise<ProviderSummary[]> {
    try {
      const status = await invoke<ProviderUpdateStatus>('check_remote_provider_updates', { profileId });
      const installed = (id: string) => status.providers.find((entry) => entry.provider === id)?.currentVersion ?? null;
      return list.map((summary) => ({ ...summary, adapterVersion: installed(summary.id) }));
    } catch (error) {
      if (!stop.signal.aborted) versionError = message(error);
      return list.map((summary) => ({ ...summary, adapterVersion: undefined }));
    }
  }

  async function check(id: string, cwd: string): Promise<void> {
    try {
      const target = {
        provider: id as AgentConversationProvider,
        executionEnvironment: profile ? ('remote' as const) : ('local' as const),
        remoteProfileId: profile?.id ?? null,
        cwd
      };
      const config = await probeAgentProviderConfigFromTauri(target, stop.signal);
      if (config) checks[id] = { config };
    } catch (error) {
      if (!stop.signal.aborted) checks[id] = { error: message(error) };
    }
  }

  function message(error: unknown): string {
    if (error instanceof Error) return error.message;
    if (error && typeof error === 'object' && 'message' in error && typeof error.message === 'string') return error.message;
    return String(error);
  }
</script>

<div class="flex flex-col gap-2" data-testid="machine-providers">
  <p class="text-muted-foreground px-1 text-[13px] font-medium">{profile ? profile.name : 'This machine'}</p>
  {#if loadError}
    <p class="text-muted-foreground px-1 text-[13px]">Couldn't read the providers: {loadError}</p>
  {/if}
  {#if versionError}
    <p class="text-muted-foreground px-1 text-[13px]">Couldn't read the installed adapter versions: {versionError}</p>
  {/if}
  {#each rows as row (row.id)}
    <div class="bg-secondary/40 flex flex-col gap-1.5 rounded-lg px-3 py-2.5 shadow-sm" data-testid="provider-row">
      <div class="flex items-center gap-2">
        <span class="text-[14px] font-semibold">{row.name}</span>
        <span class="text-muted-foreground font-mono text-[12px]">{row.adapter}</span>
        <Chip tone={row.tone} class="ml-auto text-[12px]">{row.state}</Chip>
      </div>
      {#if row.models.length}
        <p class="text-[13px]"><span class="text-muted-foreground">Models:</span> {row.models.join(' · ')}</p>
      {/if}
      {#if row.detail}
        <p class="text-muted-foreground text-[13px] break-words">{row.detail}</p>
      {/if}
      <p class="text-muted-foreground text-[13px]">{row.flags.join(' · ')}</p>
    </div>
  {/each}
</div>
