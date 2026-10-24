/**
 * worktreeManagerService.ts — every desktop call the worktree manager makes.
 *
 * The rules this file exists to keep, the same ones `gitService.ts` and
 * `contextService.ts` keep:
 *
 *  - **Nothing runs at import, and nothing polls.** The first call happens in
 *    `activate(...)`, which the shell makes when the pane comes into view. A
 *    pane nobody opens costs nothing.
 *  - **No `$effect`.** Reads happen because the user did something: opened the
 *    pane, changed session, pressed Refresh, or finished an action.
 *  - **Every call is counted** with `countInvoke('<command name>')` right
 *    before it, so the dev invoke counter stays honest.
 *  - **No stand-in data.** A `null` from a wrapper means we are not in the
 *    desktop app; the pane then says so and shows nothing.
 *
 * The read is two calls, in order, and the order matters. The worktrees come
 * first because the first record they return IS the repository's main checkout,
 * and that is the folder the summaries have to be asked about: asking about a
 * worktree instead would enumerate the same siblings from the wrong anchor. The
 * summaries are the only place the ahead/behind counts exist, and they are
 * allowed to fail on their own — a row without them shows no remote line, which
 * is honest, rather than taking the whole pane down.
 */
import { countInvoke } from '../devInvokeCounter.svelte.ts';
import { primaryCheckoutPath, type WorktreeSessionInput } from './worktreeManagerRows.ts';
import {
  applyWorktreeLoad,
  beginWorktreeAction,
  beginWorktreeLoad,
  failWorktreeAction,
  failWorktreeLoad,
  finishWorktreeAction,
  markWorktreesUnavailable,
  setForceRemoveSupport,
  setPruneSingleRowSupport,
  setWorktreeManagerInput,
  worktreeManager
} from './worktreeManagerStore.svelte.ts';
import {
  archiveWorktree as archiveWorktreeCommand,
  listRepositorySummaries,
  listWorktrees,
  readBackendCapabilities,
  removeWorktree as removeWorktreeCommand,
  WORKTREE_FORCE_REMOVE_CAPABILITY,
  WORKTREE_PRUNE_SINGLE_CAPABILITY
} from './worktreesBackend.ts';

/** Shown when the data only exists inside the desktop app. */
export const DESKTOP_ONLY_MESSAGE =
  'Worktrees live on your machine, so this list only works in the desktop app.';

/** Shown before any session has been picked. */
export const NO_PROJECT_MESSAGE =
  'Pick a session and this will show the worktrees of the repository it belongs to.';

/** What the last `activate` was pointed at, so a repeat call costs nothing. */
let loadedKey: string | null = null;

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function folderName(path: string): string {
  const parts = path.split('/').filter(Boolean);
  return parts.length > 0 ? parts[parts.length - 1] : path;
}

/** A short description of what a read would be for, to spot a repeat call. */
function inputKey(root: string | null, sessions: WorktreeSessionInput[]): string {
  return `${root ?? ''}|${sessions.map((session) => `${session.ownedId}@${session.cwd}:${session.state}`).join(',')}`;
}

/**
 * Show the pane and read what it shows.
 *
 * Idempotent: pointed at the same folder with the same sessions it does no work
 * at all, which is what lets the shell call it every time the pane comes into
 * view. Pointed at something new it reads again.
 */
export function activate(input: {
  root: string | null;
  projectName?: string;
  sessions: WorktreeSessionInput[];
}): void {
  const key = inputKey(input.root, input.sessions);
  const firstTime = !worktreeManager.activated;
  setWorktreeManagerInput(input);
  if (!firstTime && key === loadedKey) return;
  loadedKey = key;
  if (firstTime) void askWhatTheAppCanDo();
  void refresh();
}

/** Read the worktrees again. The pane's Refresh button. */
export async function refresh(): Promise<void> {
  const ticket = beginWorktreeLoad();
  const root = (worktreeManager.root ?? '').trim();
  if (!root) {
    markWorktreesUnavailable(ticket, NO_PROJECT_MESSAGE);
    return;
  }

  let worktrees;
  try {
    countInvoke('list_project_worktrees');
    worktrees = await listWorktrees(root);
  } catch (error) {
    failWorktreeLoad(ticket, `Could not read the worktrees: ${describeError(error)}`);
    return;
  }
  if (worktrees === null) {
    markWorktreesUnavailable(ticket, DESKTOP_ONLY_MESSAGE);
    return;
  }

  // The main checkout is the anchor every other worktree hangs off, so it is
  // the folder to ask about — see the note at the top of this file.
  const primary = primaryCheckoutPath(worktrees, root);
  let repositories: Awaited<ReturnType<typeof listRepositorySummaries>> = null;
  try {
    countInvoke('list_git_repository_summaries');
    repositories = await listRepositorySummaries([
      { id: primary, name: folderName(primary), path: primary }
    ]);
  } catch {
    // Losing the summaries costs the rows their "commits to push" line and
    // nothing else, so it is not worth failing the pane over.
    repositories = null;
  }

  applyWorktreeLoad(ticket, worktrees, repositories ?? []);
}

/**
 * Ask the desktop app what it can do, once per launch.
 *
 * Both answers have to be asked for rather than tried. An older build accepts
 * the forced remove and quietly performs a careful one instead; an older build
 * also answers a folder-gone row by clearing every folder-gone row rather than
 * the one that was clicked. Neither difference shows up in the outcome. See the
 * long notes in `worktreesBackend.ts`.
 */
