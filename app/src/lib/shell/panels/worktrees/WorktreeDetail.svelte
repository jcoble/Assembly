<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * WorktreeDetail.svelte — what is open under one worktree row.
   *
   * Three parts, and none of them is decided here. The plain facts about the
   * folder come from `worktreeFacts`; the verdict — what to do about this
   * worktree and why — comes from `worktreeSafety.ts` through the row; and the
   * three things that can be done about it come from `worktreeAgentPrompts.ts`.
   *
   * This used to end in three commands printed for the reader to copy into a
   * terminal themselves, because nothing in the app could be trusted to look
   * before it deleted. The two removals are still those commands — they are just
   * carried now by a session that reads the folder first, says what it found,
   * warns about anything that would be lost, and only then acts. The session
   * shows up in the sessions list like any other, so the work is watched rather
   * than taken on trust.
   *
   * Inspect starts nothing. The panel asks the app's small utility model about
   * the facts above and hands the answer back here, which is why it is the one
   * action with something to show under the buttons.
   *
   * What is on screen at rest is the short answer: the labelled facts, the one
   * sentence saying what this worktree needs, and the three things that can be
   * done about it. The paragraphs behind that answer — why it says what it
   * says, what to check first, and what pressing a button actually starts — sit
   * behind "Why", because a reader who already knows was reading past them
   * every single time a row was opened.
   */
  import type { Component } from 'svelte';
  import Archive from '@lucide/svelte/icons/archive';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ScanSearch from '@lucide/svelte/icons/scan-search';
  import Trash2 from '@lucide/svelte/icons/trash-2';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { exactLocalTime } from '$lib/shell/relativeTime';
  import { worktreeFacts, type WorktreeManagerRow } from '$lib/shell/worktrees/worktreeManagerRows';
  import { cn } from '$lib/utils';

  import type { WorktreeAgentAction, WorktreeAgentActionId } from './worktreeAgentPrompts.ts';

  interface Props {
    row: WorktreeManagerRow;
    /** The three agent actions for this row, already worked out. */
    actions: WorktreeAgentAction[];
    /** A session is being started for this row. */
    busy: boolean;
    /** Something is happening somewhere in the panel; every button waits. */
    panelBusy: boolean;
    /** The helper is being asked about this worktree right now. */
    inspecting: boolean;
    /** What the helper said about this worktree, or '' before it was asked. */
    inspectMessage: string;
    /** The message is why the helper could not answer, not an answer. */
    inspectFailed: boolean;
    onAction(id: WorktreeAgentActionId): void;
    /** Focus a session that worked here. Left out, the sessions are plain text. */
    onOpenSession?: (ownedId: string) => void;
  }
  let {
    row,
    actions,
    busy,
    panelBusy,
    inspecting,
    inspectMessage,
    inspectFailed,
    onAction,
    onOpenSession
  }: Props = $props();

  const LANE_TONE: Record<string, 'bad' | 'attention' | 'live' | 'neutral' | 'good'> = {
    blocked: 'bad',
    backup: 'attention',
    cleanup: 'live',
    review: 'neutral',
    protected: 'good'
  };

  const ICONS: Record<WorktreeAgentActionId, Component> = {
    inspect: ScanSearch,
    'archive-and-remove': Archive,
    remove: Trash2
  };

  const facts = $derived(worktreeFacts(row));

  /** The paragraphs behind the verdict are open. Closed again when the row is. */
  let whyOpen = $state(false);

  /** The two lines of section label, so they cannot drift apart. */
  const SECTION_LABEL =
    'm-0 text-(length:--text-quiet) leading-normal font-semibold text-muted-foreground';

  /**
   * A folder path, shortened from the middle so both ends survive: the repo the
   * worktree belongs to is at the front and the folder's own name is at the
   * back, and it is the stretch between them nobody reads. The whole path is on
   * the element's title, so hovering still gives the real thing.
   */
  function shortenPath(value: string): string {
    const budget = 42;
    if (value.length <= budget) return value;
    const head = Math.ceil((budget - 1) / 2);
    const tail = budget - 1 - head;
    return `${value.slice(0, head)}…${value.slice(value.length - tail)}`;
  }
</script>

