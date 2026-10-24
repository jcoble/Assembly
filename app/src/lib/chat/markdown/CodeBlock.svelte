<script lang="ts">
  import { copyText } from '../../host/clipboard';
  // A fenced block: its language, Wrap and Copy, then the code. The relay
  // sends coloured `lines` once the block is complete; until then it is plain.
  import { Kept } from '../../rows/kept.svelte';
  import type { CodeBlock } from '../../rows/store';

  // Wrap and Copied live in `kept` under this block's `place`, so a row drawn again keeps them.
  let { block, kept = new Kept(), place = '' }: { block: CodeBlock; kept?: Kept; place?: string } = $props();
  const wrapped = $derived(kept.wrapped.has(place));
  const copied = $derived(kept.copied.has(place));

  async function copy() {
    try {
      await copyText(block.value);
      kept.copied.add(place);
    } catch {
      // No clipboard access: nothing to report.
    }
  }
</script>

<div class="code">
  <div class="meta">
    <span class="fence">{block.language.trim() || 'code'}</span>
    <button type="button" aria-pressed={wrapped} onclick={() => void (wrapped ? kept.wrapped.delete(place) : kept.wrapped.add(place))}>{wrapped ? 'No wrap' : 'Wrap'}</button>
    <button type="button" onclick={() => void copy()}>{copied ? 'Copied' : 'Copy'}</button>
  </div>
  <pre class:wrapped><code
      >{#if block.lines}{#each block.lines as line, i (i)}{#if i}{'\n'}{/if}{#each line as token, j (j)}<span class={token.className}
            >{token.value}</span
          >{/each}{/each}{:else}{block.value}{/if}</code
    ></pre>
</div>

<style>
  .code {
    overflow: hidden;
    margin: 12px 0;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: var(--code-bg);
  }
  .meta {
    display: flex;
    align-items: center;
    gap: 4px;
    min-height: 30px;
    padding: 3px 8px 3px 12px;
    border-bottom: 1px solid var(--line);
    color: var(--muted);
    font-size: 12px;
  }
  .fence {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    letter-spacing: 0.03em;
  }
  .meta button {
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: inherit;
    padding: 2px 8px;
    font-size: 12px;
  }
  .meta button:hover {
    background: var(--line);
    color: var(--text);
  }
  pre {
    max-height: 360px;
    overflow: auto;
    margin: 0;
    padding: 12px;
    font: 13px/1.6 ui-monospace, SFMono-Regular, Menlo, monospace;
  }
  pre.wrapped {
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  code {
    font: inherit;
    background: none;
    padding: 0;
  }
  .keyword {
    color: var(--accent);
  }
  .string {
    color: #1a7f37;
  }
  .comment {
    color: var(--muted);
    font-style: italic;
  }
  .number {
    color: #b35900;
  }
  .type {
    color: #8250df;
  }
</style>
