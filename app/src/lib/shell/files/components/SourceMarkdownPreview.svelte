<script lang="ts" module>
  import type { Component, Snippet } from 'svelte';
  import { markedAlert, AlertRenderer } from '@humanspeak/svelte-markdown/extensions/alert';
  import { markedFootnote, FootnoteRef, FootnoteSection } from '@humanspeak/svelte-markdown/extensions/footnote';
  import { markedKatex } from '@humanspeak/svelte-markdown/extensions/katex';
  import { markedMermaid, MermaidRenderer } from '@humanspeak/svelte-markdown/extensions/mermaid';
  import {
    escapeHtml,
    HighlightedCode,
    HIGHLIGHT_CONTEXT_KEY,
    type CodeHighlighter
  } from '@humanspeak/svelte-markdown/extensions/highlight';
  import {
    allowHtmlOnly,
    defaultSanitizeAttributes,
    type RendererComponent,
    type Renderers,
    type SanitizeAttributesFn
  } from '@humanspeak/svelte-markdown';
  import { fenceLanguage, highlightCode } from '../../markdown/codeHighlight';
  import { withoutFetchingAttributes } from './editor/markdownPreview';

  // GitHub-flavoured extras the library ships. Created once so the parser's
  // token cache keys stay stable between renders.
  const extensions = [markedAlert(), markedFootnote(), markedMermaid(), markedKatex()];

  const renderers: Partial<Renderers & Record<'alert' | 'footnoteRef' | 'footnoteSection', RendererComponent>> = {
    code: HighlightedCode,
    alert: AlertRenderer,
    footnoteRef: FootnoteRef,
    footnoteSection: FootnoteSection,
    // Raw HTML in a file renders only the tags GitHub allows; forms, frames
    // and media do not.
    html: allowHtmlOnly([
      'a', 'abbr', 'b', 'blockquote', 'br', 'code', 'dd', 'del', 'details', 'div', 'dl', 'dt', 'em',
      'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'i', 'img', 'kbd', 'li', 'mark', 'ol', 'p',
      'pre', 's', 'samp', 'small', 'span', 'strong', 'sub', 'summary', 'sup', 'table',
      'tbody', 'td', 'tfoot', 'th', 'thead', 'tr', 'u', 'ul', 'var'
    ])
  };

  /** Raw HTML loses srcset, style and background before any tag renders, so a
   *  file cannot make the Preview fetch other addresses. */
  const sanitizeAttributes: SanitizeAttributesFn = (attributes, context, sanitizeUrl) =>
    defaultSanitizeAttributes(withoutFetchingAttributes(attributes), context, sanitizeUrl);

  /** The library's code renderer, coloured by the app's own highlighter
   *  (the one chat uses), which knows Rust, C#, Swift and the rest. An
   *  indented code block arrives with no language at all. */
  const highlighter: CodeHighlighter = {
    hasLang: (lang) => fenceLanguage(lang ?? '') !== 'plaintext',
    highlight: (code, lang) => {
      const lines = highlightCode(code, fenceLanguage(lang ?? '')).map((line) =>
        line.map((span) => span.className === 'plain'
          ? escapeHtml(span.value)
          : `<span class="${span.className}">${escapeHtml(span.value)}</span>`).join(''));
      return `<pre><code>${lines.join('\n')}</code></pre>`;
    }
  };

  /** Math is rare, so KaTeX and its stylesheet load the first time a file has some. */
  type KatexComponent = Component<{ text: string; displayMode?: boolean }>;
  let katexRenderer: Promise<KatexComponent> | null = null;
  /** Not async: `{#await}` gets the one cached promise, so a re-render never shows it pending again. */
  function loadKatex(): Promise<KatexComponent> {
    katexRenderer ??= importKatex();
    return katexRenderer;
  }

  async function importKatex(): Promise<KatexComponent> {
    const [module] = await Promise.all([
      import('@humanspeak/svelte-markdown/extensions/katex'),
      import('katex/dist/katex.min.css')
    ]);
    return module.KatexRenderer;
  }
</script>

