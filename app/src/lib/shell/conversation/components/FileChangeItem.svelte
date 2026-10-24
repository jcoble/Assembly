<script lang="ts">
  import { conversationDisclosureContext, type ConversationDisclosureContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import { getContext } from 'svelte';
  import Check from '@lucide/svelte/icons/check';
  import Copy from '@lucide/svelte/icons/copy';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  import { highlightCode, languageForPath, plainHighlightedLines, type HighlightedLine } from '../../markdown/codeHighlight.ts';

  interface Props {
    item: Extract<ConversationDisplayItem, { kind: 'file' }>;
    path?: string;
    diff?: string;
    onFileLink?(path: string): void;
  }

  const disclosure = getContext<ConversationDisclosureContext>(conversationDisclosureContext);

  let { item, path: propPath, diff: propDiff, onFileLink }: Props = $props();

  /* The same fold the tool rows use, for the same reason: a long diff is read
     by opening it, not by scrolling a fixed window that hides its own size. */
  const DIFF_FOLD_OVER_LINES = 20;
  const DIFF_LINES_KEPT = 12;

  interface DiffRow {
    tone: 'add' | 'delete' | 'context';
    /** The line's number in the file, or null before any hunk has said where we are. */
    number: number | null;
    text: string;
  }

  const path = $derived(
    propPath ?? (typeof item?.metadata?.path === 'string' ? item.metadata.path : 'File change')
  );
  /* The heading names the file, not the route to it. A repository path is
     mostly directories the reader already knows, and it pushed the counts off
     the end of the row. The whole path stays on the element's title. */
  const fileName = $derived(path.split(/[/\\]/).filter(Boolean).at(-1) ?? path);
  const diff = $derived(
    propDiff ?? (typeof item?.metadata?.diff === 'string' ? item.metadata.diff : (item?.text ?? ''))
  );

  /** The diff as numbered rows, with the plumbing lines left out.
   *
   * A hunk header says where in the file the lines that follow sit, which is
   * the only place those numbers exist — so it is read for its numbers and then
   * dropped, along with the `diff --git` and `+++`/`---` lines. What is left is
   * the change itself, which is what the reader came for. */
  const allRows = $derived.by(() => {
    const rows: DiffRow[] = [];
    let removedLine: number | null = null;
    let addedLine: number | null = null;
    for (const line of diff.split('\n')) {
      const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
      if (hunk) {
        removedLine = Number(hunk[1]);
        addedLine = Number(hunk[2]);
        continue;
      }
      if (line.startsWith('diff ') || line.startsWith('index ')
        || line.startsWith('+++') || line.startsWith('---')) continue;
      if (line.startsWith('+')) {
        rows.push({ tone: 'add', number: addedLine, text: line });
        if (addedLine !== null) addedLine += 1;
      } else if (line.startsWith('-')) {
        rows.push({ tone: 'delete', number: removedLine, text: line });
        if (removedLine !== null) removedLine += 1;
      } else {
        rows.push({ tone: 'context', number: addedLine, text: line });
        if (addedLine !== null) addedLine += 1;
        if (removedLine !== null) removedLine += 1;
      }
    }
    return rows;
  });

  /* The code in the diff, coloured in the file's language. The one-character
     mark at the front of each row is not code, so it is set aside for the
     colouring and drawn back in front. Plain until the editor answers. */
  const language = $derived(languageForPath(path));
  const bareLines = $derived(allRows.map((row) => row.text.slice(1)));
  // Every span list here is paired with a diff row, so a scan that does not
  // come back one-for-one is discarded rather than shifting the colours.
  const rowSpans = $derived.by(() => {
    const source = bareLines.join('\n');
    const scanned = highlightCode(source, language);
    return scanned.length === bareLines.length ? scanned : plainHighlightedLines(source);
  });

  const foldable = $derived(allRows.length > DIFF_FOLD_OVER_LINES);
  const diffOpen = $derived(disclosure?.get(`${item.itemId}:diff`) ?? false);
  const rows = $derived(foldable && !diffOpen ? allRows.slice(0, DIFF_LINES_KEPT) : allRows);
  const hiddenLines = $derived(allRows.length - DIFF_LINES_KEPT);
  const additions = $derived(allRows.filter((row) => row.tone === 'add').length);
  const deletions = $derived(allRows.filter((row) => row.tone === 'delete').length);
  /** Wide enough for the longest number on show, so the code starts on one line. */
  const gutterWidth = $derived(Math.max(2, String(allRows.at(-1)?.number ?? '').length));

  let copied = $state(false);

  async function copyDiff(): Promise<void> {
    if (typeof navigator === 'undefined' || !navigator.clipboard) return;
    await navigator.clipboard.writeText(diff);
    copied = true;
  }
</script>

<aside class="file-change" data-testid="timeline-file-change-item">
  <div class="file-heading">
    {#if onFileLink && path !== 'File change'}<button class="file-name" type="button" title={path} onclick={() => onFileLink?.(path)}>{fileName}</button>{:else}<strong class="file-name" title={path}>{fileName}</strong>{/if}
    <span class="counts"><em>+{additions}</em><del>-{deletions}</del></span>
    <button
      class="copy-diff"
      data-testid="copy-file-diff"
      type="button"
      aria-label={copied ? 'Copied' : 'Copy diff'}
      onclick={() => void copyDiff()}
    >{#if copied}<Check size={13} strokeWidth={2.2} />{:else}<Copy size={13} strokeWidth={1.8} />{/if}</button>
  </div>
  <pre data-testid="timeline-file-diff" style={`--gutter-width:${gutterWidth}ch`}><code>{#each rows as row, index}<span class={row.tone}><span class="line-number" aria-hidden="true">{row.number ?? ''}</span>{row.text[0] ?? ' '}{#each rowSpans[index] ?? [] as span}<span class={span.className}>{span.value}</span>{/each}{'\n'}</span>{/each}</code></pre>
  {#if foldable}
    <button
      class="fold-more"
      data-testid="timeline-file-diff-fold"
      type="button"
      aria-expanded={diffOpen}
      onclick={() => disclosure?.set(`${item.itemId}:diff`, !diffOpen)}
    >{diffOpen ? 'Show less' : `Show ${hiddenLines.toLocaleString()} more lines`}</button>
  {/if}
</aside>

<style>
  .file-change{min-width:0;border:1px solid color-mix(in srgb,var(--color-border) 72%,transparent);border-radius:8px;overflow:hidden;background:color-mix(in srgb,var(--color-bg) 78%,var(--color-surface))}
  .file-heading{display:flex;align-items:center;gap:8px;min-height:28px;padding:4px 6px 4px 10px;border-bottom:1px solid color-mix(in srgb,var(--color-border) 64%,transparent);color:var(--color-text-3)}
  .file-name{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--color-text);font:500 12px var(--font-mono);text-align:left}
  button.file-name{border:0;background:transparent;padding:0;cursor:pointer}button.file-name:hover{text-decoration:underline}
  /* The counts sit against the name rather than across the row: the pair reads
     as one caption for the file, and the copy action keeps the far end. */
  .counts{display:flex;flex:1;gap:7px;font:12px var(--font-mono)}.counts em{color:var(--color-good);font-style:normal}.counts del{color:var(--color-bad);text-decoration:none}
  .copy-diff{display:grid;place-items:center;flex:none;width:24px;height:24px;border:0;border-radius:6px;background:transparent;color:var(--color-text-3);cursor:pointer}
  .copy-diff:hover{background:color-mix(in srgb,var(--color-hover) 70%,transparent);color:var(--color-text)}
  .copy-diff:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:1px}
  pre{max-height:280px;overflow-y:auto;overflow-x:auto;margin:0;padding:6px 0;white-space:pre;tab-size:2;scrollbar-width:thin;scrollbar-color:var(--scrollbar-thumb) transparent;overscroll-behavior-x:contain}
  /* Wide as its longest line, so an added or removed line keeps its tint all
     the way across when the diff is scrolled sideways. Sized to the container
     the bands stopped at the right edge and the rest of the line sat on bare
     background. */
  code{display:block;width:max-content;min-width:100%;font:13px/1.55 var(--font-mono)}
  .fold-more{width:100%;min-height:28px;padding:4px 10px;border:0;border-top:1px solid color-mix(in srgb,var(--color-border) 45%,transparent);background:transparent;color:var(--color-text-2);font-size:12px;text-align:left;cursor:pointer}
  .fold-more:hover{background:color-mix(in srgb,var(--color-hover) 45%,transparent);color:var(--color-text)}
  .fold-more:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:-2px}
  @media (prefers-reduced-motion:no-preference){
    .fold-more{transition:background .14s ease,color .14s ease}
  }
  /* A changed line is marked twice: the tint across the row says which lines
     moved, and the bar down the very left edge survives being scrolled
     sideways past the tint's start, so a wide diff still says what it is. The
     bar is drawn as the row's left border so it stays put in the numbers
     column rather than travelling with the code. */
  code > span{display:block;min-height:1.55em;padding:0 10px 0 7px;border-left:3px solid transparent;color:var(--color-text-2)}
  code .add{border-left-color:var(--color-good);background:color-mix(in srgb,var(--color-good) 11%,transparent);color:color-mix(in srgb,var(--color-good) 76%,var(--color-text))}
  code .delete{border-left-color:var(--color-bad);background:color-mix(in srgb,var(--color-bad) 11%,transparent);color:color-mix(in srgb,var(--color-bad) 74%,var(--color-text))}
  /* Real file line numbers, right-aligned in their own column so the code
     starts on one line however many digits the file needs. They are quieter
     than the code and cannot be selected with it, so copying a diff out of the
     page does not bring the numbering along. */
  .line-number{display:inline-block;width:var(--gutter-width,3ch);margin-right:14px;color:var(--color-text-3);text-align:right;user-select:none;-webkit-user-select:none}
  /* The same five colours a code block paints with. A run the tokenizer did
     not name keeps the row's own colour, so an added or removed line still
     reads as one. */
  code .keyword{color:var(--color-accent)}
  code .string{color:var(--color-good)}
  code .comment{color:var(--color-text-3);font-style:italic}
  code .number{color:var(--color-attention)}
  code .type{color:var(--color-live)}
  code .plain{color:inherit}
</style>
