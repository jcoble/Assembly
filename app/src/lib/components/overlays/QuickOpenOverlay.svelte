<script lang="ts">
  /**
   * QuickOpenOverlay.svelte — the Cmd+P "Open source file" finder (and, when the
   * query starts with `#`, the workspace-symbol finder).
   *
   * Presentational only: it renders the centered overlay (backdrop, search field,
   * results list) and emits every action via callbacks. The page owns all quick-open
   * state (`quickOpenVisible`/`quickOpenQuery`/`quickOpenIndex` and the
   * `workspaceSymbol*` triple), every derived (`parsedQuickOpenQuery`,
   * `quickOpenWorkspaceSymbolMode`, `quickOpenWorkspaceSymbolQuery`,
   * `quickOpenResults`, `quickOpenActiveResultCount`), the index-clamp and
   * workspace-symbol-fetch `$effect`s, and the keyboard-nav handler — those stay in
   * the page and read page-owned results/index.
   *
   * The one bit of logic that lives here is the search input ref + an imperative
   * `focusInput()` handle: the page opens quick-open from three paths (plain,
   * symbol-mode, go-to-line) and used to focus + move the cursor to end on the input
   * directly. The element now lives in this child, so the page calls `focusInput()`
   * (via `bind:this`) instead; focus + cursor-to-end covers all three modes.
   *
   * No teleport: this overlay renders inline (no `use:panelAction`), so its scoped
   * style block works normally. (Do not write that tag out in full here — see the
   * same note in `CommandPaletteOverlay.svelte`: the type checker reads the rest of
   * the file as CSS if it finds an opening style tag inside this comment.)
   */
  import { Search, FileCode2 } from '@lucide/svelte';
  import type { SourceRecord, SourceWorkspaceSymbol } from '$lib/sourceData';

  interface Props {
    /** Whether the finder is shown. The page already gates render on this; it is
     *  still accepted so the markup mirrors the sibling overlay's contract. */
    visible: boolean;
    /** Bindable search text (page owns `quickOpenQuery`). */
    query?: string;
    /** Bindable active-row index (page owns `quickOpenIndex`). */
    index?: number;
    /** Filtered file rows (= page `quickOpenResults`). Read-only. */
    fileResults: SourceRecord[];
    /** Workspace-symbol rows (= page `workspaceSymbolResults`). Read-only. */
    symbolResults: SourceWorkspaceSymbol[];
    /** Whether the query selects symbol mode (= page `quickOpenWorkspaceSymbolMode`). */
    symbolMode: boolean;
    /** The trimmed symbol query after `#` (= page `quickOpenWorkspaceSymbolQuery`).
     *  Empty string drives the "Type a symbol name after #" empty state. */
    symbolQuery: string;
    /** Whether workspace-symbol search is in flight (= page `workspaceSymbolLoading`). */
    symbolLoading: boolean;
    /** Workspace-symbol error text (= page `workspaceSymbolError`), '' when none. */
    symbolError: string;
    /** Whether workspace symbols are available — `preview && sourceIntelligenceAvailable`
     *  in the page; drives the "Open a C# or TypeScript file first" empty state. */
    symbolsAvailable: boolean;
    /** Go-to-line target parsed from the query (= page `parsedQuickOpenQuery.targetLine`);
     *  shown as the trailing badge on file rows. */
    targetLine: number | null;
    /** Keydown handler for the search input — arrow/enter nav lives in the page. */
    onKeydown: (event: KeyboardEvent) => void;
    /** Invoked when a file row is chosen. */
    onSelectFile: (record: SourceRecord) => void;
    /** Invoked when a workspace-symbol row is chosen. */
    onSelectSymbol: (symbol: SourceWorkspaceSymbol) => void;
    /** Invoked when the backdrop is clicked. */
    onClose: () => void;
  }

  let {
    visible,
    query = $bindable(''),
    index = $bindable(0),
    fileResults,
    symbolResults,
    symbolMode,
    symbolQuery,
    symbolLoading,
    symbolError,
    symbolsAvailable,
    targetLine,
    onKeydown,
    onSelectFile,
    onSelectSymbol,
    onClose
  }: Props = $props();

  let inputEl: HTMLInputElement | null = null;

  /** Focus the search input and move the cursor to the end. The page used to do this
   *  imperatively from each open path; the element now lives here, so the page calls
   *  this via `bind:this`. Cursor-to-end is a no-op (position 0) for the empty plain
   *  query and matches the symbol/go-to-line paths that pre-fill the query. */
  export function focusInput() {
    inputEl?.focus();
    const cursor = inputEl?.value.length ?? 0;
    inputEl?.setSelectionRange(cursor, cursor);
  }
</script>

