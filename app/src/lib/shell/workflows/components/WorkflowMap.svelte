<!--
  WorkflowMap.svelte — a run's steps as boxes laid out by `needs` depth, with
  one SVG layer under them for the arrows. A step that sends work back (a
  review, or a check with `on_fail`) draws a loop arrow over the boxes with its
  round. Boxes are plain HTML; a state change repaints only its box. Nothing
  animates: a running step is a static ring with its elapsed time.
-->
<script lang="ts">
  import Maximize2 from '@lucide/svelte/icons/maximize-2';

  import { closeupKind } from '../workflowCloseup.ts';
  import { layoutWorkflowMap } from '../workflowMapLayout.ts';
  import { shortDuration, stepGlyph } from '../workflowBoard.ts';
  import { workflowErrorText, type WorkflowNodeRunRecord, type WorkflowRunRecord } from '../workflowTypes.ts';
  import StepGlyphIcon from './StepGlyphIcon.svelte';

  interface Props {
    run: WorkflowRunRecord;
    selectedId: string | null;
    now: number;
    onSelect: (nodeId: string) => void;
    /** Opens a review or proof step's close-up. */
    onCloseup: (nodeId: string) => void;
  }

  let { run, selectedId, now, onSelect, onCloseup }: Props = $props();

  const BOX_W = 184, BOX_H = 104, GAP_X = 44, GAP_Y = 24, TOP = 96;
  const layout = $derived(layoutWorkflowMap(run.definition.nodes));
  const place = $derived(new Map(layout.boxes.map((box) => [box.id, {
    x: box.column * (BOX_W + GAP_X), y: TOP + box.row * (BOX_H + GAP_Y)
  }])));
  const width = $derived(Math.max(1, layout.columns * (BOX_W + GAP_X) - GAP_X));
  const height = $derived(TOP + Math.max(1, layout.rows) * (BOX_H + GAP_Y));
  const maxRounds = $derived(Number(run.definition.limits?.max_rounds ?? 0) || null);

  const nodeOf = (id: string): WorkflowNodeRunRecord | undefined => run.nodes.find((node) => node.nodeId === id);

  const arrows = $derived(run.definition.nodes.flatMap((definition) => definition.dependsOn.flatMap((need) => {
    const from = place.get(need), to = place.get(definition.id);
    if (!from || !to) return [];
    const x1 = from.x + BOX_W, y1 = from.y + BOX_H / 2, x2 = to.x, y2 = to.y + BOX_H / 2;
    return [`M${x1} ${y1} C${x1 + GAP_X / 2} ${y1} ${x2 - GAP_X / 2} ${y2} ${x2} ${y2}`];
  })));

  /** A step that sends work back draws an arc from its top to its target's top, labelled with its round. */
  const loops = $derived.by(() => {
    // Loops back to the same step stack their arcs and labels instead of drawing over each other.
    const perTarget = new Map<string, number>();
    return run.definition.nodes.flatMap((definition) => {
    const review = definition.step?.review as { of?: unknown } | undefined;
    const target = typeof review?.of === 'string' ? review.of : typeof definition.step?.on_fail === 'string' ? definition.step.on_fail : null;
    const from = place.get(definition.id), to = target ? place.get(target) : undefined;
    if (!from || !to || !target) return [];
    const x1 = from.x + BOX_W / 2, x2 = to.x + BOX_W / 2, top = Math.min(from.y, to.y);
    const node = nodeOf(definition.id);
    const label = node?.startedAtMs
      ? `round ${node.attempt}${maxRounds && node.attempt <= maxRounds ? ` of ${maxRounds}` : ''}`
      : `sends back to ${target}`;
    // Wider loops arc higher, so their labels sit at different heights.
    const stacked = perTarget.get(target) ?? 0;
    perTarget.set(target, stacked + 1);
    const rise = Math.min(TOP * 1.7, 40 + Math.abs(x2 - x1) * 0.2 + stacked * 36);
    return [{ id: definition.id, path: `M${x1} ${from.y} Q${(x1 + x2) / 2} ${top - rise} ${x2} ${to.y}`, label, x: (x1 + x2) / 2, y: top - rise / 2 - 4 }];
    });
  });

  function subtitle(definitionStep: Record<string, unknown> | undefined, node: WorkflowNodeRunRecord | undefined): string {
    const review = definitionStep?.review as { agent?: { model?: unknown; provider?: unknown } } | undefined;
    const agent = (definitionStep?.agent ?? review?.agent) as { model?: unknown; provider?: unknown } | undefined;
    const who = agent ? String(agent.model ?? agent.provider ?? '') : 'run';
    const attempt = node?.startedAtMs && node.attempt > 1 ? `attempt ${node.attempt}` : '';
    return [who, attempt].filter(Boolean).join(' · ');
  }

  function detail(node: WorkflowNodeRunRecord | undefined): string {
    if (!node) return '';
    if (node.failure) return workflowErrorText(node.failure.message);
    if (!node.startedAtMs) return '';
    const end = node.finishedAtMs ?? (node.state === 'running' || node.state === 'starting' ? now : null);
    return end === null ? '' : shortDuration(end - node.startedAtMs);
  }

  const FILL: Record<string, string> = {
    running: 'bg-[var(--color-live-bg)]', failed: 'bg-[var(--color-bad-bg)]', 'needs-you': 'bg-[var(--color-attention-bg)]'
  };
