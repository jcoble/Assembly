<script lang="ts">
  /**
   * BrowserPanel.svelte — the embedded "Browser" dock (a localhost preview frame).
   *
   * Presentational only: it renders the dock chrome (header, URL form, runtime
   * shortcuts, the preview iframe) and emits every action via callbacks. The page
   * owns all browser state (`browserUrl`/`browserInputUrl`/`browserFrameKey`/
   * `browserError`, the `activeBrowserUrl` derived) and the URL-mutating functions;
   * this component holds NO `$state` of its own.
   *
   * Teleport: the root `<section>` keeps `use:panelAction={'browser'}`. The action
   * itself stays in the page (it wires the Dockview teleport bridge + terminal
   * hooks) and is passed in as the `panelAction` prop.
   */
  import { Network, RefreshCw, ExternalLink, X } from '@lucide/svelte';
  import type { Action } from 'svelte/action';
  import type { RuntimeContext } from '../../tauriSource.js';
  import type { SourceDockPanelID } from '../../sourceDockLayout.js';

  interface Props {
    /** The URL currently shown in the frame (= page `activeBrowserUrl`). Read-only. */
    url: string;
    /** Bindable URL input text (page owns `browserInputUrl`). */
    inputUrl?: string;
    /** Bumped to force-remount the iframe (page owns `browserFrameKey`). */
    frameKey: number;
    /** Error string to surface above the frame; empty hides the banner. */
    error: string;
    /** Runtime shortcuts for the selected project (= `selectedProjectRuntimeContexts`). */
    runtimeContexts: RuntimeContext[];
    /** Maps a runtime context to its preview URL (page helper). */
    runtimeContextUrl: (context: RuntimeContext) => string;
    /** Dockview teleport action — kept on the root node, supplied by the page. */
    panelAction: Action<HTMLElement, SourceDockPanelID>;
    onSubmit: (event: SubmitEvent) => void;
    onReload: () => void;
    onOpenExternal: () => void;
    onHide: () => void;
    onOpenRuntimeContext: (context: RuntimeContext) => void;
  }

  let {
    url,
    inputUrl = $bindable(''),
    frameKey,
    error,
    runtimeContexts,
    runtimeContextUrl,
    panelAction,
    onSubmit,
    onReload,
    onOpenExternal,
    onHide,
    onOpenRuntimeContext,
  }: Props = $props();
</script>