<div class="quick-open-layer">
  <button
    class="quick-open-backdrop"
    type="button"
    aria-label="Close quick open"
    onclick={onClose}
  ></button>
  <div
    class="quick-open-panel"
    role="dialog"
    aria-modal="true"
    aria-label="Open source file"
  >
    <label class="quick-open-search">
      <span class="quick-open-icon">
        <Search size={17} strokeWidth={1.8} />
      </span>
      <input
        bind:this={inputEl}
        bind:value={query}
        onkeydown={onKeydown}
        placeholder={symbolMode ? 'Search workspace symbols' : 'Open source file'}
        autocomplete="off"
      />
    </label>

    <div
      class="quick-open-results"
      role="listbox"
      aria-label={symbolMode ? 'Matching workspace symbols' : 'Matching source files'}
    >
      {#if symbolMode}
        {#if !symbolsAvailable}
          <div class="quick-open-empty">Open a C# or TypeScript file first</div>
        {:else if !symbolQuery}
          <div class="quick-open-empty">Type a symbol name after #</div>
        {:else if symbolLoading}
          <div class="quick-open-empty">Searching workspace symbols</div>
        {:else if symbolError}
          <div class="quick-open-empty">{symbolError}</div>
        {:else if symbolResults.length === 0}
          <div class="quick-open-empty">No matching workspace symbols</div>
        {:else}
          {#each symbolResults as symbol, i (`${symbol.path}:${symbol.line}:${symbol.column}:${symbol.symbolName}`)}
            <button
              class:active={i === index}
              type="button"
              role="option"
              aria-selected={i === index}
              title={symbol.detail}
              onclick={() => onSelectSymbol(symbol)}
            >
              <span class="quick-open-result-icon">
                <FileCode2 size={15} strokeWidth={1.8} />
              </span>
              <span>
                <strong>{symbol.symbolName}</strong>
                <small>{symbol.detail}</small>
              </span>
              <em>{symbol.kind}</em>
            </button>
          {/each}
        {/if}
      {:else if fileResults.length === 0}
        <div class="quick-open-empty">No matching source files</div>
      {:else}
        {#each fileResults as record, i (record.path)}
          <button
            class:active={i === index}
            type="button"
            role="option"
            aria-selected={i === index}
            title={record.relativePath}
            onclick={() => onSelectFile(record)}
          >
            <span class="quick-open-result-icon">
              <FileCode2 size={15} strokeWidth={1.8} />
            </span>
            <span>
              <strong>{record.fileName}</strong>
              <small>{record.relativePath}</small>
            </span>
            <em>{targetLine ? `line ${targetLine}` : record.language}</em>
          </button>
        {/each}
      {/if}
    </div>
  </div>
</div>

<style>
  .quick-open-layer {
    position: fixed;
    inset: 0;
    z-index: 40;
    display: grid;
    place-items: start center;
    padding: 72px 16px 16px;
  }

  .quick-open-backdrop {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    border: 0;
    background: rgba(7, 9, 10, 0.56);
    backdrop-filter: blur(10px);
    cursor: default;
  }

  .quick-open-panel {
    position: relative;
    z-index: 1;
    width: min(720px, calc(100vw - 32px));
    overflow: hidden;
    border: 1px solid rgba(255, 255, 255, 0.13);
    border-radius: 12px;
    background: rgba(22, 24, 24, 0.98);
    box-shadow: 0 28px 80px rgba(0, 0, 0, 0.44);
  }

  .quick-open-search {
    display: grid;
    grid-template-columns: 22px minmax(0, 1fr);
    align-items: center;
    gap: 10px;
    height: 50px;
    padding: 0 14px;
    color: #9aa5a1;
    border-bottom: 1px solid rgba(255, 255, 255, 0.1);
    background: rgba(255, 255, 255, 0.045);
  }

  .quick-open-icon,
  .quick-open-result-icon {
    display: grid;
    place-items: center;
    min-width: 0;
  }

  .quick-open-icon {
    color: #6fdfcf;
  }

  .quick-open-search input {
    height: 100%;
    font-size: 15px;
    font-weight: 700;
  }

  .quick-open-results {
    display: grid;
    gap: 3px;
    max-height: 368px;
    padding: 7px;
    overflow: auto;
  }

  .quick-open-results button {
    display: grid;
    grid-template-columns: 22px minmax(0, 1fr) auto;
    align-items: center;
    gap: 10px;
    width: 100%;
    min-width: 0;
    height: 44px;
    padding: 0 9px;
    color: #cbd3d1;
    text-align: left;
    border: 0;
    border-radius: 8px;
    background: transparent;
    cursor: pointer;
  }

  .quick-open-results button:hover,
  .quick-open-results button.active {
    color: #f2f6f5;
    background: rgba(92, 226, 207, 0.12);
  }

  .quick-open-result-icon {
    color: #8d9995;
  }

  .quick-open-results button.active .quick-open-result-icon {
    color: #6fdfcf;
  }

  .quick-open-results button span {
    display: grid;
    min-width: 0;
  }

  .quick-open-results strong,
  .quick-open-results small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .quick-open-results strong {
    font-size: 13px;
    line-height: 1.15;
  }

  .quick-open-results small,
  .quick-open-results em {
    color: #7f8b87;
    font-size: 10px;
    font-style: normal;
    font-weight: 760;
  }

  .quick-open-empty {
    display: grid;
    place-items: center;
    min-height: 112px;
    color: #9aa5a1;
    font-size: 13px;
    font-weight: 700;
  }
</style>
