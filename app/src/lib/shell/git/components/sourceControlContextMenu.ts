/** Pure snapshots and placement for Source Control row context menus. */

import type { ProjectGitFileStatus } from '../../../tauriSource.ts';
import type { GitCommitFileChange } from '../gitBackendExtra.ts';

export type SourceControlFileAction =
  | 'open-diff'
  | 'stage'
  | 'unstage'
  | 'discard'
  | 'open-file'
  | 'copy-path';

export type SourceControlFileHandler =
  | 'pick-file'
  | 'run-file-action'
  | 'ask-to-discard'
  | 'open-in-editor'
  | 'copy-path';

export interface SourceControlContextMenuItem<Action extends string, Handler extends string> {
  id: Action;
  label: string;
  enabled: boolean;
  handler: Handler;
}

export type SourceControlCommitFileAction =
  | 'open-commit-diff'
  | 'open-current-file'
  | 'copy-commit-path';

export type SourceControlContextMenuAction =
  | SourceControlFileAction
  | SourceControlCommitAction
  | SourceControlCommitFileAction;

export interface SourceControlContextMenuAnchor {
  left: number;
  right: number;
  top: number;
  bottom: number;
  containingBlockLeft: number;
  containingBlockRight: number;
  containingBlockTop: number;
  containingBlockBottom: number;
}

export interface SourceControlContextMenuSnapshot {
  key: string;
  anchor: SourceControlContextMenuAnchor;
  items: SourceControlContextMenuItem<SourceControlContextMenuAction, string>[];
}

export interface SourceControlCommitMenuSnapshot extends SourceControlContextMenuSnapshot {
  kind: 'commit';
  target: { sha: string; isMerge: boolean };
}

export interface SourceControlFileMenuSnapshot extends SourceControlContextMenuSnapshot {
  kind: 'file';
  target: {
    groupAction: 'stage' | 'unstage';
    file: ProjectGitFileStatus;
  };
}

export interface SourceControlCommitFileMenuSnapshot extends SourceControlContextMenuSnapshot {
  kind: 'commit-file';
  target: { sha: string; file: GitCommitFileChange };
}

export type SourceControlMenuSnapshot =
  | SourceControlCommitMenuSnapshot
  | SourceControlFileMenuSnapshot
  | SourceControlCommitFileMenuSnapshot;

const MENU_GAP = 4;
const EDGE_PADDING = 8;

export function sourceControlContextMenuAnchor(event: MouseEvent): SourceControlContextMenuAnchor {
  const target = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
  const containingBlock = target?.closest<HTMLElement>('.dv-render-overlay');
  const containingBlockRect = containingBlock?.getBoundingClientRect();
  return {
    left: event.clientX,
    right: event.clientX,
    top: event.clientY,
    bottom: event.clientY,
    containingBlockLeft: containingBlockRect?.left ?? 0,
    containingBlockRight: containingBlockRect?.right ?? window.innerWidth,
    containingBlockTop: containingBlockRect?.top ?? 0,
    containingBlockBottom: containingBlockRect?.bottom ?? window.innerHeight
  };
}

/** Place once from the hidden menu's measured size, clamped to its Dockview block. */
export function placeSourceControlContextMenu(
  anchor: SourceControlContextMenuAnchor,
  menu: { width: number; height: number }
): { left: number; top: number } {
  const minLeft = anchor.containingBlockLeft + EDGE_PADDING;
  const maxLeft = Math.max(minLeft, anchor.containingBlockRight - menu.width - EDGE_PADDING);
  const minTop = anchor.containingBlockTop + EDGE_PADDING;
  const maxTop = Math.max(minTop, anchor.containingBlockBottom - menu.height - EDGE_PADDING);
  const preferredLeft = anchor.right + MENU_GAP;
  const preferredTop = anchor.bottom + MENU_GAP;
  const viewportLeft = Math.min(maxLeft, Math.max(minLeft, preferredLeft));
  const viewportTop = Math.min(maxTop, Math.max(minTop, preferredTop));
  return {
    left: viewportLeft - anchor.containingBlockLeft,
    top: viewportTop - anchor.containingBlockTop
  };
}

export interface SourceControlFileMenuOptions {
  groupAction: 'stage' | 'unstage';
  canWrite: boolean;
  busy: boolean;
  deleted: boolean;
  hasRoot: boolean;
}

/**
 * Keep the menu tied to the controls already on the changed-file row. The
 * handler names are deliberate testable receipts: the menu dispatcher calls
 * the same component functions as the left-click row and icon buttons.
 */
