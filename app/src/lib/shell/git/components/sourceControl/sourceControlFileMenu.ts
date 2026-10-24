/**
 * sourceControlFileMenu.ts — the actions one changed file offers.
 *
 * Three that change the repository and five that only read it. Stage, unstage
 * and discard are offered per file because that is how a commit is actually
 * put together: a working copy usually holds two or three unrelated changes,
 * and "Stage All" can only make one commit out of all of them.
 *
 * Which of the three apply is the file's own state: a file with work git has
 * not been told about can be staged, a file with work in the index can be
 * unstaged, and anything with changes at all can have them thrown away. Each
 * action says whether it can run and, when it cannot, why — an item that is off
 * without a reason teaches nothing.
 *
 * Discarding is the one action here that destroys work, so it never runs on
 * being chosen: the panel asks first. Nothing in this file confirms anything.
 *
 * Pure: no DOM, no backend, no clipboard. The panel maps an id onto the real
 * work, so this list can be checked in a plain Node test.
 */

import type { ProjectGitFileStatus } from '../../../../tauriSource.ts';
import {
  hasGitFileUnstagedChanges,
  isGitFileDeleted,
  isGitFileStaged
} from '../../gitPanelStore.svelte.ts';
import { revealDisabledReason } from '../../../../workspacePaths.ts';

export type SourceControlFileActionId =
  | 'view'
  | 'stage'
  | 'unstage'
  | 'discard'
  | 'copy-path'
  | 'copy-relative-path'
  | 'open-in-editor'
  | 'reveal-in-finder';

export interface SourceControlFileAction {
  id: SourceControlFileActionId;
  label: string;
  enabled: boolean;
  /** Why the action is off, in plain words. Null while it is available. */
  disabledReason: string | null;
}

const NO_REPOSITORY = 'This panel is not pointed at a repository folder yet.';
const FILE_IS_GONE = 'This file was deleted, so there is nothing on disk to open.';
const NOTHING_TO_STAGE = 'Everything in this file is already staged.';
const NOTHING_TO_UNSTAGE = 'Nothing about this file is staged.';

function action(
  id: SourceControlFileActionId,
  label: string,
  reason: string | null
): SourceControlFileAction {
  return { id, label, enabled: reason === null, disabledReason: reason };
}

/** What the panel knows that the file itself does not. */
export interface SourceControlFileActionContext {
  /** False in a browser, which can read a repository but never change one. */
  canWrite: boolean;
  /** Why not, in the words the panel already uses for its buttons. */
  readOnlyReason: string;
  /** True while another source-control action is still running. */
  busy: boolean;
  /** Opening an inspected worktree must not transfer editor ownership to it. */
  canOpenInEditor?: boolean;
}

/**
 * Every action for one changed file, in the order the panel draws them.
 *
 * `root` is the repository folder the panel is pointed at; an empty one means
 * the panel has no folder yet, which leaves only the repository-relative path
 * answerable. The context decides the three write actions and nothing else: a
 * page that cannot change the repository still copies a path.
 */
export function sourceControlFileActions(
  file: ProjectGitFileStatus,
  root: string,
  context: SourceControlFileActionContext
): SourceControlFileAction[] {
  const noRoot = root.trim() === '' ? NO_REPOSITORY : null;
  const gone = isGitFileDeleted(file) ? FILE_IS_GONE : null;
  const cannotWrite = context.canWrite
    ? context.busy
      ? 'Wait for the current source-control action to finish.'
      : null
    : context.readOnlyReason;
  const write = noRoot ?? cannotWrite;
  const cannotOpenInEditor = context.canOpenInEditor === false ? context.readOnlyReason : null;

  return [
    action('view', 'View changes', noRoot ?? gone),
    action(
      'stage',
      'Stage this file',
      write ?? (hasGitFileUnstagedChanges(file) ? null : NOTHING_TO_STAGE)
    ),
    action(
      'unstage',
      'Unstage this file',
      write ?? (isGitFileStaged(file) ? null : NOTHING_TO_UNSTAGE)
    ),
    action('discard', 'Discard changes…', write),
    action('copy-path', 'Copy path', noRoot),
    action('copy-relative-path', 'Copy relative path', null),
    action('open-in-editor', 'Open in editor', noRoot ?? gone ?? cannotOpenInEditor),
    action('reveal-in-finder', 'Reveal in Finder', noRoot ?? gone ?? revealDisabledReason(root))
  ];
}
