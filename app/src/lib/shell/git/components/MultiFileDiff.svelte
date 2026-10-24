<script lang="ts">
  /**
   * MultiFileDiff.svelte — several files' changes stacked in one scrolling
   * column, with the changed-file tree on the right.
   *
   * It only draws. The caller hands it the files (the same `SourceGitDiff`
   * record every diff read already returns) and decides where they came from.
   *
   * Cheap by construction: no editor instance anywhere. A file's lines are not
   * put in the page until its section comes within a screen of the viewport —
   * until then it is an empty box of the right height, so the scrollbar and the
   * tree's jumps stay true — and once in the page, off-screen sections skip
   * layout and paint (`content-visibility`).
   */
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ChevronsUpDown from '@lucide/svelte/icons/chevrons-up-down';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import Eye from '@lucide/svelte/icons/eye';
  import EyeOff from '@lucide/svelte/icons/eye-off';
  import Plus from '@lucide/svelte/icons/plus';
  import { onDestroy, type Snippet } from 'svelte';
  import { Button } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import FileIcon from '$lib/shell/components/explorer/FileIcon.svelte';
  import {
    hasGrammar,
    highlightSource,
    languageForPath,
    plainHighlightedLines,
    type HighlightedLine
  } from '$lib/shell/markdown/codeHighlight';
  import type { DiffLine } from '$lib/shell/git/parseUnifiedDiff';
  import { changedFileTree, parsedDiffOf, splitRows, unifiedRows, type DiffRow, type SplitRow } from '$lib/shell/git/diffRows';
  import type { DiffMode } from '$lib/shell/sessionWorkspaces';
  import type { SourceGitDiff } from '$lib/tauriSource';

  interface Props {
    files: readonly SourceGitDiff[];
    mode: DiffMode;
    /** The file to bring into view, e.g. the one picked in Source Control. */
    focusPath?: string;
    /** Open the file itself at a line. Absent when there is no file on disk to open. */
    onOpenLine?: (relativePath: string, line: number | null) => void;
    /** Read the file's full current text so a collapsed run of lines can open.
     * Absent when each file already carries its text or none can be had. */
    onLoadFullText?: (relativePath: string) => void;
    /** Files marked viewed: their lines fold away and their tree row dims.
     * Bind it to keep the marks beyond this component's life. */
    viewed?: Record<string, true>;
    /** Start a comment on a line: LEFT is the old side, RIGHT the new one.
     * When present, hovering a line offers a button for it. */
    onLineComment?: (relativePath: string, side: 'LEFT' | 'RIGHT', line: number) => void;
    /** Drawn under a file's lines, e.g. the comment being written on it. */
    fileFooter?: Snippet<[string]>;
  }
  let { files, mode, focusPath = '', onOpenLine, onLoadFullText, viewed = $bindable({}), onLineComment, fileFooter }: Props = $props();

  /** Long files are cut so one huge diff cannot stall the tab. */
  const MAX_ROWS = 2000;
  const LINE_PX = 20;
  const SIDES = ['left', 'right'] as const;
  const BAR_PX = 28;

  let filter = $state('');
  let activePath = $state('');
  let closedFolders = $state<Record<string, true>>({});
  let mounted = $state<Record<string, true>>({});
  let expanded = $state<Record<string, string[]>>({});
  let textRequested = $state<Record<string, true>>({});
  const sections: Record<string, HTMLElement> = {};
  let scroller = $state<HTMLElement | null>(null);

  const byPath = $derived(new Map(files.map((file) => [file.relativePath, file])));
  const shownPaths = $derived.by(() => {
    const needle = filter.trim().toLowerCase();
    return files.map((file) => file.relativePath).filter((path) => !needle || path.toLowerCase().includes(needle));
  });
  const tree = $derived(changedFileTree(shownPaths));
  /** Sections run in the tree's order, so the list and the tree read alike. */
  const ordered = $derived(tree.filter((entry) => entry.kind === 'file').map((entry) => byPath.get(entry.path)!));
  const visibleTree = $derived(
    tree.filter((entry) => !Object.keys(closedFolders).some((folder) => entry.path.startsWith(`${folder}/`)))
  );

  function fullLines(file: SourceGitDiff): string[] | null {
    if (file.modifiedContent === null || file.modifiedContent === undefined) return null;
    const lines = file.modifiedContent.replace(/\r\n/g, '\n').split('\n');
    if (lines.at(-1) === '') lines.pop();
    return lines;
  }

  function rowsOf(file: SourceGitDiff): DiffRow[] {
    const canLoad = Boolean(onLoadFullText) && !textRequested[file.relativePath];
    return unifiedRows(parsedDiffOf(file), fullLines(file), new Set(expanded[file.relativePath] ?? []), canLoad).slice(0, MAX_ROWS);
  }

  /** Colour each unbroken run of shown lines in one pass, so a comment or
   * string spanning lines keeps its colour; a collapsed bar starts a fresh
   * scan, so an unclosed comment cannot paint past it. A scan that does not
   * come back one line per row is dropped for plain text. */
  function spansOf(rows: readonly DiffRow[], language: string): Map<DiffLine, HighlightedLine> {
    const spans = new Map<DiffLine, HighlightedLine>();
    let run: DiffLine[] = [];
    const flush = () => {
      const source = run.map((line) => line.text).join('\n');
      let scanned = hasGrammar(language) ? highlightSource(source, language) : plainHighlightedLines(source);
      if (scanned.length !== run.length) scanned = plainHighlightedLines(source);
      run.forEach((line, index) => spans.set(line, scanned[index] ?? []));
      run = [];
    };
    for (const row of rows) {
      if (row.kind !== 'line') flush();
      else if (row.line.kind !== 'note') run.push(row.line);
    }
    flush();
    return spans;
  }

  function heightOf(rows: readonly (DiffRow | SplitRow)[]): number {
    return rows.reduce((total, row) => total + (row.kind === 'gap' ? BAR_PX : row.kind === 'label' ? 24 : LINE_PX), 0);
  }

  function openGap(file: SourceGitDiff, key: string): void {
    const path = file.relativePath;
    expanded[path] = [...(expanded[path] ?? []), key];
    if (fullLines(file) === null && onLoadFullText && !textRequested[path]) {
      textRequested[path] = true;
      onLoadFullText(path);
    }
  }

  /** Forget opened bars and text reads: the caller is about to hand over a
   * new set of files. Which sections are drawn is kept, because the observer
   * does not report a section already in view a second time. */
  export function reset(): void {
    expanded = {};
    textRequested = {};
  }

  /** The pending re-align frame of the last jump; a new jump or unmounting cancels it. */
  let jumpFrame = 0;

  function jumpTo(path: string): void {
    activePath = path;
    const section = sections[path];
    section?.scrollIntoView({ block: 'start' });
    // Files drawn around the target after the jump can change height (a
    // sideways scrollbar appears) and push it off the top; align once more
    // after they are drawn.
    cancelAnimationFrame(jumpFrame);
    jumpFrame = requestAnimationFrame(() => {
      jumpFrame = requestAnimationFrame(() => section?.scrollIntoView({ block: 'start' }));
    });
  }
  onDestroy(() => cancelAnimationFrame(jumpFrame));

  /** Jump once per requested file, as soon as its section exists — not again
   * every time another file finishes loading. */
  let jumpedTo = '';
  $effect(() => {
    const path = focusPath;
    if (!path || path === jumpedTo || !byPath.has(path) || !sections[path]) return;
    jumpedTo = path;
    jumpTo(path);
  });

  /** One observer for every section: it only marks a section as worth drawing. */
  let observer: IntersectionObserver | null = null;
  $effect(() => {
    if (!scroller) return;
    const watcher = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const path = (entry.target as HTMLElement).dataset.path;
          if (entry.isIntersecting && path && !mounted[path]) mounted[path] = true;
        }
      },
      { root: scroller, rootMargin: '100% 0px' }
    );
    observer = watcher;
    for (const element of Object.values(sections)) watcher.observe(element);
    return () => {
      watcher.disconnect();
      observer = null;
    };
  });

  function watch(element: HTMLElement) {
    observer?.observe(element);
    return { destroy: () => observer?.unobserve(element) };
  }

  function folderOf(path: string): string {
    const slash = path.lastIndexOf('/');
    return slash < 0 ? '' : path.slice(0, slash + 1);
  }

  function nameOf(path: string): string {
    return path.slice(path.lastIndexOf('/') + 1);
  }

  function marker(line: DiffLine): string {
    return line.kind === 'added' ? '+' : line.kind === 'removed' ? '-' : ' ';
  }

  function toggleViewed(path: string): void {
    if (viewed[path]) delete viewed[path];
    else viewed[path] = true;
  }

  function gapText(count: number | null): string {
    if (count === null) return 'More unchanged lines may follow';
    return `${count} unmodified ${count === 1 ? 'line' : 'lines'}`;
  }
