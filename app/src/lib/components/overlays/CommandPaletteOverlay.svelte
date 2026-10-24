<script lang="ts">
  /**
   * CommandPaletteOverlay.svelte — the Cmd+K "Run command" palette.
   *
   * Presentational only: it renders the centered overlay (backdrop, search field,
   * results list) and emits every action via callbacks. The page owns all palette
   * state (`commandPaletteVisible`/`commandPaletteQuery`/`commandPaletteIndex`), the
   * `commandPaletteResults` derived, the index-clamp `$effect`, and the keyboard-nav
   * handler — those stay in the page and read page-owned results/index.
   *
   * The one bit of logic that lives here is the search input ref + a focus `$effect`:
   * because the input element now lives in this child, the page can no longer focus
   * it imperatively on open, so we focus it from here whenever `visible` flips true.
   *
   * No teleport: this overlay renders inline (no `use:panelAction`), so its scoped
   * style block works normally. (Do not write that tag out in full here: the type
   * checker's Svelte-to-TypeScript step looks for the opening style tag as plain
   * text, finds the one in this comment first, and then reads the rest of the file
   * as CSS — after which the component has no default export and every file that
   * imports it fails to type-check.)
   */
  import { Search, MoreHorizontal } from '@lucide/svelte';

  /** A single command palette row. Matches the page's `SourceCommandPaletteItem`
   *  for the fields this overlay reads; `perform` is invoked by the page via
   *  `onSelect`, so it is not needed here. */
  interface CommandItem {
    id: string;
    label: string;
    detail: string;
    disabled?: boolean;
  }

  interface Props {
    /** Whether the palette is shown. The page already gates render on this; it is
     *  still passed so the focus effect can react to open. */
    visible: boolean;
    /** Bindable search text (page owns `commandPaletteQuery`). */
    query?: string;
    /** Bindable active-row index (page owns `commandPaletteIndex`). */
    index?: number;
    /** Filtered command rows (= page `commandPaletteResults`). Read-only. */
    commands: CommandItem[];
    /** Keydown handler for the search input — arrow/enter nav lives in the page. */
    onKeydown: (event: KeyboardEvent) => void;
    /** Invoked when a row is chosen (click or Enter elsewhere). */
    onSelect: (item: CommandItem) => void;
    /** Invoked when the backdrop is clicked. */
    onClose: () => void;
  }

  let {
    visible,
    query = $bindable(''),
    index = $bindable(0),
    commands,
    onKeydown,
    onSelect,
    onClose
  }: Props = $props();

  let inputEl: HTMLInputElement | null = null;

  // Focus the search input when the palette opens. The page used to do this
  // imperatively; the element now lives here, so the focus follows it.
  $effect(() => {
    if (visible) {
      inputEl?.focus();
    }
  });
</script>

<div class="command-palette-layer">
  <button
    class="command-palette-backdrop"
    type="button"
    aria-label="Close command palette"
    onclick={onClose}
  ></button>
  <div
    class="command-palette-panel"
    role="dialog"
    aria-modal="true"
    aria-label="Command palette"
  >
    <label class="command-palette-search">
      <span class="command-palette-icon">
        <Search size={17} strokeWidth={1.8} />
      </span>
      <input
        bind:this={inputEl}
        bind:value={query}
        onkeydown={onKeydown}
        placeholder="Run command"
        autocomplete="off"
      />
      <kbd>Cmd+K</kbd>
    </label>

    <div class="command-palette-results" role="listbox" aria-label="Matching commands">
      {#if commands.length === 0}
        <div class="command-palette-empty">No matching commands</div>
      {:else}
        {#each commands as item, i (item.id)}
          <button
            class:active={i === index}
            class:disabled={item.disabled}
            type="button"
            role="option"
            aria-selected={i === index}
            disabled={item.disabled}
            title={item.detail}
            onclick={() => void onSelect(item)}
          >
            <span class="command-palette-result-icon">
              <MoreHorizontal size={15} strokeWidth={1.8} />
            </span>
            <span>
              <strong>{item.label}</strong>
              <small>{item.detail}</small>
            </span>
          </button>
        {/each}
      {/if}
    </div>
  </div>
</div>

<style>
  .command-palette-layer {
    position: fixed;
    inset: 0;
    z-index: 40;
    display: grid;
    place-items: start center;
    padding: 72px 16px 16px;
  }

  .command-palette-backdrop {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    border: 0;
    background: rgba(7, 9, 10, 0.56);
    backdrop-filter: blur(10px);
    cursor: default;
  }

  .command-palette-panel {
    position: relative;
    z-index: 1;
    width: min(720px, calc(100vw - 32px));
    overflow: hidden;
    border: 1px solid rgba(255, 255, 255, 0.13);
    border-radius: 12px;
    background: rgba(22, 24, 24, 0.98);
    box-shadow: 0 28px 80px rgba(0, 0, 0, 0.44);
  }

  .command-palette-search {
    display: grid;
    grid-template-columns: 22px minmax(0, 1fr) auto;
    align-items: center;
    gap: 10px;
    height: 46px;
    padding: 0 14px;
    color: #9aa5a1;
    border-bottom: 1px solid rgba(255, 255, 255, 0.1);
    background: rgba(255, 255, 255, 0.045);
  }

  .command-palette-icon,
  .command-palette-result-icon {
    display: grid;
    place-items: center;
    min-width: 0;
  }

  .command-palette-icon {
    color: #6fdfcf;
  }

  .command-palette-search input {
    height: 100%;
    font-size: 15px;
    font-weight: 700;
  }

  .command-palette-search kbd {
    color: #7f8b87;
    font-family: var(--font-mono);
    font-size: 10px;
    font-weight: 800;
  }

  .command-palette-results {
    display: grid;
    gap: 3px;
    max-height: 368px;
    padding: 7px;
    overflow: auto;
  }

  .command-palette-results button {
    display: grid;
    grid-template-columns: 22px minmax(0, 1fr);
    align-items: center;
    gap: 10px;
    width: 100%;
    min-width: 0;
    height: 38px;
    padding: 0 9px;
    color: #cbd3d1;
    text-align: left;
    border: 0;
    border-radius: 8px;
    background: transparent;
    cursor: pointer;
  }

  .command-palette-results button:hover:not(:disabled),
  .command-palette-results button.active {
    color: #f2f6f5;
    background: rgba(92, 226, 207, 0.12);
  }

  .command-palette-results button:disabled {
    cursor: default;
    opacity: 0.44;
  }

  .command-palette-result-icon {
    color: #8d9995;
  }

  .command-palette-results button.active .command-palette-result-icon {
    color: #6fdfcf;
  }

  .command-palette-results button span {
    display: grid;
    min-width: 0;
  }

  .command-palette-results strong,
  .command-palette-results small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .command-palette-results strong {
    font-size: 12px;
    line-height: 1.15;
  }

  .command-palette-results small {
    color: #7f8b87;
    font-size: 10px;
    font-style: normal;
    font-weight: 760;
  }

  .command-palette-empty {
    display: grid;
    place-items: center;
    min-height: 112px;
    color: #9aa5a1;
    font-size: 13px;
    font-weight: 700;
  }
</style>