export function sourceControlFileContextMenuItems(
  options: SourceControlFileMenuOptions
): SourceControlContextMenuItem<SourceControlFileAction, SourceControlFileHandler>[] {
  const indexAction = options.groupAction;
  return [
    {
      id: 'open-diff',
      label: 'Open diff',
      enabled: !options.deleted,
      handler: 'pick-file'
    },
    {
      id: indexAction,
      label: indexAction === 'stage' ? 'Stage changes' : 'Unstage changes',
      enabled: options.canWrite && !options.busy,
      handler: 'run-file-action'
    },
    {
      id: 'discard',
      // "Discard changes" is git's own phrase and the one the confirmation
      // repeats. The item is never disabled for being destructive — it is
      // disabled only when the repository cannot be written to at all, because
      // an item you cannot press teaches nothing about what it would do.
      label: 'Discard changes…',
      enabled: options.canWrite && !options.busy,
      handler: 'ask-to-discard'
    },
    {
      id: 'open-file',
      label: 'Open file in editor',
      enabled: options.hasRoot && !options.deleted,
      handler: 'open-in-editor'
    },
    {
      id: 'copy-path',
      label: 'Copy path',
      enabled: true,
      handler: 'copy-path'
    }
  ];
}

export type SourceControlCommitAction = 'toggle-commit' | 'copy-hash';
export type SourceControlCommitHandler = 'toggle-commit' | 'copy-hash';

export function sourceControlCommitContextMenuItems(
  expanded: boolean
): SourceControlContextMenuItem<SourceControlCommitAction, SourceControlCommitHandler>[] {
  return [
    {
      id: 'toggle-commit',
      label: expanded ? 'Hide changed files' : 'View changed files',
      enabled: true,
      handler: 'toggle-commit'
    },
    {
      id: 'copy-hash',
      label: 'Copy commit hash',
      enabled: true,
      handler: 'copy-hash'
    }
  ];
}

export function sourceControlCommitFileContextMenuItems(
  readable: boolean
): SourceControlContextMenuItem<SourceControlCommitFileAction, SourceControlCommitFileAction>[] {
  return [
    {
      id: 'open-commit-diff',
      label: 'Open diff',
      enabled: readable,
      handler: 'open-commit-diff'
    },
    {
      id: 'open-current-file',
      label: 'Open current file in editor',
      enabled: readable,
      handler: 'open-current-file'
    },
    {
      id: 'copy-commit-path',
      label: 'Copy path',
      enabled: true,
      handler: 'copy-commit-path'
    }
  ];
}

export function snapshotSourceControlCommitMenu(input: {
  sha: string;
  isMerge: boolean;
  expanded: boolean;
  anchor: SourceControlContextMenuAnchor;
}): SourceControlCommitMenuSnapshot {
  return {
    kind: 'commit',
    key: `commit:${input.sha}:${input.expanded ? 'open' : 'closed'}`,
    target: { sha: input.sha, isMerge: input.isMerge },
    anchor: { ...input.anchor },
    items: sourceControlCommitContextMenuItems(input.expanded)
  };
}

export function snapshotSourceControlFileMenu(input: {
  groupAction: 'stage' | 'unstage';
  file: ProjectGitFileStatus;
  canWrite: boolean;
  busy: boolean;
  hasRoot: boolean;
  anchor: SourceControlContextMenuAnchor;
}): SourceControlFileMenuSnapshot {
  const file = { ...input.file };
  return {
    kind: 'file',
    key: `file:${input.groupAction}:${file.relativePath}`,
    target: { groupAction: input.groupAction, file },
    anchor: { ...input.anchor },
    items: sourceControlFileContextMenuItems({
      groupAction: input.groupAction,
      canWrite: input.canWrite,
      busy: input.busy,
      deleted: file.badge === 'D' || file.worktreeStatus === 'deleted',
      hasRoot: input.hasRoot
    })
  };
}

export function snapshotSourceControlCommitFileMenu(input: {
  sha: string;
  file: GitCommitFileChange;
  readable: boolean;
  anchor: SourceControlContextMenuAnchor;
}): SourceControlCommitFileMenuSnapshot {
  return {
    kind: 'commit-file',
    key: `commit-file:${input.sha}:${input.file.relativePath}`,
    target: { sha: input.sha, file: { ...input.file } },
    anchor: { ...input.anchor },
    items: sourceControlCommitFileContextMenuItems(input.readable)
  };
}
