<script lang="ts">
  import { Activity, Network, Braces, FolderGit2, GitBranch } from '@lucide/svelte';
  import CollapsibleSection from '$lib/components/CollapsibleSection.svelte';
  import Chip from '$lib/components/Chip.svelte';
  import Badge from '$lib/components/Badge.svelte';
  import type { Tone } from '$lib/components/tone.js';

  // Minimal structural shapes — only the fields this panel renders. The page passes its existing
  // derived values (which are structurally compatible supersets of these).
  type RunRow = {
    id: string;
    title: string;
    status: string;
    phase?: string;
    progress?: number;
    taskID?: string | null;
  };
  type RuntimeRow = {
    pid: number | string;
    port: number | string;
    command?: string;
    rootLabel?: string;
    cwd?: string;
  };
  type AgentRow = {
    title: string;
    provider?: string;
  };
  type WorktreeRow = {
    path: string;
    branch: string;
    repo?: string;
    taskID?: string | null;
    isDirty?: boolean;
  };
  type RepoRow = {
    projectID?: string;
    path: string;
    isDirty?: boolean;
    error?: string | null;
    unstagedCount?: number;
    stagedCount?: number;
    untrackedCount?: number;
    ahead?: number;
    behind?: number;
    hasUpstream?: boolean;
  };

  type Props = {
    runs?: RunRow[];
    runsSummary?: string;
    runsLoading?: boolean;

    runtimeContexts?: RuntimeRow[];
    runtimeSummary?: string;
    runtimeLoading?: boolean;

    agents?: AgentRow[];
    agentsSummary?: string;
    agentsLoading?: boolean;

    worktrees?: WorktreeRow[];
    worktreesSummary?: string;
    worktreesSafetyStats?: string;
    worktreesLoading?: boolean;

    repos?: RepoRow[];
    reposSummary?: string;
    reposLoading?: boolean;

    /** Optional label for the active project, used in empty states. */
    projectName?: string;
  };

  let {
    runs = [],
    runsSummary = '',
    runsLoading = false,
    runtimeContexts = [],
    runtimeSummary = '',
    runtimeLoading = false,
    agents = [],
    agentsSummary = '',
    agentsLoading = false,
    worktrees = [],
    worktreesSummary = '',
    worktreesSafetyStats = '',
    worktreesLoading = false,
    repos = [],
    reposSummary = '',
    reposLoading = false,
    projectName = ''
  }: Props = $props();

  /** How many rows we surface per section before deferring to the summary line. */
  const ROW_LIMIT = 6;

  function runStatusTone(status: string): Tone {
    const value = status.toLowerCase();
    if (value.includes('fail') || value.includes('error') || value.includes('block')) return 'bad';
    if (value.includes('wait') || value.includes('review') || value.includes('attention') || value.includes('approval'))
      return 'attention';
    if (value.includes('run') || value.includes('active') || value.includes('progress') || value.includes('live'))
      return 'live';
    if (value.includes('done') || value.includes('complete') || value.includes('success') || value.includes('merged'))
      return 'good';
    return 'neutral';
  }

  /** A run is "live" while it is actively progressing — used to show a pulse affordance. */
  function isRunLive(status: string): boolean {
    return runStatusTone(status) === 'live';
  }

  function repoSyncLabel(repo: RepoRow): string {
    if (!repo.hasUpstream) return 'no upstream';
    const parts: string[] = [];
    if (repo.ahead) parts.push(`↑${repo.ahead}`);
    if (repo.behind) parts.push(`↓${repo.behind}`);
    return parts.length > 0 ? parts.join(' ') : 'in sync';
  }

  function repoSyncTone(repo: RepoRow): Tone {
    if (!repo.hasUpstream) return 'muted';
    if (repo.behind) return 'attention';
    if (repo.ahead) return 'live';
    return 'good';
  }

  function repoDirtyCount(repo: RepoRow): number {
    return (repo.unstagedCount ?? 0) + (repo.stagedCount ?? 0) + (repo.untrackedCount ?? 0);
  }

  function repoName(repo: RepoRow): string {
    const segments = repo.path.split('/').filter(Boolean);
    return segments[segments.length - 1] ?? repo.path;
  }

  /** Render the trailing tail of a path so the meaningful end stays visible when truncated. */
  function tailPath(path: string, segments = 2): string {
    const parts = path.split('/').filter(Boolean);
    if (parts.length <= segments) return path;
    return `…/${parts.slice(-segments).join('/')}`;
  }

  /** Contextual empty-state copy that mentions the active project when known. */
  function emptyLabel(noun: string): string {
    return projectName ? `No ${noun} in ${projectName}` : `No ${noun}`;
  }