<section class="browser-dock" aria-label="Browser dock" use:panelAction={'browser'}>
  <header class="browser-dock-header">
    <div class="browser-dock-title">
      <Network size={14} strokeWidth={2} />
      <strong>Browser</strong>
      <span>{url || 'No runtime URL'}</span>
    </div>
    <div class="browser-dock-actions">
      <button
        class="file-action-button icon-only"
        type="button"
        aria-label="Reload browser dock"
        title="Reload browser dock"
        disabled={!url}
        onclick={onReload}
      >
        <RefreshCw size={13} strokeWidth={2} />
      </button>
      <button
        class="file-action-button icon-only"
        type="button"
        aria-label="Open browser URL externally"
        title="Open browser URL externally"
        disabled={!url}
        onclick={onOpenExternal}
      >
        <ExternalLink size={13} strokeWidth={2} />
      </button>
      <button
        class="file-action-button icon-only"
        type="button"
        aria-label="Hide browser dock"
        title="Hide browser dock"
        onclick={onHide}
      >
        <X size={13} strokeWidth={2} />
      </button>
    </div>
  </header>

  <form class="browser-url-form" onsubmit={onSubmit}>
    <input
      bind:value={inputUrl}
      aria-label="Browser dock URL"
      autocomplete="off"
      spellcheck="false"
      placeholder="localhost:5177"
    />
    <button
      class="file-action-button"
      type="submit"
      disabled={!inputUrl.trim()}
    >
      <Network size={13} strokeWidth={2} />
      <span>Open</span>
    </button>
  </form>

  {#if runtimeContexts.length > 0}
    <div class="browser-runtime-list" aria-label="Browser runtime shortcuts">
      {#each runtimeContexts as context (`browser:${context.pid}:${context.port}:${context.cwd}`)}
        <button
          type="button"
          class:active={url === runtimeContextUrl(context)}
          title={runtimeContextUrl(context)}
          onclick={() => onOpenRuntimeContext(context)}
        >
          <span>:{context.port}</span>
          <strong>{context.command}</strong>
          <small>{context.rootLabel}</small>
        </button>
      {/each}
    </div>
  {/if}

  {#if error}
    <div class="browser-error">{error}</div>
  {/if}

  {#if url}
    <div class="browser-frame-wrap">
      {#key `${frameKey}:${url}`}
        <iframe
          class="browser-frame"
          title="Browser dock preview"
          src={url}
          sandbox="allow-downloads allow-forms allow-modals allow-popups allow-same-origin allow-scripts"
          referrerpolicy="no-referrer"
        ></iframe>
      {/key}
    </div>
  {:else}
    <div class="browser-empty">Start a runtime or enter a localhost URL.</div>
  {/if}
</section>

<style>
  /*
   * Base `.file-action-button` chrome lives once in `src/app.css` as a `:global`
   * rule shared across every surface. The compound overrides below
   * (`.browser-dock-actions .file-action-button`, `.browser-url-form
   * .file-action-button`) stay scoped here — they qualify the button with this
   * component's own ancestors and layer on top of the global base.
   */
  .browser-dock {
    display: grid;
    grid-template-rows: auto auto auto minmax(0, 1fr);
    flex: 1 1 auto;
    gap: 6px;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    max-height: none;
    margin-top: 0;
    overflow: hidden;
    padding: 7px;
    border: 1px solid rgba(255, 255, 255, 0.105);
    border-radius: 8px;
    background: rgba(15, 18, 18, 0.92);
  }

  .browser-dock-header,
  .browser-url-form {
    display: grid;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }

  .browser-dock-header {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .browser-dock-title,
  .browser-dock-actions {
    display: inline-flex;
    align-items: center;
    min-width: 0;
  }

  .browser-dock-title {
    gap: 6px;
    color: #dce4e2;
  }

  .browser-dock-title strong,
  .browser-dock-title span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .browser-dock-title strong {
    font-size: 11px;
    font-weight: 860;
  }

  .browser-dock-title span {
    color: #8d9995;
    font-size: 10px;
    font-weight: 760;
  }

  .browser-dock-actions {
    justify-content: end;
    gap: 5px;
  }

  .browser-dock-actions .file-action-button {
    display: inline-grid;
    place-items: center;
    width: 28px;
    height: 24px;
    min-width: 28px;
    padding: 0;
    border-radius: 6px;
  }

  .browser-url-form {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .browser-url-form input {
    width: 100%;
    height: 28px;
    min-width: 0;
    padding: 0 9px;
    color: #e6efec;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.045);
    font-family: var(--font-mono);
    font-size: 11px;
    font-weight: 720;
  }

  .browser-url-form input:focus-visible {
    outline: 1px solid rgba(92, 226, 207, 0.52);
    outline-offset: 1px;
  }

  .browser-url-form .file-action-button {
    display: inline-flex;
    width: auto;
    height: 28px;
    gap: 5px;
    padding: 0 9px;
    border-radius: 6px;
    font-size: 10px;
    font-weight: 820;
  }

  .browser-runtime-list {
    display: flex;
    gap: 5px;
    min-width: 0;
    overflow-x: auto;
    padding-bottom: 2px;
    scrollbar-width: thin;
  }

  .browser-runtime-list button {
    display: inline-grid;
    grid-template-columns: auto minmax(0, auto) auto;
    align-items: center;
    flex: 0 0 auto;
    gap: 6px;
    max-width: 220px;
    height: 26px;
    min-width: 0;
    padding: 0 8px;
    color: #b7c3bf;
    border: 1px solid rgba(255, 255, 255, 0.07);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.035);
  }

  .browser-runtime-list button.active {
    color: #dffdf8;
    border-color: rgba(92, 226, 207, 0.28);
    background: rgba(92, 226, 207, 0.1);
  }

  .browser-runtime-list span,
  .browser-runtime-list small {
    color: #72e2cf;
    font-family: var(--font-mono);
    font-size: 10px;
    font-weight: 820;
  }

  .browser-runtime-list strong,
  .browser-runtime-list small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .browser-runtime-list strong {
    font-size: 10px;
    font-weight: 820;
  }

  .browser-runtime-list small {
    max-width: 82px;
    color: #8d9995;
  }

  .browser-frame-wrap {
    min-height: 128px;
    min-width: 0;
    height: 100%;
    overflow: hidden;
    border: 1px solid rgba(92, 226, 207, 0.11);
    border-radius: 7px;
    background: rgba(8, 11, 11, 0.72);
  }

  .browser-frame {
    display: block;
    width: 100%;
    height: 100%;
    min-height: 128px;
    border: 0;
    background: #101414;
  }

  .browser-empty,
  .browser-error {
    display: grid;
    place-items: center;
    min-height: 54px;
    color: #798481;
    border: 1px dashed rgba(255, 255, 255, 0.08);
    border-radius: 6px;
    font-size: 10px;
    font-weight: 760;
  }

  .browser-error {
    min-height: 28px;
    color: #d8aa55;
  }
</style>
