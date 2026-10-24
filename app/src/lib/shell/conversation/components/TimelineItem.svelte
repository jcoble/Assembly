<script lang="ts">
  import { onMount } from 'svelte';
  import {
    conversationFileLinkProvenance,
    type ConversationDisplayItem,
    type ConversationFileLinkProvenance
  } from '$lib/shell/conversation/conversationTimeline.ts';
  import { conversationItemHasVisibleContent } from '$lib/shell/conversation/conversationItemVisibility.ts';
  import UserMessageItem from './UserMessageItem.svelte';
  import AssistantMessageItem from './AssistantMessageItem.svelte';
  import ReasoningItem from './ReasoningItem.svelte';
  import TaskListItem from './TaskListItem.svelte';
  import CommandItem from './CommandItem.svelte';
  import FileChangeItem from './FileChangeItem.svelte';
  import FileEditsItem from './FileEditsItem.svelte';
  import ConversationToolUI from './ConversationToolUI.svelte';
  import SubagentSection from './SubagentSection.svelte';
  import ApprovalItem from './ApprovalItem.svelte';
  import UserInputItem from './UserInputItem.svelte';
  import ErrorItem from './ErrorItem.svelte';
  import CompactionItem from './CompactionItem.svelte';
  import { trackConversationTimelineItem } from '$lib/shell/resourceDiagnostics.svelte.ts';

  interface Props {
    item: ConversationDisplayItem;
    assistantLabel?: string;
    onApprovalDecision?(requestId: string, decision: string): void;
    onFileLink?(path: string, provenance?: ConversationFileLinkProvenance): void;
    /** Opens the plan chip above the composer. The transcript only notes that
     * the plan moved; the plan itself lives in one place. */
    onPlanOpen?(): void;
  }
  // `assistantLabel` is retired: nothing in the transcript carries a role
  // heading any more. It stays accepted only so the timeline that still passes
  // it keeps type-checking; the container drops it in its own pass.
  let { item, onApprovalDecision, onFileLink, onPlanOpen }: Props = $props();
  const visible = $derived(conversationItemHasVisibleContent(item));
  const fileLinkProvenance = $derived(conversationFileLinkProvenance(item));
  const openFileLink = (path: string): void => onFileLink?.(path, fileLinkProvenance);
  onMount(trackConversationTimelineItem);
</script>

{#if visible}
  <div class="timeline-item" data-testid="conversation-timeline-item" data-item-id={item.itemId} data-kind={item.kind}>
    {#if item.kind === 'user'}<UserMessageItem {item} onFileLink={openFileLink} />
    {:else if item.kind === 'assistant'}<AssistantMessageItem {item} onFileLink={openFileLink} />
    {:else if item.kind === 'reasoning'}<ReasoningItem {item} onFileLink={openFileLink} />
    {:else if item.kind === 'plan'}<button class="plan-line" data-testid="timeline-plan-item" type="button" onclick={() => onPlanOpen?.()}>Plan updated · {item.steps.length} {item.steps.length === 1 ? 'step' : 'steps'}</button>
    {:else if item.kind === 'tasks'}<TaskListItem {item} />
    {:else if item.kind === 'command'}<CommandItem {item} onFileLink={openFileLink} />
    {:else if item.kind === 'file'}<FileChangeItem {item} onFileLink={openFileLink} />
    {:else if item.kind === 'fileEdits'}<FileEditsItem {item} onFileLink={openFileLink} />
    {:else if item.kind === 'tool'}<ConversationToolUI itemId={item.itemId} {item} onFileLink={openFileLink} />
    {:else if item.kind === 'subagent'}<SubagentSection {item} />
    {:else if item.kind === 'approval'}<ApprovalItem {item} onDecision={onApprovalDecision} />
    {:else if item.kind === 'input'}<UserInputItem {item} />
    {:else if item.kind === 'error'}<ErrorItem {item} />
    {:else if item.kind === 'compaction'}<CompactionItem {item} />
    {:else if item.kind === 'unknown'}<div class="unknown-item" data-testid="timeline-unknown-item">{item.text}</div>{/if}
  </div>
{/if}

<style>
  /* The turn container owns the spacing. Items add only the small distinctions
     needed inside it, so a run of tools reads as one compact sequence while a
     prompt and its answer still remain separate. */
  .timeline-item{display:block;min-width:0}
  .timeline-item[data-kind='tool'],
  .timeline-item[data-kind='reasoning'],
  .timeline-item[data-kind='subagent'],
  .timeline-item[data-kind='command'],
  .timeline-item[data-kind='file'],
  .timeline-item[data-kind='fileEdits'],
  .timeline-item[data-kind='plan'],
  .timeline-item[data-kind='tasks']{margin-top:0}
  .timeline-item[data-kind='user']{margin-bottom:4px}
  .timeline-item[data-kind='assistant']{margin-top:2px}
  .timeline-item:first-child{margin-top:0}
  /* The plan is drawn once, in the chip above the composer. All the transcript
     owes the reader is that it moved, and a way back to it. */
  .plan-line{display:inline-flex;align-items:center;padding:0;border:0;background:transparent;color:var(--color-text-3);font:12px/1.4 inherit;cursor:pointer}
  .plan-line:hover{color:var(--color-text-2)}
  .plan-line:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:3px;border-radius:4px}
  .unknown-item{padding:12px;border-left:2px solid var(--color-border);color:var(--color-text-2);white-space:pre-wrap;font-size:13px}
</style>
