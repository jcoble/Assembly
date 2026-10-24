/**
 * The right-click roster for a session row.
 *
 * The row draws this list and runs the actions; this module decides what is on
 * it and what is switched off. An item is switched off when the app has no way
 * to carry it out yet — it still shows, with a plain sentence saying why, so
 * the menu does not change shape from row to row.
 */

import type { MyWorkStatus } from './myWorkViewOptions.ts';

export type SessionRowMenuAction =
  | 'rename'
  | 'move-to-project'
  | 'pin'
  | 'unpin'
  | 'mark-orchestrator'
  | 'unmark-orchestrator'
  | 'mark-done'
  | 'reopen'
  | 'archive'
  | 'unsettle'
  | 'copy-deeplink'
  | 'copy-session-id'
  | 'copy-resume-command'
  | 'copy-worktree-path'
  | 'continue-in-new-session'
  | 'open-in-editor'
  | 'open-source-control'
  | 'reveal-in-finder'
  | 'delete';

export interface SessionRowMenuItem {
  id: SessionRowMenuAction;
  label: string;
  enabled: boolean;
  /** Why the item is off. Shown as its tooltip; absent when it is on. */
  disabledReason?: string;
  /** The tooltip when the item is on, if it should say more than the label. */
  hint?: string;
  /** A destructive item, drawn in the warning tone. */
  destructive?: boolean;
  /** A rule is drawn above this item. */
  startsGroup?: boolean;
}

export interface SessionRowMenuInput {
  /** Where the session sits in the lifecycle right now. */
  status: MyWorkStatus;
  /** The id a person would paste elsewhere, when the session has one. */
  sessionId: string | null;
  /** The command that resumes the session in its provider's CLI, when there is one. */
  resumeCommand: string | null;
  /** The checkout the session runs in, when it has one. */
  worktreePath: string | null;
  /** Whether the session is pinned to the top of the rail. */
  pinned: boolean;
  /** Whether the owner marked this session as the main orchestrator. */
  orchestrator: boolean;
  /** False for a helper or a workflow worker, which cannot be the main orchestrator. */
  topLevel: boolean;
}

const NO_BACKEND = 'Not available yet';

export function sessionRowMenuItems(input: SessionRowMenuInput): SessionRowMenuItem[] {
  const items: SessionRowMenuItem[] = [
    { id: 'rename', label: 'Rename', enabled: true },
    { id: 'move-to-project', label: 'Move to project…', enabled: true },
    input.pinned
      ? { id: 'unpin', label: 'Unpin', enabled: true }
      : { id: 'pin', label: 'Pin to top', enabled: true },
    input.orchestrator
      ? { id: 'unmark-orchestrator', label: 'Unmark main orchestrator', enabled: true }
      : input.topLevel
        ? {
            id: 'mark-orchestrator',
            label: 'Make main orchestrator',
            enabled: true,
            hint: 'The session you mostly talk to; it can reach every other top-level session'
          }
        : {
            id: 'mark-orchestrator',
            label: 'Make main orchestrator',
            enabled: false,
            disabledReason: 'Only a top-level session can be the main orchestrator'
          }
  ];

  // The lifecycle moves the rail already performs, in the order a session
  // travels: Working to Done to Settled, and back again.
  if (input.status === 'working') {
    items.push({ id: 'mark-done', label: 'Mark done', enabled: true });
  }
  if (input.status === 'done') {
    items.push({ id: 'reopen', label: 'Move back to Working', enabled: true });
  }
  if (input.status === 'settled') {
    items.push({ id: 'unsettle', label: 'Move back to Done', enabled: true });
  } else {
    items.push({ id: 'archive', label: 'Archive (Settle)', enabled: true });
  }

  items.push(
    {
      id: 'copy-deeplink',
      label: 'Copy deeplink',
      enabled: true,
      hint: 'An assembly:// link that opens this session from anywhere',
      startsGroup: true
    },
    {
      id: 'copy-session-id',
      label: 'Copy session id',
      enabled: Boolean(input.sessionId),
      disabledReason: input.sessionId ? undefined : 'This session has no id recorded',
      hint: input.sessionId ?? undefined
    },
    {
      id: 'copy-resume-command',
      label: 'Copy resume command',
      enabled: Boolean(input.resumeCommand),
      disabledReason: input.resumeCommand ? undefined : 'This session has no resume command',
      hint: input.resumeCommand
        ? `${input.resumeCommand}\nDo not keep the session open in the app and the CLI at the same time; both would write to it.`
        : undefined
    },
    {
      id: 'copy-worktree-path',
      label: 'Copy worktree path',
      enabled: Boolean(input.worktreePath),
      disabledReason: input.worktreePath ? undefined : 'This session has no worktree recorded'
    },
    // follow-up: no command forks a session into a fresh one yet.
    {
      id: 'continue-in-new-session',
      label: 'Continue in new session',
      enabled: false,
      disabledReason: NO_BACKEND
    },
    { id: 'open-in-editor', label: 'Open in editor', enabled: true, startsGroup: true },
    // The row used to carry this as a hover button. It moved here when the row's
    // gutter came down to two, and the jump itself is unchanged.
    { id: 'open-source-control', label: 'Open source control', enabled: true },
    // follow-up: no command opens a path in Finder yet.
    {
      id: 'reveal-in-finder',
      label: 'Reveal in Finder',
      enabled: false,
      disabledReason: NO_BACKEND
    },
    { id: 'delete', label: 'Delete', enabled: true, destructive: true, startsGroup: true }
  );

  return items;
}
