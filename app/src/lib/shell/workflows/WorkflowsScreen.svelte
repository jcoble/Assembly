<!--
  WorkflowsScreen.svelte — every workflow run on one full page.

  Opened from the Agents panel's Workflows tab, over the whole frame like the
  Projects screen; the shell under it stays as it was, so Back returns to it.
  The Board shows each run in the column of the step it is at; a card opens
  that run's page, with its Map and its Cost. On the Map, a step opens its
  details below it; answers, approvals and retries go through the engine's
  own commands, and the run that comes back replaces the one shown.
-->
<script lang="ts">
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
  import CircleStop from '@lucide/svelte/icons/circle-stop';
  import Pause from '@lucide/svelte/icons/pause';
  import Play from '@lucide/svelte/icons/play';
  import { onMount, untrack } from 'svelte';

  import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import * as Tabs from '$lib/components/ui/tabs/index.js';
  import { sessionRowJump } from '$lib/shell/components/sessionRowJump.ts';
  import { adoptSession } from '$lib/shell/conversation/conversationService.ts';
  import { listAgentMessagesFromTauri, listenAgentMessagesChanged } from '$lib/tauriSource.ts';
  import { parseRemoteWorkspacePath } from '$lib/workspacePaths.ts';
  import { costText, currentStep, runTask, shortDuration } from './workflowBoard.ts';
  import {
    answerWorkflowPause,
    approveWorkflowGate,
    cancelWorkflowRun,
    followWorkflowRuns,
    listWorkflowRunHistory,
    listWorkflowRuns,
    pauseWorkflowRun,
    readWorkflowUsage,
    resumeWorkflowRun,
    retryWorkflowNode,
    watchWorkflowRuns
  } from './workflowService.ts';
  import { buildWorkflowTimeline, type WorkflowTimelineItem } from './workflowTimeline.ts';
  import { workflowErrorText, type WorkflowPauseDecision, type WorkflowRunRecord, type WorkflowUsageRow } from './workflowTypes.ts';
  import WorkflowBoard from './components/WorkflowBoard.svelte';
  import WorkflowCost from './components/WorkflowCost.svelte';
  import WorkflowMap from './components/WorkflowMap.svelte';
  import WorkflowProject from './components/WorkflowProject.svelte';
  import { projectIndex, projectProgress } from './workflowProject.ts';
  import WorkflowStepPanel from './components/WorkflowStepPanel.svelte';
  import WorkflowStageCloseup from './components/WorkflowStageCloseup.svelte';
  import { closeupKind } from './workflowCloseup.ts';

  interface Props {
    /** The active session's project; a remote project's runs are on that machine's engine. */
    root: string;
    onBack: () => void;
  }

  let { root, onBack }: Props = $props();

  const machine = $derived(parseRemoteWorkspacePath(root)?.profileId ?? null);
  let runs = $state<WorkflowRunRecord[]>([]);
  let loaded = $state(false);
  let error = $state('');
  let openRunId = $state<string | null>(null);
  let runTab = $state('map');
  let chosenStepId = $state<string | null>(null);
  /** The review or proof step shown up close instead of the Map. */
  let closeupId = $state<string | null>(null);
  let busy = $state(false);
  let timeline = $state<{ runId: string; items: WorkflowTimelineItem[]; loading: boolean; error: string } | null>(null);
  let now = $state(Date.now());
  /** Usage rows of every run, for the board's costs; read when the board shows. */
  let boardUsage = $state<WorkflowUsageRow[]>([]);
  /** The open run's usage rows for its Cost tab; null while reading. */
  let runUsage = $state<WorkflowUsageRow[] | null>(null);
  const openRun = $derived(runs.find((run) => run.id === openRunId) ?? null);
  /** The open run's slices, when it is a project with child runs. */
  const openProject = $derived(openRun ? projectProgress(openRun, projectIndex(runs)) : null);
  /** The step whose details show: the one chosen on the Map, else where the run is. */
  const shownStep = $derived(openRun
    ? openRun.nodes.find((node) => node.nodeId === chosenStepId) ?? currentStep(openRun) : null);
  const ENDED = ['completed', 'cancelled', 'failed'];
  /** Ends every read and listener when the screen closes. */
  const stop = new AbortController();

  // One clock for every elapsed time on screen; it runs only while a running run or step is shown.
  const ticking = $derived(runs.some((run) => run.nodes.some((node) => node.state === 'running' || node.state === 'starting')
    && (openRunId === null || openRunId === run.id)));
  $effect(() => {
    if (!ticking) return;
    now = Date.now();
    const timer = window.setInterval(() => (now = Date.now()), 1000);
    return () => window.clearInterval(timer);
  });

  onMount(() => {
    void follow(stop.signal);
    return () => {
      stop.abort();
      if (!machine) void watchWorkflowRuns(false, null);
    };
  });

  // This Mac's engine sends the board a summary of each changed run and the open run in full.
  $effect(() => {
    const runId = openRunId;
    if (!machine) untrack(() => void watchWorkflowRuns(true, runId));
  });

  /** Reads the runs, then follows changes: pushed by this Mac's engine, or read again from a remote one. */
  async function follow(signal: AbortSignal): Promise<void> {
    let reading = false, again = false;
    const reread = async (): Promise<void> => {
      if (reading) { again = true; return; }
      reading = true;
      do { again = false; await read(signal); } while (again && !signal.aborted);
      reading = false;
    };
    // A change can be new usage (this Mac's engine also sends a run on each usage row), so the board's costs are read again.
    if (machine) await listenAgentMessagesChanged(() => { void reread(); if (!openRunId) void readBoardUsage(signal); }, signal);
    else await followWorkflowRuns((run) => { upsert(run); if (!openRunId) void readBoardUsage(signal); }, signal);
    void readBoardUsage(signal);
    await reread();
  }

  function upsert(run: WorkflowRunRecord): void {
    const index = runs.findIndex((candidate) => candidate.id === run.id);
    if (index < 0) runs = [...runs, run];
    else if (run.lastSequence >= runs[index].lastSequence) runs[index] = run;
  }

  const costOf = (run: WorkflowRunRecord): string => costText(boardUsage.filter((row) => row.nodeRunId.startsWith(`${run.id}:`)));

  let usageReads = 0;
  async function readBoardUsage(signal: AbortSignal): Promise<void> {
    const read = ++usageReads;
    try {
      const rows = await readWorkflowUsage(machine, null);
      if (!signal.aborted && read === usageReads) boardUsage = rows ?? [];
    } catch {
      // The board shows no cost rather than an error; the Cost tab reports a failed read.
    }
  }

  // The Cost tab reads the open run's usage when it opens and whenever a new record of the run arrives.
  $effect(() => {
    const run = runTab === 'cost' ? openRun : null;
    if (!run) return;
    const controller = new AbortController();
    void readRunUsage(run.id, controller.signal);
    return () => controller.abort();
  });

  async function readRunUsage(runId: string, signal: AbortSignal): Promise<void> {
    try {
      const rows = await readWorkflowUsage(machine, runId);
      if (!signal.aborted) runUsage = rows;
    } catch (cause) {
      if (!signal.aborted) error = `Could not read the run's usage: ${workflowErrorText(cause)}`;
    }
  }

  // The open run's timeline, read again only when the run moves on.
  const timelineKey = $derived(openRun ? `${openRun.id}\n${openRun.updatedAtMs}` : '');
  $effect(() => {
    if (!timelineKey) return;
    const run = untrack(() => openRun);
    if (!run) return;
    const controller = new AbortController();
    const kept = untrack(() => (timeline?.runId === run.id ? timeline.items : []));
    timeline = { runId: run.id, items: kept, loading: true, error: '' };
    void loadTimeline(run, kept, controller.signal);
    return () => controller.abort();
  });

  async function loadTimeline(run: WorkflowRunRecord, kept: WorkflowTimelineItem[], signal: AbortSignal): Promise<void> {
    try {
      const [history, messages] = await Promise.all([
        listWorkflowRunHistory(machine, run.id, signal),
        listAgentMessagesFromTauri({ runId: run.id, machine: machine ?? 'mac', before: null, limit: 200 }, signal)
      ]);
      if (!signal.aborted) timeline = { runId: run.id, items: buildWorkflowTimeline(history ?? [], messages ?? [], run), loading: false, error: '' };
    } catch (cause) {
      if (!signal.aborted) timeline = { runId: run.id, items: kept, loading: false, error: `Could not load the log: ${workflowErrorText(cause)}` };
    }
  }

  const key = (action: string): string => `workflows-screen:${action}:${crypto.randomUUID()}`;

  /** Runs one engine command and shows the run it returns. */
  async function control(label: string, action: () => Promise<WorkflowRunRecord | null>): Promise<void> {
    busy = true;
    error = '';
    try {
      const run = await action();
      if (!stop.signal.aborted && run) upsert(run);
    } catch (cause) {
      if (!stop.signal.aborted) error = `${label}: ${workflowErrorText(cause)}`;
    } finally {
      busy = false;
    }
  }

  function answer(run: WorkflowRunRecord, nodeId: string, decision: WorkflowPauseDecision): void {
    void control('Answer', () => answerWorkflowPause({ runId: run.id, nodeId, decision, reply: null, idempotencyKey: key('answer') }, machine));
  }

  async function openChat(ownedId: string): Promise<void> {
    try {
      if (!(await adoptSession(ownedId, stop.signal))) throw new Error('Agent conversation is not available yet.');
      if (stop.signal.aborted) return;
      await sessionRowJump(ownedId, 'session');
      onBack();
    } catch (cause) {
      if (!stop.signal.aborted) error = `Could not open the chat: ${workflowErrorText(cause)}`;
    }
  }

  function openRunPage(runId: string | null): void {
    openRunId = runId;
    chosenStepId = null;
    closeupId = null;
    runTab = 'map';
    timeline = null;
    runUsage = null;
    // The board holds summaries; the open run is read again in full.
    if (runId) void read(stop.signal);
    else void readBoardUsage(stop.signal);
  }

  async function read(signal: AbortSignal): Promise<void> {
    try {
      const next = await listWorkflowRuns(machine);
      if (signal.aborted) return;
      runs = next ?? [];
      error = next === null ? 'Workflow runs are shown in the desktop app.' : '';
    } catch (cause) {
      if (!signal.aborted) error = `Could not load workflows: ${workflowErrorText(cause)}`;
    } finally {
      if (!signal.aborted) loaded = true;
    }
  }
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<div
  class="workflows-screen"
  role="region"
  aria-label="Workflows"
  data-testid="workflows-screen"
  onkeydown={(event) => { if (event.key === 'Escape') { if (closeupId) closeupId = null; else if (openRun) openRunPage(null); else onBack(); } }}
