<script lang="ts">
  /**
   * ConversationList.svelte — the Codex / VS Code-style conversation switcher rail.
   *
   * Presentational only: it renders a scrollable list of agent sessions, an active
   * selection, a per-row LIVE indicator, and an overflow menu of actions. All
   * behaviour (opening, closing, saving, …) is delegated to callbacks supplied by
   * the parent. It owns no business logic and performs no IO.
   *
   * Each row carries TWO menus over the same item list:
   *   - a right-click ContextMenu on the whole row, and
   *   - a click-opened overflow Menu (⋯ button) for discoverability.
   */
  import type { AgentSession } from '../tauriSource.js';
  import {
    GitBranch,
    MoreHorizontal,
    Save,
    History,
    Wrench,
    ClipboardCopy,
    X,
  } from '@lucide/svelte';
  import Badge from './Badge.svelte';
  import Chip from './Chip.svelte';
  import Tooltip from './Tooltip.svelte';
  import IconButton from './IconButton.svelte';
  import ContextMenu, { type ContextMenuItem } from './ContextMenu.svelte';
  import Menu from './Menu.svelte';
  import SearchInput from './SearchInput.svelte';
  import type { Tone } from './tone.js';

  /**
   * `branchHint` is part of the conversation rail's shape (per spec) but not yet
   * on the exported `AgentSession`. Widen locally so the field is typed without
   * editing the shared model.
   */
  type ConversationSession = AgentSession & { branchHint?: string | null };

  interface Props {
    sessions: ConversationSession[];
    activeKey: string | null;
    /** Keys (per `keyFor`) that currently have a live terminal attached. */
    liveKeys: Set<string>;
    keyFor: (s: ConversationSession) => string;
    /** Bindable search text. Drives the internal SearchInput. */
    query?: string;
    /**
     * When false (default), the component filters `sessions` itself using `query`.
     * Set true when the parent already filters the list — the SearchInput then only
     * reflects/forwards `query` and no internal filtering is applied.
     */
    filter?: boolean;
    onOpen?: (s: ConversationSession) => void;
    onClose?: (s: ConversationSession) => void;
    onSave?: (s: ConversationSession) => void;
    onRestore?: (s: ConversationSession) => void;
    onRepair?: (s: ConversationSession) => void;
    onCopyResume?: (s: ConversationSession) => void;
  }

  let {
    sessions,
    activeKey,
    liveKeys,
    keyFor,
    query = $bindable(''),
    filter = false,
    onOpen,
    onClose,
    onSave,
    onRestore,
    onRepair,
    onCopyResume,
  }: Props = $props();

  // ── Provider → presentation ──────────────────────────────────────────
  type ProviderKind = 'codex' | 'claude' | 'antigravity' | 'cmux';

  function providerKind(provider: string): ProviderKind {
    if (provider === 'codex') return 'codex';
    if (provider === 'claude') return 'claude';
    if (provider === 'antigravity') return 'antigravity';
    return 'cmux'; // cmux-<agent> and any unknown provider read as muted
  }

  function providerTone(provider: string): Tone {
    switch (providerKind(provider)) {
      case 'codex':
        return 'good';
      case 'claude':
        return 'attention';
      case 'antigravity':
        return 'good';
      default:
        return 'muted';
    }
  }

  function providerLabel(provider: string): string {
    const kind = providerKind(provider);
    if (kind === 'codex') return 'Codex';
    if (kind === 'claude') return 'Claude';
    if (kind === 'antigravity') return 'Antigravity';
    // cmux-<agent> → "cmux · <agent>"; a bare "cmux" stays "cmux".
    const agent = provider.startsWith('cmux-') ? provider.slice('cmux-'.length) : '';
    return agent ? `cmux · ${agent}` : provider;
  }

  // ── Task id (TSK-123) extraction ─────────────────────────────────────
  const TASK_RE = /TSK-\d+/i;
  function taskId(s: ConversationSession): string | null {
    const haystack = `${s.title} ${s.projectPath ?? ''}`;
    const match = haystack.match(TASK_RE);
    return match ? match[0].toUpperCase() : null;
  }

  // ── Path display: front-truncate so the meaningful tail stays visible ─
  function displayPath(path: string | null): string {
    if (!path) return '';
    // Collapse a leading home directory to "~" for compactness.
    const home = path.replace(/^\/Users\/[^/]+/, '~').replace(/^\/home\/[^/]+/, '~');
    const segments = home.split('/').filter(Boolean);
    if (segments.length <= 3) return home;
    // Keep the last three segments; CSS handles any remaining overflow.
    return `…/${segments.slice(-3).join('/')}`;
  }

  // ── Filtering ────────────────────────────────────────────────────────
  const visibleSessions = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (filter || !q) return sessions;
    return sessions.filter((s) => {
      const hay = [s.title, s.projectPath ?? '', providerLabel(s.provider), s.provider]
        .join(' ')
        .toLowerCase();
      return hay.includes(q);
    });
  });

  function isLive(s: ConversationSession): boolean {
    return liveKeys.has(keyFor(s));
  }

  function isActive(s: ConversationSession): boolean {
    return activeKey !== null && activeKey === keyFor(s);
  }

  // ── Action menu (shared by the right-click ContextMenu and the ⋯ Menu) ─
  function menuItems(s: ConversationSession): ContextMenuItem[] {
    return [
      { id: 'save', label: 'Save', icon: Save, onselect: () => onSave?.(s) },
      { id: 'restore', label: 'Restore', icon: History, onselect: () => onRestore?.(s) },
      { id: 'repair', label: 'Repair', icon: Wrench, onselect: () => onRepair?.(s) },
      {
        id: 'copy',
        label: 'Copy resume',
        icon: ClipboardCopy,
        onselect: () => onCopyResume?.(s),
      },
      {
        id: 'close',
        label: 'Close',
        icon: X,
        danger: true,
        separatorBefore: true,
        onselect: () => onClose?.(s),
      },
    ];
  }

  // Activating a row (click / Enter / Space).
  function activate(s: ConversationSession) {
    onOpen?.(s);
  }

  function onRowKeydown(event: KeyboardEvent, s: ConversationSession) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      activate(s);
    }
  }
