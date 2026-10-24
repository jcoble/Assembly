/**
 * worktreesBackend.ts — the desktop commands the worktree manager needs, and
 * nothing else.
 *
 * This lane owns its own wrappers (the wave rule for a parallel lane), written
 * the same way `tauriSource.ts` writes its own: import `@tauri-apps/api/core`
 * only inside the call, and answer `null` when we are not running inside the
 * desktop app. `null` means "nothing was invoked" — the pane then says the
 * plain truth about where this data lives instead of inventing any.
 *
 * Three of the five calls already exist in `tauriSource.ts` and are re-used from
 * there. Two do not:
 *
 *  - **removing a worktree the destructive way.** The existing wrapper cannot
 *    send `force`, and this pane's whole point is the button that can. So the
 *    remove is written out here, and `force: true` is put on the wire ONLY when
 *    the caller explicitly asks for it.
 *  - **asking the desktop app what it can do.** An older build of the desktop
 *    app has a `remove_project_worktree` command with no `force` on it at all.
 *    Sending `force: true` to that build does NOT fail: the extra field is
 *    dropped on the way in and an ordinary, careful remove happens instead —
 *    which would refuse, while our screen claimed it had forced. There is no
 *    way to tell those two builds apart from the outcome, so the app is asked
 *    outright: `read_backend_capabilities` lists what this build supports, and
 *    the forced remove is offered only when `worktreeForceRemove` is in that
 *    list. A build too old to even have that command answers with an error,
 *    which is caught here and read as "it cannot".
 */
import {
  archiveProjectWorktreeFromTauri,
  isNativeTauriRuntime,
  listGitRepositorySummariesFromTauri,
  listProjectWorktreesFromTauri,
  type GitRepositorySummary,
  type ProjectWorktree,
  type ProjectWorktreeActionResult,
  type ProjectWorktreeArchiveResult,
  type RuntimeContextProject
} from '../../tauriSource.ts';

/** The name the desktop app uses for "this build can force-remove a worktree". */
export const WORKTREE_FORCE_REMOVE_CAPABILITY = 'worktreeForceRemove';

/**
 * The name the desktop app uses for "this build clears ONE folder-gone row".
 *
 * A build without it answers a click on a folder-gone row by running git's
 * repo-wide tidy-up, which clears every row whose folder is gone — not the one
 * that was clicked. Both builds accept exactly the same call and both answer
 * "done", so there is nothing in the outcome to tell them apart. The app is
 * asked outright, and the confirmation dialog says which of the two will happen
 * before anybody presses anything.
 */
export const WORKTREE_PRUNE_SINGLE_CAPABILITY = 'worktreePruneSingle';

/** List the worktrees of the repository `root` belongs to. */
export async function listWorktrees(root: string): Promise<ProjectWorktree[] | null> {
  return await listProjectWorktreesFromTauri(root);
}

/** Read each project's repository summary — the only source of ahead/behind. */
export async function listRepositorySummaries(
  projects: RuntimeContextProject[]
): Promise<GitRepositorySummary[] | null> {
  return await listGitRepositorySummariesFromTauri(projects);
}

/** Take a full, recoverable backup of a worktree before anything is removed. */
export async function archiveWorktree(
  root: string,
  path: string
): Promise<ProjectWorktreeArchiveResult | null> {
  return await archiveProjectWorktreeFromTauri(root, path);
}

/**
 * Remove a worktree.
 *
 * `force` is left off the wire entirely unless it is `true`, so the everyday
 * remove is byte-for-byte the call the app has always made: it refuses anything
 * that would lose work, and that refusal is the safety net.
 */
export async function removeWorktree(
  root: string,
  path: string,
  force = false
): Promise<ProjectWorktreeActionResult | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  const args: { root: string; path: string; force?: boolean } = { root, path };
  if (force) args.force = true;
  return invoke<ProjectWorktreeActionResult>('remove_project_worktree', args);
}

/**
 * What this build of the desktop app can do, as a list of names.
 *
 * `null` means we are not in the desktop app. An empty list means the app is
 * running but is too old to answer the question — which is itself the answer,
 * so the error is swallowed here rather than shown to anyone.
 */
export async function readBackendCapabilities(): Promise<string[] | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  try {
    const capabilities = await invoke<string[]>('read_backend_capabilities');
    return Array.isArray(capabilities) ? capabilities.filter((name) => typeof name === 'string') : [];
  } catch {
    // A build that has never heard of the question cannot do the new thing.
    return [];
  }
}
