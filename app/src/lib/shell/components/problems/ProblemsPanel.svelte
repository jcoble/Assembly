<script lang="ts">
  /**
   * ProblemsPanel.svelte — what the language server thinks is wrong, in the
   * bottom dock.
   *
   * Presentation and user gestures only. Every read of the machine happens in
   * `problems/problemsService.ts`; this component reads `problemsState` and
   * calls the service when the user presses Refresh. It does NOT load anything
   * when it is mounted — the bottom dock is on screen from the moment the shell
   * opens, and loading here would be exactly the launch-time backend call the
   * shell is built to avoid. Until something opens the panel for real (see the
   * activation hop in the integration notes) or the user presses Refresh, it
   * says so and asks nothing.
   *
   * Clicking a problem opens its file at that line through `openFileBus`, the
   * same route the explorer and the palette use.
   */
  import type { Snippet } from 'svelte';

  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import CircleX from '@lucide/svelte/icons/circle-x';
  import Info from '@lucide/svelte/icons/info';
  import Lightbulb from '@lucide/svelte/icons/lightbulb';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Search from '@lucide/svelte/icons/search';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
  import { Badge } from '$lib/components/ui/badge/index.js';
  import * as Collapsible from '$lib/components/ui/collapsible/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import type { SourceDiagnosticSeverity } from '$lib/sourceData.ts';
  import { requestOpenFile } from '$lib/shell/openFileBus.ts';
  /* Refresh goes through the shell, not the service: only the shell knows
     which session is active, and the panel deliberately loads nothing on its
     own (the bottom dock is on screen from launch). */
  import { refreshProblemsForSelection } from '$lib/shell/shellPanels.ts';
  import {
    countProblems,
    describeProblemCounts,
    describeProblemsEmptyState,
    describeProblemsSource,
    filterProblemRows,
    groupProblemsByFile,
    problemRowKey,
    problemsEmptyKind,
    problemsState,
    setProblemsFilter,
    severityWord,
    type ProblemRow
  } from '$lib/shell/problems/problemsStore.svelte.ts';

  interface Props {
    /** Replaces the title at the start of the header, used by the dock's tabs. */
    headerStart?: Snippet;
    /** Anything the region around this panel wants on the end of its header row.
     * The bottom dock puts its "Reset layout" button here — it used to float in
     * the same corner and landed on top of Refresh. */
    headerEnd?: Snippet;
  }
  let { headerStart, headerEnd }: Props = $props();

  /** Files the user has folded shut. Everything is open until it is closed. */
  let closedFiles = $state<Record<string, boolean>>({});

  const visibleRows = $derived(filterProblemRows(problemsState.rows, problemsState.filter));
  const groups = $derived(groupProblemsByFile(visibleRows));
  const totals = $derived(countProblems(visibleRows));
  const emptyKind = $derived(
    problemsEmptyKind({
      activated: problemsState.activated,
      loading: problemsState.loading,
      root: problemsState.root,
      rows: problemsState.rows,
      filter: problemsState.filter,
      error: problemsState.error,
      unavailableReason: problemsState.unavailableReason,
      source: problemsState.source,
      filesConsidered: problemsState.filesConsidered
    })
  );
  const emptyState = $derived(emptyKind ? describeProblemsEmptyState(emptyKind) : null);
  const sourceLine = $derived(
    describeProblemsSource(problemsState.source, problemsState.filesConsidered)
  );

  const SEVERITY_ICONS = {
    error: CircleX,
    warning: TriangleAlert,
    info: Info,
    hint: Lightbulb
  } as const;

  /** The tone each severity is drawn in — the shell's status colours. */
  const SEVERITY_TONE: Record<SourceDiagnosticSeverity, string> = {
    error: 'text-[var(--color-bad)]',
    warning: 'text-[var(--color-attention)]',
    info: 'text-[var(--color-live)]',
    hint: 'text-[var(--color-text-2)]'
  };

  /** The folder part of a path, for the dimmed half of a file heading. */
  function folderPart(relativePath: string): string {
    const cut = relativePath.lastIndexOf('/');
    return cut === -1 ? '' : relativePath.slice(0, cut);
  }

  function isOpen(path: string): boolean {
    return closedFiles[path] !== true;
  }

  /** bits-ui reports the state it wants; we keep it, so the fold is ours. */
  function setFileOpen(path: string, open: boolean): void {
    closedFiles = { ...closedFiles, [path]: !open };
  }

  function openProblem(row: ProblemRow): void {
    requestOpenFile({ path: row.path, line: row.line, column: row.column });
  }