export async function askWhatTheAppCanDo(): Promise<void> {
  countInvoke('read_backend_capabilities');
  const capabilities = await readBackendCapabilities();
  if (capabilities === null) {
    // Not the desktop app at all; nothing here can remove anything.
    setForceRemoveSupport('unavailable');
    setPruneSingleRowSupport('unavailable');
    return;
  }
  setForceRemoveSupport(
    capabilities.includes(WORKTREE_FORCE_REMOVE_CAPABILITY) ? 'available' : 'unavailable'
  );
  setPruneSingleRowSupport(
    capabilities.includes(WORKTREE_PRUNE_SINGLE_CAPABILITY) ? 'available' : 'unavailable'
  );
}

/** The everyday remove: the desktop app refuses anything that would lose work. */
export async function removeWorktree(path: string): Promise<void> {
  const root = (worktreeManager.root ?? '').trim();
  if (!root || !path) return;
  beginWorktreeAction(path, 'remove');
  try {
    countInvoke('remove_project_worktree');
    const result = await removeWorktreeCommand(root, path);
    if (result === null) {
      failWorktreeAction(DESKTOP_ONLY_MESSAGE);
      return;
    }
    landAction(result.message, result.worktrees);
  } catch (error) {
    failWorktreeAction(`The worktree was not removed: ${describeError(error)}`);
  }
}

/**
 * Clear git's record of a worktree whose folder is already gone.
 *
 * It is the same command as the everyday remove — the desktop app recognises a
 * folder-gone worktree and clears the record instead of deleting anything — but
 * it is a separate function because what it DOES is different enough that the
 * sentence afterwards has to be different too.
 *
 * That sentence is written here rather than passed through from the desktop
 * app, because this is the one case where the app's own wording ("Pruned
 * missing worktree metadata …") is both jargon and, on an older build,
 * incomplete: it names one branch while having cleared several. What is said
 * here follows what the app told us it can do, so it is right for both builds.
 */
export async function clearWorktreeEntry(path: string, branch: string): Promise<void> {
  const root = (worktreeManager.root ?? '').trim();
  if (!root || !path) return;
  beginWorktreeAction(path, 'clear');
  try {
    countInvoke('remove_project_worktree');
    const result = await removeWorktreeCommand(root, path);
    if (result === null) {
      failWorktreeAction(DESKTOP_ONLY_MESSAGE);
      return;
    }
    // A new app says exactly what happened in plain words — including "there
    // was nothing to clear" when git's records were already gone — so its own
    // sentence is the honest one to show. Only the older build, whose message
    // is jargon and names one branch while clearing several, gets ours.
    const cleared = `Cleared git’s record of “${branch}”. Nothing on disk was touched.`;
    landAction(
      worktreeManager.pruneSingleRowSupport === 'available'
        ? result.message
        : `${cleared} This app build clears them together, so any other row whose folder was gone is cleared too.`,
      result.worktrees
    );
  } catch (error) {
    failWorktreeAction(`Nothing was cleared: ${describeError(error)}`);
  }
}

/**
 * The destructive remove. Only ever sent when the desktop app has told us it
 * understands it — see `askWhatTheAppCanDo`. Called any other way it refuses
 * here, rather than sending a request that would silently do something gentler.
 */
export async function forceRemoveWorktree(path: string): Promise<void> {
  const root = (worktreeManager.root ?? '').trim();
  if (!root || !path) return;
  if (worktreeManager.forceRemoveSupport !== 'available') {
    failWorktreeAction(
      'This copy of the desktop app cannot delete a worktree that still has work in it. Nothing was changed.'
    );
    return;
  }
  beginWorktreeAction(path, 'force-remove');
  try {
    countInvoke('remove_project_worktree');
    const result = await removeWorktreeCommand(root, path, true);
    if (result === null) {
      failWorktreeAction(DESKTOP_ONLY_MESSAGE);
      return;
    }
    landAction(result.message, result.worktrees);
  } catch (error) {
    failWorktreeAction(`The worktree was not deleted: ${describeError(error)}`);
  }
}

/** Take a full, recoverable backup of a worktree, and say where it went. */
export async function archiveWorktree(path: string): Promise<void> {
  const root = (worktreeManager.root ?? '').trim();
  if (!root || !path) return;
  beginWorktreeAction(path, 'archive');
  try {
    countInvoke('archive_project_worktree');
    const result = await archiveWorktreeCommand(root, path);
    if (result === null) {
      failWorktreeAction(DESKTOP_ONLY_MESSAGE);
      return;
    }
    landAction(`${result.message} The backup is in ${result.archivePath}.`, result.worktrees);
  } catch (error) {
    failWorktreeAction(`Nothing was backed up: ${describeError(error)}`);
  }
}

/**
 * Land an action's result. The three action commands all answer with the list
 * as it now stands, so the rows update without a second read of the machine.
 */
function landAction(message: string, worktrees: Parameters<typeof applyWorktreeLoad>[1]): void {
  finishWorktreeAction(message, worktrees);
}

/** Forget what the last activation was for. Used when the shell tears down. */
export function resetWorktreeActivation(): void {
  loadedKey = null;
}