<script lang="ts">
  import { onDestroy, onMount, setContext } from 'svelte';
  import SvelteMarkdown, {
    defaultRenderers,
    tokenCache,
    type ImageSnippetProps,
    type ListItemSnippetProps
  } from '@humanspeak/svelte-markdown';
  import { currentTheme } from '../../settings/themes/themeService';
  import {
    frontmatterRows,
    markdownImageTarget,
    rasterImageMimeType,
    splitFrontmatter,
    taskCheckboxOffsets,
    toggleTaskAt,
    type MarkdownBlockToken
  } from './editor/markdownPreview';
  import { readSourceImageFromTauri } from '../../../tauriSource';
  import { openUrlInBrowser } from '../../workbenchNavigation';

  /**
   * A Markdown file rendered by @humanspeak/svelte-markdown. It never rewrites
   * the file: the only edit it makes is a task checkbox click, which flips that
   * one character and goes through the same draft and save path as typing.
   */
  type Props = {
    /** The file on disk; local images are found from its folder. */
    path: string;
    /** The session's project; local images outside it are not read. */
    projectRoot: string | null;
    content: string;
    fileName: string;
    readOnly?: boolean;
    scrollTop?: number;
    onScroll?: (scrollTop: number) => void;
    onChange?: (content: string) => void;
    onSave?: () => void;
  };

  let { path, projectRoot, content, fileName, readOnly = false, scrollTop = 0, onScroll, onChange, onSave }: Props = $props();
  let host: HTMLElement;
  const Image = defaultRenderers.image;

  setContext(HIGHLIGHT_CONTEXT_KEY, highlighter);

  const parts = $derived(splitFrontmatter(content));
  const frontmatter = $derived(frontmatterRows(parts.frontmatter));
  const mermaidTheme = currentTheme().monaco.base === 'vs' ? 'default' : 'dark';

  /** Local images, read once per path while this view is open. */
  const localImages = new Map<string, Promise<string>>();
  /** Not async: `{#await}` gets the one cached promise, so a re-render never shows it pending again. */
  function localImageUrl(file: string): Promise<string> {
    let url = localImages.get(file);
    if (!url) {
      url = readLocalImage(file);
      localImages.set(file, url);
    }
    return url;
  }

  async function readLocalImage(file: string): Promise<string> {
    const type = rasterImageMimeType(file) ?? (file.toLowerCase().endsWith('.svg') ? 'image/svg+xml' : '');
    const bytes = await readSourceImageFromTauri(file);
    return URL.createObjectURL(new Blob([new Uint8Array(bytes)], { type }));
  }

  /** A read still in flight on destroy is revoked once it settles. */
  async function revokeLocalImage(url: Promise<string>): Promise<void> {
    try {
      URL.revokeObjectURL(await url);
    } catch {
      // the read failed, so there is no URL to free
    }
  }

  /** The tokens the library last rendered, and the text it parsed them from. */
  let rendered: { body: string; tokens: readonly MarkdownBlockToken[] } | null = null;

  // The click's own toggle stands: cancelling it would let the browser put the
  // old state back after Svelte had already drawn the new one.
  function toggleTask(event: MouseEvent): void {
    const box = event.currentTarget as HTMLInputElement;
    const boxes = [...host.querySelectorAll<HTMLInputElement>('input.md-task')];
    const offsets = rendered?.body === parts.body
      ? taskCheckboxOffsets(parts.body, rendered.tokens).map((offset) => offset + parts.frontmatter.length)
      : [];
    const next = toggleTaskAt(content, offsets, boxes.indexOf(box), boxes.length, box.checked);
    if (next === null) box.checked = !box.checked;
    else onChange?.(next);
  }

  function onKeyDown(event: KeyboardEvent): void {
    if (!(event.metaKey || event.ctrlKey) || event.key.toLowerCase() !== 's') return;
    event.preventDefault();
    onSave?.();
  }

  onMount(() => {
    host.scrollTop = scrollTop;
  });

  // A diagram mermaid fails to draw leaves its scratch element (`d` + the diagram id) on
  // <body>. Mermaid draws one diagram at a time, so only the newest can still be in use:
  // clear the older ones when the text changes and when Preview closes.
  $effect(() => {
    void content;
    return () => {
      const scratch = [...document.querySelectorAll('body > div[id^="dmermaid-"]')];
      for (const left of scratch.slice(0, -1)) left.remove();
    };
  });

  onDestroy(() => {
    for (const url of localImages.values()) void revokeLocalImage(url);
    // The library keeps parsed documents for reuse; leaving Preview frees them.
    tokenCache.clearAllTokens();
  });
