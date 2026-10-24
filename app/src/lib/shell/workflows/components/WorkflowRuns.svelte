<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { untrack } from 'svelte';
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import Check from '@lucide/svelte/icons/check';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import Circle from '@lucide/svelte/icons/circle';
  import CircleAlert from '@lucide/svelte/icons/circle-alert';
  import CircleCheck from '@lucide/svelte/icons/circle-check';
  import CircleDashed from '@lucide/svelte/icons/circle-dashed';
  import CircleDot from '@lucide/svelte/icons/circle-dot';
  import CircleMinus from '@lucide/svelte/icons/circle-minus';
  import CircleX from '@lucide/svelte/icons/circle-x';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import MessagesSquare from '@lucide/svelte/icons/messages-square';
  import CircleStop from '@lucide/svelte/icons/circle-stop';
  import Pause from '@lucide/svelte/icons/pause';
  import Play from '@lucide/svelte/icons/play';
  import Plus from '@lucide/svelte/icons/plus';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { sessionRowJump } from '$lib/shell/components/sessionRowJump.ts';
  import { adoptSession } from '$lib/shell/conversation/conversationService.ts';
  import {
    answerWorkflowPause,
    approveWorkflowGate,
    cancelWorkflowRun,
    createWorkflowRun,
    listWorkflowFiles,
    listWorkflowRunHistory,
    listWorkflowRuns,
    pauseWorkflowRun,
    resumeWorkflowRun,
    retryWorkflowNode,
    startWorkflowRun,
    subscribeWorkflowSnapshots
  } from '$lib/shell/workflows/workflowService.ts';
  import {
    applyWorkflowSnapshots,
    beginWorkflowLoad,
    failWorkflowLoad,
    filteredWorkflowRuns,
    resetWorkflowStore,
    selectWorkflowRun,
    selectedWorkflowRun,
    upsertWorkflowSnapshot,
    workflowState
  } from '$lib/shell/workflows/workflowStore.svelte.ts';
  import WorkflowPipelineStrip from './WorkflowPipelineStrip.svelte';
  import WorkflowStepDetails, { type StepStatus } from './WorkflowStepDetails.svelte';
  import WorkflowTimeline from './WorkflowTimeline.svelte';
  import { buildWorkflowTimeline, type WorkflowTimelineItem } from '$lib/shell/workflows/workflowTimeline.ts';
  import { listAgentMessagesFromTauri, listenAgentMessagesChanged } from '$lib/tauriSource.ts';
  import { workflowErrorText, type WorkflowNodeRunRecord, type WorkflowNodeState, type WorkflowRunRecord } from '$lib/shell/workflows/workflowTypes.ts';

  interface Props {
    visible: boolean;
    root: string;
    /** The remote profile whose engine runs this project's workflows; null for this Mac. */
    machine: string | null;
    /** Show the agent messages sent during this run, named as in the run list. */
    onShowMessages: (runId: string, label: string) => void;
  }

  let { visible, root, machine, onShowMessages }: Props = $props();
  let composing = $state(false);
  let taskKey = $state('');
  let taskTitle = $state('');
  let taskBody = $state('');
  let workflowFiles = $state<string[]>([]);
  let workflowPath = $state('');
  let busy = $state<string | null>(null);
  let loadGeneration = 0;
  let actionController: AbortController | null = null;
  let now = $state(Date.now());
  /** The step whose details screen is open, by its id in the workflow; null shows the run. */
  let openStepId = $state<string | null>(null);

  const runs = $derived(filteredWorkflowRuns());
  const selected = $derived(selectedWorkflowRun());
  const orderedNodes = $derived(selected ? orderNodes(selected) : []);
  const waitingNode = $derived(selected?.nodes.find((node) => node.state === 'waiting-approval') ?? null);
  const pausedNode = $derived(selected?.nodes.find((node) => node.state === 'waiting-input') ?? null);
  const openStep = $derived(selected?.nodes.find((node) => node.nodeId === openStepId) ?? null);
  const failedNode = $derived(selected?.nodes.find((node) => node.state === 'failed') ?? null);
  const runningNode = $derived(selected?.nodes.find((node) => node.state === 'running') ?? null);
  const runningDefinition = $derived(
    runningNode ? selected?.definition.nodes.find((node) => node.id === runningNode.nodeId) ?? null : null
  );
  const stale = $derived(
    Boolean(
      runningNode?.startedAtMs &&
      runningDefinition &&
      now - runningNode.startedAtMs >= runningDefinition.timeoutSeconds * 1000
    )
  );
  /** The selected run's timeline; kept while it reloads, so an update does not blank it. */
  let timeline = $state<{ runId: string; items: WorkflowTimelineItem[]; error: string; loading: boolean } | null>(null);
  const shownTimeline = $derived(timeline && selected && timeline.runId === selected.id ? timeline : null);
  // One string, so the timeline reloads only when the run changes or moves on, not on every snapshot.
  const timelineKey = $derived(visible && selected ? `${selected.id}\n${selected.updatedAtMs}` : '');
  const completedCount = $derived(
    selected?.nodes.filter((node) => ['completed', 'skipped'].includes(node.state)).length ?? 0
  );

  function orderNodes(run: WorkflowRunRecord): WorkflowNodeRunRecord[] {
    const byId = new Map(run.nodes.map((node) => [node.nodeId, node]));
    const ordered: WorkflowNodeRunRecord[] = [];
    const visited = new Set<string>();
    function visit(id: string): void {
      if (visited.has(id)) return;
      visited.add(id);
      const node = byId.get(id);
      if (!node) return;
      run.definition.nodes.find((item) => item.id === id)?.dependsOn.forEach(visit);
      ordered.push(node);
    }
    run.definition.nodes.forEach((node) => visit(node.id));
    return ordered;
  }

  $effect(() => {
    if (!visible) return;
    const controller = new AbortController();
    const generation = ++loadGeneration;
    const owner = machine;
    const stop = untrack(() => subscribeWorkflowSnapshots((snapshot) => {
      if (controller.signal.aborted || generation !== loadGeneration || owner) return;
      if (Array.isArray(snapshot)) applyWorkflowSnapshots(snapshot);
      else upsertWorkflowSnapshot(snapshot);
    }));
    // Pushed snapshots are this Mac's. A run change on any machine re-reads the list, one read at a time.
    let reading = false, again = false;
    async function reread(): Promise<void> {
      if (reading) { again = true; return; }
      reading = true;
      do { again = false; await refresh(controller.signal, generation); } while (again && !controller.signal.aborted);
      reading = false;
    }
    void reread();
    void listenAgentMessagesChanged(() => void reread(), controller.signal);
    void loadWorkflowFiles(root, owner, controller.signal);
    return () => {
      controller.abort();
      actionController?.abort();
      actionController = null;
      stageLookup?.abort();
      stageLookup = null;
      stop();
      loadGeneration += 1;
      resetWorkflowStore();
    };
  });

  async function loadWorkflowFiles(repoRoot: string, owner: string | null, signal: AbortSignal): Promise<void> {
    if (!repoRoot) return;
    try {
      const files = await listWorkflowFiles(repoRoot, owner);
      if (signal.aborted || !files) return;
      workflowFiles = files;
      if (!files.includes(workflowPath)) workflowPath = files[0] ?? '';
    } catch (error) {
      if (!signal.aborted) workflowState.error = `Could not list workflows: ${workflowErrorText(error)}`;
    }
  }

  $effect(() => {
    if (!visible || !runningNode?.startedAtMs || !runningDefinition) return;
    const controller = new AbortController();
    const remaining = Math.max(
      0,
      runningNode.startedAtMs + runningDefinition.timeoutSeconds * 1000 - Date.now()
    );
    const timer = window.setTimeout(() => {
      if (!controller.signal.aborted) now = Date.now();
    }, remaining);
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  });

  const LIVE_STATES: WorkflowNodeState[] = ['starting', 'running'];
  const ticking = $derived(
    orderedNodes.some((node) => node.startedAtMs && !node.finishedAtMs && LIVE_STATES.includes(node.state))
  );

  // One clock for every running stage's elapsed time; stops when hidden or nothing is running.
  $effect(() => {
    if (!visible || !ticking) return;
    now = Date.now();
    const timer = window.setInterval(() => (now = Date.now()), 1000);
    return () => window.clearInterval(timer);
  });

  function elapsed(node: WorkflowNodeRunRecord): string {
    if (!node.startedAtMs) return '';
    const end = node.finishedAtMs ?? (LIVE_STATES.includes(node.state) ? now : null);
    if (end === null) return '';
    const seconds = Math.max(0, Math.floor((end - node.startedAtMs) / 1000));
    if (seconds < 60) return `${seconds}s`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ${String(seconds % 60).padStart(2, '0')}s`;
    return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`;
  }

  function runTitle(run: WorkflowRunRecord): string {
    return (run.inputSnapshot as { task?: { title?: string } }).task?.title ?? run.definition.name;
  }

  function runTone(run: WorkflowRunRecord): 'live' | 'good' | 'bad' | 'attention' | 'neutral' {
    if (run.state === 'failed') return 'bad';
    if (run.state === 'completed') return 'good';
    if (run.state === 'cancelled') return 'neutral';
    return run.state === 'waiting-approval' ? 'attention' : 'live';
  }

  /** A command step has no provider; join only the parts that are there. */
  function stepMeta(node: WorkflowNodeRunRecord): string {
    return [node.provider, node.roleId, elapsed(node)].filter(Boolean).join(' · ');
  }

  function stepTitle(run: WorkflowRunRecord, nodeId: string): string {
    return run.definition.nodes.find((item) => item.id === nodeId)?.title ?? nodeId;
  }

  /** The nearest earlier step that failed, walking back through what this step depends on. */
  function failedUpstream(run: WorkflowRunRecord, nodeId: string, seen = new Set<string>()): string | null {
    for (const id of run.definition.nodes.find((item) => item.id === nodeId)?.dependsOn ?? []) {
      if (seen.has(id)) continue;
      seen.add(id);
      if (run.nodes.find((node) => node.nodeId === id)?.state === 'failed') return id;
      const further = failedUpstream(run, id, seen);
      if (further) return further;
    }
    return null;
  }

  /** What a step's state means here and now, in words: "blocked" alone reads like it is waiting on something. */
  function stepStatus(run: WorkflowRunRecord, node: WorkflowNodeRunRecord): StepStatus {
    const time = elapsed(node);
    switch (node.state) {
      case 'completed': return { chip: null, tone: 'good', summary: time ? `Completed in ${time}` : 'Completed', notRun: false };
      case 'failed': return { chip: 'failed', tone: 'bad', summary: node.failure?.message ?? 'Failed', notRun: false };
      case 'starting':
      case 'running': return { chip: 'running', tone: 'live', summary: time ? `Running for ${time}` : 'Starting', notRun: false };
      case 'ready':
      case 'queued': return { chip: 'queued', tone: 'live', summary: 'Queued to start', notRun: false };
      case 'waiting-approval': return { chip: 'needs approval', tone: 'attention', summary: 'Waiting for your approval', notRun: false };
      case 'waiting-input': return { chip: 'needs you', tone: 'attention', summary: node.failure?.message ?? 'Waiting for your answer', notRun: false };
      case 'skipped': return { chip: null, tone: 'count', summary: 'Skipped', notRun: true };
    }
    // Blocked or cancelled: say whether it ran at all, and if not, why not.
    if (node.startedAtMs) return { chip: 'stopped', tone: 'neutral', summary: node.failure?.message ?? 'Stopped before it finished', notRun: false };
    const failed = failedUpstream(run, node.nodeId);
    if (failed) return { chip: null, tone: 'count', summary: `Not run — ${stepTitle(run, failed)} failed`, notRun: true };
    if (['completed', 'failed', 'cancelled'].includes(run.state)) {
      return { chip: null, tone: 'count', summary: 'Not run — the run stopped first', notRun: true };
    }
    const waitingOn = run.definition.nodes.find((item) => item.id === node.nodeId)?.dependsOn
      .find((id) => !['completed', 'skipped'].includes(run.nodes.find((other) => other.nodeId === id)?.state ?? ''));
    return { chip: null, tone: 'count', summary: waitingOn ? `Waits for ${stepTitle(run, waitingOn)}` : 'Waiting to start', notRun: false };
  }

  /** A failed run names where it stopped and why, in one line. */
  function stoppedLine(run: WorkflowRunRecord, node: WorkflowNodeRunRecord | null): string | null {
    if (run.state !== 'failed' || !node) return null;
    const why = node.failure?.message;
    return why
      ? `Stopped at ${stepTitle(run, node.nodeId)}: ${why.charAt(0).toLowerCase()}${why.slice(1)}`
      : `Stopped at ${stepTitle(run, node.nodeId)}`;
  }

  function retryStep(run: WorkflowRunRecord, node: WorkflowNodeRunRecord): void {
    void control('Retry', () => retryWorkflowNode({ runId: run.id, nodeId: node.nodeId, idempotencyKey: key('retry') }, machine));
  }

  function key(action: string): string {
    return `agents:${action}:${crypto.randomUUID()}`;
  }

  async function refresh(signal: AbortSignal, generation = loadGeneration): Promise<void> {
    beginWorkflowLoad();
    try {
      const snapshots = await listWorkflowRuns(machine);
      if (signal.aborted || generation !== loadGeneration) return;
      applyWorkflowSnapshots(snapshots);
      if (!workflowState.selectedWorkflowRunId && snapshots?.length) {
        selectWorkflowRun(snapshots.at(-1)?.id ?? null);
      }
    } catch (error) {
      if (!signal.aborted && generation === loadGeneration) {
        failWorkflowLoad(`Could not load workflows: ${workflowErrorText(error)}`);
      }
    }
  }

  async function control(
    label: string,
    action: () => Promise<WorkflowRunRecord | null>
  ): Promise<void> {
    actionController?.abort();
    const controller = new AbortController();
    actionController = controller;
    const generation = loadGeneration;
    busy = label;
    workflowState.error = null;
    try {
      const run = await action();
      if (controller.signal.aborted || generation !== loadGeneration || !run) return;
      upsertWorkflowSnapshot(run);
      selectWorkflowRun(run.id);
    } catch (error) {
      if (!controller.signal.aborted && generation === loadGeneration) {
        workflowState.error = `${label}: ${workflowErrorText(error)}`;
      }
    } finally {
      controller.abort();
      if (actionController === controller) actionController = null;
      if (generation === loadGeneration) busy = null;
    }
  }

  async function createAndStart(): Promise<void> {
    const task = { key: taskKey.trim(), title: taskTitle.trim(), body: taskBody.trim() };
    if (!task.key || !task.title || !root || !workflowPath) return;
    await control('Starting workflow', async () => {
      const created = await createWorkflowRun({
        repoRoot: root,
        workflowPath,
        task,
        input: {},
        idempotencyKey: key('create')
      }, machine);
      if (!created) return null;
      const started = await startWorkflowRun({ runId: created.id, idempotencyKey: key('start') }, machine);
      return started ?? created;
    });
    if (!workflowState.error) {
      taskKey = '';
      taskTitle = '';
      taskBody = '';
      composing = false;
    }
  }

  $effect(() => {
    if (!timelineKey) return;
    const run = untrack(() => selected);
    if (!run) return;
    const owner = machine;
    const controller = new AbortController();
    // Untracked: the load reads and writes the timeline it keeps while reloading.
    untrack(() => { void loadTimeline(run, owner, controller.signal); });
    return () => controller.abort();
  });

  /** Reads the run's history and messages and merges them with its snapshot. */
  async function loadTimeline(run: WorkflowRunRecord, owner: string | null, signal: AbortSignal): Promise<void> {
    const kept = timeline?.runId === run.id ? timeline.items : [];
    timeline = { runId: run.id, items: kept, error: '', loading: true };
    try {
      const [history, messages] = await Promise.all([
        listWorkflowRunHistory(owner, run.id, signal),
        // This Mac is 'mac' to the message relay; no machine at all would read every machine.
        listAgentMessagesFromTauri({ runId: run.id, machine: owner ?? 'mac', before: null, limit: 200 }, signal)
      ]);
      if (signal.aborted) return;
      timeline = { runId: run.id, items: buildWorkflowTimeline(history ?? [], messages ?? [], run), error: '', loading: false };
    } catch (error) {
      if (!signal.aborted) timeline = { runId: run.id, items: kept, error: `Could not load the timeline: ${workflowErrorText(error)}`, loading: false };
    }
  }

  // A newer click, another run, or the view going away makes a slow lookup's answer stale.
  let stageOpen = 0;
  let stageLookup: AbortController | null = null;
  // Choosing another run stops a stage lookup for the old one.
  $effect(() => {
    void workflowState.selectedWorkflowRunId;
    untrack(() => {
      stageLookup?.abort();
      openStepId = null;
    });
  });

  async function openStage(ownedId: string | null): Promise<void> {
    if (!ownedId) return;
    stageLookup?.abort();
    const lookup = new AbortController();
    stageLookup = lookup;
    const attempt = ++stageOpen;
    const generation = loadGeneration;
    const runId = workflowState.selectedWorkflowRunId;
    const current = (): boolean => !lookup.signal.aborted && attempt === stageOpen && generation === loadGeneration
      && runId === workflowState.selectedWorkflowRunId;
    try {
      const adopted = await adoptSession(ownedId, lookup.signal);
      if (!current()) return;
      if (!adopted) throw new Error('Agent conversation is not available yet.');
      await sessionRowJump(ownedId, 'session');
    } catch (error) {
      if (current()) workflowState.error = `Could not open agent: ${workflowErrorText(error)}`;
    }
  }
