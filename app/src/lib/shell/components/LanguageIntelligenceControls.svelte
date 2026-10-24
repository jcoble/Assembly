<script lang="ts">
  /** Compact global Editor/Supercharged switch shown in the editor status bar. */
  import { onDestroy } from 'svelte';
  import { Switch } from '$lib/components/ui/switch/index.js';
  import { settings } from '$lib/shell/settings/settingsStore.svelte';
  import { setLanguageServersEnabled } from '$lib/shell/files/editor/sourceIntelligence';
  import { readLanguageServerState } from '$lib/shell/files/components/editor/languageServerStatus';
  import { languageIntelligenceBar } from '$lib/shell/files/editor/languageIntelligenceBar.svelte';

  let busy = $state(false);
  const stopController = new AbortController();
  const stopSignal = stopController.signal;

  onDestroy(() => stopController.abort());

  async function chooseMode(enabled: boolean): Promise<void> {
    if (stopSignal.aborted || busy || enabled === settings.intelligence.languageServers) return;
    busy = true;
    const result = await setLanguageServersEnabled(enabled);
    if (stopSignal.aborted) return;
    if (result.supported) {
      settings.intelligence.languageServers = enabled;
    }
    busy = false;
  }

  /** Explain what the global choice will do before a project is open. */
  const hoverText = $derived(
    languageIntelligenceBar.hasProject
      ? [languageIntelligenceBar.title, `Supercharged: ${settings.intelligence.languageServers ? 'on' : 'off'}`]
          .filter((part) => part.length > 0)
          .join(' · ')
      : settings.intelligence.languageServers
        ? 'Supercharged is on. A language server starts when you open a supported project file.'
        : 'Editor mode is on. Files keep syntax highlighting without starting a language server.'
  );

  const serverState = $derived(readLanguageServerState(languageIntelligenceBar.status));
  const tone = $derived(
    !settings.intelligence.languageServers
      ? 'off'
      : languageIntelligenceBar.hasProject && serverState === 'ready'
        ? 'running'
        : 'waiting'
  );
</script>

<div class="language-switch" data-tone={tone} title={hoverText}>
  <span class="mode-label">Supercharged</span>
  <Switch
    size="sm"
    checked={settings.intelligence.languageServers}
    disabled={busy}
    onCheckedChange={(checked) => void chooseMode(checked)}
    aria-label="Supercharged editor"
  />
</div>

<style>
  .language-switch {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    flex: 0 0 auto;
    min-width: max-content;
    height: 100%;
    padding: 0;
    color: var(--color-text-2);
    user-select: none;
  }

  /* A footer word like its neighbours, not a heading. */
  .mode-label {
    font-size: 12px;
    font-weight: 400;
    color: var(--color-text-3);
  }

  .language-switch :global([data-slot='switch']) {
    padding: 0;
  }

  .language-switch[data-tone='off'] :global([data-slot='switch']) {
    background: var(--switch-track-off);
  }

  .language-switch[data-tone='waiting'] :global([data-slot='switch']) {
    background: var(--switch-track-waiting);
  }

  .language-switch[data-tone='running'] :global([data-slot='switch']) {
    background: var(--switch-track-running);
  }

  .language-switch :global([data-slot='switch-thumb']) {
    background: var(--switch-knob);
  }
</style>
