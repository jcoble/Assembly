<script lang="ts">
  /**
   * ActivityWorktreesPanel.svelte — the "worktrees" activity-mode body.
   *
   * Presentational only: it renders the worktree cleanup runbook strip and the
   * per-worktree rows (safety badge, decision/plan lanes, owner chips, saved
   * workspace chip and the row ⋯ action menu). The page owns ALL worktrees
   * state (`projectWorktrees`/loading/error/source plus every derived value and
   * the `activeWorktreeRowActionMenu`) and every worktree function; this
   * component holds NO `$state` of its own. Every page value the markup reads is
   * a prop and every action is an `on*`/predicate callback.
   *
   * Renders inline in the activity area (no teleport). The wrapping
   * `{:else if sourceActivityMode === 'worktrees'}` gate, the
   * `.activity-panel-list` container and the `{#if length === 0}` empty state
   * stay in the page; this component renders the non-empty body.
   */
  import {
    Braces,
    Copy,
    ExternalLink,
    FolderOpen,
    FolderSearch,
    History,
    MoreHorizontal,
    PanelBottom,
    Save,
    Terminal,
    Trash2,
  } from '@lucide/svelte';
  import type { ProjectWorktree } from '$lib/tauriSource';
  import type { WorkspaceSnapshot } from '$lib/workspaceSnapshot';
  import type {
    WorktreeDecisionLane,
    WorktreePrimaryAction,
    WorktreeSafetyKind,
    WorktreeSafetySummary,
  } from '$lib/worktreeSafety';
  import type { WorktreeCleanupPlan } from '$lib/worktreeCleanupPlan';
  import type { WorktreeCleanupRunbookModel } from '$lib/worktreeCleanupRunbook';

  /** Session-ownership chip (matches the page-local `WorktreeOwnerChip`). */
  type OwnerChip = {
    id: string;
    label: string;
    title: string;
    tone: 'live' | 'saved' | 'warning' | 'muted';
  };

  interface Props {
    /** Worktrees to render (= page `filteredProjectWorktrees`, already non-empty). */
    worktrees: ProjectWorktree[];
    /** Cleanup runbook model (= page `projectWorktreeCleanupRunbook`). */
    cleanupRunbook: WorktreeCleanupRunbookModel;
    /** Current busy action key (= page `fileActionBusy`) — drives disabled states. */
    fileActionBusy: string;

    /** Per-worktree safety summary (page helper `projectWorktreeSafety`). */
    worktreeSafety: (worktree: ProjectWorktree) => WorktreeSafetySummary;
    /** Per-worktree cleanup plan (page helper `projectWorktreeCleanupPlan`). */
    cleanupPlan: (worktree: ProjectWorktree) => WorktreeCleanupPlan;
    /** Decision lane for a safety summary (page helper `worktreeDecisionLane`). */
    decisionLane: (safety: WorktreeSafetySummary) => WorktreeDecisionLane;
    /** Primary action for a worktree (page helper `projectWorktreePrimaryAction`). */
    primaryAction: (worktree: ProjectWorktree) => WorktreePrimaryAction;
    /** Eligibility kind for row styling (page helper `projectWorktreeEligibilityKind`). */
    eligibilityKind: (worktree: ProjectWorktree) => WorktreeSafetyKind;
    /** Latest saved workspace snapshot, or null (page helper `latestWorktreeWorkspaceSnapshot`). */
    latestSnapshot: (worktree: ProjectWorktree) => WorkspaceSnapshot | null;
    /** Session-ownership chips for a worktree (page helper `worktreeOwnerChips`). */
    ownerChips: (worktree: ProjectWorktree, safety: WorktreeSafetySummary) => OwnerChip[];
    /** Task URL for a task id, or null (page helper `gitTaskUrl`). */
    gitTaskUrl: (taskID: string | null) => string | null;
    /** Short activity label for a worktree (page helper `projectWorktreeActivityLabel`). */
    activityLabel: (worktree: ProjectWorktree) => string;
    /** Short label for a cleanup plan (page helper `worktreeCleanupPlanLabel`). */
    cleanupPlanLabel: (plan: WorktreeCleanupPlan) => string;
    /** Full cleanup-plan report for the row title (page helper `formatWorktreeCleanupPlanReport`). */
    cleanupPlanReport: (worktree: ProjectWorktree, plan: WorktreeCleanupPlan) => string;
    /** Saved workspace snapshot tooltip (page helper `worktreeWorkspaceSnapshotTitle`). */
    snapshotTitle: (worktree: ProjectWorktree) => string;
    /** Saved workspace snapshot label (page helper `worktreeWorkspaceSnapshotLabel`). */
    snapshotLabel: (worktree: ProjectWorktree) => string;
    /** Whether the row ⋯ menu is open for this worktree (page predicate, scope 'activity'). */
    rowActionMenuOpen: (worktree: ProjectWorktree) => boolean;

    /** Copy the whole cleanup runbook. */
    onCopyRunbook: () => void;
    /** Right-click a row → open its ⋯ menu (page `openWorktreeRowActionMenu`, scope 'activity'). */
    onRowContextMenu: (worktree: ProjectWorktree) => void;
    /** Toggle a row's ⋯ menu (page `toggleWorktreeRowActionMenu`, scope 'activity'). */
    onToggleRowActionMenu: (worktree: ProjectWorktree) => void;
    /** Close any open row ⋯ menu (page `closeWorktreeRowActionMenu`). */
    onCloseRowActionMenu: () => void;
    /** Copy a worktree's cleanup plan. */
    onCopyCleanupPlan: (worktree: ProjectWorktree) => void;
    /** Run a worktree's primary action (backup/cleanup/audit). */
    onRunPrimaryAction: (worktree: ProjectWorktree) => void;
    /** Open a worktree in the source browser. */
    onOpenInSourceBrowser: (worktree: ProjectWorktree) => void;
    /** Copy a worktree's path. */
    onCopyPath: (worktree: ProjectWorktree) => void;
    /** Open a worktree's path. */
    onOpenPath: (worktree: ProjectWorktree) => void;
    /** Open a worktree in an external terminal. */
    onOpenTerminal: (worktree: ProjectWorktree) => void;
    /** Open a worktree in an embedded terminal. */
    onOpenEmbeddedTerminal: (worktree: ProjectWorktree) => void;
    /** Reveal a worktree's path in the OS file manager. */
    onRevealPath: (worktree: ProjectWorktree) => void;
    /** Restore a saved workspace snapshot. */
    onRestoreSnapshot: (snapshot: WorkspaceSnapshot) => void;
  }

  let {
    worktrees,
    cleanupRunbook,
    fileActionBusy,
    worktreeSafety,
    cleanupPlan,
    decisionLane,
    primaryAction,
    eligibilityKind,
    latestSnapshot,
    ownerChips,
    gitTaskUrl,
    activityLabel,
    cleanupPlanLabel,
    cleanupPlanReport,
    snapshotTitle,
    snapshotLabel,
    rowActionMenuOpen,
    onCopyRunbook,
    onRowContextMenu,
    onToggleRowActionMenu,
    onCloseRowActionMenu,
    onCopyCleanupPlan,
    onRunPrimaryAction,
    onOpenInSourceBrowser,
    onCopyPath,
    onOpenPath,
    onOpenTerminal,
    onOpenEmbeddedTerminal,
    onRevealPath,
    onRestoreSnapshot,
  }: Props = $props();
