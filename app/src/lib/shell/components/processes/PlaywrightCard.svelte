<script lang="ts">
  /**
   * PlaywrightCard.svelte — the "Playwright processes" card at the bottom of the
   * Resources panel.
   *
   * THE PROBLEM IT SOLVES
   * When a Playwright run or an agent's browser session is interrupted, its
   * browsers and helpers keep running. In Activity Monitor they are called
   * "Google Chrome" and carry the Chrome icon, so they are indistinguishable
   * from the browser the user actually browses in — and force-quitting the
   * wrong one closes their tabs. This card lists ONLY processes the desktop app
   * positively identified as Playwright's (a Playwright profile folder or a
   * Playwright script path), grouped the way the operating system groups them,
   * and stops them by group. It can never signal the user's own Chrome, because
   * the desktop side only signals process ids it listed in the same call.
   *
   * SHAPE
   * NO props, NO IO at mount, NO `$effect`. It renders an inert line until the
   * shell calls `activate()` on `playwrightService`, which the Resources panel
   * does when it opens; after that the only things that reach the backend are
   * the refresh button and the two stop buttons, each behind a confirmation
   * dialog.
   */
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import Drama from '@lucide/svelte/icons/drama';

  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { refresh, stopAll, stopSession } from '$lib/shell/processes/playwrightService';
  import {
    playwrightState,
    summarizePlaywrightGroups,
    type PlaywrightGroup
  } from '$lib/shell/processes/playwrightStore.svelte';

  /** Is the card's section open? Starts open, like its neighbours. */
  let expanded = $state(true);

  /** Which groups have their process list unfolded, by process group id. */
  let openPids = $state<Record<number, boolean>>({});

  /** The group the "stop this session" question is being asked about. */
  let confirmingGroup = $state<PlaywrightGroup | null>(null);
  let confirmSessionOpen = $state(false);
  let confirmAllOpen = $state(false);

  const summary = $derived(summarizePlaywrightGroups(playwrightState.groups));
  const canStopOne = $derived(playwrightState.perSessionStopSupported !== 'no');

  function togglePids(pgid: number): void {
    openPids[pgid] = !openPids[pgid];
  }

  function askStopSession(group: PlaywrightGroup): void {
    confirmingGroup = group;
    confirmSessionOpen = true;
  }

  function stopConfirmedSession(): void {
    const group = confirmingGroup;
    confirmSessionOpen = false;
    confirmingGroup = null;
    if (group) void stopSession(group.pgid);
  }

  function stopConfirmedAll(): void {
    confirmAllOpen = false;
    void stopAll();
  }

  /** "3 processes" — used in both dialogs so the counts read the same way. */
  function countProcesses(value: number): string {
    return `${value} ${value === 1 ? 'process' : 'processes'}`;
  }

  function countSessions(value: number): string {
    return `${value} ${value === 1 ? 'session' : 'sessions'}`;
  }

  /** Total processes across every group, for the stop-everything question. */
  const totalProcesses = $derived(
    playwrightState.groups.reduce((total, group) => total + group.processCount, 0)
  );
</script>

