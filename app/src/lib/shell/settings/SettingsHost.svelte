<!--
  SettingsHost.svelte — puts the settings screen on the /next shell.

  It owns three things and nothing else:
    1. the /next stylesheets, so the screen (and everything else rendered
       inside /next) paints in the /next palette;
    2. whether the screen is showing;
    3. loading the screen itself, which only happens the first time
       someone asks for it.

  The settings surface is `$lib/shell/settings/SettingsDialog.svelte` — the
  /next shell's own, built from the shadcn components. It is pulled in on
  demand rather than with the rest of the shell so that starting the app costs
  nothing for a screen most launches never show. The host also owns the one
  launch-time update check; destruction aborts its result publication.

  Usage:
    <script>
      import SettingsHost from '$lib/shell/settings/SettingsHost.svelte';
      let settingsHost: SettingsHost;
    </script>
    <SettingsHost bind:this={settingsHost} />
    <button onclick={() => settingsHost.open()}>Settings</button>
-->
<script lang="ts">
  /* The /next palette, and the Tailwind + shadcn variables built from it.
     Imported here so that anything mounting this host gets the right colors
     even before the page imports the files itself. */
  import '$lib/shell/styles/nextTokens.css';
  import '$lib/shell/styles/next.css';
  import { onDestroy, onMount } from 'svelte';

  import type { ProblemsLocation } from '$lib/shell/settings/settingsStore.svelte';
  import { checkForProviderUpdates } from '$lib/shell/providers/providerUpdateService.svelte';
  import { checkForAppUpdate } from '$lib/shell/appUpdateService.svelte';

  /** The real settings surface, referred to by type only — no load yet. */
  type SettingsDialogComponent =
    (typeof import('$lib/shell/settings/SettingsDialog.svelte'))['default'];

  interface Props {
    /** Passed straight through to the dialog: the user moved the Problems
     * list, and the shell needs to open or close the bottom strip now. */
    onProblemsLocationChange?: (location: ProblemsLocation) => void;
    onRemoteConnected?: (profileId: string) => void;
  }
  let { onProblemsLocationChange, onRemoteConnected }: Props = $props();

  let SettingsDialog = $state<SettingsDialogComponent | null>(null);
  let dialogOpen = $state(false);
  let section = $state('appearance');
  let query = $state('');
  let loadFailure = $state<string | null>(null);
  /** Guards a second open() while the first load is still in flight. */
  let loading = false;
  let mounted = true;
  let loadGeneration = 0;
  const updateOwner = new AbortController();

  onMount(() => {
    void checkForAppUpdate(updateOwner.signal, true);
    void checkForProviderUpdates(updateOwner.signal);
  });

  onDestroy(() => {
    updateOwner.abort();
    mounted = false;
    loadGeneration += 1;
  });

  /**
   * Show the settings dialog, at `atSection` when given. Safe to call
   * repeatedly; the first call loads the panel, later calls just reopen it.
   */
  export function open(atSection?: string): void {
    if (atSection) {
      section = atSection;
      query = '';
    }
    void openDialog();
  }

  async function openDialog(): Promise<void> {
    loadFailure = null;
    if (SettingsDialog) {
      dialogOpen = true;
      return;
    }
    if (loading) return;
    loading = true;
    const generation = ++loadGeneration;
    try {
      const module = await import('$lib/shell/settings/SettingsDialog.svelte');
      if (mounted && generation === loadGeneration) {
        SettingsDialog = module.default;
        dialogOpen = true;
      }
    } catch (error) {
      if (mounted && generation === loadGeneration) {
        loadFailure = error instanceof Error ? error.message : String(error);
      }
    } finally {
      if (generation === loadGeneration) loading = false;
    }
  }

  /** Hide the settings dialog. */
  export function close(): void {
    dialogOpen = false;
  }

  /** Whether the settings dialog is currently showing. */
  export function isOpen(): boolean {
    return dialogOpen;
  }
</script>

{#if SettingsDialog}
  <SettingsDialog bind:open={dialogOpen} bind:activeSection={section} bind:query {onProblemsLocationChange} {onRemoteConnected} />
{/if}

{#if loadFailure}
  <!-- Only ever seen if the settings screen fails to load — a silent no-op
       button would be worse than a one-line explanation. -->
  <div
    role="alert"
    class="fixed bottom-3 left-1/2 z-[200] max-w-[480px] -translate-x-1/2 rounded-md border
           border-destructive/40 bg-destructive/10 px-3 py-1.5 text-[13px] text-destructive"
  >
    Settings could not be opened: {loadFailure}
  </div>
{/if}
