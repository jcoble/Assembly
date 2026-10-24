<script lang="ts" module>
  let sharedObserver: IntersectionObserver | null = null;
  const observerCallbacks = new Map<Element, () => void>();

  function releaseSharedObserverIfIdle(): void {
    if (observerCallbacks.size > 0) return;
    sharedObserver?.disconnect();
    sharedObserver = null;
  }

  function getSharedObserver(): IntersectionObserver | null {
    if (typeof IntersectionObserver === 'undefined') return null;
    if (!sharedObserver) {
      sharedObserver = new IntersectionObserver((entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            const cb = observerCallbacks.get(entry.target);
            if (cb) {
              observerCallbacks.delete(entry.target);
              sharedObserver?.unobserve(entry.target);
              cb();
              releaseSharedObserverIfIdle();
            }
          }
        }
      }, { rootMargin: '400px' });
    }
    return sharedObserver;
  }

  function observeCodeBlock(el: Element, onIntersect: () => void): () => void {
    const obs = getSharedObserver();
    if (!obs) {
      onIntersect();
      return () => {};
    }
    observerCallbacks.set(el, onIntersect);
    obs.observe(el);
    return () => {
      observerCallbacks.delete(el);
      obs.unobserve(el);
      releaseSharedObserverIfIdle();
    };
  }
</script>

<script lang="ts">
  import Check from '@lucide/svelte/icons/check';
  import Copy from '@lucide/svelte/icons/copy';
  import WrapText from '@lucide/svelte/icons/wrap-text';
  import {
    cachedHighlight,
    highlightCode,
    fenceLanguage,
    plainHighlightedLines,
    type HighlightedLine
  } from '../../markdown/codeHighlight.ts';

  interface Props {
    value: string;
    /** The fence's info string, e.g. `ts` or `bash`. */
    info?: string;
  }

  let { value, info = '' }: Props = $props();

  const language = $derived(fenceLanguage(info));
  /** The fence's info string names the block; it is one token or nothing. */
  const label = $derived(info.trim() || 'code');
  /** Long lines scroll sideways until someone asks for them to wrap. */
  let wrapped = $state(false);
  let copied = $state(false);
  let host = $state<HTMLDivElement | null>(null);
  let onScreen = $state(false);
  /** Plain until the block is worth scanning, so the code is readable at once; a block
   *  coloured before, scrolled back into view, is drawn coloured straight away. */
  const lines = $derived(
    onScreen ? highlightCode(value, language) : cachedHighlight(value, language) ?? plainHighlightedLines(value)
  );

  // A block is only scanned once a person can actually see it. A transcript
  // scrolled past a hundred prose messages never pays for the ones above.
  $effect(() => {
    if (!host || onScreen) return;
    return observeCodeBlock(host, () => {
      onScreen = true;
    });
  });

  async function copyCode(): Promise<void> {
    if (typeof navigator === 'undefined' || !navigator.clipboard) return;
    await navigator.clipboard.writeText(value);
    copied = true;
  }
</script>

<div class="code-wrap" data-testid="conversation-code-block" bind:this={host}>
  <div class="code-meta">
    <span class="code-language">{label}</span>
    <button
      class="meta-action"
      data-testid="wrap-conversation-code"
      type="button"
      aria-pressed={wrapped}
      onclick={() => (wrapped = !wrapped)}
    >
      <WrapText size={13} strokeWidth={1.8} />{wrapped ? 'No wrap' : 'Wrap'}
    </button>
    <button class="meta-action" data-testid="copy-conversation-code" type="button" onclick={() => void copyCode()}>
      {#if copied}<Check size={13} strokeWidth={2.2} />Copied{:else}<Copy size={13} strokeWidth={1.8} />Copy{/if}
    </button>
  </div>
  <pre class:wrapped data-testid="conversation-code-body"><code>{#each lines as line, index}{#if index > 0}{'\n'}{/if}{#each line as span}<span class={span.className}>{span.value}</span>{/each}{/each}</code></pre>
</div>

<style>
  /* One panel, one border, one radius. The header and the code used to carry a
     border each and meet at a seam; now the box owns the edge and clips both,
     so a fence reads as a single object sitting on the page. */
  .code-wrap{overflow:hidden;margin:12px 0;border:1px solid color-mix(in srgb,var(--color-border) 70%,transparent);border-radius:10px;background:color-mix(in srgb,var(--color-surface) 34%,var(--color-bg))}
  .code-wrap:first-child{margin-top:0}
  .code-wrap:last-child{margin-bottom:0}
  /* The header names the block and carries its two utilities; the name takes
     the room, the actions sit at the end. It is barely lighter than the code
     below it, so the code stays the thing being read and the strip reads as a
     caption rather than a second slab. */
  .code-meta{display:flex;align-items:center;gap:4px;min-height:30px;padding:3px 8px 3px 12px;border-bottom:1px solid color-mix(in srgb,var(--color-border) 45%,transparent);background:color-mix(in srgb,var(--color-surface) 22%,transparent);color:var(--color-text-3);font-size:12px}
  .code-language{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-family:var(--font-mono);letter-spacing:.03em}
  .meta-action{display:inline-flex;align-items:center;gap:4px;min-height:24px;border:0;border-radius:6px;background:transparent;color:inherit;padding:2px 8px;font:inherit;cursor:pointer}
  .meta-action:hover{background:color-mix(in srgb,var(--color-hover) 70%,transparent);color:var(--color-text)}
  .meta-action:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:1px}
  /* A line too long for the measure scrolls here and nowhere else. The pane
     around it never moves sideways. The bar is the thin one the transcript
     uses, so it does not read as a second scrollbar for the page. */
  pre{max-height:360px;overflow:auto;margin:0;padding:12px;font:13px/1.6 var(--font-mono);scrollbar-width:thin;scrollbar-color:var(--scrollbar-thumb) transparent;overscroll-behavior-x:contain}
  pre.wrapped{white-space:pre-wrap;overflow-wrap:anywhere}
  /* The browser gives <code> a face of its own — plain `monospace`, which on
     a Mac is Courier — so the code inside the box was not drawing in the face
     the box asked for. It takes the box's. */
  code{font:inherit;user-select:text;-webkit-user-select:text}
  .keyword{color:var(--color-accent)}
  .string{color:var(--color-good)}
  .comment{color:var(--color-text-3);font-style:italic}
  .number{color:var(--color-attention)}
  .type{color:var(--color-live)}
  .plain{color:inherit}
  @media (prefers-reduced-motion:no-preference){
    .meta-action{transition:background .14s ease,color .14s ease}
  }
</style>