>
  <header class="flex min-w-0 items-center gap-3">
    <Button variant="ghost" size="sm" onclick={() => (closeupId ? (closeupId = null) : openRun ? openRunPage(null) : onBack())}><ArrowLeft />Back</Button>
    <Breadcrumb.Root class="min-w-0">
      <Breadcrumb.List class="flex-nowrap text-[13px]">
        {#if openRun}
          {@const task = runTask(openRun)}
          <Breadcrumb.Item>
            <Breadcrumb.Link>
              {#snippet child({ props })}<button type="button" class="{props.class} rounded-md outline-none focus-visible:ring-3 focus-visible:ring-ring/50" onclick={() => openRunPage(null)}>Workflows</button>{/snippet}
            </Breadcrumb.Link>
          </Breadcrumb.Item>
          <Breadcrumb.Separator class="flex items-center" />
          {#if closeupId}
            <Breadcrumb.Item class="min-w-0">
              <Breadcrumb.Link>
                {#snippet child({ props })}<button type="button" class="{props.class} truncate rounded-md outline-none focus-visible:ring-3 focus-visible:ring-ring/50" onclick={() => (closeupId = null)}>{task.key ? `${task.key} · ` : ''}{task.title}</button>{/snippet}
              </Breadcrumb.Link>
            </Breadcrumb.Item>
            <Breadcrumb.Separator class="flex items-center" />
            <Breadcrumb.Item class="shrink-0"><Breadcrumb.Page>{openRun.definition.nodes.find((item) => item.id === closeupId)?.title || closeupId}</Breadcrumb.Page></Breadcrumb.Item>
          {:else}
            <Breadcrumb.Item class="min-w-0"><Breadcrumb.Page class="truncate">{task.key ? `${task.key} · ` : ''}{task.title}</Breadcrumb.Page></Breadcrumb.Item>
          {/if}
        {:else}
          <Breadcrumb.Item><Breadcrumb.Page>Workflows</Breadcrumb.Page></Breadcrumb.Item>
        {/if}
      </Breadcrumb.List>
    </Breadcrumb.Root>
    {#if openRun}
      <Tabs.Root bind:value={runTab} class="ml-auto">
        <Tabs.List>
          <Tabs.Trigger value="map">Map</Tabs.Trigger>
          {#if openProject}<Tabs.Trigger value="project">Project</Tabs.Trigger>{/if}
          <Tabs.Trigger value="cost">Cost</Tabs.Trigger>
        </Tabs.List>
      </Tabs.Root>
    {/if}
  </header>

  {#if error}
    <p class="flex items-start gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-[13px] text-destructive">
      <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />{error}
    </p>
  {/if}

  {#if openRun}
    {@const run = openRun}
    <div class="flex min-w-0 flex-wrap items-center gap-2">
      <Chip tone="neutral">{run.definition.name}</Chip>
      <Chip tone={run.state === 'failed' ? 'bad' : run.state === 'completed' ? 'good' : ENDED.includes(run.state) ? 'neutral' : run.state.startsWith('waiting') ? 'attention' : 'live'}>
        {run.state} · {shortDuration((ENDED.includes(run.state) ? run.updatedAtMs : now) - run.createdAtMs)}
      </Chip>
      <span class="ml-auto flex gap-2">
        {#if run.state === 'running'}
          <Button size="sm" variant="ghost" disabled={busy} onclick={() => void control('Pause', () => pauseWorkflowRun({ runId: run.id, idempotencyKey: key('pause') }, machine))}><Pause />Pause after this step</Button>
        {:else if run.state === 'paused'}
          <Button size="sm" variant="ghost" disabled={busy} onclick={() => void control('Resume', () => resumeWorkflowRun({ runId: run.id, idempotencyKey: key('resume') }, machine))}><Play />Resume</Button>
        {/if}
        {#if !ENDED.includes(run.state)}
          <Button size="sm" variant="ghost" disabled={busy} onclick={() => void control('Stop', () => cancelWorkflowRun({ runId: run.id, idempotencyKey: key('cancel') }, machine))}><CircleStop />Stop run</Button>
        {/if}
      </span>
    </div>
    {#if runTab === 'map' && closeupId}
      <WorkflowStageCloseup {run} nodeId={closeupId} {machine} />
    {:else if runTab === 'map'}
      <WorkflowMap {run} {now} selectedId={shownStep?.nodeId ?? null} onSelect={(nodeId) => (chosenStepId = nodeId)} onCloseup={(nodeId) => (closeupId = nodeId)} />
      {#if shownStep}
        {@const step = shownStep}
        <WorkflowStepPanel
          {run}
          node={step}
          {now}
          {busy}
          local={!machine}
          timeline={timeline?.runId === run.id ? timeline : { items: [], loading: true, error: '' }}
          onAnswer={(decision) => answer(run, step.nodeId, decision)}
          onApprove={() => void control('Approve', () => approveWorkflowGate({ runId: run.id, nodeId: step.nodeId, approval: { approved: true }, idempotencyKey: key('approve') }, machine))}
          onRetry={() => void control('Retry', () => retryWorkflowNode({ runId: run.id, nodeId: step.nodeId, idempotencyKey: key('retry') }, machine))}
          onOpenChat={() => void openChat(step.ownedId)}
          onCloseup={closeupKind(run, step.nodeId) ? () => (closeupId = step.nodeId) : null}
        />
      {/if}
    {:else if runTab === 'project' && openProject}
      <WorkflowProject {run} progress={openProject} {costOf} onOpen={openRunPage} />
    {:else}
      <WorkflowCost {run} rows={runUsage} {now} />
    {/if}
  {:else if loaded && runs.length === 0 && !error}
    <p class="text-[13px] text-muted-foreground">No workflow runs yet.</p>
  {:else if runs.length}
    <WorkflowBoard {runs} {now} {costOf} onOpen={openRunPage} />
  {/if}
</div>

<style>
  .workflows-screen {
    position: absolute;
    inset: 0;
    /* Above the layout's resize handles (z-index 99), which otherwise take clicks on a strip of the screen. */
    z-index: 100;
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    min-height: 0;
    padding: var(--space-3) var(--space-5) var(--space-5);
    background: var(--color-bg);
  }
</style>