<section class="card" aria-label="Playwright processes">
  <button
    type="button"
    class="card-head"
    aria-expanded={expanded}
    onclick={() => (expanded = !expanded)}
  >
    <span class="chevron" class:open={expanded} aria-hidden="true">
      <ChevronRight size={13} />
    </span>
    <span class="card-icon" aria-hidden="true"><Drama size={13} /></span>
    <span class="card-title">Playwright processes</span>
    <span class="card-count">{playwrightState.groups.length}</span>
  </button>

  {#if expanded}
    <div class="card-body">
      <p class="explainer">These are Playwright's browsers and helpers, not your Chrome.</p>

      {#if playwrightState.error}
        <p class="state error">
          {playwrightState.error}
          <button type="button" class="retry" onclick={() => void refresh()}>try again</button>
        </p>
      {:else if playwrightState.unavailableReason}
        <p class="state">{playwrightState.unavailableReason}</p>
      {:else if playwrightState.groups.length > 0}
        {#if summary}<p class="summary">{summary}</p>{/if}

        <ul class="rows">
          {#each playwrightState.groups as group (group.pgid)}
            <li class="row">
              <div class="row-main">
                <span class="row-title" title={group.explanation}>{group.kindLabel}</span>
                <span class="chip">{group.ageLabel}</span>
              </div>
              <div class="row-meta">
                <span class="mono meta-id">group {group.pgid}</span>
                <button
                  type="button"
                  class="link"
                  aria-expanded={!!openPids[group.pgid]}
                  onclick={() => togglePids(group.pgid)}
                >
                  {openPids[group.pgid] ? 'hide' : 'show'}
                  {countProcesses(group.processCount)}
                </button>
                {#if canStopOne}
                  <button
                    type="button"
                    class="stop"
                    disabled={playwrightState.busy}
                    onclick={() => askStopSession(group)}
                  >
                    {playwrightState.stoppingPgid === group.pgid ? 'stopping…' : 'Stop this session'}
                  </button>
                {/if}
              </div>

              {#if openPids[group.pgid]}
                <ul class="pids">
                  {#each group.processes as process (process.pid)}
                    <li class="pid-row" title={process.commandLine}>
                      <span class="mono pid">{process.pid}</span>
                      <span class="pid-name">{process.name}</span>
                      <span class="pid-age">{process.ageLabel}</span>
                    </li>
                  {:else}
                    {#each group.pids as pid (pid)}
                      <li class="pid-row">
                        <span class="mono pid">{pid}</span>
                        <span class="pid-name">no detail was returned for this one</span>
                      </li>
                    {/each}
                  {/each}
                </ul>
              {/if}
            </li>
          {/each}
        </ul>

        <div class="actions">
          <button
            type="button"
            class="stop danger"
            disabled={playwrightState.busy}
            onclick={() => (confirmAllOpen = true)}
          >
            {playwrightState.stoppingAll ? 'stopping…' : 'Stop all Playwright'}
          </button>
          <button
            type="button"
            class="link"
            disabled={playwrightState.loading || playwrightState.busy}
            onclick={() => void refresh()}
          >
            {playwrightState.loading ? 'reading…' : 'refresh'}
          </button>
        </div>

        {#if !canStopOne}
          <p class="note">
            This desktop build can only stop every Playwright session at once. Stop all still works.
          </p>
        {/if}
      {:else if playwrightState.loading}
        <p class="state"><WorkingSpinner size={12} /> Looking for Playwright processes…</p>
      {:else if playwrightState.activated}
        <p class="state">No Playwright processes are running.</p>
      {:else}
        <p class="state">Nothing read yet — press refresh to look for leftover processes.</p>
      {/if}

      {#if playwrightState.lastResult}
        <p class="result">{playwrightState.lastResult}</p>
      {/if}
    </div>
  {/if}
</section>

<!-- Stop ONE session. Named in full so the question can be answered without
     counting rows, and worded so nobody has to wonder about their own browser. -->
<AlertDialog.Root bind:open={confirmSessionOpen}>
  <AlertDialog.Content
    class="rounded-lg bg-background text-foreground ring-[var(--color-border)]
           shadow-[var(--shadow-lg)]"
  >
    <AlertDialog.Header>
      <AlertDialog.Title class="text-[14px] leading-[1.4] font-semibold">
        Stop this Playwright session?
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-[var(--color-text-2)]">
        {#if confirmingGroup}
          {confirmingGroup.kindLabel} — {countProcesses(confirmingGroup.processCount)} in process
          group {confirmingGroup.pgid}, running for {confirmingGroup.ageLabel}. Only these
          Playwright processes are stopped; your own Chrome is not touched.
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">Leave it running</AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant="destructive"
        class="text-[13px]"
        onclick={stopConfirmedSession}
      >
        Stop it
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<!-- Stop EVERYTHING, saying how many groups that is before it happens. -->
<AlertDialog.Root bind:open={confirmAllOpen}>
  <AlertDialog.Content
    class="rounded-lg bg-background text-foreground ring-[var(--color-border)]
           shadow-[var(--shadow-lg)]"
  >
    <AlertDialog.Header>
      <AlertDialog.Title class="text-[14px] leading-[1.4] font-semibold">
        Stop all Playwright processes?
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-[var(--color-text-2)]">
        This stops {countSessions(playwrightState.groups.length)} ({countProcesses(totalProcesses)}).
        Any test run or agent using them will lose its browser. Your own Chrome is not touched.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">Leave them running</AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant="destructive"
        class="text-[13px]"
        onclick={stopConfirmedAll}
      >
        Stop all
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<style>
  /* The card chrome mirrors ContextPanel.svelte so this section is
     indistinguishable from its neighbours, but reads the /next colour tokens
     instead of repeating hex values, so a theme change reaches it. */
  .card {
    display: flex;
    flex-direction: column;
    border-top: 1px solid var(--color-border);
  }

  .card-head {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    padding: 7px 6px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--color-text-2);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }

  .card-head:hover {
    background: var(--color-surface);
    color: var(--color-text);
  }

  .chevron,
  .card-icon {
    display: flex;
    flex: 0 0 auto;
    align-items: center;
    color: var(--color-text-2);
  }

  .chevron {
    transition: transform 130ms ease;
  }

  .chevron.open {
    transform: rotate(90deg);
  }

  .card-title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 12px;
    font-weight: 600;
    letter-spacing: 0.06em;
    text-transform: uppercase;
  }

  .card-count {
    flex: 0 0 auto;
    border-radius: 999px;
    background: var(--color-elevated);
    color: var(--color-text-2);
    font-size: 12px;
    font-variant-numeric: tabular-nums;
    line-height: 1;
    padding: 3px 6px;
  }

  .card-body {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 0 4px 8px 22px;
    font-family: ui-sans-serif, -apple-system, system-ui, sans-serif;
  }

  .explainer,
  .summary,
  .note,
  .result {
    margin: 0;
    padding: 0 6px;
    font-size: 12px;
    line-height: 1.45;
  }

  .explainer {
    color: var(--color-text-2);
  }

  .summary {
    color: var(--color-text-2);
  }

  .note {
    color: var(--color-attention);
  }

  .result {
    padding-top: 4px;
    color: var(--color-text-2);
  }

  /* ── Rows ──────────────────────────────────────────────────────────── */
  .rows {
    display: flex;
    flex-direction: column;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  .row {
    display: flex;
    flex-direction: column;
    gap: 3px;
    padding: 6px;
    border-radius: 6px;
  }

  .row + .row {
    border-top: 1px solid var(--color-border);
  }

  .row-main {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }

  .row-title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--color-text);
    font-size: 13px;
    line-height: 1.35;
  }

  .row-meta {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 2px 10px;
    min-width: 0;
    color: var(--color-text-2);
    font-size: 12px;
    line-height: 1.3;
    font-variant-numeric: tabular-nums;
  }

  .meta-id {
    flex-shrink: 0;
  }

  .mono {
    font-family: var(--font-mono);
  }

  .chip {
    flex: 0 0 auto;
    border-radius: 4px;
    background: var(--color-elevated);
    color: var(--color-text-2);
    font-size: 12px;
    letter-spacing: 0.02em;
    padding: 1px 6px;
    white-space: nowrap;
  }

  /* ── The pid list behind the expander ──────────────────────────────── */
  .pids {
    display: flex;
    flex-direction: column;
    gap: 2px;
    margin: 2px 0 0;
    padding: 4px 0 2px 2px;
    border-left: 1px solid var(--color-border);
    list-style: none;
  }

  .pid-row {
    display: flex;
    align-items: baseline;
    gap: 8px;
    min-width: 0;
    padding-left: 8px;
    color: var(--color-text-2);
    font-size: 12px;
    line-height: 1.4;
  }

  .pid {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
  }

  .pid-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--color-text);
  }

  .pid-age {
    flex: 0 0 auto;
  }

  /* ── Buttons ───────────────────────────────────────────────────────── */
  .actions {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 6px 6px 0;
  }

  .stop {
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--color-text-2);
    font: inherit;
    font-size: 12px;
    padding: 2px 8px;
    cursor: pointer;
  }

  .stop:hover:not(:disabled) {
    background: var(--color-bad-bg);
    color: var(--color-bad);
  }

  .stop.danger {
    border: 0;
    background: var(--color-bad-bg);
    color: var(--color-bad);
  }

  .stop.danger:hover:not(:disabled) {
    background: var(--color-bad-bg);
  }

  .link {
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--color-text-2);
    font: inherit;
    font-size: 12px;
    padding: 0 2px;
    cursor: pointer;
    text-decoration: underline;
  }

  .link:hover:not(:disabled) {
    color: var(--color-text);
  }

  .stop:disabled,
  .link:disabled {
    opacity: 0.5;
    cursor: default;
  }

  /* ── States ────────────────────────────────────────────────────────── */
  .state {
    margin: 0;
    padding: 8px 6px;
    color: var(--color-text-2);
    font-size: 12px;
    line-height: 1.45;
  }

  .state.error {
    color: var(--color-bad);
  }

  .retry {
    margin-left: 6px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--color-accent);
    font: inherit;
    font-size: 12px;
    padding: 0 2px;
    cursor: pointer;
    text-decoration: underline;
  }

  button:focus-visible {
    outline: none;
    box-shadow: var(--focus-ring);
  }

  @media (prefers-reduced-motion: reduce) {
    .chevron {
      transition: none;
    }
  }
</style>
