<!-- The Board's left column: the cards agents write, in sections by state. -->
<script lang="ts">
  import AlarmClock from '@lucide/svelte/icons/alarm-clock';
  import Check from '@lucide/svelte/icons/check';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
  import Trash2 from '@lucide/svelte/icons/trash-2';
  import Workflow from '@lucide/svelte/icons/workflow';

  import { Button, buttonVariants } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { openUrlInBrowser } from '$lib/shell/workbenchNavigation.ts';
  import { cn } from '$lib/utils.js';
  import BoardNotes from './BoardNotes.svelte';
  import BoardQuestions from './BoardQuestions.svelte';
  import { changeBoardCard, type BoardCard, type BoardCardAction, type MachineBoard } from './boardSource.ts';

  interface Props {
    boards: MachineBoard[];
    /** The header's filter: every section, or one state's. */
    filter: 'all' | BoardCard['state'];
    /** Notion Tasks is connected, so task keys and links show. */
    notion: boolean;
    /** Aborted when the board closes. */
    signal: AbortSignal;
    onError(text: string): void;
    onClose(): void;
    onOpenSession(ownedId: string): void;
  }
  let { boards, filter, notion, signal, onError, onClose, onOpenSession }: Props = $props();

  type Entry = { id: string; machine: string; card: BoardCard };
  const SECTIONS = [
    { state: 'needs_you', label: 'On hold · needs you', hint: 'Waiting on you; the orchestrator clears them', dot: 'bg-(--color-attention)' },
    { state: 'running', label: 'Running', hint: 'Agents are working on these', dot: 'bg-(--color-live)' },
    { state: 'in_review', label: 'In review', hint: 'Checks or a reviewer have it', dot: 'bg-(--color-merged)' }
  ] as const;
  const LINK = 'min-w-0 truncate p-0 text-left hover:underline';

  /** Cards dropped while the board is open; each stays as "Dropped · Undo" until it closes. */
  let dropped = $state<Entry[]>([]);
  /** Cards the owner opened; every card starts shut, so a waiting card is one small row. */
  let toggled = $state<Record<string, boolean>>({});
  let doneOpen = $state(false);
  let busy = $state(false);

  const entries = $derived(boards.flatMap((board) =>
    board.value.cards.map((card) => ({ id: `${board.machine}:${card.key}`, machine: board.machine, card }))));
  const done = $derived(inState('done'));
  const questions = $derived(boards.flatMap((board) => board.value.questions ?? []));
  const openQuestions = $derived(questions.filter((q) => q.answered_at_ms === null).length);

  const snoozed = (card: BoardCard): boolean => card.snoozed_until_ms !== null && card.snoozed_until_ms > Date.now();
  const isOpen = (entry: Entry): boolean => toggled[entry.id] ?? false;
  const isDropped = (entry: Entry): boolean => dropped.some((other) => other.id === entry.id);
  const machineLabel = (machine: string): string => projectMachineLabel(machine === 'mac' ? 'local' : machine, rail.remoteProfiles);
  const sessionTitle = (ownedId: string): string => rail.owned.find((session) => session.ownedId === ownedId)?.title || 'Session';

  /** A section's cards across machines: snoozed ones last, then newest first. */
  function inState(state: BoardCard['state']): Entry[] {
    const live = entries.filter((entry) => entry.card.state === state && !isDropped(entry));
    return [...live, ...dropped.filter((entry) => entry.card.state === state)].sort((a, b) =>
      Number(snoozed(a.card)) - Number(snoozed(b.card)) || b.card.updated_at_ms - a.card.updated_at_ms);
  }

  /** A state's cards on every machine, past the 40-card limit, less those dropped before the reload. */
  function total(state: BoardCard['state']): number {
    return boards.reduce((sum, board) => sum + (board.value.counts[state] ?? 0), 0)
      - entries.filter((entry) => entry.card.state === state && isDropped(entry)).length;
  }

  /** Cards the machines counted but did not send. */
  function notShown(state: BoardCard['state'], list: Entry[]): number {
    return total(state) - list.filter((entry) => !isDropped(entry)).length;
  }

  function when(ms: number): string {
    const date = new Date(ms);
    return date.toDateString() === new Date().toDateString()
      ? date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
      : date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  /** "PR #344" from a GitHub pull request link, else the link without its scheme. */
  const prLabel = (url: string): string => {
    const number = /\/pull\/(\d+)/.exec(url)?.[1];
    return number ? `PR #${number}` : url.replace(/^https?:\/\//, '');
  };

  function tomorrowAtNine(): number {
    const date = new Date();
    date.setDate(date.getDate() + 1);
    date.setHours(9, 0, 0, 0);
    return date.getTime();
  }

  function message(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
  }

  /** Owner actions go to the machine that owns the card; the change signal reloads the board. */
  async function act(entry: Entry, action: BoardCardAction, at = Date.now()): Promise<void> {
    busy = true;
    onError('');
    try {
      const changed = await changeBoardCard(entry.machine, entry.card.key, action, at, signal);
      if (signal.aborted) return;
      if (action === 'drop' && changed) dropped = [...dropped, entry];
      if (action === 'undo') dropped = dropped.filter((other) => other.id !== entry.id);
    } catch (cause) {
      if (!signal.aborted) onError(`Could not change "${entry.card.title}": ${message(cause)}`);
    }
    if (!signal.aborted) busy = false;
  }

  async function clearDone(): Promise<void> {
    busy = true;
    onError('');
    try {
      for (const board of boards.filter((board) => board.value.counts.done)) {
        await changeBoardCard(board.machine, '', 'clear_done', Date.now(), signal);
      }
    } catch (cause) {
      if (!signal.aborted) onError(`Could not clear done cards: ${message(cause)}`);
    }
    if (!signal.aborted) busy = false;
  }

  function follow(url: string): void {
    onClose();
    void openUrlInBrowser({ url });
  }
</script>

{#snippet heading(label: string, hint: string, count: number, dot: string)}
  <h3><i class={`dot ${dot}`}></i>{label}</h3><span class="muted">{hint}</span><strong>{count}</strong>
{/snippet}

{#snippet more(count: number)}
  {#if count > 0}<p class="muted px-0.5">{count} more not shown. Mark cards done or drop them to see the rest.</p>{/if}
{/snippet}

{#snippet cardRow(entry: Entry)}
  {@const card = entry.card}
  {#if isDropped(entry)}
    <li class="fold-row">
      <span class="truncate"><span class="quiet">Dropped</span> {card.title}</span>
      <Button size="xs" variant="ghost" disabled={busy} onclick={() => void act(entry, 'undo')}>Undo</Button>
    </li>
  {:else if snoozed(card)}
    <li class="fold-row">
      <span class="truncate"><span class="quiet">Snoozed</span> {card.title}</span>
      <span class="flex-none text-(--color-text)">until {when(card.snoozed_until_ms ?? 0)}</span>
    </li>
  {:else}
    {@const open = isOpen(entry)}
    <li class="card">
      <button type="button" class="chead" aria-expanded={open} onclick={() => (toggled[entry.id] = !open)}>
        <ChevronRight class={cn('size-3.5 shrink-0 text-(--color-text-3) transition-transform', open && 'rotate-90')} aria-hidden="true" />
        {#if notion && card.task_key}<span class="key">{card.task_key}</span>{/if}
        <span class={cn('ctitle', open && 'wrap')}>{card.title}</span>
        <span class="ctime">{when(card.updated_at_ms)}</span>
      </button>
      {#if card.summary}<p class={cn('sum', !open && 'one')}>{card.summary}</p>{/if}
      {#if card.stale}<p class="stale">No update since {when(card.updated_at_ms)}</p>{/if}
      {#if !open}
        {#if card.steps.length}
          <p class="first-step">
            <span class="circ">1</span><span class="truncate">{card.steps[0]}</span>
            {#if card.steps.length > 1}<span class="more">· {card.steps.length} steps</span>{/if}
          </p>
        {/if}
        {#if card.pr_url || card.run_id}
          <p class="cmeta">
            {#if card.pr_url}<span class="linkchip"><GitPullRequest class="size-3" aria-hidden="true" />{prLabel(card.pr_url)}</span>{/if}
            {#if card.run_id}<span class="linkchip"><Workflow class="size-3" aria-hidden="true" />{[card.run_step, card.run_state ?? 'Not found'].filter(Boolean).join(' · ')}</span>{/if}
          </p>
        {/if}
      {:else}
        <div class="detail">
          {#if card.steps.length}
            <p class="sh">What to do<span class="quiet-label">{card.steps.length} {card.steps.length === 1 ? 'step' : 'steps'}</span></p>
            <ol class="steps">
              {#each card.steps as step, index (index)}
                <li><span class="circ">{index + 1}</span><span class="min-w-0">{step}</span></li>
              {/each}
            </ol>
          {/if}
          <div class="actions">
            {#if card.pr_url}
              {@const url = card.pr_url}
              <Button size="sm" onclick={() => follow(url)}><ExternalLink />Open {prLabel(url)}</Button>
            {/if}
            {#if card.state !== 'done'}
              <Button size="sm" variant="secondary" disabled={busy} onclick={() => void act(entry, 'done')}><Check />Mark done</Button>
            {/if}
            {#if card.state === 'needs_you'}
              <DropdownMenu.Root>
                <DropdownMenu.Trigger class={buttonVariants({ variant: 'secondary', size: 'sm' })} disabled={busy}><AlarmClock />Snooze<ChevronDown /></DropdownMenu.Trigger>
                <DropdownMenu.Content align="start" class="w-48">
                  <DropdownMenu.Item onSelect={() => void act(entry, 'snooze', Date.now() + 3_600_000)}>For 1 hour</DropdownMenu.Item>
                  <DropdownMenu.Item onSelect={() => void act(entry, 'snooze', tomorrowAtNine())}>Until tomorrow 9 AM</DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu.Root>
            {/if}
            <Button size="sm" variant="secondary" disabled={busy} onclick={() => void act(entry, 'drop')}><Trash2 />Drop</Button>
          </div>
          {#if card.note}<div class="well"><span>Note for you</span><p>{card.note}</p></div>{/if}
          {#if card.pr_url || (notion && card.task_url) || card.run_id || card.session_owned_id}
            <div class="metrics">
              {#if card.pr_url}
                {@const url = card.pr_url}
                <span><small>Pull request</small><strong><button type="button" class={LINK} onclick={() => follow(url)}>{prLabel(url)}</button></strong><em>{url.replace(/^https?:\/\//, '')}</em></span>
              {/if}
              {#if notion && card.task_url}
                {@const url = card.task_url}
                <span><small>Task</small><strong><button type="button" class={LINK} onclick={() => follow(url)}>{card.task_key ?? url}</button></strong></span>
              {/if}
              {#if card.run_id}
                <span><small>Workflow</small><strong>{card.run_state ?? 'Not found'}</strong>{#if card.run_step}<em>Step {card.run_step}</em>{/if}</span>
              {/if}
              {#if card.session_owned_id}
                {@const ownedId = card.session_owned_id}
                <span><small>Session</small><strong><button type="button" class={LINK} onclick={() => onOpenSession(ownedId)}>{sessionTitle(ownedId)}</button></strong><em>{card.session_state ?? 'Closed'}</em></span>
              {/if}
            </div>
          {/if}
          <section class="notes">
            <h4 class="notes-head">Your notes<span>{card.notes.length ? `${card.notes.length} ${card.notes.length === 1 ? 'note' : 'notes'} · ${when(card.notes[0].created_at_ms)}` : 'None yet'}</span></h4>
            <BoardNotes notes={card.notes} machine={entry.machine} target={`card:${card.key}`} {signal} adding {onError} />
          </section>
          <p class="foot"><span class="truncate">Written by {card.writer_name ?? card.written_by} · {machineLabel(entry.machine)}</span><span class="flex-none">Updated {when(card.updated_at_ms)}</span></p>
        </div>
      {/if}
    </li>
  {/if}
{/snippet}

<div class="col" data-testid="board-cards">
  {#if entries.length === 0 && dropped.length === 0 && questions.length === 0}
    <p class="muted px-1 py-6 text-center">No cards yet. Agents post them with the <code>board_update</code> tool.</p>
  {:else if filter !== 'all' && !total(filter) && !inState(filter).length && !(filter === 'needs_you' && openQuestions)}
    <p class="muted px-1 py-6 text-center">No cards here.</p>
  {/if}
  {#if filter === 'all' || filter === 'needs_you'}<BoardQuestions {boards} {filter} {signal} {onError} {onOpenSession} />{/if}
  {#each SECTIONS as section (section.state)}
    {@const list = inState(section.state)}
    {#if (filter === 'all' || filter === section.state) && (list.length || total(section.state))}
      <section class="panel">
        <div class="ph">{@render heading(section.label, section.hint, total(section.state), section.dot)}</div>
        <ul class="list">
          {#each list as entry (entry.id)}{@render cardRow(entry)}{/each}
        </ul>
        {@render more(notShown(section.state, list))}
      </section>
    {/if}
  {/each}
  {#if (filter === 'all' || filter === 'done') && (done.length || total('done'))}
    <section class="panel">
      <div class="flex items-center gap-2">
        <h3 class="min-w-0 flex-1">
          <button type="button" class="done-toggle" aria-expanded={doneOpen} onclick={() => (doneOpen = !doneOpen)}>
            <i class="dot bg-(--color-good)"></i>Done today
            <span class="ml-auto font-normal text-(--color-text-2) tabular-nums">{total('done')} {total('done') === 1 ? 'card' : 'cards'}</span>
            <ChevronDown class={cn('size-3.5 text-(--color-text-2) transition-transform', doneOpen && 'rotate-180')} aria-hidden="true" />
          </button>
        </h3>
        <Button size="xs" variant="secondary" disabled={busy} onclick={() => void clearDone()}>Clear</Button>
      </div>
      {#if doneOpen}
        <ul class="list">
          {#each done as entry (entry.id)}{@render cardRow(entry)}{/each}
        </ul>
        {@render more(notShown('done', done))}
      {/if}
    </section>
  {/if}
</div>

<style>
  /* Surfaces from the Usage screen (UsageWorkspace.svelte): .panel, .provider-card, .provider-metrics, .quota-empty. */
  /* Panels carry a full hairline and cards sit a step lighter with their own edge, so every boundary reads. */
  .col { display: flex; flex-direction: column; gap: 10px; min-width: 0; min-height: 0; overflow-y: auto; }
  .panel { display: flex; flex: none; flex-direction: column; gap: 10px; padding: 12px; border: 1px solid var(--color-border); border-radius: var(--radius-lg); background: color-mix(in srgb, var(--color-elevated) 38%, var(--color-surface)); }
  .ph { display: flex; align-items: baseline; gap: 8px; min-width: 0; padding: 0 2px; }
  h3 { margin: 0; display: flex; align-items: center; gap: 7px; font-size: 0.88rem; line-height: 1.35; font-weight: 600; white-space: nowrap; }
  .ph strong { margin-left: auto; font-size: 0.88rem; font-variant-numeric: tabular-nums; }
  .muted { margin: 0; min-width: 0; overflow: hidden; color: var(--color-text-2); font-size: 0.76rem; line-height: 1.45; text-overflow: ellipsis; }
  .ph .muted { white-space: nowrap; }
  .dot { display: inline-block; flex: none; width: 8px; height: 8px; border-radius: 2px; }
  .list, .steps { display: flex; flex-direction: column; margin: 0; padding: 0; list-style: none; }
  .list { gap: 8px; }
  .card { min-width: 0; padding: 10px 12px 11px; border: 1px solid var(--color-border); border-radius: var(--radius-md); background: color-mix(in srgb, var(--color-elevated) 72%, var(--color-surface)); }
  .chead, .done-toggle { display: flex; align-items: center; gap: 8px; width: 100%; min-width: 0; border-radius: var(--radius-sm); text-align: left; }
  /* A wrapped title keeps the chevron, key and time on its first line. */
  .chead { align-items: flex-start; }
  .chead > :global(svg) { margin-top: 3px; }
  .chead .key { margin-top: 1px; }
  .done-toggle { padding: 2px; font: inherit; font-weight: 600; }
  .chead:focus-visible, .done-toggle:focus-visible { outline: none; box-shadow: var(--focus-ring); }
  .key { flex: none; padding: 1px 6px; border-radius: var(--radius-xs); background: color-mix(in srgb, var(--color-bg) 44%, transparent); color: var(--color-text-2); font-size: 12px; font-weight: 600; font-variant-numeric: tabular-nums; }
  .ctitle { min-width: 0; overflow: hidden; font-size: 14px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
  .ctitle.wrap { white-space: normal; overflow-wrap: anywhere; }
  .ctime { flex: none; margin-left: auto; padding-top: 2px; color: var(--color-text-3); font-size: 0.75rem; font-variant-numeric: tabular-nums; }
  .sum { margin: 4px 0 0; color: var(--color-text-2); font-size: 13px; line-height: 1.45; white-space: pre-wrap; overflow-wrap: anywhere; }
  .sum.one { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .stale { margin: 3px 0 0; color: var(--color-attention); font-size: 0.75rem; }
  .first-step { display: flex; align-items: center; gap: 8px; min-width: 0; margin: 6px 0 0; font-size: 13px; }
  .more { flex: none; color: var(--color-text-3); font-size: 0.75rem; }
  .circ { display: grid; flex: none; place-items: center; width: 18px; height: 18px; border-radius: 50%; background: color-mix(in srgb, var(--color-text) 10%, transparent); color: var(--color-text-2); font-size: 12px; font-weight: 700; font-variant-numeric: tabular-nums; }
  .cmeta { display: flex; align-items: center; gap: 6px; min-width: 0; margin: 6px 0 0; }
  .linkchip { display: inline-flex; align-items: center; gap: 5px; min-width: 0; height: 22px; padding: 0 8px; border-radius: var(--radius-sm); background: color-mix(in srgb, var(--color-bg) 44%, transparent); color: var(--color-text-2); font-size: 12px; white-space: nowrap; }
  .detail { display: flex; flex-direction: column; gap: 10px; margin-top: 10px; }
  .sh { display: flex; align-items: baseline; margin: 0 2px; font-size: 0.75rem; font-weight: 600; }
  .quiet-label { margin-left: auto; color: var(--color-text-3); font-weight: 400; white-space: nowrap; }
  .steps li { display: flex; align-items: center; gap: 10px; padding: 5px 2px; font-size: 13px; line-height: 1.4; border-top: 1px solid color-mix(in srgb, var(--color-border) 28%, transparent); }
  .steps li:first-child { border-top: 0; }
  .actions { display: flex; flex-wrap: wrap; gap: 6px; }
  .well { display: grid; gap: 2px; padding: 7px 10px; border-radius: var(--radius-sm); background: color-mix(in srgb, var(--color-bg) 48%, transparent); }
  .well > span { font-size: 0.75rem; font-weight: 600; }
  .well p { margin: 0; color: var(--color-text-2); font-size: 13px; line-height: 1.45; white-space: pre-wrap; overflow-wrap: anywhere; }
  .metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 6px; }
  .metrics > span { display: grid; gap: 1px; min-width: 0; padding: 7px 10px; border-radius: var(--radius-sm); background: color-mix(in srgb, var(--color-bg) 44%, transparent); }
  .metrics small { color: var(--color-text-2); font-size: 0.75rem; }
  .metrics strong { display: flex; min-width: 0; overflow: hidden; font-size: 0.82rem; line-height: 1.25; text-overflow: ellipsis; white-space: nowrap; }
  .metrics em { overflow: hidden; color: var(--color-text-3); font-size: 0.75rem; font-style: normal; text-overflow: ellipsis; white-space: nowrap; }
  .notes { display: flex; flex-direction: column; gap: 4px; padding-top: 8px; border-top: 1px solid color-mix(in srgb, var(--color-border) 28%, transparent); }
  .notes-head { display: flex; justify-content: space-between; gap: 12px; margin: 0 2px 2px; color: var(--color-text-2); font-size: 0.75rem; font-weight: 400; }
  .notes-head > span { color: var(--color-text); font-variant-numeric: tabular-nums; }
  .foot { display: flex; gap: 8px; margin: 0; color: var(--color-text-3); font-size: 0.75rem; }
  .foot > span:last-child { margin-left: auto; }
  .fold-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-width: 0; min-height: 34px; padding: 4px 6px 4px 12px; border: 1px dashed var(--color-border); border-radius: var(--radius-md); color: var(--color-text-2); font-size: 0.75rem; }
  .quiet { color: var(--color-text-3); }
</style>
