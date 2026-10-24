<script lang="ts">
  import { conversationDisclosureContext, type ConversationDisclosureContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import { onMount, getContext } from 'svelte';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import FileText from '@lucide/svelte/icons/file-text';
  import { Button } from '$lib/components/ui/button/index.js';
  import { openUrlInBrowser } from '$lib/shell/workbenchNavigation.ts';
  import { userMessageOverflowsFold } from '$lib/shell/conversation/conversationTimeline.ts';
  import { parseSafeMarkdown, type SafeInlinePart, type SafeMarkdownBlock } from '$lib/shell/conversation/conversationMessageSafety.ts';
  import { trackConversationMessageRenderer } from '$lib/shell/resourceDiagnostics.svelte.ts';
  import CodeBlock from './CodeBlock.svelte';

  interface Props {
    text: string;
    role: 'user' | 'assistant';
    itemId?: string;
    completed?: boolean;
    blocks?: readonly SafeMarkdownBlock[] | null;
    showImages?: boolean;
    onFileLink?(path: string): void;
  }

  const disclosure = getContext<ConversationDisclosureContext>(conversationDisclosureContext);

  let { text, role, itemId = 'message', completed = true, blocks: incomingBlocks, showImages = false, onFileLink }: Props = $props();
  const blocks = $derived(incomingBlocks ?? parseSafeMarkdown(text));

  /* A pasted log or a long brief is worth keeping, but not worth scrolling
     past every time the conversation is reopened. A sent message shows its
     first ten lines and offers the rest; a reply is never folded, because the
     reply is the thing being read. */
  let bodyHost = $state<HTMLElement | null>(null);
  const expanded = $derived(disclosure?.get(`${itemId}:message`) ?? false);
  let folded = $state(false);
  onMount(trackConversationMessageRenderer);

  $effect(() => {
    const host = bodyHost;
    if (role !== 'user' || !host) return;
    void blocks;
    const nextFolded = userMessageOverflowsFold(
      host.scrollHeight,
      Number.parseFloat(getComputedStyle(host).lineHeight)
    );
    if (folded !== nextFolded) folded = nextFolded;
  });
</script>

<article
  data-testid={`${role}-message-item`}
  data-item-id={itemId}
  class:user={role === 'user'}
  class:assistant={role === 'assistant'}
  class:streaming={!completed}
>
  <div
    class="turn-body"
    class:folded={folded && !expanded}
    data-testid="conversation-message-body"
    bind:this={bodyHost}
  >
    {#each blocks as block, blockIndex}{@render node(block, blockIndex === 0)}{/each}
  </div>
  {#if folded}
    <div class="fold-row">
      <Button
        variant="ghost"
        size="sm"
        class="text-[13px] text-muted-foreground"
        data-testid="conversation-message-fold"
        aria-expanded={expanded}
        onclick={() => disclosure?.set(`${itemId}:message`, !expanded)}
      >
        <span class="fold-chevron" class:open={expanded} aria-hidden="true"><ChevronRight size={13} strokeWidth={1.8} /></span>
        {expanded ? 'Show less' : 'Show more'}
      </Button>
    </div>
  {/if}
</article>

<!-- Blocks nest: a list item can hold a list, a quote can hold anything. This
     renders one block and calls itself for whatever is underneath it. -->
{#snippet node(block: SafeMarkdownBlock, first = false)}
  {#if block.kind === 'code'}
    <CodeBlock value={block.value} info={block.language} />
  {:else if block.kind === 'heading'}
    <h3 class={`heading level-${Math.min(block.level, 4)}`} data-testid="conversation-markdown-heading">{#each block.parts as part}{@render inline(part)}{/each}</h3>
  {:else if block.kind === 'quote'}
    <blockquote data-testid="conversation-markdown-quote">{#each block.blocks as inner}{@render node(inner)}{/each}</blockquote>
  {:else if block.kind === 'rule'}
    <hr data-testid="conversation-markdown-rule" />
  {:else if block.kind === 'list'}
    {#if block.ordered}<ol data-testid="conversation-markdown-list">{#each block.items as entry}{@render row(entry)}{/each}</ol>
    {:else}<ul data-testid="conversation-markdown-list">{#each block.items as entry}{@render row(entry)}{/each}</ul>{/if}
  {:else if block.kind === 'table'}
    <div class="table-scroll" data-testid="conversation-markdown-table"><table><thead><tr>{#each block.headers as cell}<th>{#each cell as part}{@render inline(part)}{/each}</th>{/each}</tr></thead><tbody>{#each block.rows as tableRow}<tr>{#each tableRow as cell}<td>{#each cell as part}{@render inline(part)}{/each}</td>{/each}</tr>{/each}</tbody></table></div>
  {:else}
    <p data-testid={first ? 'conversation-markdown-paragraph' : undefined}>{#each block.parts as part}{@render inline(part)}{/each}</p>
  {/if}
{/snippet}

{#snippet row(entry: { task: boolean; checked: boolean; parts: SafeInlinePart[]; blocks: SafeMarkdownBlock[] })}
  <li class:task-row={entry.task}>{#if entry.task}<input data-testid="conversation-task-checkbox" type="checkbox" checked={entry.checked} disabled />{/if}{#each entry.parts as part}{@render inline(part)}{/each}{#each entry.blocks as inner}{@render node(inner)}{/each}</li>
{/snippet}

{#snippet inline(part: SafeInlinePart)}
  {#if part.kind === 'strong'}<strong>{#each part.parts as inner}{@render inline(inner)}{/each}</strong>
  {:else if part.kind === 'emphasis'}<em>{#each part.parts as inner}{@render inline(inner)}{/each}</em>
  {:else if part.kind === 'strike'}<del>{#each part.parts as inner}{@render inline(inner)}{/each}</del>
  {:else if part.kind === 'code'}<code class="inline-code">{part.value}</code>
  {:else if part.kind === 'link'}<a href={part.href} onclick={(event) => { event.preventDefault(); void openUrlInBrowser({ url: part.href }); }}>{#each part.parts as inner}{@render inline(inner)}{/each}</a>
  {:else if part.kind === 'image'}
    <a href={part.href} onclick={(event) => { event.preventDefault(); void openUrlInBrowser({ url: part.href }); }}>
      {#if showImages}<img class="pr-image" src={part.href} alt={part.alt} loading="lazy" decoding="async" referrerpolicy="no-referrer" />
      {:else}{part.alt}{/if}
    </a>
  {:else if part.kind === 'file-link'}<button class="file-link" data-testid="conversation-file-link" type="button" title={part.path} onclick={() => onFileLink?.(part.path)}><FileText size={13} strokeWidth={1.8} aria-hidden="true" />{#each part.parts as inner}{@render inline(inner)}{/each}</button>
  {:else}{part.value}{/if}
{/snippet}

<style>
  /* Nothing announces who is speaking. The shape does: a reply runs the full
     768px measure with no box around it, a prompt is a narrow bubble on the
     right. That asymmetry is the only role marking in the transcript. */
  article{max-width:768px;user-select:text;-webkit-user-select:text}
  .turn-body{font-size:14px;line-height:1.5}

  /* The transcript's scale, inside a message: 6px between blocks, 2px between
     the rows of a list, and 10px above a heading so it reads as owning the
     section under it rather than floating between two. */
  .turn-body p{margin:0 0 6px;white-space:pre-wrap}
  .turn-body p:last-child,
  .turn-body ul:last-child,
  .turn-body ol:last-child,
  .turn-body blockquote:last-child,
  .turn-body .table-scroll:last-child{margin-bottom:0}
  .heading{margin:10px 0 4px;font-weight:640;line-height:1.4;letter-spacing:-.01em}
  .heading:first-child{margin-top:0}
  .heading.level-1{font-size:18px}
  .heading.level-2{font-size:16px}
  .heading.level-3{font-size:15px}
  .heading.level-4{font-size:14px;color:var(--color-text-2)}

  /* A quote is marked by the rule down its side and the quieter text, and by
     nothing else. */
  .turn-body blockquote{margin:0 0 6px;padding:3px 0 3px 14px;border-left:2px solid color-mix(in srgb,var(--color-accent) 42%,var(--color-border));color:var(--color-text-2)}
  .turn-body ul,.turn-body ol{margin:0 0 6px;padding-left:24px}
  .turn-body li{margin-bottom:2px;padding-left:4px}
  .turn-body li:last-child{margin-bottom:0}
  .turn-body li::marker{color:var(--color-text-3)}
  .turn-body li.task-row{list-style:none;margin-left:-18px;padding-left:0}
  .turn-body a{color:var(--color-accent);text-decoration:underline;text-underline-offset:2px}
  .pr-image{display:block;max-width:100%;max-height:480px;width:auto;height:auto;object-fit:contain;border-radius:6px}
  .file-link{display:inline-flex;align-items:baseline;gap:4px;border:0;background:transparent;color:var(--color-accent);padding:0;text-decoration:underline;text-underline-offset:2px;font:inherit;cursor:pointer}
  .file-link :global(svg){align-self:center;flex:none}
  .inline-code{padding:2px 6px;border-radius:5px;background:color-mix(in srgb,var(--color-surface) 78%,var(--color-bg));font:13px/1.4 var(--font-mono)}

  /* The fade is a gradient in the bubble's colour laid over the last lines, not a mask: a mask makes the engine paint the text offscreen first. */
  .turn-body.folded{position:relative;max-height:calc(10 * 1.5em);overflow:hidden}
  .turn-body.folded::after{content:'';position:absolute;left:0;right:0;bottom:0;height:26px;background:linear-gradient(to bottom,transparent,var(--color-elevated));pointer-events:none}
  .fold-row{display:flex;margin-top:6px}
  .fold-chevron{display:grid;place-items:center;color:var(--color-text-3)}
  .fold-chevron.open{transform:rotate(90deg)}
  @media (prefers-reduced-motion:no-preference){.fold-chevron{transition:transform .14s ease}}

  .user{width:fit-content;max-width:80%;margin-left:auto;padding:10px 16px;border-radius:18px;background:var(--color-elevated);font-size:14px;line-height:1.55}

  .table-scroll{overflow:auto;margin-bottom:8px;border:1px solid color-mix(in srgb,var(--color-border) 70%,transparent);border-radius:8px}
  .table-scroll table{border-collapse:collapse;min-width:100%;font-size:13px}
  .table-scroll th,.table-scroll td{padding:7px 10px;border-bottom:1px solid color-mix(in srgb,var(--color-border) 55%,transparent);text-align:left;vertical-align:top}
  .table-scroll tr:last-child td{border-bottom:0}
  .table-scroll th{background:color-mix(in srgb,var(--color-surface) 65%,transparent);font-weight:620;white-space:nowrap}
  /* A task row draws its own box. The platform control paints a disabled
     checkbox the same grey whether or not it is ticked, so a finished task
     looked no different from an open one — which is the only thing the box is
     there to say. It stays a real checkbox for anything reading the page. */
  .turn-body input[type=checkbox]{appearance:none;-webkit-appearance:none;display:inline-grid;place-content:center;flex:none;width:13px;height:13px;margin:0 8px 0 0;border:1.5px solid color-mix(in srgb,var(--color-border) 95%,var(--color-text));border-radius:4px;background:transparent;vertical-align:-2px}
  .turn-body input[type=checkbox]:checked{border-color:var(--color-accent);background:var(--color-accent)}
  .turn-body input[type=checkbox]:checked::after{content:'';width:6px;height:3px;border:1.6px solid var(--color-bg);border-top:0;border-right:0;transform:translateY(-1px) rotate(-45deg)}
  /* A sub-list is one step in, and gives up the gap under its parent row so the
     two read as one group rather than two lists. */
  .turn-body li > :global(ul),.turn-body li > :global(ol){margin:2px 0 0}
  .turn-body hr{margin:10px 0;border:0;border-top:1px solid color-mix(in srgb,var(--color-border) 70%,transparent)}
  .turn-body del{color:var(--color-text-3);text-decoration-thickness:1px}

</style>
