<script lang="ts">
  /**
   * ActivityClipboardPanel.svelte — the "clipboard" activity mode (paste cleanup).
   *
   * Presentational only: it renders the paste-cleanup chrome (mode toolbar,
   * history chips, the input/output/reply grid, the footer copy buttons) and
   * emits every action via callbacks. The page owns all paste-cleanup state
   * (`pasteCleanupInput`/`pasteCleanupReplyDraft`/`pasteCleanupMode`/
   * `pasteCleanupHistory`, the derived output/stats/visible-history values and
   * `fileActionBusy`) and every paste-cleanup function; this component holds NO
   * `$state` of its own.
   *
   * Renders inline in the activity area (no teleport). The wrapping
   * `{#if sourceActivityMode === 'clipboard'}` gate stays in the page.
   */
  import { Copy, History, X } from '@lucide/svelte';
  import type { PasteCleanupHistoryItem, PasteCleanupMode } from '$lib/pasteCleanup';

  interface Props {
    /** Bindable cleanup input text (page owns `pasteCleanupInput`). */
    input?: string;
    /** Bindable reply draft text (page owns `pasteCleanupReplyDraft`). */
    replyDraft?: string;
    /** Bindable cleanup mode (page owns `pasteCleanupMode`). */
    mode?: PasteCleanupMode;
    /** Selectable cleanup modes (= page `pasteCleanupModes`). */
    modes: readonly PasteCleanupMode[];
    /** Cleaned output (= page `pasteCleanupOutput`). Read-only. */
    output: string;
    /** Cleaned-text stats label (= page `pasteCleanupStats`). */
    stats: string;
    /** Cleaned reply output (= page `pasteCleanupReplyOutput`). Read-only. */
    replyOutput: string;
    /** Reply-draft stats label (= page `pasteCleanupReplyStats`). */
    replyStats: string;
    /** Visible history slice (= page `visiblePasteCleanupHistory`). */
    history: PasteCleanupHistoryItem[];
    /** Current busy action key (= page `fileActionBusy`) — drives disabled states. */
    fileActionBusy: string;
    /** Short label for a history item kind (page helper). */
    historyKindLabel: (kind: PasteCleanupHistoryItem['kind']) => string;
    /** Tooltip title for a history item (page helper). */
    historyItemTitle: (item: PasteCleanupHistoryItem) => string;
    /** Mode `<select>` change handler (page persists the mode). */
    onModeChange: (event: Event) => void;
    /** Read from the clipboard into the input. */
    onPaste: () => void;
    /** Clear the input. */
    onClearInput: () => void;
    /** Restore a history item back into the panel. */
    onRestoreHistoryItem: (item: PasteCleanupHistoryItem) => void;
    /** Copy a history item to the clipboard. */
    onCopyHistoryItem: (item: PasteCleanupHistoryItem) => void;
    /** Clear all paste-cleanup history. */
    onClearHistory: () => void;
    /** Copy the cleaned output. */
    onCopyOutput: () => void;
    /** Copy the reply draft. */
    onCopyReplyDraft: () => void;
  }

  let {
    input = $bindable(''),
    replyDraft = $bindable(''),
    mode = $bindable('plain'),
    modes,
    output,
    stats,
    replyOutput,
    replyStats,
    history,
    fileActionBusy,
    historyKindLabel,
    historyItemTitle,
    onModeChange,
    onPaste,
    onClearInput,
    onRestoreHistoryItem,
    onCopyHistoryItem,
    onClearHistory,
    onCopyOutput,
    onCopyReplyDraft,
  }: Props = $props();
</script>

