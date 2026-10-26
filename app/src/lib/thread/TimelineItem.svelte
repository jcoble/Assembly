<script lang="ts">
  import { getContext } from 'svelte';
  import type { Item, SafeMarkdownBlock } from '../rows/store';
  import type { Kept } from '../rows/kept.svelte';
  import { checkoutLine, turnLine, type TurnStats } from './turns';
  import Message from '../chat/Message.svelte';
  import Text from '../chat/Text.svelte';
  import SentText from '../composer/SentText.svelte';
  import Thinking from '../chat/Thinking.svelte';
  import Tool from '../chat/Tool.svelte';
  import Fallback from '../chat/Fallback.svelte';
  import Compaction from '../chat/Compaction.svelte';
  import { THREAD_OPEN } from '../chat/tools/tool';
  import Ask from '../chat/Ask.svelte';
  import Task from '../chat/Task.svelte';
  import Subagent from '../chat/Subagent.svelte';
  import MessageMeta from '../chat/MessageMeta.svelte';
  import { errorNotice } from '../chat/notices';

  // One item's row in the timeline (Conversation.svelte draws the row around it). `blocks`: a streaming
  // message's revealed blocks. `senders`: the label of each session a prompt came from, by its thread id.
  let {
    item,
    label,
    kept,
    meta,
    speaker,
    senders,
    stats,
    now,
    plain,
    blocks,
  }: {
    item: Item;
    label: boolean;
    kept: Kept;
    meta: { text: string; at: number; align: 'start' | 'end' } | null;
    speaker: string;
    senders: Record<string, string>;
    stats: Record<string, TurnStats>;
    now: number;
    plain: string | null;
    blocks: SafeMarkdownBlock[] | undefined;
  } = $props();
  const openThread = getContext<(threadId: string) => void>(THREAD_OPEN);
  // Memory bisect only (Conversation.svelte's ?plain=): a message's words as plain text.
  const words = (x: unknown): string =>
    typeof x !== 'object' || !x
      ? ''
      : Array.isArray(x)
        ? x.map(words).join(' ')
        : Object.entries(x).map(([k, v]) => (k === 'value' && typeof v === 'string' ? v : k === 'lines' ? '' : words(v))).join(' ');
</script>

{#if item.kind === 'user'}
  {@const moved = item.runId ? checkoutLine(stats[item.runId]) : null}
  {#if moved}<div class="mark checkout">{moved}</div>{/if}
  <Message role="user" sender={item.from ? { label: senders[item.from] ?? 'Another session', open: () => openThread(item.from!) } : null}>{#if plain === 'prompts'}<p>{item.text}</p>{:else}<SentText text={item.text} images={plain !== 'images'} {kept} />{/if}</Message>
{:else if item.kind === 'stats'}
  <div class="mark turn">{turnLine(item.stats, null, now)}</div>
{:else if item.kind === 'note'}
  <div class="mark turn">{item.text}</div>
{:else if item.kind === 'compaction'}
  <Compaction value={{ before: item.preTokens, after: item.postTokens }} />
{:else if item.kind === 'error'}
  {@const card = errorNotice(item.error)}
  {#if card}<Message role="assistant" {label} {speaker} notice={card} />{/if}
{:else}
  <Message role="assistant" {label} {speaker}>
    {#if item.kind === 'message' && plain === 'markdown'}<p>{words(blocks ?? item.blocks)}</p>
    {:else if item.kind === 'message'}<Text blocks={blocks ?? item.blocks} {kept} />
    {:else if item.kind === 'thinking'}<Thinking text={item.text} {kept} />
    {:else if item.kind === 'tool' && plain === 'tools'}<p>{item.tool.summary}</p>
    {:else if item.kind === 'tool'}<Tool itemId={item.itemId} tool={item.tool} {kept} />
    {:else if item.kind === 'ask'}<Ask askId={item.askId} ask={item.ask} />
    {:else if item.kind === 'task'}<Task taskId={item.taskId} task={item.task} />
    {:else if item.kind === 'subagent'}<Subagent sessionId={item.sessionId} subagent={item.subagent} />
    {:else}<Fallback type={item.type} />{/if}
  </Message>
{/if}
{#if meta}<MessageMeta text={meta.text} at={meta.at} {now} align={meta.align} />{/if}

<style>
  /* As Conversation.svelte's .mark. */
  .mark {
    color: var(--muted);
    font-size: 12px;
    margin: -10px 0 18px;
    font-variant-numeric: tabular-nums;
  }
  .checkout {
    margin: 0 0 12px;
    padding-top: 8px;
    border-top: 1px dashed var(--line);
  }
</style>
