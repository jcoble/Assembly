<!--
  The Board drop-down: cards agents write on the left, live state on the right.
  Mounted only while open; closing it drops everything it read.
-->
<script lang="ts">
  import { untrack } from 'svelte';

  import * as Popover from '$lib/components/ui/popover/index.js';
  import { readNotionTaskSettings } from '$lib/shell/notionTasks.ts';
  import type { OwnedSession } from '$lib/shell/ownedSessions.ts';
  import { openInboxSession } from '$lib/shell/workflows/ownerInboxActions.ts';
  import BoardCards from './BoardCards.svelte';
  import BoardLive from './BoardLive.svelte';
  import { readBoard, type MachineBoard } from './boardSource.ts';

  interface Props {
    /** The button's badge number: what needs the owner. */
    badge: number;
    /** Sessions waiting on the owner, from the rail. */
    sessions: OwnedSession[];
    /** Changes whenever the board may have changed. */
    version: number;
    onClose(): void;
  }
  let { badge, sessions, version, onClose }: Props = $props();

  let boards = $state<MachineBoard[]>([]);
  let loadedAt = $state<number | null>(null);
  let loadError = $state('');
  /** The last card or step action that failed; the next action clears it. */
  let actionError = $state('');
  let notion = $state(false);
  // Lives while the board is open; an action that finishes after it closes changes nothing.
  const shown = new AbortController();
  let loader: AbortController | null = null;
  // A newer click makes a slow session lookup stale.
  let opening = 0;

  $effect(() => {
    void readNotion();
    return () => {
      shown.abort();
      loader?.abort();
    };
  });

  $effect(() => {
    version;
    untrack(() => void load());
  });

  function message(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
  }

  /** Task keys and Notion links show only when Notion Tasks has both a data source and a token. */
  async function readNotion(): Promise<void> {
    try {
      const settings = await readNotionTaskSettings();
      if (!shown.signal.aborted) notion = Boolean(settings.dataSourceId && settings.hasToken);
    } catch {
      // Not connected: the board shows no task keys.
    }
  }

  /** Opens a session at its latest message, as the Inbox does, and closes the board. */
  async function openSession(ownedId: string): Promise<void> {
    const attempt = ++opening;
    try {
      await openInboxSession(ownedId, shown.signal, () => attempt === opening && !shown.signal.aborted);
      if (attempt === opening) onClose();
    } catch (cause) {
      if (attempt === opening && !shown.signal.aborted) actionError = `Could not open session: ${message(cause)}`;
    }
  }

  async function load(): Promise<void> {
    loader?.abort();
    const controller = (loader = new AbortController());
    const now = new Date();
    const midnight = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    try {
      const found = await readBoard(midnight, now.getTime(), controller.signal);
      if (!found || controller.signal.aborted) return;
      boards = found;
      loadedAt = Date.now();
      loadError = '';
    } catch (cause) {
      if (!controller.signal.aborted) loadError = `Could not load the board: ${message(cause)}`;
    }
  }

  /** Counted in SQL on each machine, so cards past the 40-card limit are included. */
  const count = (state: 'running' | 'in_review' | 'done'): number =>
    boards.reduce((sum, board) => sum + (board.value.counts[state] ?? 0), 0);
  /** Which sections the left column shows; the right column always shows everything. */
  let filter = $state<'all' | 'needs_you' | 'running' | 'in_review' | 'done'>('all');
  const FILTERS = [
    { value: 'all', label: 'All' }, { value: 'needs_you', label: 'Needs you' }, { value: 'running', label: 'Running' },
    { value: 'in_review', label: 'In review' }, { value: 'done', label: 'Done' }
  ] as const;
  const time = (ms: number): string => new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
</script>

<Popover.Content
  role="dialog"
  aria-label="Board"
  side="bottom"
  align="end"
  collisionPadding={{ bottom: 36, right: 8, left: 8 }}
  trapFocus={false}
  class="max-h-(--bits-popover-content-available-height) w-[min(900px,calc(100vw-16px))] gap-0 overflow-hidden rounded-(--radius-lg) border border-(--color-border) bg-(--color-surface) p-0 shadow-(--shadow-lg) ring-0"
  data-testid="board"
>
  <header class="header">
    <div class="title-block">
      <p class="eyebrow">Build board</p>
      <h2>{badge === 0 ? 'Nothing needs you' : `${badge} ${badge === 1 ? 'thing needs' : 'things need'} you`}</h2>
      <p class="muted truncate">
        {[new Date().toLocaleDateString([], { weekday: 'long', month: 'short', day: 'numeric' }), loadedAt ? `updated ${time(loadedAt)}` : null,
          `${count('running')} running`, `${count('in_review')} in review`, `${count('done')} done today`].filter(Boolean).join(' · ')}
      </p>
    </div>
    <div class="range-selector" role="group" aria-label="Show">
      {#each FILTERS as item (item.value)}
        <button type="button" class:active={filter === item.value} aria-pressed={filter === item.value} onclick={() => (filter = item.value)}>{item.label}</button>
      {/each}
    </div>
  </header>
  {#if actionError || loadError}<p role="alert" class="flex-none px-3.5 pb-2 text-[13px] text-destructive">{actionError || loadError}</p>{/if}
  <div class="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_minmax(0,318px)] gap-2.5 px-3.5 pb-3.5">
    <BoardCards {boards} {notion} {filter} signal={shown.signal} onError={(text) => (actionError = text)} {onClose} onOpenSession={openSession} />
    <BoardLive {boards} {sessions} {notion} signal={shown.signal} onError={(text) => (actionError = text)} onOpenSession={openSession} />
  </div>
</Popover.Content>

<style>
  /* The Usage screen's header and segmented control (UsageWorkspace.svelte), at the board's density. */
  .header { display: flex; flex: none; justify-content: space-between; align-items: center; gap: 16px; padding: 12px 14px 10px; }
  .title-block { display: grid; gap: 2px; min-width: 0; }
  .eyebrow { margin: 0; color: var(--color-text-3); font-size: 0.75rem; font-weight: 680; letter-spacing: 0.085em; text-transform: uppercase; line-height: 1.2; }
  h2 { margin: 0; font-size: 1.15rem; line-height: 1.2; letter-spacing: -0.028em; font-weight: 600; }
  .muted { margin: 0; color: var(--color-text-2); font-size: 0.76rem; line-height: 1.45; }
  .range-selector { display: flex; flex: none; gap: 2px; padding: 3px; border-radius: var(--radius-md); background: color-mix(in srgb, var(--color-bg) 68%, var(--color-surface)); }
  .range-selector button { min-height: 28px; border-radius: var(--radius-sm); padding: 4px 10px; color: var(--color-text-2); font-size: 13px; }
  .range-selector button:hover { color: var(--color-text); background: var(--color-hover); }
  .range-selector button.active { color: var(--color-text); background: var(--color-elevated); }
  .range-selector button:focus-visible { outline: none; box-shadow: var(--focus-ring); }
</style>
