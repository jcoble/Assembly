<!--
  WorkflowStageCloseup.svelte — one stage of a run up close, opened from a
  review or proof box on the Map or from the step details.

  A review group shows each reviewer per round, with its verdict and its
  findings from the run's stored review reports, and every answer to the
  group's waiting steps with who gave it and why. The proof step shows what it
  reported: its summary and its files (images as pictures when the run is on
  this machine).
-->
<script lang="ts">
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip, type ChipTone } from '$lib/components/ui/chip/index.js';
  import { readSourceImageFromTauri } from '$lib/tauriSource.ts';
  import { stepGlyph } from '../workflowBoard.ts';
  import { capDecisions, closeupKind, proofEvidence, proofImagePaths, reviewGroup, reviewRounds, type WorkflowReviewReport } from '../workflowCloseup.ts';
  import { readWorkflowReviews } from '../workflowService.ts';
  import { workflowErrorText, type WorkflowRunRecord } from '../workflowTypes.ts';
  import StepGlyphIcon from './StepGlyphIcon.svelte';

  interface Props {
    run: WorkflowRunRecord;
    nodeId: string;
    /** The remote machine that runs it; null for this Mac. */
    machine: string | null;
  }

  let { run, nodeId, machine }: Props = $props();

  const kind = $derived(closeupKind(run, nodeId));
  const group = $derived(kind === 'review' ? reviewGroup(run, nodeId) : []);
  const titleOf = (id: string): string => run.definition.nodes.find((item) => item.id === id)?.title || id;
  const target = $derived(((run.definition.nodes.find((item) => item.id === nodeId)?.step?.review ?? {}) as { of?: string }).of ?? '');
  const maxRounds = $derived(Number(run.definition.limits?.max_rounds ?? 0) || null);
  const nodeOf = (id: string) => run.nodes.find((item) => item.nodeId === id);
  const modelOf = (id: string): string =>
    String(((run.definition.nodes.find((item) => item.id === id)?.step?.review as { agent?: { model?: unknown } } | undefined)?.agent?.model) ?? '');

  let reports = $state<WorkflowReviewReport[]>([]);
  let reading = $state<'loading' | 'done' | string>('loading');
  const reviewers = $derived(reviewRounds(run, group, reports));
  const decisions = $derived(capDecisions(run, group));
  const stopped = $derived(group.map(nodeOf).filter((node) => node?.failure && node.state === 'waiting-input'));

  // The reports are read again whenever the run moves on.
  const readKey = $derived(kind === 'review' ? `${run.id}\n${run.updatedAtMs}` : '');
  $effect(() => {
    if (!readKey) return;
    const controller = new AbortController();
    void readReports(run.id, controller.signal);
    return () => controller.abort();
  });

  async function readReports(runId: string, signal: AbortSignal): Promise<void> {
    try {
      const rows = await readWorkflowReviews(machine, runId);
      if (signal.aborted) return;
      reports = rows ?? [];
      reading = rows === null ? 'Review reports are shown in the desktop app.' : 'done';
    } catch (cause) {
      if (!signal.aborted) reading = `Could not read the review reports: ${workflowErrorText(cause)}`;
    }
  }

  const VERDICT: Record<string, [string, ChipTone]> = { pass: ['passed', 'good'], fail: ['sent back', 'bad'] };
  const DECISION: Record<string, string> = { 'another-round': 'another round', accept: 'accept as is', stop: 'stop the run' };
  const clock = (ms: number): string => new Date(ms).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });

  const proofNode = $derived(kind === 'proof' ? nodeOf(nodeId) : undefined);
  const proof = $derived(proofNode ? proofEvidence(run, proofNode) : { summary: '', files: [] });
  const fileName = (path: string): string => path.split('/').at(-1) ?? path;

  // Pictures of the proof images, read from the run's machine; their object URLs are let go with them.
  let pictures = $state<Record<string, string>>({});
  const images = $derived(proof.files.filter((file) => proofImagePaths([file], machine).length).join('\n'));
  $effect(() => {
    const files = images ? images.split('\n') : [];
    if (!files.length) return;
    const controller = new AbortController();
    const urls: string[] = [];
    void loadPictures(files, urls, controller.signal);
    return () => {
      controller.abort();
      urls.forEach((url) => URL.revokeObjectURL(url));
      pictures = {};
    };
  });

  async function loadPictures(files: string[], urls: string[], signal: AbortSignal): Promise<void> {
    for (const file of files) {
      try {
        const bytes = await readSourceImageFromTauri(proofImagePaths([file], machine)[0], signal);
        if (signal.aborted) return;
        const url = URL.createObjectURL(new Blob([bytes as BlobPart]));
        urls.push(url);
        pictures = { ...pictures, [file]: url };
      } catch {
        // A file that cannot be read keeps its name.
        if (signal.aborted) return;
      }
    }
  }