</script>

<div class="flex h-full min-h-0 flex-col">
  {#if workflowState.error}
    <div class="mx-2 mb-2 flex min-w-0 items-start gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-[13px] leading-snug text-destructive">
      <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
      <p class="min-w-0 flex-1 [overflow-wrap:anywhere]">{workflowState.error}</p>
    </div>
  {/if}

  {#if selected && openStep}
    <!-- One step's details, opened from its row or its pipeline segment; Back returns to the run. -->
    {@const status = stepStatus(selected, openStep)}
    <div class="flex min-w-0 items-center gap-2 px-2 pb-2" data-testid="workflow-step-header">
      <Button variant="ghost" size="sm" iconPosition="start" onclick={() => (openStepId = null)}>
        <ArrowLeft data-icon="inline-start" />Back
      </Button>
      <span class="min-w-0 flex-1 truncate text-[13px] font-medium">{stepTitle(selected, openStep.nodeId)}</span>
      {#if status.chip}
        <Chip tone={status.tone}>{status.chip}</Chip>
      {:else if openStep.state === 'completed'}
        <Chip tone="good">completed</Chip>
      {:else if status.notRun}
        <Chip tone="count">not run</Chip>
      {/if}
      {#if openStep.provider && openStep.startedAtMs}
        <Button variant="ghost" size="sm" iconPosition="start" onclick={() => void openStage(openStep.ownedId)}>
          <MessageSquare data-icon="inline-start" />Open chat
        </Button>
      {/if}
    </div>
    <ScrollArea class="min-h-0 flex-1">
      <div class="px-2 pb-3">
        <WorkflowStepDetails
          node={openStep}
          {status}
          kind={openStep.provider ? `${openStep.provider} agent · ${openStep.roleId}` : 'Run by Assembly'}
          duration={elapsed(openStep)}
          busy={busy !== null}
          onRetry={openStep.state === 'failed' ? () => retryStep(selected, openStep) : null}
          rounds={shownTimeline?.items.filter((item) => item.nodeId === openStep.nodeId
            && ['review', 'checks', 'finish', 'failed', 'timeout', 'waiting'].includes(item.kind)) ?? []}
        />
      </div>
    </ScrollArea>
  {/if}

  <!-- The run list and run view stay mounted under a step's details, so Back keeps the scroll position. -->
  <div class="flex min-h-0 flex-1 flex-col" class:hidden={openStep !== null}>
    <div class="flex min-w-0 items-center gap-3 pr-2 pb-2 pl-5">
      <p class="min-w-0 flex-1 text-sm leading-snug text-muted-foreground">
        Runs from <code>.assembly/workflows/</code> in this project.
      </p>
      <Button size="sm" variant={composing ? 'secondary' : 'default'} onclick={() => (composing = !composing)}>
        <Plus />
        New
      </Button>
    </div>

    {#if composing}
      <form class="mx-2 mb-2 flex max-h-[60%] flex-col gap-3 overflow-y-auto rounded-xl bg-foreground/8 p-3" onsubmit={(event) => { event.preventDefault(); void createAndStart(); }}>
        {#if workflowFiles.length === 0}
          <p class="text-sm text-muted-foreground">This project has no workflows yet. Ask an agent to write one in <code>.assembly/workflows/</code>.</p>
        {:else}
          <label class="flex min-w-0 flex-col gap-1 text-xs text-muted-foreground">
            Workflow
            <select bind:value={workflowPath} class="h-8 rounded-lg border border-input bg-background px-2 text-sm outline-none focus-visible:border-ring min-w-0 text-foreground">
              {#each workflowFiles as file (file)}
                <option value={file}>{file.replace('.assembly/workflows/', '')}</option>
              {/each}
            </select>
          </label>
          <div class="grid grid-cols-[8rem_1fr] gap-2">
            <input bind:value={taskKey} placeholder="TSK-123" aria-label="Task key" class="h-8 min-w-0 rounded-lg border border-input bg-background px-2 text-sm outline-none focus-visible:border-ring" />
            <input bind:value={taskTitle} placeholder="Task title" aria-label="Task title" class="h-8 min-w-0 rounded-lg border border-input bg-background px-2 text-sm outline-none focus-visible:border-ring" />
          </div>
          <textarea
            bind:value={taskBody}
            rows="4"
            placeholder="Task description for the agents…"
            aria-label="Task description"
            class="min-h-24 resize-y rounded-xl border border-input bg-background px-3 py-2 text-sm font-normal outline-none focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/40"
          ></textarea>
          <Button type="submit" disabled={!taskKey.trim() || !taskTitle.trim() || !root || !workflowPath || busy !== null}>
            <Play />
            Start workflow
          </Button>
        {/if}
      </form>
    {/if}

    {#if workflowState.loading && runs.length === 0}
      <div class="flex items-center gap-2 px-3 py-4 text-sm text-muted-foreground"><WorkingSpinner size={14} /> Loading workflows…</div>
    {:else if runs.length === 0}
      <div class="flex flex-1 flex-col items-center justify-center gap-2 p-6 text-center">
        <p class="font-medium">No workflows yet</p>
        <p class="max-w-72 text-sm text-muted-foreground">Start a run from one of this project's workflow files.</p>
      </div>
    {:else}
      <ScrollArea class="min-h-0 flex-1">
        <div class="flex min-w-0 flex-col gap-2 px-2 pb-3">
          <Card.Root size="sm" class="gap-1.5 bg-foreground/8">
            <Card.Header>
              {@render heading('Runs', runs.length)}
            </Card.Header>
            <Card.Content class="flex flex-col gap-0.5 px-1">
              {#each [...runs].reverse() as run (run.id)}
                <button
                  type="button"
                  aria-current={selected?.id === run.id ? 'true' : undefined}
                  class="flex min-w-0 items-center gap-3 rounded-lg px-2 py-1.5 text-left outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50 {selected?.id === run.id ? 'bg-secondary' : 'hover:bg-accent/60'}"
                  onclick={() => selectWorkflowRun(run.id)}
                >
                  <span class="flex min-w-0 flex-1 flex-col gap-0.5">
                    <span class="truncate text-[13px] leading-snug font-medium" title={runTitle(run)}>{runTitle(run)}</span>
                    <span class="truncate text-sm text-muted-foreground tabular-nums">{run.phase} · {run.nodes.filter((node) => ['completed', 'skipped'].includes(node.state)).length}/{run.nodes.length} steps</span>
                  </span>
                  <Chip tone={runTone(run)}>{run.state}</Chip>
                </button>
              {/each}
            </Card.Content>
          </Card.Root>

          {#if selected}
            {@const stopped = stoppedLine(selected, failedNode)}
            <!-- The selected run: what it is, where it got to, and what can be done with it. -->
            <Card.Root size="sm" class="gap-3 bg-foreground/8">
              <Card.Header>
                <div class="flex min-w-0 items-start gap-3">
                  <div class="flex min-w-0 flex-1 flex-col gap-0.5">
                    <h3 class="text-(length:--text-body) leading-snug font-semibold [overflow-wrap:anywhere]">{runTitle(selected)}</h3>
                    <p class="truncate text-sm text-muted-foreground">{selected.definition.name} · {completedCount} of {selected.nodes.length} steps done</p>
                  </div>
                  <Chip tone={runTone(selected)}>{selected.state}</Chip>
                </div>
              </Card.Header>
              <Card.Content class="flex flex-col gap-3">
                <WorkflowPipelineStrip
                  segments={orderedNodes.map((node) => {
                    const status = stepStatus(selected, node);
                    const state = status.chip ?? (node.state === 'completed' ? 'completed' : status.notRun ? 'not run' : 'waiting');
                    return { key: node.nodeId, title: stepTitle(selected, node.nodeId), tone: status.tone, label: status.summary, state, attempt: node.attempt };
                  })}
                  onSelect={(nodeId) => (openStepId = nodeId)}
                />

                {#if stopped && failedNode}
                  <div class="flex min-w-0 items-center gap-2 rounded-lg bg-destructive/10 py-1.5 pr-1.5 pl-2.5 text-[13px] leading-snug text-destructive">
                    <TriangleAlert class="size-3.5 shrink-0" aria-hidden="true" />
                    <p class="min-w-0 flex-1 [overflow-wrap:anywhere]">{stopped}</p>
                    <Button size="sm" variant="outline" disabled={busy !== null} onclick={() => retryStep(selected, failedNode)}><RotateCcw />Retry</Button>
                  </div>
                {/if}

                {#if stale}
                  <p class="flex items-start gap-2 rounded-lg bg-muted px-2.5 py-2 text-sm text-foreground">
                    <TriangleAlert class="mt-0.5 size-3.5 shrink-0" />
                    This stage has passed its expected time. It is still running; review or stop it when ready.
                  </p>
                {/if}

                <div class="flex flex-wrap items-center gap-2">
                  {#if selected.state === 'draft' || selected.state === 'queued'}
                    <Button size="sm" disabled={busy !== null} onclick={() => void control('Start', () => startWorkflowRun({ runId: selected.id, idempotencyKey: key('start') }, machine))}><Play />Start</Button>
                  {:else if selected.state === 'paused'}
                    <Button size="sm" disabled={busy !== null} onclick={() => void control('Resume', () => resumeWorkflowRun({ runId: selected.id, idempotencyKey: key('resume') }, machine))}><Play />Resume</Button>
                  {:else if selected.state === 'running'}
                    <Button size="sm" variant="outline" disabled={busy !== null} onclick={() => void control('Pause', () => pauseWorkflowRun({ runId: selected.id, idempotencyKey: key('pause') }, machine))}><Pause />Pause after stage</Button>
                  {/if}
                  {#if waitingNode}
                    <Button size="sm" disabled={busy !== null} onclick={() => void control('Approve', () => approveWorkflowGate({ runId: selected.id, nodeId: waitingNode.nodeId, approval: { approved: true }, idempotencyKey: key('approve') }, machine))}><Check />Approve handoff</Button>
                  {/if}
                  {#if pausedNode}
                    <Button size="sm" disabled={busy !== null} onclick={() => void control('Another round', () => answerWorkflowPause({ runId: selected.id, nodeId: pausedNode.nodeId, decision: 'another-round', reply: null, idempotencyKey: key('answer') }, machine))}><RotateCcw />Run another round</Button>
                    <Button size="sm" variant="outline" disabled={busy !== null} onclick={() => void control('Accept', () => answerWorkflowPause({ runId: selected.id, nodeId: pausedNode.nodeId, decision: 'accept', reply: null, idempotencyKey: key('answer') }, machine))}><Check />Accept as is</Button>
                  {/if}
                  {#if failedNode && !stopped}
                    <Button size="sm" variant="outline" disabled={busy !== null} onclick={() => retryStep(selected, failedNode)}><RotateCcw />Retry stage</Button>
                  {/if}
                  <Button size="sm" variant="secondary" onclick={() => onShowMessages(selected.id, runTitle(selected))}><MessagesSquare />Messages</Button>
                  {#if !['completed', 'cancelled', 'failed'].includes(selected.state)}
                    <Button size="sm" variant="destructive" class="ml-auto" disabled={busy !== null} onclick={() => void control('Stop', () => cancelWorkflowRun({ runId: selected.id, idempotencyKey: key('cancel') }, machine))}><CircleStop />Stop</Button>
                  {/if}
                </div>
              </Card.Content>
            </Card.Root>

            <!-- Steps in dependency order; each row opens that step's details. -->
            <Card.Root size="sm" class="gap-1.5 bg-foreground/8">
              <Card.Header>
                {@render heading('Steps', selected.nodes.length)}
              </Card.Header>
              <Card.Content class="px-1">
                <ol class="m-0 flex min-w-0 list-none flex-col gap-0.5 p-0">
                  {#each orderedNodes as node (node.id)}
                    {@const status = stepStatus(selected, node)}
                    <li class="min-w-0">
                      <button
                        type="button"
                        class="group flex w-full min-w-0 items-center gap-3 rounded-lg px-2 py-1.5 text-left outline-none transition-colors hover:bg-accent/60 focus-visible:ring-3 focus-visible:ring-ring/50"
                        onclick={() => (openStepId = node.nodeId)}
                      >
                        {@render stateIcon(node, status)}
                        <span class="flex min-w-0 flex-1 flex-col gap-0.5">
                          <span class="truncate text-[13px] leading-snug font-medium" class:text-muted-foreground={status.notRun}>{stepTitle(selected, node.nodeId)}</span>
                          <span class="truncate text-sm text-muted-foreground tabular-nums">{node.state === 'completed' ? stepMeta(node) : status.summary}</span>
                        </span>
                        {#if node.attempt > 1}
                          <span class="shrink-0 text-sm text-muted-foreground tabular-nums" title={`Ran ${node.attempt} times`}>×{node.attempt}</span>
                        {/if}
                        {#if status.chip}<Chip tone={status.tone}>{status.chip}</Chip>{/if}
                        <ChevronRight class="size-4 shrink-0 text-muted-foreground group-hover:text-foreground" aria-hidden="true" />
                      </button>
                    </li>
                  {/each}
                </ol>
              </Card.Content>
            </Card.Root>

            <WorkflowTimeline
              items={shownTimeline?.items ?? []}
              loading={shownTimeline?.loading ?? true}
              error={shownTimeline?.error ?? ''}
              local={!machine}
              onOpenStep={(nodeId) => (openStepId = nodeId)}
            />
          {/if}
        </div>
      </ScrollArea>
    {/if}
  </div>
</div>

{#snippet heading(label: string, count: number)}
  <div class="flex min-w-0 items-center gap-2">
    <h3 class="min-w-0 flex-1 truncate text-[13px] leading-tight font-semibold text-foreground">{label}</h3>
    {#if count > 0}<Chip tone="count">{count}</Chip>{/if}
  </div>
{/snippet}

{#snippet stateIcon(node: WorkflowNodeRunRecord, status: StepStatus)}
  <span class="flex size-5 shrink-0 items-center justify-center [&>svg]:size-4" aria-hidden="true">
    {#if node.state === 'completed'}
      <CircleCheck class="text-[var(--color-good)]" />
    {:else if node.state === 'failed'}
      <CircleX class="text-[var(--color-bad)]" />
    {:else if status.tone === 'live'}
      <CircleDot class="text-[var(--color-live)]" />
    {:else if status.tone === 'attention'}
      <CircleAlert class="text-[var(--color-attention)]" />
    {:else if status.notRun}
      <CircleDashed class="text-muted-foreground" />
    {:else if status.chip === 'stopped'}
      <CircleMinus class="text-muted-foreground" />
    {:else}
      <Circle class="text-muted-foreground" />
    {/if}
  </span>
{/snippet}