</script>

<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
<section
  class="markdown-preview selectable"
  aria-label={`Preview ${fileName}`}
  tabindex="-1"
  bind:this={host}
  onscroll={() => onScroll?.(host.scrollTop)}
  onkeydown={onKeyDown}
>
  <article class="markdown-body">
    {#if frontmatter.length > 0}
      <table class="frontmatter">
        <tbody>
          {#each frontmatter as [key, value]}<tr><th>{key}</th><td>{value}</td></tr>{/each}
        </tbody>
      </table>
    {/if}
    <SvelteMarkdown
      source={parts.body}
      {extensions}
      {renderers}
      {sanitizeAttributes}
      parsed={(tokens) => { rendered = { body: parts.body, tokens }; }}
    >
      {#snippet listitem(props: ListItemSnippetProps)}
        <li class:task-item={props.task}>{#if props.task}<input
              type="checkbox"
              class="md-task"
              checked={props.checked}
              disabled={readOnly}
              aria-label="Done"
              onclick={toggleTask}
            />{/if}{@render props.children?.()}</li>
      {/snippet}
      {#snippet image(props: ImageSnippetProps)}
        {@render picture(props.href, props.text ?? '', props.title)}
      {/snippet}
      {#snippet html_img(props: { attributes?: Record<string, unknown> })}
        {@render picture(
          props.attributes?.src as string | undefined,
          String(props.attributes?.alt ?? ''),
          props.attributes?.title as string | undefined,
          {
            src: props.attributes?.src,
            width: props.attributes?.width,
            height: props.attributes?.height,
            alt: props.attributes?.alt,
            title: props.attributes?.title
          }
        )}
      {/snippet}
      {#snippet link(props: { href?: string; title?: string; children?: Snippet })}
        {@render anchor(props.href, props.title, props.children)}
      {/snippet}
      {#snippet html_a(props: { attributes?: Record<string, unknown>; children?: Snippet })}
        {@render anchor(
          props.attributes?.href as string | undefined,
          props.attributes?.title as string | undefined,
          props.children
        )}
      {/snippet}
      {#snippet mermaid(props: { text: string })}
        <MermaidRenderer text={props.text} lightTheme={mermaidTheme} darkTheme={mermaidTheme} />
      {/snippet}
      {#snippet inlineKatex(props: { text: string })}
        {#await loadKatex() then Katex}<Katex text={props.text} />{/await}
      {/snippet}
      {#snippet blockKatex(props: { text: string })}
        {#await loadKatex() then Katex}<Katex text={props.text} displayMode />{/await}
      {/snippet}
    </SvelteMarkdown>
  </article>
</section>

<!-- A raw HTML <img> keeps its own attributes (width, height); a Markdown
     image uses the library's image renderer. -->
{#snippet img(src: string, alt: string, title: string | undefined, attributes: Record<string, unknown> | undefined)}
  {#if attributes}<img {src} {alt} {title}
    width={attributes.width as string | undefined}
    height={attributes.height as string | undefined}
  />{:else}<Image href={src} text={alt} {title} />{/if}
{/snippet}

{#snippet picture(href: string | undefined, alt: string, title: string | undefined, attributes?: Record<string, unknown>)}
  {@const target = markdownImageTarget(path, projectRoot, href)}
  {#if target?.kind === 'https'}
    {@render img(target.url, alt, title, attributes)}
  {:else if target?.kind === 'file'}
    {#await localImageUrl(target.path) then url}
      {@render img(url, alt, title, attributes)}
    {:catch}
      <span class="missing-image" title={target.path}>{alt || target.path}</span>
    {/await}
  {:else if target?.kind === 'http'}
    <!-- Plain http images are not fetched; the address stays one click away. -->
    <a href={target.url} target="_blank" rel="noreferrer">{alt || target.url}</a>
  {:else if alt}
    <span class="missing-image">{alt}</span>
  {/if}
{/snippet}

{#snippet anchor(href: string | undefined, title: string | undefined, children: Snippet | undefined)}
  <!-- A web link goes through the app's link handler (pull requests, tasks, the
       in-app browser); #anchors move within the page; anything else opens outside. -->
  {#if href && /^https?:/i.test(href)}
    <a {href} {title} onclick={(event) => { event.preventDefault(); void openUrlInBrowser({ url: href }); }}>{@render children?.()}</a>
  {:else}
    <a {href} {title} target={href?.startsWith('#') ? undefined : '_blank'} rel="noreferrer">{@render children?.()}</a>
  {/if}
{/snippet}

<style>
  .markdown-preview {
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: auto;
    /* Raw HTML may carry a style attribute; containment keeps anything it
       positions or paints (even position: fixed) inside the preview. */
    contain: layout paint;
    outline: none;
    color: var(--color-text);
    background: var(--color-bg);
    scrollbar-width: thin;
    scrollbar-color: var(--scrollbar-thumb) transparent;
  }

  .markdown-body {
    max-width: 920px;
    margin: 0 auto;
    padding: var(--space-5) var(--space-6) var(--space-6);
    font-size: var(--text-body);
    line-height: 1.6;
    overflow-wrap: break-word;
  }

  .markdown-body > :global(:first-child) { margin-top: 0; }
  .markdown-body > :global(:last-child) { margin-bottom: 0; }

  .markdown-body :global(:is(p, ul, ol, dl, blockquote, pre, table, details)) {
    margin: 0 0 var(--space-4);
  }

  /* Headings */
  .markdown-body :global(:is(h1, h2, h3, h4, h5, h6)) {
    margin: var(--space-5) 0 var(--space-3);
    color: var(--color-text);
    font-weight: var(--text-heading-weight);
    line-height: 1.25;
  }

  .markdown-body :global(h1) {
    padding-bottom: var(--space-2);
    border-bottom: 1px solid var(--color-border);
    font-size: 26px;
  }

  .markdown-body :global(h2) {
    padding-bottom: var(--space-1);
    border-bottom: 1px solid var(--color-border);
    font-size: 20px;
  }

  .markdown-body :global(h3) { font-size: 17px; }
  .markdown-body :global(h4) { font-size: var(--text-body); }
  .markdown-body :global(:is(h5, h6)) { font-size: var(--text-quiet); }
  .markdown-body :global(h6) { color: var(--color-text-2); }

  /* Text */
  .markdown-body :global(a) {
    color: var(--color-accent);
    text-decoration: none;
  }

  .markdown-body :global(a:hover) { text-decoration: underline; }
  .markdown-body :global(:is(strong, b)) { font-weight: 650; }

  .markdown-body :global(hr) {
    height: 2px;
    margin: var(--space-5) 0;
    border: 0;
    background: var(--color-border);
  }

  .markdown-body :global(kbd) {
    padding: 1px 6px;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    font: 12px var(--font-mono);
  }

  /* Lists */
  .markdown-body :global(:is(ul, ol)) { padding-left: 2em; }
  .markdown-body :global(:is(ul, ol) :is(ul, ol)) { margin-bottom: 0; }
  .markdown-body :global(li + li) { margin-top: var(--space-1); }
  .markdown-body :global(li > p) { margin-bottom: var(--space-2); }
  .markdown-body :global(li.task-item) { list-style: none; }

  .markdown-body :global(input.md-task) {
    width: 14px;
    height: 14px;
    margin: 0 0.5em 0.2em -1.5em;
    vertical-align: middle;
    accent-color: var(--color-accent);
    cursor: pointer;
  }

  .markdown-body :global(input.md-task:disabled) { cursor: default; }
  .markdown-body :global(input.md-task + p) { display: inline; }

  /* Quotes */
  .markdown-body :global(blockquote) {
    padding: 0 1em;
    border-left: 4px solid var(--color-border);
    color: var(--color-text-2);
  }

  .markdown-body :global(blockquote > :last-child) { margin-bottom: 0; }

  /* Code */
  .markdown-body :global(code) {
    padding: 0.15em 0.4em;
    border-radius: var(--radius-sm);
    background: var(--color-elevated);
    font-family: var(--font-mono);
    font-size: 0.86em;
  }

  .markdown-body :global(pre) {
    overflow: auto;
    padding: var(--space-4);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    font: 13px/1.5 var(--font-mono);
    white-space: pre;
  }

  .markdown-body :global(pre code) {
    padding: 0;
    border-radius: 0;
    background: transparent;
    font: inherit;
  }

  .markdown-body :global(pre .keyword) { color: var(--color-accent); }
  .markdown-body :global(pre .string) { color: var(--color-good); }
  .markdown-body :global(pre .comment) { color: var(--color-text-3); font-style: italic; }
  .markdown-body :global(pre .number) { color: var(--color-attention); }
  .markdown-body :global(pre .type) { color: var(--color-live); }

  /* Tables */
  .markdown-body :global(table) {
    display: block;
    width: max-content;
    max-width: 100%;
    overflow: auto;
    border-collapse: collapse;
    border-spacing: 0;
  }

  .markdown-body :global(:is(th, td)) {
    padding: 6px 13px;
    border: 1px solid var(--color-border);
    text-align: left;
    vertical-align: top;
  }

  .markdown-body :global(th) {
    background: var(--color-surface);
    font-weight: 600;
  }

  .markdown-body :global(tbody tr:nth-child(2n)) {
    background: color-mix(in srgb, var(--color-surface) 55%, transparent);
  }

  .markdown-body :global(:is(th, td)[align='center']) { text-align: center; }
  .markdown-body :global(:is(th, td)[align='right']) { text-align: right; }

  /* Front matter */
  .frontmatter { font-size: var(--text-quiet); }

  .frontmatter th {
    color: var(--color-text-2);
    white-space: nowrap;
  }

  .frontmatter td { white-space: pre-wrap; }

  /* Images */
  .markdown-body :global(img) {
    max-width: 100%;
    height: auto;
  }

  .missing-image {
    color: var(--color-text-3);
    font-style: italic;
  }

  /* GitHub alerts */
  .markdown-body :global(.markdown-alert) {
    margin: 0 0 var(--space-4);
    padding: var(--space-2) var(--space-4);
    border-left: 4px solid var(--alert-color, var(--color-border));
  }

  .markdown-body :global(.markdown-alert > :last-child) { margin-bottom: 0; }

  .markdown-body :global(.markdown-alert-title) {
    margin-bottom: var(--space-1);
    color: var(--alert-color);
    font-weight: 600;
  }

  .markdown-body :global(.markdown-alert-note) { --alert-color: var(--color-live); }
  .markdown-body :global(.markdown-alert-tip) { --alert-color: var(--color-good); }
  .markdown-body :global(.markdown-alert-important) { --alert-color: var(--color-accent); }
  .markdown-body :global(.markdown-alert-warning) { --alert-color: var(--color-attention); }
  .markdown-body :global(.markdown-alert-caution) { --alert-color: var(--color-bad); }

  /* Footnotes, diagrams, details */
  .markdown-body :global(.footnote-ref a) { font-size: 0.8em; }

  .markdown-body :global(.footnotes) {
    margin-top: var(--space-5);
    padding-top: var(--space-3);
    border-top: 1px solid var(--color-border);
    color: var(--color-text-2);
    font-size: var(--text-quiet);
  }

  .markdown-body :global(.mermaid-diagram) {
    margin: 0 0 var(--space-4);
    text-align: center;
  }

  .markdown-body :global(:is(.mermaid-loading, .mermaid-error)) {
    color: var(--color-text-3);
    font-size: var(--text-quiet);
  }

  .markdown-body :global(summary) { cursor: pointer; }
</style>