</script>

<div class="min-h-0 flex-1 overflow-y-auto text-[13px]" data-testid="workflow-stage-closeup">
<div class="flex flex-col gap-3">
  {#if kind === 'review'}
    <div class="flex min-w-0 flex-wrap items-center gap-2">
      <h2 class="truncate text-(length:--text-heading) font-semibold">Review of {titleOf(target)}</h2>
      <span class="text-muted-foreground">
        {group.length === 1 ? '1 reviewer' : `${group.length} reviewers`}{maxRounds ? ` · waits for a decision after ${maxRounds} rounds with findings` : ''}
      </span>
    </div>

    {#each stopped as node (node?.nodeId)}
      <p class="flex items-start gap-2 rounded-lg bg-[var(--color-attention-bg)] px-3 py-2 [overflow-wrap:anywhere]">
        <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />{titleOf(node?.nodeId ?? '')} is waiting for a decision: {node?.failure?.message}
      </p>
    {/each}

    {#if decisions.length}
      <Card.Root size="sm" class="gap-2 bg-foreground/8">
        <Card.Header><h3 class="text-[13px] font-semibold">Decisions</h3></Card.Header>
        <Card.Content>
          <ul class="m-0 flex list-none flex-col p-0">
            {#each decisions as decision, index (index)}
              <li class="flex min-w-0 flex-col gap-0.5 border-b py-1.5 last:border-b-0">
                <span><b class="font-medium">{decision.by === 'orchestrator' ? 'Decided by the orchestrator' : 'You answered'}</b>: {DECISION[decision.decision] ?? decision.decision} · {titleOf(decision.step)}</span>
                {#if decision.why}<span class="text-muted-foreground [overflow-wrap:anywhere]">Why: {decision.why}</span>{/if}
                {#if decision.reply}<span class="text-muted-foreground [overflow-wrap:anywhere]">“{decision.reply}”</span>{/if}
              </li>
            {/each}
          </ul>
        </Card.Content>
      </Card.Root>
    {/if}

    {#if reading !== 'done' && reading !== 'loading'}
      <p class="text-muted-foreground">{reading}</p>
    {/if}

    {#each reviewers as reviewer (reviewer.nodeId)}
      {@const node = nodeOf(reviewer.nodeId)}
      <Card.Root size="sm" class="gap-2 bg-foreground/8" data-testid="closeup-reviewer">
        <Card.Header>
          <div class="flex min-w-0 items-center gap-2">
            {#if node}<StepGlyphIcon glyph={stepGlyph(node)} />{/if}
            <h3 class="truncate text-[13px] font-semibold">{reviewer.title}</h3>
            <span class="truncate text-muted-foreground">{[node?.provider, modelOf(reviewer.nodeId)].filter(Boolean).join(' ')} · a fresh reviewer each round</span>
          </div>
        </Card.Header>
        <Card.Content>
          {#if !reviewer.rounds.length}
            <p class="text-muted-foreground">{reading === 'loading' ? 'Reading its reports…' : 'Has not reviewed yet.'}</p>
          {/if}
          <ol class="m-0 flex list-none flex-col p-0">
            {#each reviewer.rounds as round, index (index)}
              {@const result = round.verdict ? VERDICT[round.verdict] ?? [round.verdict, 'neutral'] : null}
              <li class="flex min-w-0 flex-col gap-1 border-b py-2 last:border-b-0">
                <div class="flex min-w-0 items-center gap-2">
                  <span class="w-10 shrink-0 font-medium tabular-nums">R{round.round}</span>
                  {#if result}
                    <Chip tone={result[1]}>{result[0]}</Chip>
                  {:else}
                    <Chip tone={node?.state === 'running' && node.attempt === round.round ? 'live' : 'neutral'}>
                      {node?.state === 'running' && node.attempt === round.round ? 'reviewing' : 'no report'}
                    </Chip>
                  {/if}
                  <span class="truncate text-muted-foreground tabular-nums">
                    {round.verdict ? (round.findings.length === 1 ? '1 finding' : `${round.findings.length} findings`) : ''}{round.atMs ? ` · ${clock(round.atMs)}` : ''}
                  </span>
                </div>
                {#if round.findings.length}
                  <ul class="m-0 ml-12 flex min-w-0 list-none flex-col rounded-lg bg-background/60 p-0">
                    {#each round.findings as finding, findingIndex (findingIndex)}
                      <li class="flex min-w-0 flex-col gap-0.5 border-b px-2.5 py-2 last:border-b-0">
                        {#if finding.severity || finding.location}
                          <span class="flex min-w-0 items-center gap-2">
                            {#if finding.severity}<Chip tone="attention">{finding.severity}</Chip>{/if}
                            {#if finding.location}<code class="min-w-0 font-mono text-sm text-muted-foreground [overflow-wrap:anywhere]">{finding.location}</code>{/if}
                          </span>
                        {/if}
                        <span class="leading-snug [overflow-wrap:anywhere]">{finding.text}</span>
                      </li>
                    {/each}
                  </ul>
                {/if}
              </li>
            {/each}
          </ol>
        </Card.Content>
      </Card.Root>
    {/each}

    <p class="text-muted-foreground">The engine keeps no findings ledger yet, so each finding shows in the round that raised it, as its reviewer wrote it.</p>
  {:else if kind === 'proof' && proofNode}
    <div class="flex min-w-0 items-center gap-2">
      <StepGlyphIcon glyph={stepGlyph(proofNode)} />
      <h2 class="truncate text-(length:--text-heading) font-semibold">{titleOf(nodeId)}</h2>
      <span class="truncate text-muted-foreground">{proofNode.attempt > 1 ? `attempt ${proofNode.attempt}` : ''}{proofNode.finishedAtMs ? `${proofNode.attempt > 1 ? ' · ' : ''}finished ${clock(proofNode.finishedAtMs)}` : ''}</span>
    </div>

    {#if proofNode.failure}
      <p class="flex items-start gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive [overflow-wrap:anywhere]">
        <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />{workflowErrorText(proofNode.failure.message)}
      </p>
    {/if}

    <Card.Root size="sm" class="gap-2 bg-foreground/8">
      <Card.Header><h3 class="text-[13px] font-semibold">Files {proof.files.length || ''}</h3></Card.Header>
      <Card.Content>
        {#if proof.files.length}
          <ul class="m-0 flex list-none flex-wrap items-start gap-3 p-0">
            {#each proof.files as file (file)}
              <li class="flex max-w-full min-w-0 flex-col gap-1">
                {#if pictures[file]}
                  <img src={pictures[file]} alt={fileName(file)} class="max-h-64 max-w-[28rem] rounded-md bg-background/60 object-contain object-top shadow-sm" />
                {/if}
                <span class="truncate font-mono text-sm text-muted-foreground" title={file}>{fileName(file)}</span>
              </li>
            {/each}
          </ul>
        {:else}
          <p class="text-muted-foreground">{proofNode.startedAtMs ? 'The proof step reported no files.' : 'The proof step has not run yet.'}</p>
        {/if}
      </Card.Content>
    </Card.Root>

    {#if proof.summary}
      <Card.Root size="sm" class="gap-2 bg-foreground/8">
        <Card.Header><h3 class="text-[13px] font-semibold">What the prover reported</h3></Card.Header>
        <Card.Content>
          <p class="leading-snug whitespace-pre-wrap [overflow-wrap:anywhere]">{proof.summary}</p>
        </Card.Content>
      </Card.Root>
    {/if}

    <p class="text-muted-foreground">The planted-value check, the engine's own screenshots with their action log, and the judge's verdicts show here once the engine records them.</p>
  {/if}
</div>
</div>
