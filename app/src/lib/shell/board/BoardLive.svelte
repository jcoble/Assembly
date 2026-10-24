<!-- The Board's right column, built from live state: what waits on the owner, workflow runs and busy sessions. -->
<script lang="ts">
  import MessageSquarePlus from '@lucide/svelte/icons/message-square-plus';
  import Workflow from '@lucide/svelte/icons/workflow';

  import { Button } from '$lib/components/ui/button/index.js';
  import { HoverActionButton, HoverActions } from '$lib/components/ui/hover-actions/index.js';
  import { sessionWaitsOnYou, type OwnedSession } from '$lib/shell/ownedSessions.ts';
  import { projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { answerInboxStep, waitingOn, type InboxDecision } from '$lib/shell/workflows/ownerInboxActions.ts';
  import { cn } from '$lib/utils.js';
  import BoardNotes from './BoardNotes.svelte';
  import type { BoardNote, BoardStep, MachineBoard } from './boardSource.ts';

  interface Props {
    boards: MachineBoard[];
    /** Sessions waiting on the owner, from the rail. */
    sessions: OwnedSession[];
    /** Notion Tasks is connected, so task keys show. */
    notion: boolean;
    /** Aborted when the board closes. */
    signal: AbortSignal;
    onError(text: string): void;
    onOpenSession(ownedId: string): void;
  }
  let { boards, sessions, notion, signal, onError, onOpenSession }: Props = $props();

  let busy = $state(false);
  /** The live row whose note field is showing. */
  let adding = $state<string | null>(null);

  const machineName = (machine: string): string => projectMachineLabel(machine, rail.remoteProfiles);
  const sessionMachine = (session: OwnedSession): string =>
    machineName(session.executionEnvironment === 'remote' ? (session.remoteProfileId ?? '') : 'local');
  const steps = $derived(boards.flatMap((board) => board.value.steps.map((step) => ({ machine: board.machine, step }))));
  const runs = $derived(boards.flatMap((board) => board.value.runs.map((run) => ({ machine: board.machine, run }))));
  /** Top-level sessions at work or waiting, each with its helpers not yet marked done, under their machine. */
  const machines = $derived.by(() => {
    const groups = new Map<string, { session: OwnedSession; helpers: OwnedSession[] }[]>();
    for (const session of rail.owned) {
      if (session.helperOf || session.runId) continue;
      if (!sessionWaitsOnYou(session) && session.runtimeState !== 'working' && session.runtimeState !== 'interrupting') continue;
      const helpers = rail.owned.filter((helper) => helper.helperOf === session.ownedId && !helper.completedAt && !helper.settledAt);
      const machine = sessionMachine(session);
      groups.set(machine, [...(groups.get(machine) ?? []), { session, helpers }]);
    }
    return [...groups];
  });

  /** Notes on a session live on the machine that runs it. */
  const sessionBoard = (session: OwnedSession): string =>
    session.executionEnvironment === 'remote' ? (session.remoteProfileId ?? '') : 'mac';
  const sessionNotes = (machine: string, target: string): BoardNote[] =>
    boards.find((board) => board.machine === machine)?.value.session_notes.find((item) => item.target === target)?.notes ?? [];

  function status(session: OwnedSession): string {
    return session.runtimeState === 'working' || session.runtimeState === 'interrupting' ? 'Working' : 'Idle';
  }

  const time = (ms: number): string => new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

  function message(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
  }

  async function act(label: string, machine: string, step: BoardStep, decision: InboxDecision): Promise<void> {
    busy = true;
    onError('');
    try {
      await answerInboxStep({ machine, runId: step.run_id, nodeId: step.node_id }, decision);
    } catch (cause) {
      // The step may have been answered elsewhere; the change signal shows what is waiting now.
      if (!signal.aborted) onError(`${label}: ${message(cause)}`);
    }
    if (!signal.aborted) busy = false;
  }
</script>

{#snippet addNote(target: string)}
  <HoverActions class="flex-none" label="Note actions">
    <HoverActionButton label="Add note" size="xs" onclick={() => (adding = target)}><MessageSquarePlus /></HoverActionButton>
  </HoverActions>
{/snippet}

{#snippet notesFor(notes: BoardNote[], machine: string, target: string)}
  {#if notes.length || adding === target}
    <div class="flex min-w-0 flex-col gap-1 pt-1">
      <BoardNotes {notes} {machine} {target} {signal} adding={adding === target} {onError} onDone={() => (adding = null)} />
    </div>
  {/if}
{/snippet}

{#snippet sessionRow(session: OwnedSession, helpers: number)}
  {@const machine = sessionBoard(session)}
  {@const target = `session:${session.ownedId}`}
  {@const waits = sessionWaitsOnYou(session)}
  <div class="group flex min-w-0 items-center gap-1">
    <button type="button" class="srow" onclick={() => onOpenSession(session.ownedId)}>
      <span class="min-w-0 flex-1">
        <span class={cn('block truncate', helpers > 0 && 'font-semibold')}>{session.title}</span>
        {#if helpers}<span class="block text-xs text-(--color-text-3)">{helpers} {helpers === 1 ? 'helper' : 'helpers'}</span>{/if}
      </span>
      <span class={cn('val', !waits && status(session) === 'Idle' && 'text-(--color-text-2)')}>
        <i class={cn('dot', waits ? 'bg-(--color-attention)' : status(session) === 'Working' ? 'bg-(--color-live)' : 'bg-(--color-idle)')}></i>{waits ? waitingOn(session) : status(session)}
      </span>
    </button>
    {@render addNote(target)}
  </div>
  <div class="pl-2">{@render notesFor(sessionNotes(machine, target), machine, target)}</div>
{/snippet}

<div class="col" data-testid="board-live">
  <section class="panel">
    <div class="ph"><h3>Messages for you</h3><span class="muted">Same as the Inbox</span><strong>{steps.length + sessions.length}</strong></div>
    {#if steps.length === 0 && sessions.length === 0}
      <p class="muted px-0.5">Nothing is waiting for you.</p>
    {:else}
    <ul class="list">
      {#each steps as { machine, step } (`${machine}:${step.run_id}:${step.node_id}`)}
        <li class="msg group">
          <span class="from">
            <Workflow class="size-3.5 flex-none text-(--color-text-2)" aria-hidden="true" />
            <span class="truncate">{[notion ? step.task_key : null, step.workflow_name].filter(Boolean).join(' · ') || step.run_id}</span>
            <span class="ml-auto flex-none tabular-nums">{time(step.updated_at_ms)}</span>
            {@render addNote(`step:${step.run_id}/${step.node_id}`)}
          </span>
          <span class="from">
            <span class="truncate">{step.node_id} · {machineName(machine === 'mac' ? 'local' : machine)}</span>
            <span class="val ml-auto"><i class="dot bg-(--color-attention)"></i>{step.state === 'waiting-approval' ? 'Needs approval' : 'Needs an answer'}</span>
          </span>
          {#if step.reason}<p class="line-clamp-3" title={step.reason}>{step.reason}</p>{/if}
          <span class="flex flex-wrap gap-1 pt-1.5">
            {#if step.state === 'waiting-approval'}
              <Button size="xs" disabled={busy} onclick={() => void act('Approve', machine, step, 'approve')}>Approve</Button>
            {:else}
              <Button size="xs" disabled={busy} onclick={() => void act('Another round', machine, step, 'another-round')}>Another round</Button>
              <Button size="xs" variant="secondary" disabled={busy} onclick={() => void act('Accept', machine, step, 'accept')}>Accept</Button>
              <Button size="xs" variant="secondary" disabled={busy} onclick={() => void act('Stop', machine, step, 'stop')}>Stop</Button>
            {/if}
            {#if step.owned_id}
              {@const ownedId = step.owned_id}
              <Button size="xs" variant="secondary" onclick={() => onOpenSession(ownedId)}>Open</Button>
            {/if}
          </span>
          {@render notesFor(step.notes, machine, `step:${step.run_id}/${step.node_id}`)}
        </li>
      {/each}
      {#each sessions as session (session.ownedId)}
        <li class="msg">{@render sessionRow(session, 0)}</li>
      {/each}
    </ul>
    {/if}
  </section>

  {#if runs.length}
    <section class="panel">
      <div class="ph"><h3>Workflow runs</h3><span class="muted">Unfinished</span><strong>{runs.length}</strong></div>
      <ul class="list">
        {#each runs as { machine, run } (`${machine}:${run.id}`)}
          {@const waiting = run.state.startsWith('waiting')}
          <li class="msg group">
            <span class="flex min-w-0 items-center gap-1.5 text-[13px]">
              <Workflow class="size-3.5 flex-none text-(--color-text-2)" aria-hidden="true" />
              <span class="min-w-0 truncate font-semibold">{run.name ?? run.id}</span>
              {#if notion && run.task_key}<span class="flex-none text-xs text-(--color-text-3)">{run.task_key}</span>{/if}
              <strong class="ml-auto flex-none text-xs tabular-nums">{run.steps_done} / {run.steps_total}</strong>
              {@render addNote(`run:${run.id}`)}
            </span>
            <span class="truncate text-xs text-(--color-text-2)">
              {[run.current_step ? `Step ${run.current_step}` : null, run.state, run.round !== null ? `round ${run.round}` : null,
                run.findings !== null ? `${run.findings} found` : null, machineName(machine === 'mac' ? 'local' : machine)].filter(Boolean).join(' · ')}
            </span>
            {#if run.steps_total > 0}
              <span class="quota-bar"><i class={waiting ? 'bg-(--color-attention)' : 'bg-(--color-live)'} style:width={`${Math.min(100, (run.steps_done / run.steps_total) * 100)}%`}></i></span>
            {/if}
            {@render notesFor(run.notes, machine, `run:${run.id}`)}
          </li>
        {/each}
      </ul>
    </section>
  {/if}

  {#if machines.length}
    <section class="panel">
      <div class="ph"><h3>Sessions</h3><span class="muted">Busy or waiting</span><strong>{machines.reduce((total, [, rows]) => total + rows.length, 0)}</strong></div>
      {#each machines as [machine, rows] (machine)}
        <h4 class="mgrp">{machine}</h4>
        <ul class="sessions">
          {#each rows as { session, helpers } (session.ownedId)}
            <li>
              {@render sessionRow(session, helpers.length)}
              {#if helpers.length}
                <ul class="helpers">
                  {#each helpers as helper (helper.ownedId)}<li>{@render sessionRow(helper, 0)}</li>{/each}
                </ul>
              {/if}
            </li>
          {/each}
        </ul>
      {/each}
    </section>
  {/if}
</div>

<style>
  /* Surfaces from the Usage screen (UsageWorkspace.svelte): .panel, .provider-card, .quota-bar, .eyebrow. */
  /* The same panel and card recipe as BoardCards.svelte. */
  .col { display: flex; flex-direction: column; gap: 10px; min-width: 0; min-height: 0; overflow-y: auto; }
  .panel { display: flex; flex: none; flex-direction: column; gap: 10px; padding: 12px; border: 1px solid var(--color-border); border-radius: var(--radius-lg); background: color-mix(in srgb, var(--color-elevated) 38%, var(--color-surface)); }
  .ph { display: flex; align-items: baseline; gap: 8px; min-width: 0; padding: 0 2px; }
  h3 { margin: 0; font-size: 0.88rem; line-height: 1.35; font-weight: 600; white-space: nowrap; }
  .ph strong { margin-left: auto; font-size: 0.88rem; font-variant-numeric: tabular-nums; }
  .muted { margin: 0; min-width: 0; overflow: hidden; color: var(--color-text-2); font-size: 0.76rem; line-height: 1.45; text-overflow: ellipsis; white-space: nowrap; }
  .dot { display: inline-block; flex: none; width: 8px; height: 8px; border-radius: 2px; }
  .list, .sessions, .helpers { display: flex; flex-direction: column; margin: 0; padding: 0; list-style: none; }
  .list { gap: 8px; }
  .msg { display: flex; flex-direction: column; min-width: 0; padding: 9px 11px; border: 1px solid var(--color-border); border-radius: var(--radius-md); background: color-mix(in srgb, var(--color-elevated) 72%, var(--color-surface)); font-size: 13px; }
  .msg p { margin: 4px 0 0; line-height: 1.45; }
  .from { display: flex; align-items: center; gap: 6px; min-width: 0; color: var(--color-text-3); font-size: 0.75rem; }
  .val { display: inline-flex; flex: none; align-items: center; gap: 6px; color: var(--color-text); font-size: 0.75rem; font-weight: 600; white-space: nowrap; }
  .quota-bar { display: flex; height: 7px; margin-top: 7px; overflow: hidden; border-radius: 999px; background: color-mix(in srgb, var(--color-border) 32%, transparent); }
  .quota-bar i { display: block; height: 100%; min-width: 0; border-radius: 999px; }
  .mgrp { margin: 0; padding: 0 2px; color: var(--color-text-3); font-size: 0.75rem; font-weight: 680; letter-spacing: 0.085em; text-transform: uppercase; }
  .sessions > li + li { border-top: 1px solid color-mix(in srgb, var(--color-border) 28%, transparent); }
  .srow { display: flex; flex: 1; align-items: center; gap: 10px; min-width: 0; padding: 5px 4px; border-radius: var(--radius-sm); font-size: 13px; text-align: left; }
  .srow:hover { background: var(--color-hover); }
  .srow:focus-visible { outline: none; box-shadow: var(--focus-ring); }
  .helpers { margin-left: 14px; }
  .helpers > li { position: relative; padding-left: 16px; }
  .helpers > li::before { content: ""; position: absolute; left: 3px; top: -6px; width: 10px; height: 22px; border-left: 1px solid var(--color-field-border); border-bottom: 1px solid var(--color-field-border); border-bottom-left-radius: 6px; }
</style>