</script>

<div class="conversation-list">
  {#if !filter}
    <div class="conversation-list__search">
      <SearchInput bind:value={query} placeholder="Search conversations" />
    </div>
  {/if}

  {#if visibleSessions.length === 0}
    <div class="conversation-list__empty" role="status">
      <p class="conversation-list__empty-title">No conversations</p>
      <p class="conversation-list__empty-hint">
        {query.trim() ? 'No matches for your search.' : 'Start a session to see it here.'}
      </p>
    </div>
  {:else}
    <ul class="conversation-list__rows" role="listbox" aria-label="Conversations">
      {#each visibleSessions as session (keyFor(session))}
        {@const active = isActive(session)}
        {@const live = isLive(session)}
        {@const tsk = taskId(session)}
        {@const path = displayPath(session.projectPath)}
        <li class="row-wrap" role="presentation">
          <ContextMenu items={menuItems(session)}>
            <div
              class="row"
              class:row--active={active}
              role="option"
              aria-selected={active}
              tabindex="0"
              title={session.title}
              onclick={() => activate(session)}
              onkeydown={(e) => onRowKeydown(e, session)}
            >
              <span class="row__accent" aria-hidden="true"></span>

              <div class="row__body">
                <div class="row__top">
                  <Badge tone={providerTone(session.provider)}>
                    {providerLabel(session.provider)}
                  </Badge>

                  <span class="row__title">{session.title}</span>

                  {#if tsk}
                    <span class="row__tsk">
                      <Chip tone="neutral" size="xs">{tsk}</Chip>
                    </span>
                  {/if}

                  {#if live}
                    <span class="row__live">
                      <Tooltip text="Live terminal running" side="top">
                        <span class="row__live-dot" aria-label="Live terminal running"></span>
                      </Tooltip>
                    </span>
                  {/if}
                </div>

                {#if session.branchHint || path}
                  <div class="row__meta">
                    {#if session.branchHint}
                      <span class="row__branch">
                        <GitBranch size={11} aria-hidden="true" />
                        <span class="row__branch-name">{session.branchHint}</span>
                      </span>
                    {/if}
                    {#if session.branchHint && path}
                      <span class="row__meta-sep" aria-hidden="true">·</span>
                    {/if}
                    {#if path}
                      <span class="row__path" dir="rtl">{path}</span>
                    {/if}
                  </div>
                {/if}
              </div>

              <!-- Stop clicks inside the overflow zone (the ⋯ button and its
                   menu trigger) from bubbling up and activating the row. -->
              <div
                class="row__overflow"
                role="presentation"
                onclick={(e) => e.stopPropagation()}
              >
                <Menu items={menuItems(session)} align="end">
                  {#snippet trigger()}
                    <IconButton
                      icon={MoreHorizontal}
                      label="Conversation actions"
                      variant="ghost"
                      size="sm"
                    />
                  {/snippet}
                </Menu>
              </div>
            </div>
          </ContextMenu>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  /* ── Shell ───────────────────────────────────────────────────────── */
  .conversation-list {
    display: flex;
    flex-direction: column;
    min-height: 0;
    height: 100%;
    background: var(--color-bg);
    color: var(--color-text);
  }

  .conversation-list__search {
    padding: var(--space-3) var(--space-3) var(--space-2);
    flex-shrink: 0;
  }

  /* ── Rows container ──────────────────────────────────────────────── */
  .conversation-list__rows {
    list-style: none;
    margin: 0;
    padding: var(--space-1) var(--space-2) var(--space-3);
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    overflow-y: auto;
    min-height: 0;
    flex: 1;
  }

  .row-wrap {
    margin: 0;
  }

  /* ── Row ─────────────────────────────────────────────────────────── */
  .row {
    position: relative;
    display: flex;
    align-items: flex-start;
    gap: var(--space-2);
    padding: var(--space-3);
    border-radius: var(--radius-md);
    cursor: pointer;
    user-select: none;
    outline: none;
    transition:
      background-color 130ms ease,
      box-shadow 130ms ease;
  }

  .row:focus-visible {
    box-shadow: var(--focus-ring);
  }

  .row--active {
    background-color: var(--color-surface);
  }

  /* Left accent bar — only painted on the active row. */
  .row__accent {
    position: absolute;
    left: 0;
    top: 50%;
    transform: translateY(-50%);
    width: 3px;
    height: 0;
    border-radius: var(--radius-pill);
    background-color: var(--color-accent);
    transition:
      height 150ms ease,
      opacity 150ms ease;
    opacity: 0;
  }

  .row--active .row__accent {
    height: calc(100% - var(--space-4));
    opacity: 1;
  }

  /* ── Body (everything except the trailing overflow button) ───────── */
  .row__body {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
  }

  .row__top {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    min-width: 0;
  }

  .row__title {
    flex: 1;
    min-width: 0;
    font-size: var(--text-md);
    font-weight: var(--weight-medium);
    color: var(--color-text);
    line-height: 1.3;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .row--active .row__title {
    font-weight: var(--weight-semibold);
  }

  .row__tsk {
    flex-shrink: 0;
  }

  /* ── LIVE indicator ──────────────────────────────────────────────── */
  .row__live {
    display: inline-flex;
    align-items: center;
    flex-shrink: 0;
  }

  .row__live-dot {
    display: block;
    width: 8px;
    height: 8px;
    border-radius: var(--radius-pill);
    background-color: var(--color-live);
    box-shadow: 0 0 0 3px var(--color-live-bg);
  }

  /* ── Secondary line: branch + path ───────────────────────────────── */
  .row__meta {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    min-width: 0;
    font-size: var(--text-xs);
    color: var(--color-text-3);
    line-height: 1.4;
  }

  .row__branch {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    flex-shrink: 0;
    max-width: 45%;
  }

  .row__branch-name {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .row__meta-sep {
    flex-shrink: 0;
    opacity: 0.6;
  }

  /* Front-truncate the path: dir="rtl" makes the ellipsis fall at the start
     so the meaningful tail (project / file) stays visible. */
  .row__path {
    flex: 1;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    text-align: left;
    unicode-bidi: plaintext;
  }

  /* ── Overflow button ─────────────────────────────────────────────── */
  .row__overflow {
    flex-shrink: 0;
    /* Reveal on focus / active so resting rows stay clean. */
    opacity: 0;
    transition: opacity 130ms ease;
  }

  .row:focus-within .row__overflow,
  .row--active .row__overflow {
    opacity: 1;
  }

  /* ── Empty state ─────────────────────────────────────────────────── */
  .conversation-list__empty {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: var(--space-1);
    padding: var(--space-6) var(--space-4);
    text-align: center;
  }

  .conversation-list__empty-title {
    margin: 0;
    font-size: var(--text-md);
    font-weight: var(--weight-medium);
    color: var(--color-text-2);
  }

  .conversation-list__empty-hint {
    margin: 0;
    font-size: var(--text-sm);
    color: var(--color-text-3);
  }
</style>