</script>

<div class="map-scroll" data-testid="workflow-map">
  <div class="relative" style:width={`${width}px`} style:height={`${height}px`}>
    <svg class="pointer-events-none absolute inset-0 overflow-visible text-muted-foreground" {width} {height} aria-hidden="true">
      <defs>
        <marker id="map-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">
          <path d="M0 0 L8 4 L0 8 z" fill="currentColor" />
        </marker>
      </defs>
      {#each arrows as path (path)}
        <path d={path} fill="none" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#map-arrow)" />
      {/each}
      {#each loops as loop (loop.id)}
        <path d={loop.path} fill="none" stroke="currentColor" stroke-dasharray="4 4" stroke-opacity="0.7" marker-end="url(#map-arrow)" />
        <text x={loop.x} y={loop.y} text-anchor="middle" fill="currentColor" font-size="13">{loop.label}</text>
      {/each}
    </svg>
    {#each run.definition.nodes as definition (definition.id)}
      {@const at = place.get(definition.id)}
      {@const node = nodeOf(definition.id)}
      {@const glyph = node ? stepGlyph(node) : 'waiting'}
      {@const closeup = closeupKind(run, definition.id)}
      {#if at}
        <button
          type="button"
          class="absolute flex flex-col gap-1 overflow-hidden rounded-xl p-3 text-left shadow-sm outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50 {selectedId === definition.id ? 'bg-accent' : (FILL[glyph] ?? 'bg-card')} hover:bg-accent/80"
          class:opacity-60={glyph === 'skipped' || glyph === 'waiting'}
          style:left={`${at.x}px`} style:top={`${at.y}px`} style:width={`${BOX_W}px`} style:height={`${BOX_H}px`}
          aria-pressed={selectedId === definition.id}
          onclick={() => onSelect(definition.id)}
        >
          <span class="flex min-w-0 items-center gap-1.5 text-[13px] font-semibold" class:pr-6={closeup}>
            <StepGlyphIcon {glyph} /><span class="truncate">{definition.title}</span>
          </span>
          <span class="truncate text-[13px] text-muted-foreground">{subtitle(definition.step, node)}</span>
          <span class="line-clamp-2 text-[13px] text-muted-foreground tabular-nums [overflow-wrap:anywhere]">{detail(node)}</span>
        </button>
        {#if closeup}
          <!-- A sibling of the box, not inside it: a button cannot hold another button. -->
          <button
            type="button"
            class="absolute flex size-6 items-center justify-center rounded-md text-muted-foreground outline-none hover:bg-background/60 hover:text-foreground focus-visible:ring-3 focus-visible:ring-ring/50"
            style:left={`${at.x + BOX_W - 28}px`} style:top={`${at.y + 4}px`}
            aria-label={`Close-up of ${definition.title}`}
            title="Close-up"
            onclick={() => onCloseup(definition.id)}
          ><Maximize2 class="size-3.5" aria-hidden="true" /></button>
        {/if}
      {/if}
    {/each}
  </div>
</div>

<style>
  .map-scroll {
    flex: 0 1 auto;
    min-height: 0;
    overflow: auto;
    padding: var(--space-2) 0 var(--space-3);
  }
</style>
