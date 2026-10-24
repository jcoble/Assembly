<!-- Agents > Inbox: workflow steps and sessions on every machine that are waiting on the owner. -->
<script lang="ts">
  import { untrack } from 'svelte';
  import Check from '@lucide/svelte/icons/check';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import CircleStop from '@lucide/svelte/icons/circle-stop';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import type { OwnedSession } from '$lib/shell/ownedSessions.ts';
  import { answerInboxStep, openInboxSession, waitingOn, type InboxDecision } from '$lib/shell/workflows/ownerInboxActions.ts';
  import { listenAgentMessagesChanged, listOwnerInboxFromTauri, type OwnerInboxStep } from '$lib/tauriSource.ts';
  import { workflowErrorText } from '$lib/shell/workflows/workflowTypes.ts';

  interface Props {
    visible: boolean;
    /** Sessions waiting on the owner, from the rail. */
    sessions: OwnedSession[];
    /** How many workflow steps the last load found. */
    stepCount?: number;
  }
  let { visible, sessions, stepCount = $bindable(0) }: Props = $props();

  let steps = $state<OwnerInboxStep[]>([]);
  let loading = $state(false);
  let busy = $state(false);
  // An action's refusal stays until the next action; a reload clears only its own failure.
  let error = $state('');
  let loadError = $state('');
  let loader: AbortController | null = null;
  // Lives while the inbox is shown; an action that finishes after it closes changes nothing.
  let shown: AbortController | null = null;
  // A newer click, or the view going away, makes a slow session lookup stale.
  let opening = 0;

  $effect(() => {
    if (!visible) return;
    shown = new AbortController();
    busy = false;
    untrack(() => start());
    // A run change on any machine reloads the list, one load at a time.
    const signal = shown.signal;
    let reading = false;
    let again = false;
    void listenAgentMessagesChanged(async () => {
      if (reading) { again = true; return; }
      reading = true;
      do { again = false; await start(); } while (again && !signal.aborted);
      reading = false;
    }, signal);
    return () => {
      shown?.abort();
      shown = null;
      loader?.abort();
      loader = null;
      opening += 1;
    };
  });

  async function start(): Promise<void> {
    loader?.abort();
    const controller = new AbortController();
    loader = controller;
    await load(controller.signal);
  }

  function message(cause: unknown): string {
    return workflowErrorText(cause);
  }

  async function load(signal: AbortSignal): Promise<void> {
    loading = true;
    loadError = '';
    try {
      const found = (await listOwnerInboxFromTauri(signal)) ?? [];
      if (signal.aborted) return;
      steps = found;
      stepCount = found.length;
    } catch (cause) {
      if (!signal.aborted) loadError = `Could not load the inbox: ${message(cause)}`;
    }
    if (!signal.aborted) loading = false;
  }

  async function act(label: string, step: OwnerInboxStep, decision: InboxDecision): Promise<void> {
    const signal = shown?.signal;
    if (!signal) return;
    busy = true;
    error = '';
    try {
      await answerInboxStep(step, decision);
      if (signal.aborted) return;
      start();
    } catch (cause) {
      // The step may have been answered elsewhere; show what is waiting now, then the error.
      if (!signal.aborted) {
        start();
        error = `${label}: ${message(cause)}`;
      }
    }
    if (!signal.aborted) busy = false;
  }

  async function open(ownedId: string): Promise<void> {
    const attempt = ++opening;
    const signal = shown?.signal;
    if (!signal) return;
    try {
      await openInboxSession(ownedId, signal, () => attempt === opening);
    } catch (cause) {
      if (attempt === opening) error = `Could not open session: ${message(cause)}`;
    }
  }
</script>

<div class="flex h-full min-h-0 flex-col" data-testid="owner-inbox">
  <div class="flex min-w-0 items-center gap-2 border-b border-border px-3 py-2">
    <p class="min-w-0 flex-1 truncate text-sm text-muted-foreground">Steps and sessions waiting for you.</p>
    <Button size="sm" variant="ghost" disabled={loading} onclick={() => start()}><RefreshCw />Refresh</Button>
  </div>
  {#if error || loadError}<p role="alert" class="px-3 py-2 text-sm text-destructive">{error || loadError}</p>{/if}

  {#if steps.length === 0 && sessions.length === 0}
    <EmptyState title={loading ? 'Loading the inbox' : 'Nothing is waiting for you.'} />
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <ul class="flex flex-col gap-1 p-2">
        {#each steps as step (`${step.machine}:${step.runId}:${step.nodeId}`)}
          <li class="flex min-w-0 flex-col gap-1.5 rounded-lg px-2 py-2">
            <span class="flex w-full min-w-0 items-center gap-2 text-sm">
              <span class="min-w-0 flex-1 truncate font-medium">
                {step.taskKey || step.workflowName ? [step.taskKey, step.workflowName].filter(Boolean).join(' · ') : step.runId}
              </span>
              <Chip tone="attention">{step.state === 'waiting-approval' ? 'Approval' : 'Answer'}</Chip>
            </span>
            <span class="truncate text-sm text-muted-foreground">
              {step.nodeId}{step.machine !== 'mac' ? ` · ${step.machine}` : ''}
            </span>
            {#if step.reason}<span class="line-clamp-3 text-[13px]" title={step.reason}>{step.reason}</span>{/if}
            <span class="flex flex-wrap gap-2">
              {#if step.state === 'waiting-approval'}
                <Button size="sm" disabled={busy} onclick={() => void act('Approve', step, 'approve')}><Check />Approve</Button>
              {:else}
                <Button size="sm" disabled={busy} onclick={() => void act('Another round', step, 'another-round')}><RotateCcw />Another round</Button>
                <Button size="sm" variant="outline" disabled={busy} onclick={() => void act('Accept', step, 'accept')}><Check />Accept</Button>
                <Button size="sm" variant="ghost" disabled={busy} onclick={() => void act('Stop', step, 'stop')}><CircleStop />Stop</Button>
              {/if}
              {#if step.ownedId}
                <Button size="sm" variant="ghost" onclick={() => step.ownedId && void open(step.ownedId)}>Open</Button>
              {/if}
            </span>
          </li>
        {/each}
        {#each sessions as session (session.ownedId)}
          <li>
            <button
              type="button"
              class="flex w-full min-w-0 items-center gap-2 rounded-lg px-2 py-2 text-left hover:bg-accent/60 focus-visible:ring-3 focus-visible:ring-ring/50"
              onclick={() => void open(session.ownedId)}
            >
              <span class="min-w-0 flex-1 truncate text-[13px] font-medium">{session.title}</span>
              <Chip tone="attention">{waitingOn(session)}</Chip>
            </button>
          </li>
        {/each}
      </ul>
    </ScrollArea>
  {/if}
</div>
