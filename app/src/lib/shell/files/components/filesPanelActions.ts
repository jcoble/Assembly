/**
 * filesPanelActions.ts — the actions one row of the Files panel offers.
 *
 * Six on a folder and four on a file. The two that put something new on disk
 * need somewhere to put it, so they belong to folders only; the other four
 * apply to anything the tree can show.
 *
 * A file gets a four-item menu rather than a six-item one with two entries
 * greyed out: "New file" inside a file is not a thing that could ever apply,
 * and an item that is off for a reason nobody can fix teaches nothing.
 *
 * Pure: no DOM, no backend, no clipboard. The panel maps an id onto the real
 * work, so this list can be checked in a plain Node test.
 */
import type { FileTreeNode } from './fileTreeModel.ts';

export type FilesPanelActionId =
  | 'new-file'
  | 'new-folder'
  | 'rename'
  | 'delete'
  | 'reveal-in-finder'
  | 'copy-path';

export interface FilesPanelAction {
  id: FilesPanelActionId;
  /** The words the menu draws. */
  label: string;
  /** True for the one action that changes what is on disk destructively. The
   * path goes to the Trash rather than away, so this colours the item red and
   * nothing more — there is no question to ask when the Trash is the undo. */
  destructive: boolean;
}

const ROW_ACTIONS: readonly FilesPanelAction[] = [
  { id: 'rename', label: 'Rename', destructive: false },
  { id: 'delete', label: 'Delete', destructive: true },
  { id: 'reveal-in-finder', label: 'Reveal in Finder', destructive: false },
  { id: 'copy-path', label: 'Copy path', destructive: false }
];

const FOLDER_ACTIONS: readonly FilesPanelAction[] = [
  { id: 'new-file', label: 'New file', destructive: false },
  { id: 'new-folder', label: 'New folder', destructive: false }
];

/** Every action for one row, in the order the menu draws them. */
export function filesPanelActions(node: FileTreeNode): FilesPanelAction[] {
  return node.isDirectory ? [...FOLDER_ACTIONS, ...ROW_ACTIONS] : [...ROW_ACTIONS];
}
