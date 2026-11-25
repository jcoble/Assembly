<!--
  BrowserPreviewPane.svelte — side-by-side embedded browser preview surface for secondary browser tabs.

  Renders an address/reload toolbar and a live `<iframe>` beside native GPUIKit Chat without
  disturbing `#native-chat-slot` or crossing feature boundaries.
-->
<script lang="ts">
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import Globe from '@lucide/svelte/icons/globe';
  import RotateCw from '@lucide/svelte/icons/rotate-cw';
  import X from '@lucide/svelte/icons/x';

  interface Props {
    /** Currently loaded http(s) URL, or '' for a blank new browser tab. */
    url: string;
    /** Tab title for iframe accessibility. */
    title: string;
    /** Incremented by the host when re-triggering preview for the same URL to force reload. */
    reloadTick?: number;
    /** Called with a validated http(s) URL when the user submits a new address. */
    onNavigate: (url: string) => void;
    /** Closes the active browser secondary tab. */
    onClose: () => void;
  }
  let { url, title, reloadTick = 0, onNavigate, onClose }: Props = $props();

  let draftUrl = $state('');
  let localReloadSeq = $state(0);
  let urlError = $state<string | null>(null);

  function parseHttpUrl(raw: string): string | null {
    const trimmed = raw.trim();
    if (!trimmed) return null;
    try {
      const parsed = new URL(trimmed);
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
      return parsed.href;
    } catch {
      return null;
    }
  }

  const activeUrl = $derived(parseHttpUrl(url));
  const frameKey = $derived(`${activeUrl ?? ''}::${reloadTick}::${localReloadSeq}`);

  $effect(() => {
    draftUrl = url;
    urlError = null;
  });

  function submitAddress(event: SubmitEvent): void {
    event.preventDefault();
    const trimmed = draftUrl.trim();
    if (!trimmed) {
      urlError = 'Enter an http:// or https:// URL.';
      return;
    }
    const candidate = /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`;
    const valid = parseHttpUrl(candidate);
    if (!valid) {
      urlError = 'Only http:// and https:// URLs can be previewed.';
      return;
    }
    urlError = null;
    if (valid === activeUrl) {
      localReloadSeq += 1;
    } else {
      onNavigate(valid);
    }
  }
</script>

<div class="browser-pane" data-testid="browser-preview-pane">
  <header class="browser-toolbar">
    <button
      type="button"
      class="tool-btn"
      title="Reload preview"
      aria-label="Reload preview"
      disabled={!activeUrl}
      onclick={() => (localReloadSeq += 1)}
    >
      <RotateCw size={14} />
    </button>

    <form class="address-form" onsubmit={submitAddress}>
      <Globe size={13} class="address-icon" aria-hidden="true" />
      <input
        type="text"
        bind:value={draftUrl}
        placeholder="http://localhost:5173"
        aria-label="Preview URL"
        autocomplete="off"
        spellcheck="false"
        class="address-input"
      />
    </form>

    {#if activeUrl}
      <a
        href={activeUrl}
        target="_blank"
        rel="noopener noreferrer"
        class="tool-btn"
        title="Open in external window"
        aria-label="Open in external window"
      >
        <ExternalLink size={14} />
      </a>
    {/if}

    <button
      type="button"
      class="tool-btn"
      title="Close preview tab"
      aria-label="Close preview tab"
      onclick={onClose}
    >
      <X size={14} />
    </button>
  </header>

  {#if urlError}
    <p class="url-error" role="alert">{urlError}</p>
  {/if}

  <div class="browser-body">
    {#if activeUrl}
      {#key frameKey}
        <iframe
          src={activeUrl}
          title={title || activeUrl}
          class="preview-frame"
          referrerpolicy="no-referrer-when-downgrade"
        ></iframe>
      {/key}
    {:else}
      <div class="browser-empty">
        <Globe size={24} class="empty-glyph" aria-hidden="true" />
        <p class="empty-title">Browser preview</p>
        <p class="empty-copy">
          Enter an <span class="mono">http://</span> or <span class="mono">https://</span> URL above
          to load a live preview beside Chat.
        </p>
      </div>
    {/if}
  </div>
</div>

<style>
  .browser-pane {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    background: var(--color-surface-solid);
  }

  .browser-toolbar {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 38px;
    padding: 0 8px;
    border-bottom: 1px solid var(--color-border);
    flex-shrink: 0;
  }

  .tool-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    border: none;
    border-radius: 6px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
    flex-shrink: 0;
    text-decoration: none;
  }

  .tool-btn:hover:not(:disabled) {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .tool-btn:disabled {
    opacity: 0.4;
    cursor: default;
  }

  .address-form {
    display: flex;
    align-items: center;
    gap: 6px;
    flex: 1;
    min-width: 0;
    height: 26px;
    padding: 0 8px;
    border-radius: 6px;
    border: 1px solid var(--color-field-border);
    background: rgba(255, 255, 255, 0.04);
    color: var(--color-text-3);
  }

  .address-form:focus-within {
    border-color: var(--focus-border);
  }

  .address-input {
    flex: 1;
    min-width: 0;
    border: none;
    background: transparent;
    color: var(--color-text);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 12px;
    outline: none;
  }

  .url-error {
    margin: 0;
    padding: 6px 10px;
    border-bottom: 1px solid var(--color-border);
    color: var(--color-bad);
    font-size: 12px;
    flex-shrink: 0;
  }

  .browser-body {
    flex: 1;
    min-width: 0;
    min-height: 0;
    display: flex;
    position: relative;
  }

  .preview-frame {
    width: 100%;
    height: 100%;
    border: none;
    background: #ffffff;
  }

  .browser-empty {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 6px;
    padding: 24px;
    text-align: center;
    color: var(--color-text-2);
  }

  .empty-title {
    margin: 0;
    color: var(--color-text);
    font-size: 13px;
    font-weight: 500;
  }

  .empty-copy {
    margin: 0;
    max-width: 36ch;
    color: var(--color-text-3);
    font-size: 12px;
  }

  .mono {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }
</style>
