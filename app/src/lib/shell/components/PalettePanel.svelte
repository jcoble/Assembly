<script lang="ts">
  /**
   * PalettePanel.svelte — the command palette host for /next.
   *
   * It owns only the palette's own state (shown/hidden, search text, highlighted
   * row) and the keyboard shortcuts that open it. The rows come from the shared
   * action list in `commandRegistry.ts`, and the drawing is done by the existing
   * `CommandPaletteOverlay`, reused unchanged.
   *
   * It is a page-level overlay, not a dock panel: it renders nothing at all while
   * closed, does no work until the user opens it, and never calls the backend. The
   * only actions it can run are the ones other parts of the shell have registered.
   *
   * Shortcuts: Cmd+K (Ctrl+K) and Cmd+Shift+P (Ctrl+Shift+P) open it, Escape closes
   * it. Arrow keys move the highlight, Enter runs the highlighted action.
   */
  import { onDestroy } from 'svelte';

  import CommandPaletteOverlay from '$lib/components/overlays/CommandPaletteOverlay.svelte';
  import {
    filterCommands,
    registerCommands,
    runPaletteCommand,
    type PaletteCommandRow
  } from '$lib/shell/palette/commandRegistry';

  interface Props {
    /** Put every panel back to its starting position. Omitted: the row is greyed out. */
    onResetLayout?: () => void;
    /** Open the settings dialog. */
    onOpenSettings?: () => void;
    /** Restore the hidden bottom dock. */
    onShowBottomDock?: () => void;
  }

  let { onResetLayout, onOpenSettings, onShowBottomDock }: Props = $props();

  let visible = $state(false);
  let query = $state('');
  let index = $state(0);
  /** Bumped on every open so the rows are read from the action list again — other
   *  panels may have added actions since the last time the palette was open. */
  let openCount = $state(0);
  /** Shown briefly when a chosen action fails; the palette itself is gone by then. */
  let failure = $state<string | null>(null);

  /** The rows for the current search. Nothing is computed while the palette is closed. */
  let rows = $derived.by<PaletteCommandRow[]>(() => {
    void openCount; // re-read the action list on every open
    if (!visible) return [];
    return filterCommands(query);
  });

  /** The highlight can never point past the end of a shrinking result list. */
  let activeIndex = $derived(Math.min(index, Math.max(0, rows.length - 1)));

  function describeError(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
  }

  /** Show the palette with an empty search. Safe to call when it is already open. */
  export function open(): void {
    visible = true;
    query = '';
    index = 0;
    failure = null;
    openCount += 1;
  }

  export function close(): void {
    visible = false;
    query = '';
    index = 0;
  }

  async function choose(row: PaletteCommandRow): Promise<void> {
    if (row.disabled) return;
    close();
    try {
      await runPaletteCommand(row);
    } catch (error) {
      failure = `${row.label} did not finish: ${describeError(error)}`;
    }
  }

  /** The overlay hands back the row it drew; find ours again by id and run it. */
  async function chooseById(id: string): Promise<void> {
    const row = rows.find((candidate) => candidate.id === id);
    if (row) await choose(row);
  }

  /** Arrow/Enter/Escape handling for the search field. */
  function handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      index = Math.min(activeIndex + 1, Math.max(0, rows.length - 1));
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      index = Math.max(activeIndex - 1, 0);
      return;
    }

    if (event.key === 'Enter') {
      event.preventDefault();
      const row = rows[activeIndex];
      if (row && !row.disabled) void choose(row);
    }
  }

  /** The open shortcuts, kept the same as the old shell's. */
  function handleWindowKeydown(event: KeyboardEvent): void {
    const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';

    if ((event.metaKey || event.ctrlKey) && (key === 'k' || (event.shiftKey && key === 'p'))) {
      event.preventDefault();
      open();
      return;
    }

    // Escape also closes the palette when the focus has drifted off its input.
    if (visible && event.key === 'Escape') {
      event.preventDefault();
      close();
    }
  }

  // The three actions the shell itself offers. They are registered once, under one
  // name, so mounting the palette twice would still leave a single copy of each.
  const removeShellCommands = registerCommands('shell', [
    {
      id: 'reset-layout',
      label: 'Reset layout',
      detail: 'Put every panel back to its starting position',
      disabled: () => onResetLayout == null,
      perform: () => onResetLayout?.()
    },
    {
      id: 'open-settings',
      label: 'Open settings',
      detail: 'Fonts, terminal and appearance',
      disabled: () => onOpenSettings == null,
      perform: () => onOpenSettings?.()
    },
    {
      id: 'show-bottom-dock',
      label: 'Show bottom dock',
      detail: 'Open the Problems and Terminal strip at the bottom',
      disabled: () => onShowBottomDock == null,
      perform: () => onShowBottomDock?.()
    }
  ]);

  onDestroy(removeShellCommands);
</script>

<svelte:window onkeydown={handleWindowKeydown} />

{#if visible}
  <CommandPaletteOverlay
    {visible}
    bind:query
    index={activeIndex}
    commands={rows}
    onKeydown={handleKeydown}
    onSelect={(item) => void chooseById(item.id)}
    onClose={close}
  />
{/if}

{#if failure}
  <div class="palette-failure" role="status">
    <span>{failure}</span>
    <button type="button" onclick={() => (failure = null)}>Dismiss</button>
  </div>
{/if}

<style>
  /* Shown after the palette has closed, so it is styled on its own in the /next
     palette rather than inside the reused overlay. */
  .palette-failure {
    position: fixed;
    top: 14px;
    left: 50%;
    transform: translateX(-50%);
    z-index: 45;
    display: flex;
    align-items: center;
    gap: 10px;
    max-width: min(560px, calc(100vw - 32px));
    padding: 6px 8px 6px 12px;
    border: 1px solid var(--color-border);
    border-radius: 7px;
    background: var(--color-surface);
    color: var(--color-text);
    font-family: ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    font-size: 12px;
    box-shadow: 0 12px 32px rgba(0, 0, 0, 0.42);
  }

  .palette-failure button {
    flex: none;
    padding: 2px 8px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--color-text-2);
    font-family: var(--font-mono);
    font-size: 12px;
    cursor: pointer;
  }

  .palette-failure button:hover {
    color: var(--color-text);
    background: var(--color-hover);
  }
</style>
