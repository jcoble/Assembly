<!--
  WorkflowBoard.svelte — every run as a card in the column of the step it is
  at, Needs you first. Columns are at least 150px wide and scroll sideways in
  a narrow window. Filters narrow the cards by workflow and by who started the run.
-->
<script lang="ts">
  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip, type ChipTone } from '$lib/components/ui/chip/index.js';
  import { FilterPills } from '$lib/components/ui/filter-pills/index.js';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { BOARD_COLUMNS, currentStep, runTask, shortDuration, stepGlyph, type BoardColumnId } from '../workflowBoard.ts';
  import { workflowErrorText, type WorkflowRunRecord } from '../workflowTypes.ts';
  import { projectColumn, projectIndex, projectProgress, sliceCount } from '../workflowProject.ts';
  import StepGlyphIcon from './StepGlyphIcon.svelte';

  interface Props {
    runs: WorkflowRunRecord[];
    /** The shared clock, for the elapsed time of running cards. */
    now: number;
    /** A run's cost text ("$1.07", "$1.07 + $0.74 est."), or '' when nothing is measured. */
    costOf: (run: WorkflowRunRecord) => string;
    onOpen: (runId: string) => void;
  }

  let { runs, now, costOf, onOpen }: Props = $props();

  let filters = $state<Record<string, string[]>>({});

  const startedBy = (run: WorkflowRunRecord): string => {
    const starter = (run.inputSnapshot as { started_by?: unknown } | null)?.started_by;
    if (typeof starter !== 'string') return 'You';
    return rail.owned.find((session) => session.ownedId === starter)?.title || 'An agent';
  };

  const groups = $derived([
    { id: 'workflow', label: 'Workflow', options: [...new Set(runs.map((run) => run.definition.name))].map((name) => ({ value: name, label: name })) },
    { id: 'starter', label: 'Started by', options: [...new Set(runs.map(startedBy))].map((name) => ({ value: name, label: name })) }
  ]);
  const shown = $derived(runs.filter((run) =>
    (!filters.workflow?.length || filters.workflow.includes(run.definition.name))
    && (!filters.starter?.length || filters.starter.includes(startedBy(run)))));
  /** Who is whose parent, read once per change of the runs. */
  const index = $derived(projectIndex(runs));
  const byColumn = $derived.by(() => {
    const columns = new Map<BoardColumnId, WorkflowRunRecord[]>(BOARD_COLUMNS.map((column) => [column.id, []]));
    for (const run of [...shown].sort((a, b) => b.updatedAtMs - a.updatedAtMs)) columns.get(projectColumn(run, index))?.push(run);
    return columns;
  });
  const active = $derived(shown.filter((run) => !['completed', 'cancelled', 'failed'].includes(run.state)).length);

  const ENDED = ['completed', 'cancelled', 'failed'];
  const elapsed = (run: WorkflowRunRecord): string =>
    shortDuration((ENDED.includes(run.state) ? run.updatedAtMs : now) - run.createdAtMs);

  /** One chip per step that loops (a review, or a check that sends work back), once it has run. */
  function rounds(run: WorkflowRunRecord): { id: string; label: string; tone: ChipTone }[] {
    return run.definition.nodes.flatMap((definition) => {
      const node = run.nodes.find((candidate) => candidate.nodeId === definition.id);
      const loops = Boolean(definition.step?.review || definition.step?.on_fail);
      if (!node || !loops || !node.startedAtMs) return [];
      const glyph = stepGlyph(node);
      const tone: ChipTone = glyph === 'passed' ? 'good' : glyph === 'failed' ? 'bad' : glyph === 'running' ? 'live' : glyph === 'needs-you' ? 'attention' : 'neutral';
      return [{ id: definition.id, label: `${definition.id} R${node.attempt}`, tone }];
    });
  }

  const stepTitle = (run: WorkflowRunRecord, nodeId: string): string =>
    run.definition.nodes.find((node) => node.id === nodeId)?.title ?? nodeId;
</script>

<div class="flex min-w-0 items-center gap-3">
  <FilterPills label="Filter runs" {groups} value={filters} onChange={(next) => (filters = next)} />
  <p class="ml-auto text-[13px] text-muted-foreground tabular-nums">
    {shown.length} runs · {active} active · {byColumn.get('needs-you')?.length ?? 0} need you
  </p>
</div>

<div class="board" data-testid="workflow-board">
  {#each BOARD_COLUMNS as column (column.id)}
    {@const cards = byColumn.get(column.id) ?? []}
    <section class="flex min-w-[150px] flex-1 flex-col gap-2" aria-label={column.label}>
      <h3 class="flex items-baseline gap-1.5 text-[13px] font-semibold" class:text-[var(--color-attention)]={column.id === 'needs-you' && cards.length > 0}>
        {column.label}<span class="font-normal text-muted-foreground tabular-nums">{cards.length}</span>
      </h3>
      {#each cards as run (run.id)}
        {@const task = runTask(run)}
        {@const project = projectProgress(run, index)}
        <!-- A project card shows the step its current slice is at. -->
        {@const stepRun = project?.current?.run ?? run}
        {@const step = currentStep(stepRun)}
        {@const cost = costOf(run)}
        <button type="button" class="rounded-xl text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50" onclick={() => onOpen(run.id)}>
          <Card.Root size="sm" class="gap-2 bg-card shadow-sm transition-colors hover:bg-accent/60">
            <Card.Content class="flex flex-col gap-2">
              <div class="flex min-w-0 items-center gap-1.5">
                <Chip tone="neutral">{project ? 'project' : task.key ? 'task' : 'one-off'}</Chip>
                {#if task.key}<span class="truncate font-mono text-sm text-muted-foreground">{task.key}</span>{/if}
              </div>
              <p class="line-clamp-3 text-[13px] leading-snug font-semibold">{task.title}</p>
              {#if project}<p class="text-[13px] text-muted-foreground tabular-nums">{sliceCount(project)}{project.current ? ` · at ${project.current.key}` : ''}</p>{/if}
              {#if step}
                <div class="flex min-w-0 items-start gap-1.5 text-[13px] text-muted-foreground">
                  <StepGlyphIcon glyph={stepGlyph(step)} />
                  <span class="line-clamp-3 min-w-0 [overflow-wrap:anywhere]" title={step.failure ? workflowErrorText(step.failure.message) : undefined}>
                    {stepTitle(stepRun, step.nodeId)}{step.attempt > 1 ? ` · attempt ${step.attempt}` : ''}{step.failure ? `: ${workflowErrorText(step.failure.message)}` : ''}
                  </span>
                </div>
              {/if}
              {#if rounds(stepRun).length}
                <div class="flex flex-wrap gap-1">
                  {#each rounds(stepRun) as round (round.id)}<Chip tone={round.tone} class="max-w-full"><span class="truncate">{round.label}</span></Chip>{/each}
                </div>
              {/if}
              <div class="flex items-center justify-between gap-2 text-[13px] text-muted-foreground tabular-nums">
                <span>{elapsed(run)}</span>
                {#if cost}<span>{cost}</span>{/if}
              </div>
            </Card.Content>
          </Card.Root>
        </button>
      {:else}
        <p class="text-[13px] text-muted-foreground">None</p>
      {/each}
    </section>
  {/each}
</div>

<style>
  .board {
    display: flex;
    flex: 1 1 auto;
    gap: var(--space-3);
    min-height: 0;
    overflow: auto;
    align-items: flex-start;
    padding-bottom: var(--space-3);
  }
</style>
