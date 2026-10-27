<script lang="ts">
  import { getContext, untrack } from 'svelte';
  import { Kept } from '../rows/kept.svelte';
  import type { ToolDetail, ToolPayload } from '../rows/store';
  import { commandOf, foldOutput, TOOL_DETAIL } from './tools/tool';
  import { DIFF_FOLD, DIFF_SHOWN, toolTitle } from '../session/fold';
  // `kept` holds what must survive the row being drawn again (rows/kept.svelte.ts).
  let { itemId, tool, kept = new Kept() }: { itemId: string; tool: ToolPayload; kept?: Kept } = $props();

  // Lines of output mounted at a time; "Show earlier" adds another page.
  const TAIL = 200;
  const readDetail = getContext<(itemId: string) => Promise<ToolDetail>>(TOOL_DETAIL);
  const failed = $derived(tool.state === 'failed');

  // Closed, the call is its one-line row. Opening it fetches the detail (again
  // if the call finishes while open); closing it drops the detail.
  let error = $state('');
  $effect(() => {
    const state = tool.state;
    error = '';
    // A row drawn again keeps the detail it fetched for this state.
    if (kept.open && tool.hasDetail && untrack(() => kept.detail && kept.detailFor === state)) return;
    kept.detail = null;
    kept.detailFor = null;
    kept.shown = TAIL;
    if (!kept.open || !tool.hasDetail) return;
    let current = true;
    readDetail(itemId).then(
      (value) => {
        if (current) {
          kept.detail = value;
          kept.detailFor = state;
        }
      },
      (reason) => {
        if (current) error = String(reason);
      },
    );
    return () => {
      current = false;
    };
  });
  const command = $derived(kept.detail ? commandOf(tool.name, kept.detail.input) : null);
  const fold = $derived(kept.detail?.output ? foldOutput(kept.detail.output, kept.shown) : null);
  const diff = $derived(kept.detail?.diff ? kept.detail.diff.replace(/\n$/, '').split('\n') : null);
  // A diff past DIFF_FOLD lines shows its first DIFF_SHOWN until asked for the rest.
  const diffShown = $derived(diff && diff.length > DIFF_FOLD && !kept.allDiff ? diff.slice(0, DIFF_SHOWN) : diff);
  const lineClass = (line: string) => (line.startsWith('+') ? 'add' : line.startsWith('-') ? 'remove' : '');
</script>

<details class="tool" class:failed bind:open={kept.open}>
  <summary title={tool.path}>
    <span class="name">{toolTitle(tool.name)}</span>
    <span class="summary">{tool.summary}</span>
    {#if tool.added || tool.removed}<span class="count"><span class="add">+{tool.added ?? 0}</span> <span class="remove">−{tool.removed ?? 0}</span></span>{/if}
    {#if tool.state === 'started'}<span class="state">Running</span>
    {:else if failed}<span class="state failed">Failed</span>{/if}
  </summary>
  {#if kept.open}
    <div class="body">
      {#if command}<pre class="input">{command}</pre>{/if}
      {#if diff}
        <pre class="diff">{#each diffShown ?? [] as line, i (i)}<span class={lineClass(line)}>{line}</span>{/each}</pre>
        {#if diffShown && diffShown.length < diff.length}
          <button class="ghost earlier" onclick={() => (kept.allDiff = true)}>Show {diff.length - diffShown.length} more lines</button>
        {/if}
      {/if}
      {#if fold}
        {#if fold.hidden}
          <button class="ghost earlier" onclick={() => (kept.shown += TAIL)}>Show {Math.min(TAIL, fold.hidden)} earlier lines ({fold.hidden} hidden)</button>
        {/if}
        <pre>{fold.text}</pre>
      {/if}
      {#if tool.state === 'started'}<div class="waiting">Output arrives when the tool finishes.</div>
      {:else if error}<div class="waiting">{error}</div>
      {:else if !tool.hasDetail || (kept.detail && !command && !diff && !fold)}<div class="waiting">No output.</div>
      {:else if !kept.detail}<div class="waiting">Loading…</div>{/if}
    </div>
  {/if}
</details>

<style>
  .tool {
    border: 1px solid var(--line);
    border-radius: 8px;
    padding: 6px 10px;
    margin: 0 0 8px;
    font-size: 13px;
  }
  summary {
    cursor: pointer;
    display: flex;
    gap: 8px;
    align-items: baseline;
  }
  .name {
    font-weight: 600;
  }
  .summary {
    color: var(--muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    flex: 1;
  }
  .state {
    color: var(--accent);
    font-size: 12px;
    white-space: nowrap;
  }
  .failed .name,
  .state.failed {
    color: var(--danger);
  }
  /* As in Assembly, an opened failed call says so in its body too. */
  .tool.failed[open] {
    border-color: color-mix(in srgb, var(--danger) 34%, var(--line));
  }
  .failed pre {
    background: color-mix(in srgb, var(--danger) 7%, var(--code-bg));
  }
  .count {
    font-size: 12px;
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
  }
  .add {
    color: #1a7f37;
  }
  .remove {
    color: var(--danger);
  }
  /* Open, the call is capped and scrolls inside itself. */
  .body {
    max-height: 320px;
    overflow: auto;
  }
  .earlier {
    margin: 6px 0 0;
    padding: 2px 8px;
    font-size: 12px;
  }
  .waiting {
    color: var(--muted);
    font-size: 12px;
    margin: 6px 0 0;
  }
  pre {
    background: var(--code-bg);
    border-radius: 6px;
    padding: 8px;
    margin: 6px 0 0;
    white-space: pre-wrap;
    font-size: 12px;
  }
  .input {
    color: var(--muted);
  }
  .diff span {
    display: block;
  }
</style>
