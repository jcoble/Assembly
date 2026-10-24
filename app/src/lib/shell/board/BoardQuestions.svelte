<!-- The Board's Questions panel: what agents asked the owner in chat, answered here or there. -->
<script lang="ts">
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ExternalLink from '@lucide/svelte/icons/external-link';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { cn } from '$lib/utils.js';
  import { answerQuestion, questionState } from '../workflows/ownerInboxActions.ts';
  import { answerBoardQuestion, type BoardQuestion, type MachineBoard } from './boardSource.ts';
  import { isFoldOpen, readFolds, writeFolds } from './boardQuestionFolds.ts';

  interface Props {
    boards: MachineBoard[];
    /** Answered questions show only under All. */
    filter: 'all' | 'needs_you';
    /** Aborted when the board closes. */
    signal: AbortSignal;
    onError(text: string): void;
    onOpenSession(ownedId: string): void;
  }
  let { boards, filter, signal, onError, onOpenSession }: Props = $props();

  type Row = { id: string; machine: string; q: BoardQuestion };
  const rows = $derived(boards.flatMap((board) =>
    (board.value.questions ?? []).map((q) => ({ id: `${board.machine}:${q.id}`, machine: board.machine, q }))));
  const open = $derived(rows.filter((row) => row.q.answered_at_ms === null));
  const answered = $derived(rows.filter((row) => row.q.answered_at_ms !== null));
  const showAnswered = $derived(filter === 'all' && answered.length > 0);
  const ended = (row: Row): boolean => row.q.session_state === 'closed' || row.q.session_state === 'failed';
  const liveOpen = $derived(open.filter((row) => !ended(row)));

  /** Rows the owner opened or shut, saved so closing the board or the app keeps them. */
  let folds = $state(readFolds(localStorage));
  let drafts = $state<Record<string, string>>({});
  const ANSWERED_OPEN_KEY = 'assembly.board.answeredOpen';
  let answeredOpen = $state(localStorage.getItem(ANSWERED_OPEN_KEY) === 'true');

  const isOpen = (row: Row): boolean => isFoldOpen(folds, row.id, row.q.answered_at_ms !== null);
  function fold(row: Row, value: boolean): void {
    delete folds[row.id];
    folds[row.id] = value;
    writeFolds(localStorage, folds);
  }
  function toggleAnswered(): void {
    answeredOpen = !answeredOpen;
    localStorage.setItem(ANSWERED_OPEN_KEY, String(answeredOpen));
  }
  const machineLabel = (machine: string): string => projectMachineLabel(machine === 'mac' ? 'local' : machine, rail.remoteProfiles);

  function when(ms: number): string {
    const date = new Date(ms);
    return date.toDateString() === new Date().toDateString()
      ? date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
      : date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  /** "asked 2 days ago" for an open question older than a day, so stale ones stand out. */
  function age(q: BoardQuestion): string {
    const days = Math.floor((Date.now() - q.asked_at_ms) / 86_400_000);
    return q.answered_at_ms === null && days >= 1 ? `asked ${days} ${days === 1 ? 'day' : 'days'} ago` : '';
  }

  /** Sends "Q<n>: <text>" to the question's session; `record` false leaves it open. */
  async function send(row: Row, text: string, record = true): Promise<void> {
    onError('');
    try {
      await answerQuestion({ machine: row.machine, id: row.q.id, askedAtMs: row.q.asked_at_ms, ownedId: row.q.session_owned_id }, text, signal, record);
      if (record) drafts[row.id] = '';
    } catch (cause) {
      if (!signal.aborted) onError(`Q${row.q.id}: ${cause instanceof Error ? cause.message : String(cause)}`);
    }
  }

  /** A live session is told "Q<n>: dismissed"; an ended one can't be, so the question is only marked. */
  async function dismiss(row: Row): Promise<void> {
    if (!ended(row)) return send(row, 'dismissed');
    onError('');
    try {
      await answerBoardQuestion(row.machine, row.q.id, row.q.asked_at_ms, 'dismissed', Date.now());
    } catch (cause) {
      onError(`Q${row.q.id}: ${cause instanceof Error ? cause.message : String(cause)}`);
    }
  }

  function keydown(event: KeyboardEvent, row: Row): void {
    const text = drafts[row.id]?.trim();
    if (event.key === 'Enter' && text) void send(row, text);
  }
</script>

{#snippet questionRow(row: Row)}
  {@const q = row.q}
  {@const expanded = isOpen(row)}
  {@const status = questionState(`${row.id}:${q.asked_at_ms}`)}
  <li class={cn('card', q.answered_at_ms !== null && 'answered')}>
    <button type="button" class="chead" aria-expanded={expanded} onclick={() => fold(row, !expanded)}>
      <ChevronRight class={cn('size-3.5 shrink-0 text-(--color-text-3) transition-transform', expanded && 'rotate-90')} aria-hidden="true" />
      <span class="key">Q{q.id}</span>
      <span class={cn('ctitle', expanded && 'wrap')}>{q.title}</span>
      <span class="ctime">{q.answered_at_ms === null ? when(q.asked_at_ms) : `Answered ${when(q.answered_at_ms)}`}</span>
    </button>
    <p class="src">
      {[q.session_name || 'Session', boards.length > 1 ? machineLabel(row.machine) : '', ended(row) ? 'Session ended' : ''].filter(Boolean).join(' · ')}
      {#if age(q)}<span class="age">· {age(q)}</span>{/if}
    </p>
    {#if q.answered_at_ms !== null}<p class="sum one">{q.answer === 'dismissed' ? 'Dismissed' : `You answered: ${q.answer}`}</p>{/if}
    {#if expanded}
      <div class="detail">
        {#if q.body}<p class="body">{q.body}</p>{/if}
        {#if q.answered_at_ms === null}
          {#if q.recommended}<div class="well"><span>Recommended</span><p>{q.recommended}</p></div>{/if}
          <div class="foot">
            {#if status.unrecorded}
              <p class="sum">Sent, but not recorded.</p>
              <div class="actions">
                <Button size="sm" variant="secondary" disabled={status.busy} onclick={() => void send(row, status.unrecorded ?? '')}>Retry</Button>
                <Button size="sm" variant="secondary" onclick={() => onOpenSession(q.session_owned_id)}><ExternalLink />Open</Button>
              </div>
            {:else if ended(row)}
              <p class="sum">This session has ended, so it can't take an answer. Dismiss clears the question.</p>
              <div class="actions">
                <Button size="sm" variant="secondary" disabled={status.busy} onclick={() => void dismiss(row)}>Dismiss</Button>
                <Button size="sm" variant="secondary" onclick={() => onOpenSession(q.session_owned_id)}><ExternalLink />Open</Button>
              </div>
            {:else}
              <Input
                bind:value={drafts[row.id]}
                maxlength={600}
                placeholder="Type an answer…"
                aria-label={`Answer Q${q.id}`}
                disabled={status.busy}
                onkeydown={(event) => keydown(event, row)}
                class="h-[30px] rounded-(--radius-sm) border-0 bg-[color-mix(in_srgb,var(--color-bg)_44%,transparent)] text-[13px] dark:bg-[color-mix(in_srgb,var(--color-bg)_44%,transparent)]"
              />
              <div class="actions">
                {#if q.recommended}
                  {@const recommended = q.recommended}
                  <Button size="sm" variant="secondary" disabled={status.busy} onclick={() => void send(row, recommended)}>As recommended</Button>
                {/if}
                <Button size="sm" variant="secondary" disabled={status.busy} onclick={() => void send(row, 'no')}>No</Button>
                <Button size="sm" variant="secondary" disabled={status.busy} onclick={() => void send(row, 'explain more', false)}>Explain more</Button>
                <Button size="sm" variant="secondary" disabled={status.busy} onclick={() => void dismiss(row)}>Dismiss</Button>
                <Button size="sm" variant="secondary" onclick={() => onOpenSession(q.session_owned_id)}><ExternalLink />Open</Button>
              </div>
            {/if}
          </div>
        {:else}
          <div class="actions">
            <Button size="sm" variant="secondary" onclick={() => onOpenSession(q.session_owned_id)}><ExternalLink />Open</Button>
          </div>
        {/if}
      </div>
    {/if}
  </li>
{/snippet}

{#if open.length || showAnswered}
  <section class="panel" data-testid="board-questions">
    <div class="ph"><h3><i class="dot bg-(--color-attention)"></i>Questions</h3><span class="muted">Agents are waiting on these</span><strong>{liveOpen.length}</strong></div>
    {#if open.length}
      <ul class="list">
        {#each open as row (row.id)}{@render questionRow(row)}{/each}
      </ul>
    {/if}
    {#if showAnswered}
      <div class={cn('fold', open.length > 0 && 'ruled')}>
        <h3>
          <button type="button" class="done-toggle" aria-expanded={answeredOpen} onclick={toggleAnswered}>
            <i class="dot bg-(--color-good)"></i>Answered today
            <span class="ml-auto font-normal text-(--color-text-2) tabular-nums">{answered.length}</span>
            <ChevronDown class={cn('size-3.5 text-(--color-text-2) transition-transform', answeredOpen && 'rotate-180')} aria-hidden="true" />
          </button>
        </h3>
        {#if answeredOpen}
          <ul class="list">
            {#each answered as row (row.id)}{@render questionRow(row)}{/each}
          </ul>
        {/if}
      </div>
    {/if}
  </section>
{/if}

<style>
  /* The Stats & Usage recipe (UsageWorkspace.svelte .panel and .provider-card), shared with BoardCards.svelte. */
  .panel { display: flex; flex: none; flex-direction: column; gap: 10px; padding: 12px; border: 1px solid var(--color-border); border-radius: var(--radius-lg); background: color-mix(in srgb, var(--color-elevated) 38%, var(--color-surface)); }
  .ph { display: flex; align-items: baseline; gap: 8px; min-width: 0; padding: 0 2px; }
  h3 { margin: 0; display: flex; align-items: center; gap: 7px; font-size: 0.88rem; line-height: 1.35; font-weight: 600; white-space: nowrap; }
  .ph strong { margin-left: auto; font-size: 0.88rem; font-variant-numeric: tabular-nums; }
  .muted { margin: 0; min-width: 0; overflow: hidden; color: var(--color-text-2); font-size: 0.76rem; line-height: 1.45; text-overflow: ellipsis; white-space: nowrap; }
  .dot { display: inline-block; flex: none; width: 8px; height: 8px; border-radius: 2px; }
  .list { display: flex; flex-direction: column; margin: 0; padding: 0; list-style: none; gap: 8px; }
  /* Each question is its own raised card with a hairline edge, a step above the panel. */
  .card { min-width: 0; padding: 10px 12px 11px; border: 1px solid var(--color-border); border-radius: var(--radius-md); background: color-mix(in srgb, var(--color-elevated) 72%, var(--color-surface)); }
  .card.answered { background: color-mix(in srgb, var(--color-elevated) 50%, var(--color-surface)); }
  .card.answered .ctitle { color: var(--color-text-2); }
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
  .src { margin: 3px 0 0 22px; overflow: hidden; color: var(--color-text-3); font-size: 0.75rem; line-height: 1.4; text-overflow: ellipsis; white-space: nowrap; }
  .age { color: var(--color-attention); }
  .body { margin: 0; color: var(--color-text); font-size: 13px; line-height: 1.5; white-space: pre-wrap; overflow-wrap: anywhere; }
  /* The answer field and buttons are the card's footer, under a hairline. */
  .foot { display: flex; flex-direction: column; gap: 8px; padding-top: 10px; border-top: 1px solid color-mix(in srgb, var(--color-border) 70%, transparent); }
  .sum { margin: 3px 0 0 22px; color: var(--color-text-2); font-size: 13px; line-height: 1.45; white-space: pre-wrap; overflow-wrap: anywhere; }
  .foot .sum { margin: 0; }
  .sum.one { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .detail { display: flex; flex-direction: column; gap: 10px; margin-top: 10px; }
  .actions { display: flex; flex-wrap: wrap; gap: 6px; }
  /* Recommended is an inset, like the Usage metric wells. */
  .well { display: grid; gap: 2px; padding: 8px 10px; border-left: 2px solid color-mix(in srgb, var(--color-accent) 55%, transparent); border-radius: var(--radius-sm); background: color-mix(in srgb, var(--color-bg) 44%, transparent); }
  .well > span { color: var(--color-text-2); font-size: 0.75rem; font-weight: 600; }
  .well p { margin: 0; color: var(--color-text); font-size: 13px; line-height: 1.45; white-space: pre-wrap; overflow-wrap: anywhere; }
  .fold { display: flex; flex-direction: column; gap: 8px; }
  .fold.ruled { padding-top: 8px; border-top: 1px solid color-mix(in srgb, var(--color-border) 70%, transparent); }
</style>