</script>

<div class="workbench-context-panel" aria-label="Context">
  <CollapsibleSection title="Runs" icon={Activity} badge={runs.length} expanded={true}>
    <div class="wcp-section-body">
      {#if runsSummary}<p class="wcp-summary">{runsSummary}</p>{/if}
      {#if runs.length > 0}
        <ul class="wcp-list">
          {#each runs.slice(0, ROW_LIMIT) as run (run.id)}
            <li class="wcp-row">
              <div class="wcp-row-main">
                {#if isRunLive(run.status)}<span class="wcp-pulse" aria-hidden="true"></span>{/if}
                <span class="wcp-row-title" title={run.title}>{run.title}</span>
                <Chip tone={runStatusTone(run.status)} size="xs">{run.status}</Chip>
              </div>
              {#if run.phase || typeof run.progress === 'number' || run.taskID}
                <div class="wcp-row-meta">
                  {#if run.phase}<span class="wcp-meta-strong">{run.phase}</span>{/if}
                  {#if typeof run.progress === 'number'}<span class="wcp-num">{run.progress}%</span>{/if}
                  {#if run.taskID}<span class="wcp-mono wcp-meta-id">{run.taskID}</span>{/if}
                </div>
              {/if}
            </li>
          {/each}
        </ul>
        {#if runs.length > ROW_LIMIT}
          <p class="wcp-more">+{runs.length - ROW_LIMIT} more</p>
        {/if}
      {:else if runsLoading}
        <p class="wcp-state wcp-state--loading">Loading runs…</p>
      {:else}
        <p class="wcp-state">{emptyLabel('orchestration runs')}</p>
      {/if}
    </div>
  </CollapsibleSection>

  <div class="wcp-divider" role="presentation"></div>

  <CollapsibleSection title="Runtime" icon={Network} badge={runtimeContexts.length} expanded={true}>
    <div class="wcp-section-body">
      {#if runtimeSummary}<p class="wcp-summary">{runtimeSummary}</p>{/if}
      {#if runtimeContexts.length > 0}
        <ul class="wcp-list">
          {#each runtimeContexts.slice(0, ROW_LIMIT) as ctx (`${ctx.pid}:${ctx.port}`)}
            <li class="wcp-row">
              <div class="wcp-row-main">
                <Badge tone="live">:{ctx.port}</Badge>
                <span class="wcp-row-title" title={ctx.command}>{ctx.command ?? 'process'}</span>
                <span class="wcp-mono wcp-meta-id">pid {ctx.pid}</span>
              </div>
              {#if ctx.rootLabel || ctx.cwd}
                <div class="wcp-row-meta">
                  {#if ctx.rootLabel}<span class="wcp-meta-strong">{ctx.rootLabel}</span>{/if}
                  {#if ctx.cwd}<span class="wcp-mono wcp-truncate" title={ctx.cwd}>{tailPath(ctx.cwd)}</span>{/if}
                </div>
              {/if}
            </li>
          {/each}
        </ul>
        {#if runtimeContexts.length > ROW_LIMIT}
          <p class="wcp-more">+{runtimeContexts.length - ROW_LIMIT} more</p>
        {/if}
      {:else if runtimeLoading}
        <p class="wcp-state wcp-state--loading">Scanning runtime…</p>
      {:else}
        <p class="wcp-state">{emptyLabel('runtime contexts')}</p>
      {/if}
    </div>
  </CollapsibleSection>

  <div class="wcp-divider" role="presentation"></div>

  <CollapsibleSection title="Agents" icon={Braces} badge={agents.length} expanded={true}>
    <div class="wcp-section-body">
      {#if agentsSummary}<p class="wcp-summary">{agentsSummary}</p>{/if}
      {#if agents.length > 0}
        <ul class="wcp-list">
          {#each agents.slice(0, ROW_LIMIT) as agent, index (`${agent.title}:${index}`)}
            <li class="wcp-row">
              <div class="wcp-row-main">
                <span class="wcp-row-title" title={agent.title}>{agent.title}</span>
                {#if agent.provider}<Chip tone="neutral" size="xs">{agent.provider}</Chip>{/if}
              </div>
            </li>
          {/each}
        </ul>
        {#if agents.length > ROW_LIMIT}
          <p class="wcp-more">+{agents.length - ROW_LIMIT} more</p>
        {/if}
      {:else if agentsLoading}
        <p class="wcp-state wcp-state--loading">Loading agents…</p>
      {:else}
        <p class="wcp-state">{emptyLabel('agent sessions')}</p>
      {/if}
    </div>
  </CollapsibleSection>

  <div class="wcp-divider" role="presentation"></div>

  <CollapsibleSection title="Worktrees" icon={FolderGit2} badge={worktrees.length} expanded={true}>
    <div class="wcp-section-body">
      {#if worktreesSummary}<p class="wcp-summary">{worktreesSummary}</p>{/if}
      {#if worktreesSafetyStats}<p class="wcp-subtle">{worktreesSafetyStats}</p>{/if}
      {#if worktrees.length > 0}
        <ul class="wcp-list">
          {#each worktrees.slice(0, ROW_LIMIT) as worktree (worktree.path)}
            <li class="wcp-row">
              <div class="wcp-row-main">
                <span class="wcp-row-title" title={worktree.branch}>{worktree.branch}</span>
                {#if worktree.isDirty}
                  <Chip tone="attention" size="xs">dirty</Chip>
                {:else}
                  <Chip tone="good" size="xs">clean</Chip>
                {/if}
              </div>
              <div class="wcp-row-meta">
                {#if worktree.repo}<span class="wcp-meta-strong">{worktree.repo}</span>{/if}
                {#if worktree.taskID}<span class="wcp-mono wcp-meta-id">{worktree.taskID}</span>{/if}
                <span class="wcp-mono wcp-truncate" title={worktree.path}>{tailPath(worktree.path)}</span>
              </div>
            </li>
          {/each}
        </ul>
        {#if worktrees.length > ROW_LIMIT}
          <p class="wcp-more">+{worktrees.length - ROW_LIMIT} more</p>
        {/if}
      {:else if worktreesLoading}
        <p class="wcp-state wcp-state--loading">Scanning worktrees…</p>
      {:else}
        <p class="wcp-state">{emptyLabel('worktrees')}</p>
      {/if}
    </div>
  </CollapsibleSection>

  <div class="wcp-divider" role="presentation"></div>

  <CollapsibleSection title="Git" icon={GitBranch} badge={repos.length} expanded={true}>
    <div class="wcp-section-body">
      {#if reposSummary}<p class="wcp-summary">{reposSummary}</p>{/if}
      {#if repos.length > 0}
        <ul class="wcp-list">
          {#each repos.slice(0, ROW_LIMIT) as repo (repo.path)}
            <li class="wcp-row">
              <div class="wcp-row-main">
                <span class="wcp-row-title" title={repo.path}>{repoName(repo)}</span>
                {#if repo.error}
                  <Chip tone="bad" size="xs">error</Chip>
                {:else if repo.isDirty}
                  <Chip tone="attention" size="xs">{repoDirtyCount(repo)} changed</Chip>
                {:else}
                  <Chip tone="good" size="xs">clean</Chip>
                {/if}
              </div>
              <div class="wcp-row-meta">
                {#if repo.error}
                  <span class="wcp-meta-error" title={repo.error}>{repo.error}</span>
                {:else}
                  <span class="wcp-sync wcp-sync--{repoSyncTone(repo)}">{repoSyncLabel(repo)}</span>
                {/if}
              </div>
            </li>
          {/each}
        </ul>
        {#if repos.length > ROW_LIMIT}
          <p class="wcp-more">+{repos.length - ROW_LIMIT} more</p>
        {/if}
      {:else if reposLoading}
        <p class="wcp-state wcp-state--loading">Scanning repositories…</p>
      {:else}
        <p class="wcp-state">{emptyLabel('repository summaries')}</p>
      {/if}
    </div>
  </CollapsibleSection>
</div>

<style>
  .workbench-context-panel {
    display: flex;
    flex-direction: column;
    gap: var(--space-1, 4px);
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
    padding: var(--space-3, 12px) var(--space-2, 8px) var(--space-4, 16px);
    background: var(--color-bg, #191a21);
    scrollbar-width: thin;
  }

  /* ── Hairline rhythm between sections ─────────────────────────────── */
  .wcp-divider {
    height: 1px;
    margin: var(--space-2, 8px) var(--space-3, 12px);
    background: var(--color-border, rgba(255, 255, 255, 0.08));
  }

  /* ── Section body ─────────────────────────────────────────────────── */
  .wcp-section-body {
    display: flex;
    flex-direction: column;
    gap: var(--space-2, 8px);
    /* Align content with the section title (chevron + icon gutter), breathe at the foot. */
    padding: var(--space-1, 4px) var(--space-2, 8px) var(--space-1, 4px) var(--space-5, 20px);
  }

  .wcp-summary {
    margin: 0;
    padding-inline: var(--space-2, 8px);
    font-size: var(--text-sm, 12px);
    line-height: 1.45;
    color: var(--color-text-2, #aab6b2);
  }

  .wcp-subtle {
    margin: 0;
    padding-inline: var(--space-2, 8px);
    font-size: var(--text-xs, 11px);
    line-height: 1.4;
    color: var(--color-text-3, #8d9995);
  }

  /* ── Row list ─────────────────────────────────────────────────────── */
  .wcp-list {
    display: flex;
    flex-direction: column;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  .wcp-row {
    display: flex;
    flex-direction: column;
    gap: var(--space-1, 4px);
    padding: var(--space-2, 8px);
    border-radius: var(--radius-sm, 6px);
    /* Borderless by default; hover reveals a faint surface tint for scanability. */
    transition: background-color 120ms ease;
  }

  .wcp-row:hover {
    background: var(--color-surface, rgba(255, 255, 255, 0.045));
  }

  /* Hairline separators *between* rows only — never around them. */
  .wcp-row + .wcp-row {
    position: relative;
  }

  .wcp-row + .wcp-row::before {
    content: '';
    position: absolute;
    top: 0;
    left: var(--space-2, 8px);
    right: var(--space-2, 8px);
    height: 1px;
    background: var(--color-border, rgba(255, 255, 255, 0.08));
    opacity: 0.5;
  }

  /* Suppress the divider when either neighbouring row is hovered, so the
     hover surface reads as one clean block. */
  .wcp-row:hover + .wcp-row::before,
  .wcp-row:hover::before {
    opacity: 0;
  }

  .wcp-row-main {
    display: flex;
    align-items: center;
    gap: var(--space-2, 8px);
    min-width: 0;
  }

  .wcp-row-title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    font-size: var(--text-sm, 12px);
    font-weight: var(--weight-semibold, 600);
    line-height: 1.35;
    color: var(--color-text, #f2f6f5);
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Secondary line — tabular, tertiary colour, wraps gracefully. */
  .wcp-row-meta {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--space-1, 4px) var(--space-2, 8px);
    min-width: 0;
    font-size: var(--text-xs, 11px);
    line-height: 1.3;
    color: var(--color-text-3, #8d9995);
    font-variant-numeric: tabular-nums;
  }

  /* Slightly brighter meta token for the most useful secondary field. */
  .wcp-meta-strong {
    color: var(--color-text-2, #aab6b2);
  }

  .wcp-num {
    color: var(--color-text-2, #aab6b2);
    font-variant-numeric: tabular-nums;
  }

  /* Identifier tokens (task ids, pids) — quiet, monospaced, never grow. */
  .wcp-meta-id {
    flex-shrink: 0;
    color: var(--color-text-3, #8d9995);
  }

  .wcp-mono {
    font-family: var(--font-mono, var(--font-mono));
  }

  /* Path-like tokens truncate from the front (see tailPath) and ellipsize. */
  .wcp-truncate {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Git sync indicator — tone-tinted text, no chip weight. */
  .wcp-sync {
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.01em;
  }
  .wcp-sync--good {
    color: var(--color-good, #8bdc9b);
  }
  .wcp-sync--live {
    color: var(--color-live, #5ce2cf);
  }
  .wcp-sync--attention {
    color: var(--color-attention, #d8aa55);
  }
  .wcp-sync--muted {
    color: var(--color-text-3, #8d9995);
  }

  .wcp-meta-error {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    color: var(--color-bad, #f36f6f);
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Still live dot for actively running rows. */
  .wcp-pulse {
    flex-shrink: 0;
    width: 6px;
    height: 6px;
    border-radius: var(--radius-pill, 999px);
    background: var(--color-live, #5ce2cf);
  }

  /* "+N more" deferral note. */
  .wcp-more {
    margin: 0;
    padding: var(--space-1, 4px) var(--space-2, 8px) 0;
    font-size: var(--text-xs, 11px);
    color: var(--color-text-3, #8d9995);
  }

  /* ── Empty + loading states — muted, centered, quiet. ─────────────── */
  .wcp-state {
    margin: 0;
    padding: var(--space-3, 12px) var(--space-2, 8px);
    font-size: var(--text-xs, 11px);
    line-height: 1.4;
    color: var(--color-text-3, #8d9995);
    text-align: center;
  }

  .wcp-state--loading {
    opacity: 0.7;
  }
</style>
