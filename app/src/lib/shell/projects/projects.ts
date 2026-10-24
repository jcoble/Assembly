/**
 * Plain rules for the project registry as the new-session screen shows it.
 * The records themselves live in SQLite; see `projectRegistry.svelte.ts`.
 */

import type { ProjectRecord, RepositoryCheckout } from '../../tauriSource.ts';
import { parseRemoteWorkspacePath, remoteWorkspacePath, runningSessionFolder } from '../../workspacePaths.ts';
import { formatAge } from '../relativeTime.ts';

/**
 * Two letters for a project: the first letters of its first and last word, or
 * the first and last letter of a single word (mac-command-bar → MB,
 * EdiPlatform → EM).
 */
export function projectBadge(title: string): string {
  const words = title.split(/[^A-Za-z0-9]+/).filter(Boolean);
  if (words.length > 1) return `${words[0][0]}${words.at(-1)![0]}`.toUpperCase();
  const word = words[0] ?? '';
  return (word.length > 1 ? `${word[0]}${word.at(-1)}` : word).toUpperCase();
}

/** Projects on this Mac or a saved remote machine, in registry order. */
export function visibleProjects(
  projects: readonly ProjectRecord[],
  savedProfileIds: readonly string[]
): ProjectRecord[] {
  return projects.filter((project) => project.machine === 'local' || savedProfileIds.includes(project.machine));
}

/** Projects on `machine` (all machines when null) whose name or folder contains `query`, ignoring case. */
export function filterProjects(
  projects: readonly ProjectRecord[],
  machine: string | null,
  query: string
): ProjectRecord[] {
  const needle = query.trim().toLowerCase();
  return projects.filter((project) => (machine === null || project.machine === machine)
    && (!needle || project.title.toLowerCase().includes(needle) || project.rootPath.toLowerCase().includes(needle)));
}

/** How many projects each machine has, keyed by machine id. */
export function projectCountsByMachine(projects: readonly ProjectRecord[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const project of projects) counts[project.machine] = (counts[project.machine] ?? 0) + 1;
  return counts;
}

/** A folder under a home directory, shown from `~`. */
export function shortProjectPath(path: string): string {
  return path.replace(/^\/(?:home|Users)\/[^/]+(?=\/|$)/, '~');
}

/** A path longer than `max` characters, cut in the middle so its start and its last folder stay readable. */
export function middleTruncatePath(path: string, max: number): string {
  if (path.length <= max) return path;
  const last = path.slice(path.lastIndexOf('/'));
  const keep = max - last.length - 1;
  return keep > 0 ? `${path.slice(0, keep)}…${last}` : `…${last}`;
}

/** When the project was last used, as the short age the app prints elsewhere ("5m", "2h", "3d"); empty if never. */
export function projectLastUsedLabel(lastUsedMs: number | null, now: Date): string {
  return lastUsedMs === null ? '' : formatAge(new Date(lastUsedMs).toISOString(), now);
}

type DraftOwnedSession = { ownedId: string; projectId: string | null; lastActivity: string | null };

/**
 * The project a new draft starts in: the active session's, else the most
 * recently active session's, else none. Only projects in `projects` count.
 */
export function defaultDraftProjectId(
  projects: readonly ProjectRecord[],
  owned: readonly DraftOwnedSession[],
  activeOwnedId: string | null
): string | null {
  const visible = (id: string | null): id is string => Boolean(id) && projects.some((project) => project.id === id);
  const active = owned.find((session) => session.ownedId === activeOwnedId);
  if (active && visible(active.projectId)) return active.projectId;
  const recent = owned
    .filter((session) => visible(session.projectId))
    .sort((a, b) => (Date.parse(b.lastActivity ?? '') || 0) - (Date.parse(a.lastActivity ?? '') || 0));
  return recent[0]?.projectId ?? null;
}

export function projectMachineLabel(
  machine: string,
  profiles: readonly { id: string; name: string }[]
): string {
  if (machine === 'local') return 'This Mac';
  return profiles.find((profile) => profile.id === machine)?.name ?? 'Remote machine';
}

/**
 * Whether the Add project path names a folder to create: its parent is the
 * listed folder and a direct check confirmed the path does not exist.
 */
export function isNewFolderPath(
  path: string,
  listing: { path: string; directories: readonly string[] } | null,
  pathExists: boolean
): boolean {
  const trimmed = path.trim().replace(/\/+$/, '');
  const slash = trimmed.lastIndexOf('/');
  const name = trimmed.slice(slash + 1);
  const parent = trimmed.slice(0, slash) || '/';
  return Boolean(listing) && slash >= 0 && parent === listing!.path
    && name !== '' && name !== '.' && name !== '..' && !pathExists;
}

/** A folder the right panel can show instead of the session's: a project's root or one of its other worktrees.
 * `path` carries its machine; `folder` is the same folder as that machine names it. */
export type OtherProjectOption = { projectId: string; path: string; folder: string; label: string; detail: string; worktree: boolean };

const trimSlash = (path: string): string => path.replace(/\/+$/, '');

/**
 * Projects on this Mac, then on each connected saved machine, each followed by its other worktrees.
 * Paths carry their machine; `checkouts` is keyed by that machine-qualified project root.
 */
export function otherProjectGroups(
  projects: readonly ProjectRecord[],
  profiles: readonly { id: string; name: string }[],
  connections: Readonly<Record<string, string>>,
  checkouts: Readonly<Record<string, readonly RepositoryCheckout[]>>
): { machine: string; label: string; options: OtherProjectOption[] }[] {
  const machines = ['local', ...profiles.filter((profile) => connections[profile.id] === 'connected').map((profile) => profile.id)];
  return machines.map((machine) => {
    const options = projects.filter((project) => project.machine === machine).flatMap((project) => {
      const root = machine === 'local' ? project.rootPath : remoteWorkspacePath(machine, project.rootPath);
      const worktrees = (checkouts[root] ?? []).filter((checkout) => trimSlash(checkout.path) !== trimSlash(root)).map((checkout) => {
        const native = parseRemoteWorkspacePath(checkout.path)?.path ?? checkout.path;
        const folder = trimSlash(native).split('/').at(-1) ?? native;
        const branch = checkout.branch.trim();
        return { projectId: project.id, path: checkout.path, folder: native, label: branch && branch !== folder ? `${folder} — ${branch}` : folder, detail: shortProjectPath(native), worktree: true };
      });
      return [{ projectId: project.id, path: root, folder: project.rootPath, label: project.title, detail: shortProjectPath(project.rootPath), worktree: false }, ...worktrees];
    });
    return { machine, label: projectMachineLabel(machine, profiles), options };
  }).filter((group) => group.options.length > 0);
}

/** The running session whose working folder is `folder`, inside it, or holds it, on the same machine; null when there is none. */
export function sessionWorkingIn<S extends { cwd: string; executionEnvironment: string; remoteProfileId?: string | null; state: string; runtimeState?: string }>(
  folder: string,
  sessions: readonly S[]
): S | null {
  const target = trimSlash(folder);
  return sessions.find((session) => {
    const cwd = runningSessionFolder(session);
    return cwd !== null && (cwd === target || cwd.startsWith(`${target}/`) || target.startsWith(`${cwd}/`));
  }) ?? null;
}

export function moveProjectDestinations(
  projects: readonly ProjectRecord[],
  session: { executionEnvironment?: string; remoteProfileId?: string | null }
): ProjectRecord[] {
  const machine = session.executionEnvironment === 'remote' ? session.remoteProfileId : 'local';
  return machine ? filterProjects(projects, machine, '') : [];
}
