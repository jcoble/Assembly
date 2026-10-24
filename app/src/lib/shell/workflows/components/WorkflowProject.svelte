<!--
  WorkflowProject.svelte — a project run's Project tab: the project run as one
  row, then each slice (the runs whose parent_task is this run's task) with its
  steps. The current slice starts open; a slice opens its own run page.
  Milestones are left out: the approved plan does not record them yet.
-->
<script lang="ts">
  import ChevronRight from '@lucide/svelte/icons/chevron-right';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Card from '$lib/components/ui/card/index.js';
  import * as Collapsible from '$lib/components/ui/collapsible/index.js';
  import { runTask, stepGlyph } from '../workflowBoard.ts';
  import { runGlyph, sliceCount, type ProjectProgress } from '../workflowProject.ts';
  import { workflowErrorText, type WorkflowRunRecord } from '../workflowTypes.ts';
  import StepGlyphIcon from './StepGlyphIcon.svelte';

  interface Props {
    run: WorkflowRunRecord;
    progress: ProjectProgress;
    /** A run's cost text, or '' when nothing is measured. */
    costOf: (run: WorkflowRunRecord) => string;
    onOpen: (runId: string) => void;
  }

  let { run, progress, costOf, onOpen }: Props = $props();

  /** Slices opened or closed by hand; the others follow "the current slice is open". */
  let opened = $state<Record<string, boolean>>({});

  const steps = (of: WorkflowRunRecord): string =>
    `${of.state} · ${of.nodes.filter((node) => node.state === 'completed' || node.state === 'skipped').length} / ${of.nodes.length} steps`;
  const ordered = (of: WorkflowRunRecord) => of.definition.nodes
    .map((definition) => ({ definition, node: of.nodes.find((node) => node.nodeId === definition.id) }))
    .filter((step) => step.node);
  const task = $derived(runTask(run));
</script>

<div class="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto" data-testid="workflow-project">
  <p class="text-[13px] text-muted-foreground tabular-nums">
    {sliceCount(progress)} done · slices are the runs started for {task.key}. Milestones are not shown: the approved plan does not record them yet.
  </p>
  <Card.Root size="sm" class="gap-0 py-0 shadow-sm">
    <div class="row bg-muted/60 font-semibold">
      <span class="flex min-w-0 items-center gap-2.5">
        <StepGlyphIcon glyph={runGlyph(run)} />
        <span class="truncate">Project run · {run.definition.name}</span>
      </span>
      <span class="text-muted-foreground">{steps(run)}</span>
      <span class="text-right text-muted-foreground">{costOf(run)}</span>
    </div>
    {#each progress.slices as slice (slice.key)}
      {@const open = opened[slice.key] ?? slice.key === progress.current?.key}
      <Collapsible.Root {open} onOpenChange={(next) => (opened[slice.key] = next)}>
        <div class="row">
          <Collapsible.Trigger class="flex min-w-0 items-center gap-2.5 rounded-md text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50">
            <ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform {open ? 'rotate-90' : ''}" aria-hidden="true" />
            <StepGlyphIcon glyph={slice.glyph} />
            <span class="truncate">
              {slice.number ? `Slice ${slice.number} · ` : ''}<span class="font-mono text-muted-foreground">{slice.key}</span> · {slice.title}
            </span>
          </Collapsible.Trigger>
          <span class="text-muted-foreground">{steps(slice.run)}</span>
          <span class="text-right text-muted-foreground">{costOf(slice.run)}</span>
        </div>
        <Collapsible.Content>
          <div class="flex flex-col gap-1.5 py-2 pr-4 pl-[60px]">
            {#each ordered(slice.run) as { definition, node } (definition.id)}
              <div class="flex min-w-0 items-center gap-2 text-[13px]">
                <StepGlyphIcon glyph={stepGlyph(node!)} />
                <span class="truncate" title={node!.failure ? workflowErrorText(node!.failure.message) : undefined}>
                  {definition.title}{node!.attempt > 1 ? ` · attempt ${node!.attempt}` : ''}{node!.failure ? `: ${workflowErrorText(node!.failure.message)}` : ''}
                </span>
              </div>
            {/each}
            <Button variant="ghost" size="sm" class="mt-1 w-fit" onclick={() => onOpen(slice.run.id)}>Open this slice's run</Button>
          </div>
        </Collapsible.Content>
      </Collapsible.Root>
    {/each}
  </Card.Root>
</div>

<style>
  .row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 200px 160px;
    gap: var(--space-3);
    align-items: center;
    padding: 9px 16px;
    font-size: 14px;
  }
</style>
