<!--
  RunShortcuts.svelte — always-mounted owner for Run preference hydration and global shortcuts.

  Mounted once by `+page.svelte` so saved run shortcuts keep working after switching right-panel
  tabs or closing the right panel, while `/socket/terminal` output watching and `terminal/list`
  reconciliation stay strictly visibility-scoped inside `RunPanel.svelte` (zero idle cost).
-->
<script lang="ts">
  import { connectionStore } from '$lib/application/connection.svelte';
  import { preferencesStore } from '$lib/application/preferences.svelte';

  import { matchKeybinding } from './keybindingCapture';
  import {
    RUN_DEFINITIONS_PREF_KEY,
    RUN_RUNS_PREF_KEY,
    orderShortcutsForRoot
  } from './model';
  import { runStore } from './store.svelte';

  interface Props {
    /** Active session or project working folder, or '' when none is selected. */
    root: string;
    /** Optional human-readable project name for Run empty-state copy. */
    projectName?: string;
    /** Opens a configured preview URL without disrupting native GPUIKit Chat. */
    onOpenPreview?: (url: string) => void;
  }
  let { root, projectName = '', onOpenPreview }: Props = $props();

  const backendConnected = $derived(connectionStore.status === 'connected');
  const shortcutOrder = $derived(orderShortcutsForRoot(runStore.definitions, runStore.activeRoot));

  // Keep active project context updated for default folder & shortcut priority.
  $effect(() => {
    runStore.setContext(root, projectName);
  });

  // Hydrate and stay in sync with `.NET` preferences immediately on load (no backend polling).
  $effect(() => {
    if (!preferencesStore.loaded) return;
    const rawDefs = preferencesStore.get(RUN_DEFINITIONS_PREF_KEY);
    const rawRuns = preferencesStore.get(RUN_RUNS_PREF_KEY);
    runStore.syncFromPreferences(rawDefs, rawRuns);
  });

  function isSuppressed(target: EventTarget | null): boolean {
    if (typeof document !== 'undefined') {
      if (document.querySelector('[role="dialog"], [role="alertdialog"]')) return true;
    }
    if (!(target instanceof HTMLElement)) return false;
    if (target.isContentEditable) return true;
    const tag = target.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || tag === 'BUTTON';
  }

  function onKeydown(event: KeyboardEvent): void {
    if (!backendConnected || event.defaultPrevented || isSuppressed(event.target)) return;
    const matched = matchKeybinding(event, shortcutOrder);
    if (!matched) return;
    event.preventDefault();
    const existingRow = runStore.rows.find((item) => item.definition.id === matched.id);
    if (existingRow && (existingRow.state === 'running' || existingRow.state === 'starting')) {
      void runStore.restart(matched.id, onOpenPreview);
    } else {
      void runStore.launch(matched.id, onOpenPreview);
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />
