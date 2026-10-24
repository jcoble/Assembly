<script lang="ts">
  // A message's blocks from the relay, drawn as Svelte markup: every value is
  // text and no HTML string is ever rendered.
  import type { SafeInlinePart, SafeMarkdownBlock } from '../rows/store';
  import type { Kept } from '../rows/kept.svelte';
  import CodeBlock from './markdown/CodeBlock.svelte';
  import { openUrl } from '../host/openUrl';

  // `kept` holds each code block's Wrap and Copied, by its place in the message.
  let { blocks, kept }: { blocks: SafeMarkdownBlock[]; kept?: Kept } = $props();
</script>

{#snippet list(blocks: SafeMarkdownBlock[], place: string)}
  {#each blocks as block, i (i)}
    {#if block.kind === 'heading'}
      <svelte:element this={`h${Math.min(block.level + 2, 6)}`}>{@render inline(block.parts)}</svelte:element>
    {:else if block.kind === 'paragraph'}
      <p>{@render inline(block.parts)}</p>
    {:else if block.kind === 'list'}
      <svelte:element this={block.ordered ? 'ol' : 'ul'}>
        {#each block.items as item, j (j)}
          <li class:task={item.task}>
            {#if item.task}<input type="checkbox" checked={item.checked} disabled />{/if}{@render inline(item.parts)}{@render list(item.blocks, `${place}${i}.${j}.`)}
          </li>
        {/each}
      </svelte:element>
    {:else if block.kind === 'code'}
      <CodeBlock {block} {kept} place={`${place}${i}`} />
    {:else if block.kind === 'quote'}
      <blockquote>{@render list(block.blocks, `${place}${i}.`)}</blockquote>
    {:else if block.kind === 'table'}
      <div class="table">
        <table>
          <thead>
            <tr>{#each block.headers as cell, j (j)}<th>{@render inline(cell)}</th>{/each}</tr>
          </thead>
          <tbody>
            {#each block.rows as row, r (r)}
              <tr>{#each row as cell, j (j)}<td>{@render inline(cell)}</td>{/each}</tr>
            {/each}
          </tbody>
        </table>
      </div>
    {:else if block.kind === 'rule'}
      <hr />
    {/if}
  {/each}
{/snippet}

{#snippet inline(parts: SafeInlinePart[])}
  {#each parts as part, i (i)}
    {#if part.kind === 'text'}{part.value}
    {:else if part.kind === 'code'}<code>{part.value}</code>
    {:else if part.kind === 'strong'}<strong>{@render inline(part.parts)}</strong>
    {:else if part.kind === 'emphasis'}<em>{@render inline(part.parts)}</em>
    {:else if part.kind === 'strike'}<del>{@render inline(part.parts)}</del>
    {:else if part.kind === 'link'}<a href={part.href} rel="noreferrer" onclick={(event) => { event.preventDefault(); void openUrl(part.href); }}>{@render inline(part.parts)}</a>
    {:else if part.kind === 'file-link'}<span class="file-link" title={part.path}>{@render inline(part.parts)}</span>
    {/if}
  {/each}
{/snippet}

<div class="markdown">{@render list(blocks, '')}</div>

<style>
  .markdown {
    line-height: 1.55;
    margin: 0 0 8px;
  }
  .markdown :global(p),
  .markdown :global(ul),
  .markdown :global(ol),
  .markdown :global(blockquote) {
    margin: 0 0 10px;
  }
  .markdown :global(p) {
    white-space: pre-wrap;
  }
  .markdown :global(h3),
  .markdown :global(h4),
  .markdown :global(h5),
  .markdown :global(h6) {
    margin: 16px 0 8px;
  }
  .markdown :global(:not(pre) > code) {
    font: 12.5px ui-monospace, SFMono-Regular, Menlo, monospace;
    background: var(--code-bg);
    border-radius: 4px;
    padding: 1px 4px;
  }
  .markdown :global(blockquote) {
    border-left: 3px solid var(--line);
    padding-left: 12px;
    color: var(--muted);
  }
  .markdown :global(a),
  .file-link {
    color: var(--accent);
    text-decoration: underline;
    text-underline-offset: 2px;
  }
  .markdown :global(li.task) {
    list-style: none;
    margin-left: -18px;
  }
  /* Drawn by hand: a disabled native box looks the same ticked or not. */
  .markdown :global(input[type='checkbox']) {
    appearance: none;
    display: inline-grid;
    place-content: center;
    width: 13px;
    height: 13px;
    margin: 0 8px 0 0;
    border: 1.5px solid var(--muted);
    border-radius: 4px;
    vertical-align: -2px;
  }
  .markdown :global(input[type='checkbox']:checked) {
    border-color: var(--accent);
    background: var(--accent);
  }
  .markdown :global(input[type='checkbox']:checked::after) {
    content: '';
    width: 6px;
    height: 3px;
    border: 1.6px solid var(--panel);
    border-top: 0;
    border-right: 0;
    transform: translateY(-1px) rotate(-45deg);
  }
  .markdown :global(li > ul),
  .markdown :global(li > ol) {
    margin: 2px 0 0;
  }
  .markdown :global(del) {
    color: var(--muted);
  }
  .markdown :global(hr) {
    margin: 10px 0;
    border: 0;
    border-top: 1px solid var(--line);
  }
  .table {
    overflow: auto;
    margin-bottom: 8px;
    border: 1px solid var(--line);
    border-radius: 8px;
  }
  table {
    border-collapse: collapse;
    min-width: 100%;
    font-size: 13px;
  }
  th,
  td {
    padding: 7px 10px;
    border-bottom: 1px solid var(--line);
    text-align: left;
    vertical-align: top;
  }
  tr:last-child td {
    border-bottom: 0;
  }
  th {
    background: var(--code-bg);
    font-weight: 600;
    white-space: nowrap;
  }
</style>
