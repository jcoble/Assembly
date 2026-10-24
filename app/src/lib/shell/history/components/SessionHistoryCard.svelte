<script lang="ts">
  /**
   * SessionHistoryCard.svelte — one past session in the History panel.
   *
   * A single 32 px line: the agent's mark, the title and how long ago the
   * session was active. Clicking the line opens the session. The chevron that
   * takes the time's place on hover or keyboard focus opens the details right
   * under the line: the latest message, the facts the session has, and what can
   * be done with it. A fact the session does not have is left out, not shown
   * as a dash.
   *
   * The card decides nothing. Which actions a session supports comes from
   * `sessionHistoryActions`; running them belongs to the panel, which owns the
   * service and the confirm dialog.
   */
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import Copy from '@lucide/svelte/icons/copy';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import FileCode2 from '@lucide/svelte/icons/file-code-2';
  import FileText from '@lucide/svelte/icons/file-text';
  import FolderOpen from '@lucide/svelte/icons/folder-open';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import Trash2 from '@lucide/svelte/icons/trash-2';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import * as Collapsible from '$lib/components/ui/collapsible/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { HoverActionButton } from '$lib/components/ui/hover-actions/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { AGENT_ICONS, agentDisplayName } from '$lib/shell/agentIcons.ts';
  import { normalizeProvider } from '$lib/shell/ownedSessions.ts';
  import { formatMonthDayTime } from '$lib/shell/dateFormat.ts';
  import { exactLocalTime, formatAge } from '$lib/shell/relativeTime.ts';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import type {
    SessionLibraryRecord,
    SessionLibraryTurn
  } from '$lib/shell/history/library/sessionLibraryModel.ts';
  import { cn } from '$lib/utils.js';

  import { sessionHistoryActions, type SessionHistoryActionId } from './sessionHistoryActions.ts';

  interface Props {
    record: SessionLibraryRecord;
    /** The title shown, worked out by the history view model. */
    title: string;
    /** The name of the machine the session ran on. */
    machineLabel: string;
    /** The session's machine has not answered History's scan yet. */
    machineScanning?: boolean;
    /** Its resume was refused: its own CLI or app is running it. */
    runningElsewhere?: boolean;
    expanded: boolean;
    detail?: Pick<SessionLibraryRecord, 'firstPrompt' | 'latestTurns' | 'messageCount' | 'startComplete'> | null;
    detailsLoading?: boolean;
    /** The clock, passed down so forty rows do not keep forty of them. */
    now: Date;
    onToggle(): void;
    onAction(id: SessionHistoryActionId): void;
    /** Put a block of the transcript on the clipboard. */
    onCopyText(value: string): void;
    /** Open one of a session's agents' own transcripts. */
    onOpenLog(path: string): void;
  }

  let {
    record,
    title,
    machineLabel,
    machineScanning = false,
    runningElsewhere = false,
    expanded,
    detail = null,
    detailsLoading = false,
    now,
    onToggle,
    onAction,
    onCopyText,
    onOpenLog
  }: Props = $props();

  /** The turns are behind a disclosure and start closed. */
  let turnsOpen = $state(false);

  /** The two lines of small-caps section label, so they cannot drift apart. */
  const SECTION_LABEL =
    'm-0 text-xs leading-normal font-semibold tracking-wide uppercase text-muted-foreground';

  /** A quiet sentence: every "nothing here" line. */
  const QUIET_LINE = 'm-0 text-(length:--text-quiet) leading-normal text-muted-foreground';

  /** The less common actions, in the "…" menu beside the icon buttons. */
  const MENU_ACTION_IDS: readonly SessionHistoryActionId[] = [
    'open-log',
    'reveal-log',
    'copy-session-id',
    'copy-log-path'
  ];

  /** Text as a person reads it: tags, emphasis marks and headings taken out, runs of space closed up. */
  function readable(value: string | null | undefined): string {
    return (value ?? '')
      .replace(/<\/?[a-z][\w:-]*(?:\s[^>]*)?\/?>/gi, ' ')
      .replace(/\*\*|__|`|^#+\s/gm, '')
      .replace(/\s+/g, ' ')
      .trim();
  }

  /** The last few folders of a path: enough to place it, short enough to read. */
  function shortPath(value: string): string {
    const parts = value.split('/').filter(Boolean);
    return parts.length > 3 ? parts.slice(-3).join('/') : value;
  }

  const actions = $derived(new Map(sessionHistoryActions(record, machineScanning, runningElsewhere).map((item) => [item.id, item])));
  const resume = $derived(actions.get('resume-assembly'));
  const can = (id: SessionHistoryActionId): boolean => actions.get(id)?.enabled ?? false;

  const agent = $derived(normalizeProvider(record.provider));
  const ProviderIcon = $derived(AGENT_ICONS[agent.agent]);
  const providerName = $derived(agentDisplayName(agent.agent, agent.viaCmux));
  const markColour = $derived(
    agent.agent === 'claude'
      ? 'text-[var(--agent-mark-claude)]'
      : agent.agent === 'codex'
        ? 'text-[var(--agent-mark-codex)]'
        : 'text-muted-foreground'
  );
  const shownTitle = $derived(readable(title) || 'Untitled session');
  const age = $derived(formatAge(record.updatedAt, now));
  const latest = $derived(readable((detail?.latestTurns ?? record.latestTurns).at(-1)?.text));
  const firstPrompt = $derived(readable(detail?.firstPrompt ?? record.firstPrompt));
  const menuActions = $derived(MENU_ACTION_IDS.flatMap((id) => actions.get(id) ?? []));

  /**
   * Everything the scan kept of the conversation, in order: the first prompt,
   * then the window of latest turns behind it. The first prompt is dropped
   * when the window already opens with it, so it is never shown twice.
   */
  const turns = $derived.by(() => {
    const first = detail?.firstPrompt?.trim() ?? '';
    const list: SessionLibraryTurn[] = first ? [{ speaker: 'user', text: first }] : [];
    for (const turn of detail?.latestTurns ?? []) {
      const text = turn.text.trim();
      if (!text || text === first) continue;
      list.push({ speaker: turn.speaker, text });
    }
    return list;
  });

  /** The first turn and the last two. Fewer than four means all of them. */
  const shownTurns = $derived(turns.length < 4 ? turns : [turns[0], ...turns.slice(-2)]);
  /** How many sit between the first turn and the final pair. */
  const elidedTurns = $derived(turns.length < 4 ? 0 : turns.length - 3);

  /** The facts this session has, in a fixed order. Unknown ones are left out. */
  const facts = $derived.by(() => {
    const folder = record.canonicalCwd.trim();
    const branch = record.owned?.branch ?? record.available?.branchHint ?? '';
    const remote = record.machine !== 'local';
    const turns = remote ? detail?.messageCount ?? 0 : record.messageCount ?? 0;
    const list: { label: string; value: string; title?: string }[] = [
      { label: 'Session', value: record.ownedId ? `Assembly, ${providerName}` : `${providerName} transcript` },
      { label: 'Machine', value: machineLabel }
    ];
    if (folder) list.push({ label: 'Folder', value: shortPath(folder), title: folder });
    if (branch) list.push({ label: 'Branch', value: branch });
    if (record.model?.trim()) list.push({ label: 'Model', value: record.model.trim() });
    if (turns > 0) {
      list.push({ label: remote && !detail?.startComplete ? 'Saved turns' : 'Turns', value: String(turns) });
    }
    const active = new Date(record.updatedAt ?? '');
    if (!Number.isNaN(active.getTime())) {
      list.push({ label: 'Last active', value: formatMonthDayTime(active), title: exactLocalTime(record.updatedAt) });
    }
    return list;
  });
  const startMissing = $derived(record.machine !== 'local' && detail?.startComplete === false);

  /** Clicking the line opens the session; one that cannot be opened shows its details instead. */
  function openRow(): void {
    if (can('resume-assembly')) onAction('resume-assembly');
    else onToggle();
  }
</script>

<div class="history-row" class:expanded data-testid="session-history-card">
  <button type="button" class="row-open" title={shownTitle} onclick={openRow}>
    <ProviderIcon class={`size-3.5 shrink-0 ${markColour}`} aria-hidden="true" />
    <span class="row-title">{shownTitle}</span>
    <span class="row-when" title={exactLocalTime(record.updatedAt)}>{age}</span>
  </button>
  <button
    type="button"
    class="row-disclose"
    aria-label={expanded ? 'Hide details' : 'Show details'}
    aria-expanded={expanded}
    title={expanded ? 'Hide details' : 'Show details'}
    onclick={onToggle}
  >
    <ChevronRight class="size-3.5" aria-hidden="true" />
  </button>
</div>

<!-- The details are written into the page only while open: keeping every
     session's text mounted is what used to freeze the shell. -->
{#if expanded}
  <div class="history-detail">
    {#if detailsLoading}
      <p class="detail-note"><WorkingSpinner size={12} /> Loading details…</p>
    {:else if latest}
      <p class="detail-latest">{latest}</p>
    {/if}
    <dl class="detail-facts">
      {#each facts as fact (fact.label)}
        <dt>{fact.label}</dt>
        <dd title={fact.title}>{fact.value}</dd>
      {/each}
      {#if startMissing}
        <dt>First prompt</dt>
        <dd class="wrap note">Earliest messages not saved on this Mac yet</dd>
      {:else if firstPrompt && !detailsLoading}
        <dt>First prompt</dt>
        <dd class="wrap" title={firstPrompt}>{firstPrompt}</dd>
      {/if}
    </dl>

    <!-- The conversation, behind a disclosure: the first turn, then the last two. -->
    <div class="detail-section">
      <Collapsible.Root bind:open={turnsOpen}>
        <Collapsible.Trigger
          class="flex w-fit items-center gap-1 rounded-lg py-(--space-1)
                 text-(length:--text-quiet) leading-normal text-muted-foreground
                 transition-colors outline-none hover:text-foreground
                 focus-visible:ring-3 focus-visible:ring-ring/50"
        >
          {#if detailsLoading}
            <WorkingSpinner size={14} />
            Loading turns…
          {:else}
            <ChevronRight
              class={cn('size-3.5 transition-transform', turnsOpen && 'rotate-90')}
              aria-hidden="true"
            />
            Turns ({turns.length})
          {/if}
        </Collapsible.Trigger>
        <Collapsible.Content>
          <div class="flex flex-col gap-(--space-2) pt-(--space-2)">
            {#if detailsLoading}
              <p class={QUIET_LINE}><WorkingSpinner size={12} /> Loading turns…</p>
            {:else if turns.length === 0}
              <p class={QUIET_LINE}>No turns were stored for this session.</p>
            {:else}
              {#each shownTurns as turn, index (index)}
                {#if index === 1 && elidedTurns > 0}
                  <p class={QUIET_LINE}>… {elidedTurns} earlier turns</p>
                {/if}
                <!-- Each turn scrolls inside its own box, so one long answer
                     cannot push the next session off the panel. -->
                <div class="turn-block overflow-hidden rounded-lg bg-background">
                  <div
                    class="flex items-center gap-(--space-2) border-b px-(--space-2) py-1"
                    class:speaker-user={turn.speaker === 'user'}
                    class:speaker-agent={turn.speaker !== 'user'}
                  >
                    <p class="m-0 flex-1 text-xs font-semibold tracking-wide uppercase">
                      {turn.speaker === 'user' ? 'You' : 'Agent'}
                    </p>
                    <IconButton label="Copy this turn" size="xs" onclick={() => onCopyText(turn.text)}>
                      <Copy />
                    </IconButton>
                  </div>
                  <ScrollArea type="always">
                    <p
                      class="m-0 px-(--space-2) py-(--space-2) text-(length:--text-quiet)
                             leading-relaxed whitespace-pre-wrap text-foreground"
                    >
                      {turn.text}
                    </p>
                  </ScrollArea>
                </div>
              {/each}
            {/if}
          </div>
        </Collapsible.Content>
      </Collapsible.Root>
    </div>

    <!-- Who the session ran for itself, each with its own transcript. -->
    <!-- Shown only when agents were found; an empty list may just mean none were read. -->
    {#if record.subagents.length > 0}
    <section class="detail-section flex flex-col gap-(--space-2)">
      <h3 class={SECTION_LABEL}>Agents this session ran</h3>
        {#each record.subagents as subagent, index (index)}
          <ListRow class="bg-background" actionsLabel="Agent actions">
            <span class="min-w-0 flex-1 truncate">{subagent.name}</span>
            {#if subagent.kind}<Chip class="h-6 px-2 font-normal">{subagent.kind}</Chip>{/if}
            {#if subagent.messageCount}
              <Chip tone="count" class="h-6 px-2 font-normal">{subagent.messageCount}</Chip>
            {/if}
            {#snippet actions()}
              <HoverActionButton
                label={subagent.logPath
                  ? 'View log'
                  : 'No transcript file was found for this agent.'}
                tone="info"
                disabled={!subagent.logPath}
                onclick={() => subagent.logPath && onOpenLog(subagent.logPath)}
              >
                <FileCode2 />
              </HoverActionButton>
            {/snippet}
          </ListRow>
        {/each}
    </section>
    {/if}

    <div class="detail-actions">
      {#if resume && (resume.enabled || machineScanning || runningElsewhere)}
        <Button
          size="xs"
          class="mr-1 rounded-full bg-[var(--color-good-bg)] px-3 font-semibold text-[var(--color-accent)]
                 hover:bg-[color-mix(in_srgb,var(--color-accent)_20%,transparent)]"
          disabled={!resume.enabled}
          onclick={() => onAction('resume-assembly')}
        >
          <RotateCcw aria-hidden="true" /> {resume.enabled ? resume.label : resume.disabledReason}
        </Button>
      {/if}
      {#if can('copy-resume-command')}
        <IconButton label="Copy resume command" size="xs" onclick={() => onAction('copy-resume-command')}>
          <Copy />
        </IconButton>
      {/if}
      {#if can('view-log')}
        <IconButton label="View log" size="xs" onclick={() => onAction('view-log')}>
          <FileText />
        </IconButton>
      {/if}
      {#if can('open-working-directory')}
        <IconButton label="Open folder" size="xs" onclick={() => onAction('open-working-directory')}>
          <FolderOpen />
        </IconButton>
      {/if}
      {#if menuActions.length > 0}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
              <!-- Button, not IconButton: IconButton drops the trigger's props, which left the menu off-screen. -->
              <Button
                {...props}
                variant="ghost"
                size="icon-xs"
                aria-label="More actions"
                title="More actions"
                class="min-h-7 min-w-7"
              >
                <Ellipsis />
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content class="w-56" align="end">
            {#each menuActions as item (item.id)}
              <DropdownMenu.Item
                disabled={!item.enabled}
                title={item.disabledReason ?? undefined}
                onSelect={() => item.enabled && onAction(item.id)}
              >
                {item.label}
              </DropdownMenu.Item>
            {/each}
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {/if}
      {#if can('delete')}
        <IconButton
          label="Delete"
          size="xs"
          class="ml-auto hover:bg-[var(--color-bad-bg)] hover:text-[var(--color-bad)]"
          onclick={() => onAction('delete')}
        >
          <Trash2 />
        </IconButton>
      {/if}
    </div>
    {#if runningElsewhere}<p class="detail-note">This session is still running outside Assembly. Stop it there, then resume it here.</p>{/if}
  </div>
{/if}

<style>
  .history-row {
    position: relative;
    margin: 0 6px;
  }

  .row-open {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 32px;
    padding: 0 10px 0 var(--indent, 22px);
    border-radius: var(--radius-sm);
    font-size: 13px;
    color: var(--color-text);
    text-align: left;
    outline: none;
  }
  .history-row:hover .row-open {
    background: var(--color-hover);
  }
  .row-open:focus-visible,
  .row-disclose:focus-visible {
    box-shadow: var(--focus-ring);
  }

  .row-title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .row-when {
    flex: none;
    min-width: 26px;
    text-align: right;
    font-size: 12px;
    color: var(--color-text-3);
    font-variant-numeric: tabular-nums;
    transition: opacity 100ms;
  }

  /* The details chevron takes the time's place on hover, on keyboard focus and while open. */
  .row-disclose {
    position: absolute;
    top: 4px;
    right: 4px;
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    border-radius: var(--radius-xs);
    color: var(--color-text-2);
    opacity: 0;
    outline: none;
    transition: opacity 100ms;
  }
  .row-disclose:hover {
    background: color-mix(in srgb, var(--color-text) 10%, transparent);
    color: var(--color-text);
  }
  .history-row:hover .row-disclose,
  .history-row:focus-within .row-disclose,
  .history-row.expanded .row-disclose {
    opacity: 1;
  }
  .history-row:hover .row-when,
  .history-row:focus-within .row-when,
  .history-row.expanded .row-when {
    opacity: 0;
  }
  .row-disclose :global(svg) {
    transition: transform 120ms ease;
  }
  .history-row.expanded .row-disclose :global(svg) {
    transform: rotate(90deg);
    color: var(--color-text);
  }

  /* Open: the whole title shows, and the line joins the details under it. */
  .history-row.expanded .row-open {
    align-items: flex-start;
    height: auto;
    min-height: 32px;
    padding-top: 7px;
    padding-bottom: 7px;
    background: var(--color-selected);
    border-radius: var(--radius-sm) var(--radius-sm) 0 0;
  }
  .history-row.expanded .row-title {
    padding-right: 22px;
    white-space: normal;
  }
  .history-row.expanded .row-open :global(svg) {
    margin-top: 2px;
  }

  .history-detail {
    margin: 0 6px 6px;
    padding: 8px 10px 10px var(--indent, 22px);
    border-radius: 0 0 var(--radius-md) var(--radius-md);
    background: var(--color-elevated);
    font-size: 12px;
  }

  .detail-latest {
    display: -webkit-box;
    margin: 0;
    overflow: hidden;
    color: var(--color-text-2);
    line-height: 1.45;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 3;
    line-clamp: 3;
    user-select: text;
  }

  .detail-note {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: 0;
    color: var(--color-text-3);
  }

  .detail-facts {
    display: grid;
    grid-template-columns: 78px minmax(0, 1fr);
    gap: 4px 8px;
    margin: 8px 0 0;
  }
  .detail-facts dt {
    color: var(--color-text-3);
  }
  .detail-facts dd {
    min-width: 0;
    margin: 0;
    overflow: hidden;
    color: var(--color-text-2);
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .detail-facts dd.note {
    color: var(--color-text-3);
  }
  .detail-facts dd.wrap {
    display: -webkit-box;
    white-space: normal;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
  }

  .detail-section {
    margin-top: 10px;
  }

  /* Who said it, told apart at a glance. */
  .turn-block .speaker-user {
    background: color-mix(in srgb, var(--color-accent) 22%, transparent);
    color: var(--color-text);
  }

  .turn-block .speaker-agent {
    background: color-mix(in srgb, var(--color-text) 7%, transparent);
    color: var(--color-text-2, var(--color-text));
  }

  /*
    The height limit belongs on the element that scrolls: the scroll area's
    viewport is `height: 100%` of a parent with no height, so a limit on the
    outer box never gave it anything to scroll.
  */
  .turn-block :global([data-slot='scroll-area-viewport']) {
    height: auto;
    max-height: 16rem;
  }

  .detail-actions {
    display: flex;
    align-items: center;
    gap: 2px;
    margin-top: 10px;
  }
</style>