<div class="paste-cleanup-panel">
  <div class="paste-cleanup-top">
    <div class="paste-cleanup-toolbar">
      <label>
        <span>Mode</span>
        <select bind:value={mode} onchange={onModeChange} aria-label="Paste cleanup mode">
          {#each modes as cleanupMode (cleanupMode)}
            <option value={cleanupMode}>{cleanupMode}</option>
          {/each}
        </select>
      </label>
      <button
        class="file-action-button"
        type="button"
        aria-label="Read clipboard"
        title="Read clipboard"
        disabled={fileActionBusy === 'paste-read'}
        onclick={onPaste}
      >
        <Copy size={13} strokeWidth={1.9} />
        <span>{fileActionBusy === 'paste-read' ? 'Reading' : 'Paste'}</span>
      </button>
      <button
        class="file-action-button"
        type="button"
        aria-label="Clear paste cleanup text"
        title="Clear paste cleanup text"
        disabled={input.length === 0}
        onclick={onClearInput}
      >
        <X size={13} strokeWidth={1.9} />
        <span>Clear</span>
      </button>
    </div>
    {#if history.length > 0}
      <div class="paste-cleanup-history" aria-label="Paste cleanup history">
        {#each history as item (item.id)}
          <div class="paste-history-chip" class:reply={item.kind === 'reply'}>
            <button
              class="paste-history-restore"
              type="button"
              aria-label={`Restore paste cleanup ${historyKindLabel(item.kind)}`}
              title={historyItemTitle(item)}
              onclick={() => onRestoreHistoryItem(item)}
            >
              <History size={11} strokeWidth={2} />
              <span>{historyKindLabel(item.kind)}</span>
              <strong>{item.summary}</strong>
            </button>
            <button
              class="paste-history-copy"
              type="button"
              aria-label={`Copy paste cleanup ${historyKindLabel(item.kind)}`}
              title="Copy history item"
              onclick={() => onCopyHistoryItem(item)}
            >
              <Copy size={11} strokeWidth={2} />
            </button>
          </div>
        {/each}
        <button
          class="paste-history-clear"
          type="button"
          aria-label="Clear paste cleanup history"
          title="Clear paste cleanup history"
          onclick={onClearHistory}
        >
          <X size={11} strokeWidth={2} />
        </button>
      </div>
    {/if}
  </div>
  <div class="paste-cleanup-grid">
    <label>
      <span>Input</span>
      <textarea
        class="paste-cleanup-textarea"
        bind:value={input}
        aria-label="Paste cleanup input"
        spellcheck="true"
        placeholder="Paste text to clean"
      ></textarea>
    </label>
    <label>
      <span>Output</span>
      <textarea
        class="paste-cleanup-textarea"
        value={output}
        aria-label="Cleaned paste output"
        readonly
        spellcheck="false"
      ></textarea>
    </label>
    <label>
      <span>Reply</span>
      <textarea
        class="paste-cleanup-textarea"
        bind:value={replyDraft}
        aria-label="Paste cleanup reply draft"
        spellcheck="true"
        placeholder="Draft the reply to copy back"
      ></textarea>
    </label>
  </div>
  <div class="paste-cleanup-footer">
    <span>Cleaned {stats} · Reply {replyStats}</span>
    <button
      class="file-action-button"
      type="button"
      aria-label="Copy cleaned paste output"
      title="Copy cleaned paste output"
      disabled={output.trim().length === 0 || fileActionBusy === 'paste-copy'}
      onclick={onCopyOutput}
    >
      <Copy size={13} strokeWidth={1.9} />
      <span>{fileActionBusy === 'paste-copy' ? 'Copying' : 'Copy'}</span>
    </button>
    <button
      class="file-action-button"
      type="button"
      aria-label="Copy paste reply draft"
      title="Copy paste reply draft"
      disabled={replyOutput.trim().length === 0 || fileActionBusy === 'paste-reply-copy'}
      onclick={onCopyReplyDraft}
    >
      <Copy size={13} strokeWidth={1.9} />
      <span>{fileActionBusy === 'paste-reply-copy' ? 'Copying' : 'Copy Reply'}</span>
    </button>
  </div>
</div>

<style>
  /*
   * Base `.file-action-button` chrome lives once in `src/app.css` as a `:global`
   * rule shared across every surface; this panel's buttons inherit it directly.
   */
  .paste-cleanup-panel {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr) auto;
    gap: 10px;
    min-height: 0;
    overflow: hidden;
  }

  .paste-cleanup-top {
    display: grid;
    gap: 7px;
    min-width: 0;
  }

  .paste-cleanup-toolbar,
  .paste-cleanup-footer {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 7px;
    min-width: 0;
  }

  .paste-cleanup-toolbar label {
    display: inline-grid;
    grid-template-columns: auto minmax(74px, auto);
    align-items: center;
    gap: 7px;
    min-width: 0;
    height: 30px;
    padding: 0 8px;
    color: #9facaa;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.04);
    font-size: 10px;
    font-weight: 820;
  }

  .paste-cleanup-toolbar select {
    min-width: 0;
    color: #dffdf8;
    border: 0;
    outline: 0;
    background: transparent;
    font: inherit;
  }

  .paste-cleanup-history {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 5px;
    min-width: 0;
  }

  .paste-history-chip {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 24px;
    flex: 1 1 132px;
    align-items: center;
    min-width: 0;
    overflow: hidden;
    border: 1px solid rgba(92, 226, 207, 0.12);
    border-radius: 7px;
    background: rgba(92, 226, 207, 0.045);
  }

  .paste-history-chip.reply {
    border-color: rgba(216, 170, 85, 0.13);
    background: rgba(216, 170, 85, 0.045);
  }

  .paste-history-restore {
    display: grid;
    grid-template-columns: auto auto minmax(0, 1fr);
    align-items: center;
    gap: 5px;
    min-width: 0;
    height: 26px;
    padding: 0 7px;
    color: #cbd8d5;
    text-align: left;
    border: 0;
    background: transparent;
    cursor: pointer;
  }

  .paste-history-restore span {
    color: #8fd8cf;
    font-size: 8px;
    font-weight: 900;
    text-transform: uppercase;
    white-space: nowrap;
  }

  .paste-history-chip.reply .paste-history-restore span {
    color: #d8c385;
  }

  .paste-history-restore strong {
    min-width: 0;
    overflow: hidden;
    color: #dfe8e5;
    font-size: 10px;
    font-weight: 780;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .paste-history-copy,
  .paste-history-clear {
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    padding: 0;
    color: #96a39f;
    border: 0;
    border-radius: 6px;
    background: transparent;
    cursor: pointer;
  }

  .paste-history-clear {
    border: 1px solid rgba(255, 255, 255, 0.075);
    background: rgba(255, 255, 255, 0.035);
  }

  .paste-history-restore:hover,
  .paste-history-restore:focus-visible,
  .paste-history-copy:hover,
  .paste-history-copy:focus-visible,
  .paste-history-clear:hover,
  .paste-history-clear:focus-visible {
    color: #e8f6f2;
    outline: 0;
    background: rgba(92, 226, 207, 0.11);
  }

  .paste-cleanup-grid {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(150px, 1fr) minmax(150px, 1fr);
    gap: 9px;
    min-height: 0;
  }

  .paste-cleanup-grid label {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    gap: 5px;
    min-width: 0;
    min-height: 0;
  }

  .paste-cleanup-grid label > span,
  .paste-cleanup-footer span {
    color: #8d9995;
    font-size: 10px;
    font-weight: 800;
    text-transform: uppercase;
  }

  .paste-cleanup-textarea {
    width: 100%;
    min-width: 0;
    min-height: 0;
    padding: 10px;
    resize: none;
    color: #e7ecea;
    border: 1px solid rgba(255, 255, 255, 0.075);
    border-radius: 8px;
    outline: 0;
    background: rgba(0, 0, 0, 0.18);
    font: 12px/1.45 var(--font-mono);
  }

  .paste-cleanup-textarea:focus {
    border-color: rgba(92, 226, 207, 0.42);
    box-shadow: 0 0 0 2px rgba(92, 226, 207, 0.08);
  }

  .paste-cleanup-footer {
    justify-content: space-between;
  }
</style>
