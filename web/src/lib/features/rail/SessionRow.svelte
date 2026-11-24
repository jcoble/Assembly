<script lang="ts">
  /**
   * 1:1 session row matching the old SvelteKit app's rail rows + live Engine signals.
   * Uses shared `clockStore.nowMs` (zero per-row timers) and `ProviderIcon`.
   */
  import { CircleAlert, CircleCheck, Crown, HelpCircle, Pin } from '@lucide/svelte';
  import { clockStore } from '$lib/application/clock.svelte';
  import ProviderIcon from '$lib/ui/icons/ProviderIcon.svelte';
  import {
    formatElapsed,
    formatRelativeAge,
    presenceOf,
    sessionDetailsLine,
    statusLineOf
  } from './format';
  import type { RailEntry } from './types';

  interface Props {
    entry: RailEntry;
    selected: boolean;
    offline: boolean;
    onSelect: (entry: RailEntry) => void;
    onContextMenu: (entry: RailEntry, event: MouseEvent) => void;
  }

  let { entry, selected, offline, onSelect, onContextMenu }: Props = $props();

  const presence = $derived(presenceOf(entry));
  const title = $derived(entry.title?.trim() || entry.group || entry.sessionId);
  const details = $derived(sessionDetailsLine(entry, offline));
  const statusLine = $derived(statusLineOf(entry));
  const timeText = $derived(
    entry.running
      ? formatElapsed(clockStore.nowMs, entry.updatedAt)
      : formatRelativeAge(clockStore.nowMs, entry.updatedAt)
  );
  const indentPx = $derived(Math.min(entry.depth, 4) * 14);
  const isOrchestrator = $derived(entry.orchestratorAt !== null && entry.orchestratorAt !== undefined);
  const isPinned = $derived(entry.pinnedAt !== null && entry.pinnedAt !== undefined);
</script>

<div class="row-wrap" style:padding-left={`${indentPx}px`} class:child={entry.depth > 0}>
  {#if entry.depth > 0}
    <span class="tree-guide" style:left={`${Math.max(6, indentPx - 8)}px`} aria-hidden="true"></span>
  {/if}

  <button
    type="button"
    class="session-row"
    class:selected
    class:needs-you={presence === 'needs_you'}
    class:offline
    class:done={presence === 'done'}
    onclick={() => onSelect(entry)}
    oncontextmenu={(event) => {
      event.preventDefault();
      onContextMenu(entry, event);
    }}
  >
    <div class="lead">
      <ProviderIcon icon={entry.icon} remote={Boolean(entry.machine)} size={14} />
    </div>

    <div class="body">
      <div class="line-1">
        {#if presence === 'unread'}
          <span class="unread-dot" aria-label="Unread"></span>
        {:else if presence === 'working'}
          <span class="working-dot" title="Working"></span>
        {:else if presence === 'needs_you'}
          <HelpCircle size={12} class="glyph-needs" />
        {:else if presence === 'failed'}
          <CircleAlert size={12} class="glyph-failed" />
        {:else if presence === 'done'}
          <CircleCheck size={12} class="glyph-done" />
        {/if}

        {#if isOrchestrator}
          <Crown size={12} class="glyph-orchestrator" />
        {:else if isPinned}
          <Pin size={11} class="glyph-pin" />
        {/if}

        <span class="title">{title}</span>
        <span class="age" class:running={entry.running}>{timeText}</span>
      </div>

      <div class="line-2">
        <span class="details">{details}</span>
      </div>

      {#if statusLine}
        <div
          class="line-3"
          class:needs={presence === 'needs_you'}
          class:failed={presence === 'failed'}
        >
          {statusLine}
        </div>
      {/if}
    </div>
  </button>
</div>

<style>
  .row-wrap {
    position: relative;
    display: flex;
  }

  .tree-guide {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 1px;
    background: var(--color-border);
  }

  .session-row {
    position: relative;
    display: flex;
    align-items: flex-start;
    gap: 8px;
    width: 100%;
    padding: 6px 9px 6px 8px;
    border: 1px solid transparent;
    border-radius: 7px;
    background: transparent;
    color: var(--color-text);
    text-align: left;
    cursor: pointer;
    transition: background 90ms ease, border-color 90ms ease;
  }

  .session-row:hover {
    background: var(--color-hover);
  }

  .session-row.selected {
    background: var(--color-selected);
  }

  .session-row.selected::before {
    content: '';
    position: absolute;
    left: 0;
    top: 5px;
    bottom: 5px;
    width: 2.5px;
    border-radius: 999px;
    background: var(--color-accent);
  }

  .session-row.needs-you {
    border-color: rgba(245, 185, 91, 0.34);
  }

  .session-row.selected.needs-you {
    border-color: rgba(245, 185, 91, 0.58);
  }

  .session-row.done:not(.selected) {
    opacity: 0.72;
  }

  .session-row.offline .title {
    color: var(--color-text-3);
  }

  .lead {
    padding-top: 2px;
    flex-shrink: 0;
  }

  .body {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .line-1 {
    display: flex;
    align-items: center;
    gap: 5px;
    min-width: 0;
  }

  .title {
    flex: 1;
    min-width: 0;
    font-size: 12.5px;
    font-weight: 500;
    line-height: 1.3;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .age {
    flex-shrink: 0;
    font-size: 11px;
    color: var(--color-text-3);
    font-variant-numeric: tabular-nums;
  }

  .age.running {
    color: var(--color-accent);
  }

  .line-2 {
    display: flex;
    align-items: center;
    min-width: 0;
  }

  .details {
    font-size: 11px;
    color: var(--color-text-3);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .line-3 {
    font-size: 11px;
    color: var(--color-text-2);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .line-3.needs {
    color: var(--color-attention);
  }

  .line-3.failed {
    color: var(--color-bad);
  }

  .unread-dot,
  .working-dot {
    width: 6px;
    height: 6px;
    border-radius: 999px;
    flex-shrink: 0;
  }

  .unread-dot {
    background: var(--color-live);
  }

  .working-dot {
    background: var(--color-accent);
    box-shadow: 0 0 6px rgba(30, 215, 96, 0.55);
  }

  .line-1 :global(.glyph-needs) {
    color: var(--color-attention);
    flex-shrink: 0;
  }

  .line-1 :global(.glyph-failed) {
    color: var(--color-bad);
    flex-shrink: 0;
  }

  .line-1 :global(.glyph-done) {
    color: var(--color-text-3);
    flex-shrink: 0;
  }

  .line-1 :global(.glyph-orchestrator) {
    color: var(--color-accent);
    flex-shrink: 0;
  }

  .line-1 :global(.glyph-pin) {
    color: var(--color-text-2);
    flex-shrink: 0;
  }
</style>