<div class="flex flex-col gap-4">
  <!-- The plain facts, one labelled line each, on a hairline the eye can run
       along. The 8px between label and value is the label's own padding rather
       than a grid gap, so that hairline is unbroken. Anything unknown is
       absent. -->
  <dl class="m-0 grid grid-cols-[auto_minmax(0,1fr)]">
    {#each facts as fact, index (fact.label)}
      {@const ruled = index < facts.length - 1 && 'border-b border-border'}
      {@const isPath = fact.label === 'Folder'}
      <dt
        class={cn(
          'm-0 flex items-center py-2 pr-(--space-2) text-(length:--text-quiet) leading-normal text-muted-foreground',
          ruled
        )}
      >
        {fact.label}
      </dt>
      <dd
        class={cn(
          'm-0 flex min-w-0 items-center justify-end py-2 text-(length:--text-body) leading-normal text-foreground',
          ruled
        )}
        title={isPath ? fact.value : fact.stamp ? exactLocalTime(fact.stamp) : undefined}
      >
        <span class="truncate">{isPath ? shortenPath(fact.value) : fact.value}</span>
      </dd>
    {/each}
  </dl>

  <!-- The verdict: one sentence, with the reasoning a click away. -->
  <div class="flex flex-col gap-2">
    <div class="flex items-start gap-2">
      <Chip
        tone={LANE_TONE[row.lane.tone] ?? 'neutral'}
        class="mt-px h-6 px-2.5 text-(length:--text-quiet) font-normal"
      >
        {row.lane.label}
      </Chip>
      <p class="m-0 min-w-0 flex-1 text-(length:--text-body) leading-snug text-foreground">
        {row.lane.detail}
      </p>
    </div>

    <div class="flex flex-col">
      <button
        type="button"
        class="flex w-fit items-center gap-1 rounded-lg py-1 text-(length:--text-quiet)
               leading-normal text-muted-foreground transition-colors outline-none
               hover:text-foreground focus-visible:ring-3 focus-visible:ring-ring/50"
        aria-expanded={whyOpen}
        onclick={() => (whyOpen = !whyOpen)}
      >
        <ChevronRight
          class={cn('size-3.5 transition-transform', whyOpen && 'rotate-90')}
          aria-hidden="true"
        />
        Why
      </button>
      {#if whyOpen}
        <div class="flex flex-col gap-2 pt-1 pl-(--space-5)">
          <p class="m-0 text-(length:--text-quiet) leading-normal text-muted-foreground">
            {row.safety.reason}. {row.safety.recommendation}
          </p>
          {#if row.safety.decisionChecklist.length > 0}
            <ul class="m-0 flex list-none flex-col gap-1 p-0">
              {#each row.safety.decisionChecklist as item, index (index)}
                <li
                  class="flex gap-2 text-(length:--text-quiet) leading-normal text-muted-foreground"
                >
                  <span aria-hidden="true">•</span>
                  <span>{item}</span>
                </li>
              {/each}
            </ul>
          {/if}
          <p class="m-0 text-(length:--text-quiet) leading-normal text-muted-foreground">
            Inspect answers below from what this panel already knows. The other two
            start a session in this folder: it reads the worktree, explains what it
            found, warns about anything that would be lost, and only then does what
            you asked.
          </p>
        </div>
      {/if}
    </div>
  </div>

  <!-- Who has worked in this folder. Named, because "2 sessions" is not enough
       to decide whether removing the folder would interrupt anybody. -->
  {#if row.sessions.length > 0}
    <div class="flex flex-col gap-2">
      <h4 class={SECTION_LABEL}>Sessions here</h4>
      <ul class="m-0 flex min-w-0 list-none flex-wrap gap-1.5 p-0">
        {#each row.sessions as link (link.ownedId)}
          {@const label = `${link.title.trim() || 'Untitled session'}${
            link.isRunning ? ' · running' : ''
          }`}
          <li class="min-w-0 max-w-full">
            {#if onOpenSession}
              <Button
                size="sm"
                variant="secondary"
                class="max-w-full min-w-0 rounded-full text-(length:--text-quiet)"
                title={label}
                onclick={() => onOpenSession?.(link.ownedId)}
              >
                <span class="truncate">{label}</span>
              </Button>
            {:else}
              <Chip class="max-w-full min-w-0"><span class="truncate" title={label}>{label}</span></Chip>
            {/if}
          </li>
        {/each}
      </ul>
    </div>
  {/if}

  <!-- The three things that can be done about this worktree. The two removals
       start a session that looks first and says what it found before it acts;
       Inspect asks the helper and answers underneath. Inspect is the one that
       cannot lose anything, so it is the only filled button; the two that can
       are offered as words and ask a question first. -->
  <div class="flex flex-col gap-2">
    <h4 class={SECTION_LABEL}>What to do about it</h4>
    <div class="flex flex-wrap items-center gap-1">
      {#each actions as action (action.id)}
        {@const Icon = ICONS[action.id]}
        <Button
          variant={action.destructive ? 'ghost' : 'default'}
          class={cn(
            'rounded-full text-(length:--text-quiet)',
            action.destructive ? 'px-3 text-muted-foreground hover:text-foreground' : 'px-4'
          )}
          disabled={!action.enabled || panelBusy || (action.id === 'inspect' && inspecting)}
          onclick={() => onAction(action.id)}
        >
          <Icon aria-hidden="true" />
          {action.label}
        </Button>
      {/each}
    </div>
    {#if actions.some((action) => !action.enabled)}
      <p class="m-0 text-(length:--text-quiet) leading-normal text-muted-foreground">
        {actions.find((action) => !action.enabled)?.disabledReason}
      </p>
    {/if}
    {#if busy}
      <p class="m-0 text-(length:--text-quiet) leading-normal text-muted-foreground">
        <WorkingSpinner size={12} /> Starting a session…
      </p>
    {/if}

    <!-- What the helper said, kept under the row it is about. -->
    {#if inspecting}
      <p class="m-0 text-(length:--text-quiet) leading-normal text-muted-foreground">Inspecting…</p>
    {:else if inspectMessage}
      <p
        class={cn(
          'm-0 text-(length:--text-body) leading-normal break-words',
          inspectFailed ? 'text-[var(--color-bad)]' : 'text-foreground'
        )}
      >
        {inspectMessage}
      </p>
    {/if}
  </div>
</div>