</script>

<!-- One severity's count, drawn only when there is one to draw. -->
{#snippet severityCount(severity: SourceDiagnosticSeverity, value: number)}
  {#if value > 0}
    {@const Icon = SEVERITY_ICONS[severity]}
    <Badge
      variant="secondary"
      title="{value} {severityWord(severity, value)}"
      class="h-5 gap-1 px-1.5 text-[12px] font-normal {SEVERITY_TONE[severity]}"
    >
      <Icon aria-hidden="true" />
      {value}
    </Badge>
  {/if}
{/snippet}

<div
  class="flex h-full min-h-0 flex-col text-[var(--color-text)]"
  data-testid="problems-panel"
>
  <div
    class="flex shrink-0 flex-wrap items-center gap-2 border-b border-[var(--color-border)]
           px-2 py-1.5"
  >
    {#if headerStart}{@render headerStart()}{:else}<h2 class="text-[13px] font-semibold">Problems</h2>{/if}
    <div class="flex items-center gap-1">
      {@render severityCount('error', totals.error)}
      {@render severityCount('warning', totals.warning)}
      {@render severityCount('info', totals.info)}
      {@render severityCount('hint', totals.hint)}
    </div>
    <span class="text-[12px] text-[var(--color-text-2)]">
      {describeProblemCounts(totals)}
    </span>

    <div class="relative ml-auto w-[220px] max-w-[45%]">
      <Search
        class="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2
               text-[var(--color-text-3)]"
        aria-hidden="true"
      />
      <Input
        type="search"
        placeholder="Filter problems"
        aria-label="Filter problems"
        value={problemsState.filter}
        oninput={(event) => setProblemsFilter(event.currentTarget.value)}
        class="h-7 rounded-md bg-[var(--color-elevated)] pl-7 text-[13px] md:text-[13px]"
      />
    </div>

    <button
      type="button"
      class="flex h-7 shrink-0 items-center gap-1.5 rounded-md bg-[var(--color-elevated)]
             px-2 text-[12px] text-[var(--color-text-2)] transition-colors
             hover:bg-[var(--color-hover)] hover:text-[var(--color-text)]
             focus-visible:ring-3 focus-visible:ring-ring/50 outline-none
             disabled:opacity-60"
      disabled={problemsState.loading}
      title="Ask the language server again"
      onclick={() => refreshProblemsForSelection()}
    >
      <RefreshCw class="size-3.5" aria-hidden="true" />
      {problemsState.loading ? 'Looking…' : 'Refresh'}
    </button>

    {#if headerEnd}{@render headerEnd()}{/if}
  </div>

  {#if sourceLine && problemsState.rows.length > 0}
    <p class="shrink-0 px-2 py-1 text-[12px] text-[var(--color-text-2)]">{sourceLine}</p>
  {/if}

  {#if problemsState.error}
    <p class="shrink-0 px-2 py-1 text-[13px] text-[var(--color-bad)]">{problemsState.error}</p>
  {/if}

  <div class="min-h-0 flex-1 overflow-y-auto px-1 pb-2">
    {#if emptyState}
      <div class="flex flex-col gap-1 px-2 py-4">
        <p class="text-[13px] text-[var(--color-text)]">{emptyState.headline}</p>
        <p class="text-[12px] text-[var(--color-text-2)]">{emptyState.hint}</p>
        {#if sourceLine && problemsState.rows.length === 0 && problemsState.source}
          <p class="text-[12px] text-[var(--color-text-3)]">{sourceLine}</p>
        {/if}
      </div>
    {:else}
      {#each groups as group (group.path)}
        <Collapsible.Root
          open={isOpen(group.path)}
          onOpenChange={(open) => setFileOpen(group.path, open)}
          class="border-b border-[var(--color-border)]"
        >
          <Collapsible.Trigger
            class="flex w-full min-w-0 items-center gap-1.5 rounded-md px-1 py-1 text-left
                   text-[var(--color-text-2)] transition-colors hover:bg-[var(--color-elevated)]
                   hover:text-[var(--color-text)] focus-visible:ring-3 focus-visible:ring-ring/50
                   outline-none"
            title={group.path}
          >
            {#if isOpen(group.path)}
              <ChevronDown class="size-3.5 shrink-0" aria-hidden="true" />
            {:else}
              <ChevronRight class="size-3.5 shrink-0" aria-hidden="true" />
            {/if}
            <span class="shrink-0 text-[13px] text-[var(--color-text)]">{group.fileName}</span>
            {#if folderPart(group.relativePath)}
              <span class="truncate text-[12px] text-[var(--color-text-3)]">
                {folderPart(group.relativePath)}
              </span>
            {/if}
            <span class="ml-auto flex shrink-0 items-center gap-1 pl-2">
              {@render severityCount('error', group.counts.error)}
              {@render severityCount('warning', group.counts.warning)}
              {@render severityCount('info', group.counts.info)}
              {@render severityCount('hint', group.counts.hint)}
            </span>
          </Collapsible.Trigger>

          <!-- NOT `flex` on the content itself: a closed collapsible is hidden by
               the `hidden` attribute, which is a display rule the browser only
               applies by default — any display class of ours would beat it and
               the file would never fold shut. The layout lives on the div inside. -->
          <Collapsible.Content>
            <div class="flex flex-col pb-1">
              {#each group.rows as row (problemRowKey(row))}
                {@const Icon = SEVERITY_ICONS[row.severity]}
                <button
                  type="button"
                  class="problem-row flex w-full min-w-0 items-center gap-1.5 rounded-md py-0.5 pr-2
                         pl-6 text-left transition-colors hover:bg-[var(--color-elevated)]
                         focus-visible:ring-3 focus-visible:ring-ring/50 outline-none"
                  title="{severityWord(row.severity, 1)} · {row.message}"
                  onclick={() => openProblem(row)}
                >
                  <Icon
                    class="size-3.5 shrink-0 {SEVERITY_TONE[row.severity]}"
                    aria-hidden="true"
                  />
                  <span class="truncate text-[13px] text-[var(--color-text)]">{row.message}</span>
                  {#if row.source}
                    <span class="shrink-0 text-[12px] text-[var(--color-text-3)]">
                      {row.source}
                    </span>
                  {/if}
                  <span class="ml-auto shrink-0 pl-2 text-[12px] text-[var(--color-text-2)]">
                    {row.line}:{row.column}
                  </span>
                </button>
              {/each}
            </div>
          </Collapsible.Content>
        </Collapsible.Root>
      {/each}
    {/if}
  </div>
</div>

<style>
  /* A project with thousands of diagnostics puts thousands of rows in here.
     Skipping layout and paint for what is off screen keeps the panel cheap to
     scroll. `auto` means the browser uses the real height it last measured, so
     a row that has been on screen once reserves the space it really takes.

     24px is one problem row: 13px text at the app's 1.5 line-height (19.5px)
     inside py-0.5 (4px). Only rows carry this: a file group starts open and
     holds however many rows it has, so a single guessed height for a whole
     group would shift the scroll range as the reader moves through the list. */
  .problem-row {
    content-visibility: auto;
    contain-intrinsic-size: auto 24px;
  }
</style>