</script>

{#snippet code(line: DiffLine, spans: Map<DiffLine, HighlightedLine>)}
  {#if line.kind === 'note'}<span class="note">{line.text}</span>{:else}{#each spans.get(line) ?? [] as span}<span class={span.className}>{span.value}</span>{/each}{/if}
{/snippet}

{#snippet addComment(path: string, side: 'LEFT' | 'RIGHT', line: number | null)}
  {#if onLineComment && line !== null}
    <button type="button" class="add-comment" aria-label={`Comment on line ${line}`} title="Comment on this line" onclick={() => onLineComment?.(path, side, line)}><Plus size={12} aria-hidden="true" /></button>
  {/if}
{/snippet}

{#snippet gapBar(file: SourceGitDiff, row: DiffRow | SplitRow, showText: boolean)}
  {#if row.kind === 'gap'}
    <button
      type="button"
      class="gap"
      disabled={!row.expandable || !showText}
      onclick={() => openGap(file, row.key)}
    >{#if showText}<span class="gap-inner"><ChevronsUpDown size={13} aria-hidden="true" />{gapText(row.count)}</span>{/if}</button>
  {:else if row.kind === 'label'}
    <div class="label">{showText ? row.text : ''}</div>
  {/if}
{/snippet}

<div class="multi-diff" data-selectable="true">
  <div class="files" bind:this={scroller}>
    {#each ordered as file (file.relativePath)}
      {@const parsed = parsedDiffOf(file)}
      {@const rows = rowsOf(file)}
      {@const pairs = mode === 'side-by-side' ? splitRows(rows) : null}
      <section class="file" data-path={file.relativePath} bind:this={sections[file.relativePath]} use:watch>
        <header class="file-head">
          <FileIcon fileName={nameOf(file.relativePath)} />
          <span class="file-path" title={file.relativePath}><span class="dir">{folderOf(file.relativePath)}</span>{nameOf(file.relativePath)}</span>
          <span class="counts"><em>+{parsed.addedCount}</em> <del>-{parsed.removedCount}</del></span>
          <IconButton label={viewed[file.relativePath] ? 'Mark as not viewed' : 'Mark as viewed'} size="xs" onclick={() => toggleViewed(file.relativePath)}>
            {#if viewed[file.relativePath]}<EyeOff size={14} />{:else}<Eye size={14} />{/if}
          </IconButton>
          <DropdownMenu.Root>
            <DropdownMenu.Trigger>
              {#snippet child({ props })}
                <Button {...props} variant="ghost" size="icon-xs" class="min-h-7 min-w-7" aria-label="More file actions" title="More file actions"><Ellipsis size={14} /></Button>
              {/snippet}
            </DropdownMenu.Trigger>
            <DropdownMenu.Content align="end">
              {#if onOpenLine}<DropdownMenu.Item onSelect={() => onOpenLine?.(file.relativePath, null)}>Open in editor</DropdownMenu.Item>{/if}
              <DropdownMenu.Item onSelect={() => void navigator.clipboard.writeText(file.relativePath)}>Copy path</DropdownMenu.Item>
            </DropdownMenu.Content>
          </DropdownMenu.Root>
        </header>
        {#if viewed[file.relativePath]}
          <!-- Viewed: the lines fold away until the mark is taken off. -->
        {:else if file.isBinary || parsed.isBinary}
          <p class="notice">Binary file, so there is no line-by-line comparison.</p>
        {:else if parsed.isEmpty}
          <p class="notice">No line changes to show.</p>
        {:else if !mounted[file.relativePath]}
          <div style:height={`${heightOf(pairs ?? rows) + 12}px`}></div>
        {:else}
          {@const spans = spansOf(rows, languageForPath(file.relativePath))}
          {#if pairs}
          <div class="body split" style:contain-intrinsic-size={`auto ${heightOf(pairs)}px`}>
            {#each SIDES as side (side)}
              <div class="scroll">
                <div class="lines">
                  {#each pairs as row, index (index)}
                    {#if row.kind === 'pair'}
                      {@const line = side === 'left' ? row.left : row.right}
                      {#if line}
                        <div class="row {line.kind}" role="presentation" ondblclick={() => onOpenLine?.(file.relativePath, line.afterLine ?? line.beforeLine)}>
                          <span class="gutter one">{@render addComment(file.relativePath, side === 'left' ? 'LEFT' : 'RIGHT', side === 'left' ? line.beforeLine : line.afterLine)}{(side === 'left' ? line.beforeLine : line.afterLine) ?? ''}</span>
                          <span class="text">{@render code(line, spans)}</span>
                        </div>
                      {:else}
                        <div class="row filler"></div>
                      {/if}
                    {:else}
                      {@render gapBar(file, row, side === 'left')}
                    {/if}
                  {/each}
                </div>
              </div>
            {/each}
          </div>
          {:else}
          <div class="body scroll" style:contain-intrinsic-size={`auto ${heightOf(rows)}px`}>
            <div class="lines">
              {#each rows as row, index (index)}
                {#if row.kind === 'line'}
                  <div class="row {row.line.kind}" role="presentation" ondblclick={() => onOpenLine?.(file.relativePath, row.line.afterLine ?? row.line.beforeLine)}>
                    <span class="gutter">{@render addComment(file.relativePath, row.line.kind === 'removed' ? 'LEFT' : 'RIGHT', row.line.kind === 'removed' ? row.line.beforeLine : row.line.afterLine)}<span>{row.line.beforeLine ?? ''}</span><span>{row.line.afterLine ?? ''}</span><span class="mark">{marker(row.line)}</span></span>
                    <span class="text">{@render code(row.line, spans)}</span>
                  </div>
                {:else}
                  {@render gapBar(file, row, true)}
                {/if}
              {/each}
            </div>
          </div>
          {/if}
        {/if}
        {#if rows.length === MAX_ROWS && !parsed.isEmpty && !viewed[file.relativePath]}
          <p class="notice">Showing the first {MAX_ROWS} lines of this file's changes.</p>
        {/if}
        {@render fileFooter?.(file.relativePath)}
      </section>
    {:else}
      <p class="notice">{filter ? 'No changed file matches the filter.' : 'No changes.'}</p>
    {/each}
  </div>

  <aside class="tree" aria-label="Changed files">
    <Input class="filter" placeholder="Filter files..." bind:value={filter} aria-label="Filter files" />
    <div class="tree-rows">
      {#each visibleTree as entry (entry.kind + entry.path)}
        {#if entry.kind === 'folder'}
          <button
            type="button"
            class="tree-row folder"
            style:padding-left={`${8 + entry.depth * 12}px`}
            aria-expanded={!closedFolders[entry.path]}
            onclick={() => {
              if (closedFolders[entry.path]) delete closedFolders[entry.path];
              else closedFolders[entry.path] = true;
            }}
          >
            {#if closedFolders[entry.path]}<ChevronRight size={13} />{:else}<ChevronDown size={13} />{/if}
            <span class="tree-name">{entry.name}</span>
          </button>
        {:else}
          {@const file = byPath.get(entry.path)}
          {@const parsed = file ? parsedDiffOf(file) : null}
          <button
            type="button"
            class="tree-row"
            class:active={activePath === entry.path}
            class:viewed={viewed[entry.path]}
            style:padding-left={`${8 + entry.depth * 12}px`}
            title={entry.path}
            onclick={() => jumpTo(entry.path)}
          >
            <FileIcon fileName={entry.name} />
            <span class="tree-name">{entry.name}</span>
            {#if parsed}<span class="counts"><em>+{parsed.addedCount}</em> <del>-{parsed.removedCount}</del></span>{/if}
          </button>
        {/if}
      {/each}
    </div>
  </aside>
</div>

<style>
  .multi-diff {
    display: flex;
    flex: 1 1 auto;
    min-height: 0;
    min-width: 0;
    background: var(--color-bg);
    color: var(--color-text-2);
    font-size: 13px;
  }

  .files {
    flex: 1 1 auto;
    min-width: 0;
    overflow-y: auto;
    padding-bottom: 24px;
  }

  .file {
    border-bottom: 1px solid var(--color-border);
  }

  .file-head {
    position: sticky;
    top: 0;
    z-index: 2;
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 36px;
    padding: 4px 8px 4px 12px;
    background: var(--color-bg);
    border-bottom: 1px solid var(--color-border);
  }

  .file-path {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--color-text);
  }

  .dir {
    color: var(--color-text-3);
  }

  .counts {
    flex: 0 0 auto;
    font: 12px var(--font-mono);
  }

  .counts em {
    color: var(--color-good);
    font-style: normal;
  }

  .counts del {
    color: var(--color-bad);
    text-decoration: none;
  }

  .notice {
    margin: 0;
    padding: 10px 12px;
    color: var(--color-text-3);
    font-size: 13px;
  }

  .body {
    padding: 6px 0;
    content-visibility: auto;
  }

  .split {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    gap: 1px;
    background: var(--color-border);
  }

  .scroll {
    overflow-x: auto;
    overscroll-behavior-x: contain;
    scrollbar-width: thin;
    scrollbar-color: var(--scrollbar-thumb) transparent;
  }

  /* The sideways scrollbar always keeps its room: in a grid row WebKit does
   * not grow a side for a scrollbar that appears later, so the bar covered
   * the last line and a vertical scrollbar showed up. After `.scroll`, which
   * it overrides. */
  .split > .scroll {
    overflow-x: scroll;
    overflow-y: hidden;
    background: var(--color-bg);
  }

  /* As wide as the longest line, so a changed line keeps its tint all the way
   * across when the file is scrolled sideways. */
  .lines {
    width: max-content;
    min-width: 100%;
    font: 12px/20px var(--font-mono);
    tab-size: 4;
  }

  .row {
    display: flex;
    height: 20px;
    white-space: pre;
    color: var(--color-text-2);
    --row-bg: var(--color-bg);
    background: var(--row-bg);
  }

  .row.added {
    --row-bg: color-mix(in srgb, var(--color-good) 12%, var(--color-bg));
  }

  .row.removed {
    --row-bg: color-mix(in srgb, var(--color-bad) 12%, var(--color-bg));
  }

  .row.filler {
    --row-bg: repeating-linear-gradient(135deg, transparent 0 4px, var(--color-border) 4px 5px);
  }

  /* Line numbers stay put while the code scrolls sideways under them. */
  .gutter {
    position: sticky;
    left: 0;
    z-index: 1;
    display: flex;
    flex: 0 0 auto;
    padding-right: 8px;
    background: var(--row-bg);
    border-left: 3px solid transparent;
    color: var(--color-text-3);
    user-select: none;
    -webkit-user-select: none;
  }

  .row.added .gutter {
    border-left-color: var(--color-good);
  }

  .row.removed .gutter {
    border-left-color: var(--color-bad);
  }

  /* The comment button sits over the gutter's left edge and shows on hover. */
  .add-comment {
    position: absolute;
    left: 2px;
    top: 2px;
    display: grid;
    place-items: center;
    width: 16px;
    height: 16px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: var(--color-live);
    color: var(--color-bg);
    cursor: pointer;
    opacity: 0;
  }

  .row:hover .add-comment,
  .add-comment:focus-visible {
    opacity: 1;
  }

  .gutter > span {
    width: 44px;
    text-align: right;
  }

  .gutter > .mark {
    width: 20px;
    text-align: center;
  }

  .gutter.one {
    width: 52px;
    justify-content: flex-end;
  }

  .text {
    padding-right: 16px;
  }

  .text .keyword { color: var(--color-accent); }
  .text .string { color: var(--color-good); }
  .text .comment { color: var(--color-text-3); font-style: italic; }
  .text .number { color: var(--color-attention); }
  .text .type { color: var(--color-live); }
  .text .note { color: var(--color-text-3); font-style: italic; }

  .gap,
  .label {
    display: block;
    width: 100%;
    height: 28px;
    margin: 0;
    padding: 0;
    border: 0;
    background: var(--color-surface);
    color: var(--color-text-3);
    font-family: var(--font-ui);
    font-size: 12px;
    line-height: 28px;
    text-align: left;
  }

  .gap-inner {
    position: sticky;
    left: 0;
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 0 12px;
  }

  .gap:not(:disabled) {
    cursor: pointer;
  }

  .gap:not(:disabled):hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .gap:focus-visible,
  .tree-row:focus-visible {
    outline: 2px solid var(--color-focus-solid);
    outline-offset: -2px;
  }

  .label {
    height: 24px;
    padding: 0 12px;
    line-height: 24px;
    font-size: 12px;
    letter-spacing: 0.06em;
    text-transform: uppercase;
  }

  .tree {
    display: flex;
    flex: 0 0 280px;
    flex-direction: column;
    min-width: 0;
    gap: 8px;
    min-height: 0;
    padding: 8px;
    border-left: 1px solid var(--color-border);
  }

  .tree-rows {
    flex: 1 1 auto;
    min-height: 0;
    overflow-y: auto;
  }

  .tree-row {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    min-height: 28px;
    padding-right: 8px;
    border: 0;
    border-radius: 8px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
    font: inherit;
    text-align: left;
  }

  .tree-row:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .tree-row.active {
    background: var(--color-selected);
    color: var(--color-text);
  }

  .tree-row.viewed {
    opacity: 0.5;
  }

  .tree-row.folder {
    color: var(--color-text-3);
  }

  .tree-name {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