</script>

<div class="activity-worktree-runbook" aria-label="Worktree cleanup runbook controls">
  <div
    class="worktree-runbook-strip"
    aria-label="Worktree cleanup runbook summary"
    title={cleanupRunbook.headline}
  >
    <span class="worktree-runbook-chip safe">
      <strong>{cleanupRunbook.counts.safeRemovable}</strong>
      <span>safe</span>
    </span>
    <span class="worktree-runbook-chip backup">
      <strong>{cleanupRunbook.counts.backupRequired}</strong>
      <span>backup</span>
    </span>
    <span class="worktree-runbook-chip blocked">
      <strong>{cleanupRunbook.counts.blocked}</strong>
      <span>blocked</span>
    </span>
    <span class="worktree-runbook-chip saved">
      <strong>{cleanupRunbook.counts.savedWorkspaceReview}</strong>
      <span>saved</span>
    </span>
  </div>
  <button
    type="button"
    aria-label="Copy worktree cleanup runbook"
    title="Copy cleanup runbook"
    onclick={onCopyRunbook}
  >
    <Braces size={12} strokeWidth={2} />
  </button>
</div>
{#each worktrees as worktree (`activity:${worktree.path}`)}
  {@const safety = worktreeSafety(worktree)}
  {@const plan = cleanupPlan(worktree)}
  {@const lane = decisionLane(safety)}
  {@const primary = primaryAction(worktree)}
  {@const kind = eligibilityKind(worktree)}
  {@const snapshot = latestSnapshot(worktree)}
  {@const chips = ownerChips(worktree, safety)}
  <div
    class="activity-worktree-row"
    class:menu-open={rowActionMenuOpen(worktree)}
    class:blocked={kind === 'blocked'}
    class:protected={kind === 'protected'}
    class:ready={kind === 'ready'}
    title={cleanupPlanReport(worktree, plan)}
    oncontextmenu={(event) => {
      event.preventDefault();
      onRowContextMenu(worktree);
    }}
  >
    <span class={`worktree-status-badge ${safety.kind}`}>{safety.badge}</span>
    <div class="activity-row-main worktree-row-main">
      <strong>
        <span>{worktree.branch}</span>
        {#if worktree.taskID && gitTaskUrl(worktree.taskID)}
          <a
            class="git-task-link"
            href={gitTaskUrl(worktree.taskID) ?? ''}
            target="_blank"
            rel="noreferrer"
            aria-label="Open worktree task"
          >
            {worktree.taskID}
          </a>
        {/if}
      </strong>
      <small class="worktree-safety-line">
        <span class={`worktree-decision-lane ${lane.tone}`} title={lane.detail}>
          {lane.label}
        </span>
        <span>{safety.reason}</span>
        {#if safety.activeSessionCount > 0}
          <span>
            {safety.activeSessionCount}
            {safety.activeSessionCount === 1 ? 'session' : 'sessions'}
          </span>
        {/if}
        <span>{activityLabel(worktree)}</span>
      </small>
      <div class="worktree-owner-strip" aria-label="Worktree session ownership">
        {#each chips as chip (chip.id)}
          <span class={`worktree-owner-chip ${chip.tone}`} title={chip.title}>{chip.label}</span>
        {/each}
      </div>
      <small class="worktree-plan-line" title={plan.explanation}>
        <span class={`worktree-plan-lane ${plan.lane}`}>
          {cleanupPlanLabel(plan)}
        </span>
        <span>{plan.explanation}</span>
      </small>
      <small class="worktree-recommendation">{safety.recommendation}</small>
      {#if snapshot}
        <button
          class="worktree-snapshot-chip"
          type="button"
          aria-label="Restore latest saved workspace for worktree"
          title={snapshotTitle(worktree)}
          onclick={() => onRestoreSnapshot(snapshot)}
        >
          <History size={11} strokeWidth={2} />
          <span>{snapshotLabel(worktree)}</span>
        </button>
      {/if}
    </div>
    <div
      class="activity-row-actions worktree-activity-actions row-action-menu-anchor"
      class:menu-open={rowActionMenuOpen(worktree)}
      aria-label="Worktree actions"
    >
      <button
        type="button"
        aria-label="Worktree actions"
        aria-haspopup="menu"
        aria-expanded={rowActionMenuOpen(worktree)}
        title="Worktree actions"
        onclick={() => onToggleRowActionMenu(worktree)}
      >
        <MoreHorizontal size={13} strokeWidth={2} />
      </button>
      {#if rowActionMenuOpen(worktree)}
        <div class="row-action-menu" role="menu" aria-label="Worktree actions">
          <button
            type="button"
            role="menuitem"
            aria-label="Copy worktree cleanup plan"
            title="Copy cleanup plan"
            onclick={() => {
              onCloseRowActionMenu();
              onCopyCleanupPlan(worktree);
            }}
          >
            <Copy size={12} strokeWidth={2} />
            <span>Copy cleanup plan</span>
          </button>
          <button
            class={`worktree-primary-action ${primary.kind}`}
            type="button"
            role="menuitem"
            aria-label={`${primary.label} worktree: ${worktree.branch}`}
            title={primary.title}
            disabled={fileActionBusy === `worktree-primary:${worktree.path}`}
            onclick={() => {
              onCloseRowActionMenu();
              onRunPrimaryAction(worktree);
            }}
          >
            {#if primary.kind === 'cleanup'}
              <Trash2 size={12} strokeWidth={2} />
            {:else if primary.kind === 'backup'}
              <Save size={12} strokeWidth={2} />
            {:else}
              <History size={12} strokeWidth={2} />
            {/if}
            <span>{primary.label}</span>
          </button>
          <button
            type="button"
            role="menuitem"
            aria-label="Open worktree in source browser"
            title="Open worktree in source browser"
            onclick={() => {
              onCloseRowActionMenu();
              onOpenInSourceBrowser(worktree);
            }}
          >
            <FolderOpen size={12} strokeWidth={2} />
            <span>Open in source browser</span>
          </button>
          <button
            type="button"
            role="menuitem"
            aria-label="Copy worktree path"
            title="Copy worktree path"
            onclick={() => {
              onCloseRowActionMenu();
              onCopyPath(worktree);
            }}
          >
            <Copy size={12} strokeWidth={2} />
            <span>Copy path</span>
          </button>
          <button
            type="button"
            role="menuitem"
            aria-label="Open worktree path"
            title="Open worktree path"
            onclick={() => {
              onCloseRowActionMenu();
              onOpenPath(worktree);
            }}
          >
            <ExternalLink size={12} strokeWidth={2} />
            <span>Open path</span>
          </button>
          <button
            type="button"
            role="menuitem"
            aria-label="Open worktree in terminal"
            title="Open worktree in terminal"
            onclick={() => {
              onCloseRowActionMenu();
              onOpenTerminal(worktree);
            }}
          >
            <Terminal size={12} strokeWidth={2} />
            <span>Open terminal</span>
          </button>
          <button
            type="button"
            role="menuitem"
            aria-label="Open worktree in embedded terminal"
            title="Open worktree in embedded terminal"
            onclick={() => {
              onCloseRowActionMenu();
              onOpenEmbeddedTerminal(worktree);
            }}
          >
            <PanelBottom size={12} strokeWidth={2} />
            <span>Open embedded terminal</span>
          </button>
          <button
            type="button"
            role="menuitem"
            aria-label="Reveal worktree path"
            title="Reveal worktree path"
            onclick={() => {
              onCloseRowActionMenu();
              onRevealPath(worktree);
            }}
          >
            <FolderSearch size={12} strokeWidth={2} />
            <span>Reveal path</span>
          </button>
        </div>
      {/if}
    </div>
  </div>
{/each}

<style>
  /*
   * Shared activity-row base chrome copied from `+page.svelte`'s `<style>`.
   * The page's `<style>` is scoped, so these rules (used there by sessions /
   * agents / runs / git rows too) do NOT reach the rows this component now
   * authors. They are copied verbatim so the worktree rows render identically;
   * tracked for the later dedup sweep alongside `.file-action-button`. The
   * `.activity-panel-list` container + `.activity-empty` empty state stay in the
   * page, so the page's `.activity-panel-list .worktree-activity-actions.menu-open`
   * reflow rules are re-expressed here without that ancestor (the container is
   * the page's parent element around this component).
   */
  .activity-worktree-row {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    align-items: start;
    gap: 8px;
    min-width: 0;
    min-height: 58px;
    padding: 7px;
    color: #cbd3d1;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 8px;
    background: rgba(0, 0, 0, 0.14);
  }

  .activity-worktree-row .activity-row-actions {
    grid-column: 3;
    align-self: start;
    justify-content: flex-end;
    max-width: 28px;
    overflow: visible;
  }

  .activity-worktree-row.blocked {
    background: rgba(216, 170, 85, 0.09);
  }

  .activity-worktree-row.protected {
    background: rgba(255, 255, 255, 0.04);
  }

  .activity-worktree-row.ready {
    background: rgba(92, 226, 207, 0.06);
  }

  /* .activity-row-main base lives in src/app.css (:global). */
  .worktree-row-main {
    gap: 2px;
  }

  .activity-row-main strong,
  .activity-row-main small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-row-main strong {
    color: #f0f4f3;
    font-size: 11px;
    font-weight: 790;
  }

  .worktree-row-main strong {
    display: flex;
    align-items: center;
    gap: 5px;
    min-width: 0;
  }

  .worktree-row-main strong > span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .activity-row-main small {
    color: #8d9995;
    font-size: 10px;
    font-weight: 720;
  }

  .worktree-safety-line {
    display: flex;
    flex-wrap: wrap;
    gap: 3px 7px;
    line-height: 1.25;
  }

  .worktree-safety-line span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .worktree-owner-chip {
    display: inline-grid;
    place-items: center;
    min-width: 0;
    max-width: 96px;
    height: 17px;
    padding: 0 6px;
    overflow: hidden;
    color: #aeb9b6;
    border: 1px solid rgba(255, 255, 255, 0.075);
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.035);
    font-size: 8px;
    font-weight: 850;
    line-height: 17px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .worktree-owner-chip.live {
    color: #071b18;
    border-color: rgba(92, 226, 207, 0.42);
    background: #67dfd1;
  }

  .worktree-owner-chip.saved {
    color: #8fe7dc;
    border-color: rgba(92, 226, 207, 0.18);
    background: rgba(92, 226, 207, 0.08);
  }

  .worktree-owner-chip.warning {
    color: #e8c47d;
    border-color: rgba(216, 170, 85, 0.24);
    background: rgba(216, 170, 85, 0.08);
  }

  .worktree-owner-chip.muted {
    color: #8d9995;
  }

  .activity-worktree-runbook {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 26px;
    align-items: center;
    gap: 6px;
    min-width: 0;
    padding: 5px 6px;
    border: 1px solid rgba(92, 226, 207, 0.14);
    border-radius: 9px;
    background: rgba(92, 226, 207, 0.045);
  }

  .activity-worktree-runbook button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    padding: 0;
    color: #9feadf;
    border: 1px solid rgba(92, 226, 207, 0.18);
    border-radius: 7px;
    background: rgba(8, 12, 11, 0.44);
    cursor: pointer;
  }

  .activity-worktree-runbook button:hover {
    color: #071b18;
    border-color: rgba(92, 226, 207, 0.55);
    background: #67dfd1;
  }

  /*
   * `.worktree-runbook-strip`, `.worktree-runbook-chip`, `.worktree-owner-strip`
   * and `.worktree-plan-line` carry NO rules in the page — they render with
   * default flow there, so no rule is added here either (faithful move).
   */

  .worktree-snapshot-chip {
    display: inline-flex;
    align-items: center;
    justify-content: flex-start;
    gap: 4px;
    justify-self: start;
    max-width: 126px;
    height: 18px;
    min-width: 0;
    padding: 0 6px;
    color: #8fe7dc;
    border: 1px solid rgba(92, 226, 207, 0.18);
    border-radius: 999px;
    background: rgba(92, 226, 207, 0.08);
    font-size: 8px;
    font-weight: 820;
    cursor: pointer;
  }

  .worktree-snapshot-chip span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .worktree-snapshot-chip:hover,
  .worktree-snapshot-chip:focus-visible {
    color: #eafaf7;
    border-color: rgba(92, 226, 207, 0.34);
    outline: 0;
    background: rgba(92, 226, 207, 0.14);
  }

  .worktree-plan-lane {
    display: inline-grid;
    flex: 0 0 auto;
    place-items: center;
    height: 17px;
    min-width: 42px;
    padding: 0 6px;
    overflow: hidden;
    border: 1px solid rgba(255, 255, 255, 0.085);
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.035);
    font-size: 8px;
    font-weight: 880;
    letter-spacing: 0;
    line-height: 17px;
    text-overflow: ellipsis;
    text-transform: uppercase;
    white-space: nowrap;
  }

  .worktree-plan-lane.safe-remove {
    color: #071b18;
    border-color: rgba(92, 226, 207, 0.42);
    background: #67dfd1;
  }

  .worktree-plan-lane.backup-first,
  .worktree-plan-lane.review-first,
  .worktree-plan-lane.review-prunable,
  .worktree-plan-lane.review-saved-workspace,
  .worktree-plan-lane.review-confirmation {
    color: #e8c47d;
    border-color: rgba(216, 170, 85, 0.24);
    background: rgba(216, 170, 85, 0.08);
  }

  .worktree-plan-lane.blocked-active-session,
  .worktree-plan-lane.blocked-locked {
    color: #ffbd9f;
    border-color: rgba(255, 142, 96, 0.24);
    background: rgba(255, 142, 96, 0.09);
  }

  .worktree-plan-lane.blocked-protected,
  .worktree-plan-lane.keep {
    color: #aeb9b6;
  }

  .worktree-recommendation {
    color: #78837f;
  }

  .worktree-status-badge {
    display: inline-grid;
    place-items: center;
    min-width: 0;
    height: 20px;
    padding: 0 7px;
    overflow: hidden;
    color: #071b18;
    border-radius: 999px;
    background: #6fdfcf;
    font-size: 10px;
    font-weight: 820;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .worktree-status-badge.review {
    color: #dce5e2;
    background: rgba(255, 255, 255, 0.14);
  }

  .worktree-status-badge.protected {
    color: #dce5e2;
    background: rgba(255, 255, 255, 0.16);
  }

  .worktree-status-badge.ready {
    color: #071b18;
    background: #6fdfcf;
  }

  .worktree-status-badge.blocked {
    color: #211606;
    background: #d8aa55;
  }

  .worktree-decision-lane {
    display: inline-grid;
    place-items: center;
    min-width: 0;
    min-height: 18px;
    max-width: 92px;
    padding: 0 6px;
    overflow: hidden;
    color: #cbd3d1;
    border: 1px solid rgba(255, 255, 255, 0.075);
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.045);
    font-size: 8px;
    font-weight: 900;
    line-height: 1;
    text-overflow: ellipsis;
    text-transform: uppercase;
    white-space: nowrap;
  }

  .worktree-decision-lane.blocked {
    color: #ffd8a8;
    border-color: rgba(216, 170, 85, 0.24);
    background: rgba(216, 170, 85, 0.1);
  }

  .worktree-decision-lane.backup {
    color: #f0c979;
    border-color: rgba(216, 170, 85, 0.24);
    background: rgba(216, 170, 85, 0.08);
  }

  .worktree-decision-lane.cleanup {
    color: #7ce5d5;
    border-color: rgba(92, 226, 207, 0.25);
    background: rgba(92, 226, 207, 0.08);
  }

  .worktree-decision-lane.review {
    color: #cbd3d1;
    border-color: rgba(255, 255, 255, 0.1);
    background: rgba(255, 255, 255, 0.045);
  }

  .worktree-decision-lane.protected {
    color: #9fa9a6;
    border-color: rgba(255, 255, 255, 0.08);
    background: rgba(255, 255, 255, 0.035);
  }

  /*
   * Row-action chrome. The .activity-row-actions / .row-action-menu /
   * .row-action-menu-anchor bases live in src/app.css (:global); the
   * worktree-specific descendants below stay scoped to this component.
   */
  .activity-row-actions button {
    display: grid;
    place-items: center;
    width: 23px;
    height: 23px;
    padding: 0;
    color: #91a19d;
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 7px;
    background: rgba(255, 255, 255, 0.035);
    cursor: pointer;
  }

  .activity-row-actions button:disabled {
    opacity: 0.38;
    cursor: default;
  }

  .activity-row-actions button.worktree-primary-action.backup {
    color: #d8aa55;
    border-color: rgba(216, 170, 85, 0.2);
    background: rgba(216, 170, 85, 0.08);
  }

  .activity-row-actions button.worktree-primary-action.cleanup {
    color: #79eadb;
    border-color: rgba(92, 226, 207, 0.28);
    background: rgba(92, 226, 207, 0.1);
  }

  .activity-row-actions button:hover,
  .activity-row-actions button:focus-visible {
    color: #eaf5f2;
    border-color: rgba(92, 226, 207, 0.36);
    outline: 0;
    background: rgba(92, 226, 207, 0.12);
  }

  .row-action-menu button {
    display: grid;
    grid-template-columns: 15px minmax(0, 1fr);
    align-items: center;
    justify-content: stretch;
    gap: 7px;
    width: 100%;
    height: 26px;
    padding: 0 7px;
    color: #cbd3d1;
    border: 0;
    border-radius: 5px;
    background: transparent;
    font-size: 11px;
    font-weight: 730;
    text-align: left;
  }

  .row-action-menu button:hover:not(:disabled),
  .row-action-menu button:focus-visible {
    color: #f2f6f5;
    outline: 0;
    background: rgba(92, 226, 207, 0.1);
  }

  .row-action-menu button span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /*
   * Open-menu reflow rules live in `src/app.css` as global rules: their page
   * selectors include the `.activity-panel-list` ancestor (which the PAGE renders,
   * not this component), so they can't be scoped here — see the
   * "ActivityWorktreesPanel — .activity-panel-list ancestor combos" block there
   * (same `:global` pattern Task 1 used for the BrowserPanel dock shells).
   */

  /* .git-task-link base lives in src/app.css (:global). */
</style>
