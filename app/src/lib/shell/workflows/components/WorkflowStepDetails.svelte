<!--
  WorkflowStepDetails.svelte — everything about one workflow step, shown when
  its row or its pipeline segment is clicked (the parent draws the Back bar).

  A status card says what state the step is in and why, its attempt, timing
  and usage, the pull request it opened, and offers Retry when it failed. A
  review card holds the verdict and findings of the latest round, a rounds
  card lists every round's result from the run's history, and a commands card
  lists every shell command with its exit code and output (a command step's
  failed commands are its findings, so they show there only).
  Every block starts at the card's left edge; output wraps and scrolls inside
  its own block, never the panel.
-->
<script module lang="ts">
  import type { ChipTone } from '$lib/components/ui/chip/index.js';

  /** How a step reads in the run view: its chip (if any), the chip's tone, and one line saying why. */
  export interface StepStatus {
    chip: string | null;
    tone: ChipTone;
    summary: string;
    /** It never ran (an earlier step failed or the run stopped): read muted. */
    notRun: boolean;
  }
</script>

<script lang="ts">
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { sessionContextUsage } from '$lib/shell/panels/context/sessionContextModel.ts';
  import { openUrlInBrowser } from '$lib/shell/workbenchNavigation.ts';
  import type { WorkflowTimelineItem } from '$lib/shell/workflows/workflowTimeline.ts';
  import { workflowStepFacts, type WorkflowNodeRunRecord } from '$lib/shell/workflows/workflowTypes.ts';

  interface Props {
    node: WorkflowNodeRunRecord;
    status: StepStatus;
    /** Provider and role, or the step kind for a command step. */
    kind: string;
    /** How long it ran, already formatted; empty when it never started. */
    duration: string;
    busy: boolean;
    /** Retry this step; null when it has not failed. */
    onRetry: (() => void) | null;
    /** This step's results from the run's timeline, one per round or attempt, oldest first. */
    rounds: WorkflowTimelineItem[];
  }

  let { node, status, kind, duration, busy, onRetry, rounds }: Props = $props();

  // A round's result in one or two words, with the tone its chip reads in.
  const ROUND_RESULT: Partial<Record<WorkflowTimelineItem['kind'], [string, ChipTone]>> = {
    finish: ['finished', 'good'], failed: ['failed', 'bad'], timeout: ['timed out', 'attention'], waiting: ['waited for you', 'neutral']
  };
  const roundWord = $derived(rounds.some((round) => round.kind === 'review') ? 'Round' : 'Attempt');

  const facts = $derived(workflowStepFacts(node));
  const failedCommands = $derived(
    facts.commands.filter((command) => command.exitCode !== null && command.exitCode !== 0).length
  );

  const verdictTone = (verdict: string): ChipTone =>
    verdict === 'pass' ? 'good' : verdict === 'fail' ? 'bad' : 'neutral';

  const clock = (ms: number | null): string =>
    ms === null ? '' : new Date(ms).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });

  /** A finding that opens with a path ("src/a.ts:12: …") shows the path on its own line. */
  function findingParts(finding: string): { path: string | null; text: string } {
    const match = /^(\S*[/.]\S*?)(: | \()/.exec(finding);
    if (!match) return { path: null, text: finding };
    return { path: match[1], text: finding.slice(match[1].length + (match[2] === ': ' ? 2 : 1)) };
  }

  function usageSummary(step: WorkflowNodeRunRecord): string | null {
    if (!step.usage) return null;
    const usage = sessionContextUsage(null, step.usage);
    const parts: string[] = [];
    if (usage.usedTokens !== null) {
      const used = usage.usedTokens.toLocaleString();
      parts.push(usage.contextWindow === null
        ? `Context ${used} tokens`
        : `Context ${used} / ${usage.contextWindow.toLocaleString()}`);
    }
    if (usage.totalTokens !== null) parts.push(`Total ${usage.totalTokens.toLocaleString()} tokens`);
    if (usage.inputTokens !== null && usage.outputTokens !== null) {
      parts.push(`${usage.inputTokens.toLocaleString()} in, ${usage.outputTokens.toLocaleString()} out`);
    }
    return parts.length ? parts.join(' · ') : null;
  }

  const rows = $derived(
    [
      { label: 'Kind', value: kind },
      { label: 'Attempt', value: node.startedAtMs ? String(node.attempt) : '' },
      { label: 'Started', value: clock(node.startedAtMs) },
      { label: 'Finished', value: clock(node.finishedAtMs) },
      { label: 'Duration', value: duration },
      { label: 'Usage', value: node.startedAtMs ? usageSummary(node) ?? 'Not reported' : '' }
    ].filter((row) => row.value)
  );
</script>

<div class="flex min-w-0 flex-col gap-2">
  <!-- Status: what state it is in and why. -->
  <Card.Root size="sm" class="gap-3 bg-foreground/8">
    <Card.Content class="flex flex-col gap-3">
      {#if node.failure}
        <div class="flex min-w-0 items-start gap-2 rounded-lg bg-destructive/10 px-2.5 py-2 text-[13px] leading-snug text-destructive">
          <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <p class="min-w-0 flex-1 [overflow-wrap:anywhere]">{node.failure.message}</p>
        </div>
      {:else}
        <p class="text-[13px] leading-snug" class:text-muted-foreground={status.notRun}>{status.summary}</p>
      {/if}

      {#if rows.length}
        <dl class="flex flex-col">
          {#each rows as row (row.label)}
            <div class="flex min-h-7 items-center gap-3 border-b py-1.5 last:border-b-0">
              <dt class="w-20 shrink-0 text-sm text-muted-foreground">{row.label}</dt>
              <dd class="min-w-0 flex-1 text-right text-[13px] leading-tight tabular-nums [overflow-wrap:anywhere]">{row.value}</dd>
            </div>
          {/each}
        </dl>
      {/if}

      {#if facts.prUrl}
        {@const prUrl = facts.prUrl}
        <button
          type="button"
          class="-mx-1 flex min-h-7 min-w-0 items-center gap-1.5 rounded-md px-1 text-left text-sm text-primary outline-none hover:bg-accent/60 focus-visible:ring-3 focus-visible:ring-ring/50"
          title={prUrl}
          onclick={() => void openUrlInBrowser({ url: prUrl })}
        >
          <ExternalLink class="size-3.5 shrink-0" aria-hidden="true" />
          <span class="min-w-0 flex-1 truncate">{prUrl}</span>
        </button>
      {/if}

      {#if onRetry}
        <div class="flex">
          <Button size="sm" variant="outline" disabled={busy} onclick={onRetry}><RotateCcw />Retry this step</Button>
        </div>
      {/if}
    </Card.Content>
  </Card.Root>

  <!-- Review verdict and findings (a command step reports findings without a verdict). The run record keeps only the latest round. -->
  {#if facts.verdict || (facts.findings.length && !facts.commands.length)}
    <Card.Root size="sm" class="gap-2 bg-foreground/8">
      <Card.Header>
        <div class="flex min-w-0 items-center gap-2">
          <h3 class="min-w-0 flex-1 truncate text-[13px] leading-tight font-semibold">
            {facts.verdict ? `Review${node.attempt > 1 ? ` · round ${node.attempt}` : ''}` : 'Findings'}
          </h3>
          {#if facts.verdict}<Chip tone={verdictTone(facts.verdict)}>{facts.verdict}</Chip>{/if}
        </div>
      </Card.Header>
      <Card.Content>
        {#if facts.findings.length}
          <ul class="m-0 flex min-w-0 list-none flex-col rounded-lg bg-background/60 p-0">
            {#each facts.findings as finding, findingIndex (findingIndex)}
              {@const parts = findingParts(finding)}
              <li class="flex min-w-0 flex-col gap-0.5 border-b px-2.5 py-2 last:border-b-0">
                {#if parts.path}<code class="font-mono text-sm text-muted-foreground [overflow-wrap:anywhere]">{parts.path}</code>{/if}
                <span class="text-[13px] leading-snug [overflow-wrap:anywhere]">{parts.text}</span>
              </li>
            {/each}
          </ul>
        {:else}
          <p class="text-sm text-muted-foreground">No findings.</p>
        {/if}
      </Card.Content>
    </Card.Root>
  {/if}

  <!-- Every round or attempt this step has had, from the run's history; the record above keeps only the latest. -->
  {#if rounds.length > 1}
    <Card.Root size="sm" class="gap-1.5 bg-foreground/8">
      <Card.Header>
        <h3 class="min-w-0 truncate text-[13px] leading-tight font-semibold">{roundWord}s</h3>
      </Card.Header>
      <Card.Content>
        <ol class="m-0 flex min-w-0 list-none flex-col p-0">
          {#each rounds as round (round.key)}
            {@const result = round.verdict ? [round.verdict, verdictTone(round.verdict)] as const : ROUND_RESULT[round.kind]}
            <li class="flex min-h-8 min-w-0 items-center gap-3 border-b py-1.5 last:border-b-0">
              <span class="w-20 shrink-0 text-[13px] font-medium tabular-nums">{roundWord} {round.attempt ?? 1}</span>
              <span class="min-w-0 flex-1 truncate text-sm text-muted-foreground tabular-nums" class:font-mono={round.kind === 'checks'} title={round.detail}>{round.detail || clock(round.atMs)}</span>
              {#if result}<Chip tone={result[1]}>{result[0]}</Chip>{/if}
            </li>
          {/each}
        </ol>
      </Card.Content>
    </Card.Root>
  {/if}

  <!-- Commands: each one with its exit code and the last lines it printed. -->
  {#if facts.commands.length}
    <Card.Root size="sm" class="gap-2 bg-foreground/8">
      <Card.Header>
        <div class="flex min-w-0 items-center gap-2">
          <h3 class="min-w-0 flex-1 truncate text-[13px] leading-tight font-semibold">Commands</h3>
          {#if failedCommands}
            <Chip tone="bad">{failedCommands} of {facts.commands.length} failed</Chip>
          {:else if facts.commands.every((command) => command.exitCode === 0)}
            <Chip tone="good">all {facts.commands.length} passed</Chip>
          {:else}
            <Chip tone="count">{facts.commands.length}</Chip>
          {/if}
        </div>
      </Card.Header>
      <Card.Content>
        <ol class="m-0 flex min-w-0 list-none flex-col gap-1.5 p-0">
          {#each facts.commands as command, commandIndex (commandIndex)}
            <li class="min-w-0 overflow-hidden rounded-lg bg-background/60">
              <div class="flex min-h-8 min-w-0 items-center gap-2 py-1 pr-1.5 pl-2.5">
                <code class="min-w-0 flex-1 truncate font-mono text-sm text-foreground" title={command.command}>{command.command}</code>
                {#if command.exitCode !== null}
                  <Chip tone={command.exitCode === 0 ? 'good' : 'bad'}>exit {command.exitCode}</Chip>
                {/if}
              </div>
              {#if command.lastLines}
                <pre class="m-0 max-h-60 min-w-0 overflow-y-auto border-t px-2.5 py-2 font-mono text-sm leading-normal whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">{command.lastLines}</pre>
              {/if}
            </li>
          {/each}
        </ol>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
