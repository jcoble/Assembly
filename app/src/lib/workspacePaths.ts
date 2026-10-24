/** A workspace path carries its machine so late reads cannot switch hosts. */
const remotePrefix = 'assembly-remote://';
export function remoteWorkspacePath(profileId: string, path: string): string {
  if (!profileId || !path.startsWith('/')) throw new Error('Remote workspace needs a saved machine and absolute path');
  return `${remotePrefix}${encodeURIComponent(profileId)}${path}`;
}
export function parseRemoteWorkspacePath(path: string): { profileId: string; path: string } | null {
  if (!path.startsWith(remotePrefix)) return null;
  const slash = path.indexOf('/', remotePrefix.length);
  if (slash < 0) throw new Error('Invalid remote workspace path');
  return { profileId: decodeURIComponent(path.slice(remotePrefix.length, slash)), path: path.slice(slash) };
}

/** The path as its own machine knows it, for copying; a Mac path comes back unchanged. */
export function nativeWorkspacePath(path: string): string {
  return parseRemoteWorkspacePath(path)?.path ?? path;
}
/** Finder only shows files on this Mac, so a remote file says why Reveal is off. */
export function revealDisabledReason(path: string): string | null {
  return parseRemoteWorkspacePath(path) ? 'This file is on a remote machine, so Finder cannot show it.' : null;
}

/** Resolve a provider file-edit path against the session folder that owns it. */
export function workspaceChangePath(root: string, path: string): string {
  const trimmed = path.trim();
  if (!trimmed || trimmed.startsWith(remotePrefix)) return trimmed;
  const remote = parseRemoteWorkspacePath(root);
  if (remote) {
    const nativePath = trimmed.startsWith('/')
      ? trimmed
      : `${remote.path.replace(/\/+$/, '')}/${trimmed.replace(/^\/+/, '')}`;
    return remoteWorkspacePath(remote.profileId, nativePath);
  }
  if (trimmed.startsWith('/')) return trimmed;
  return root ? `${root.replace(/\/+$/, '')}/${trimmed.replace(/^\/+/, '')}` : trimmed;
}
/** The folder a session's files are browsed from: its project's root on the session's machine, else its own cwd. */
export function sessionWorkspaceRoot(
  session: { cwd: string; projectId: string | null; executionEnvironment?: string; remoteProfileId?: string | null },
  projects: readonly { id: string; machine: string; rootPath: string }[]
): string {
  const machine = session.executionEnvironment === 'remote' ? session.remoteProfileId : 'local';
  const project = projects.find((candidate) => candidate.id === session.projectId && candidate.machine === machine);
  const path = project?.rootPath.trim() || session.cwd.trim();
  if (session.executionEnvironment !== 'remote') return path;
  return path && session.remoteProfileId ? remoteWorkspacePath(session.remoteProfileId, path) : '';
}

/** The folder a session's agent works in. A project roots the files, but a checkout change moves this one, and Source Control follows it. */
export function sessionCheckoutRoot(
  session: { cwd: string; projectId: string | null; executionEnvironment?: string; remoteProfileId?: string | null }
): string {
  return sessionWorkspaceRoot(session, []);
}

type WorkingSession = { cwd: string; executionEnvironment: string; remoteProfileId?: string | null; state: string; runtimeState?: string; title?: string | null };
/** A running session's working folder on its machine, without a trailing slash; null when it is not running. */
export function runningSessionFolder(session: WorkingSession): string | null {
  if (session.state === 'exited' || ['suspended', 'closed', 'failed'].includes(session.runtimeState ?? '')) return null;
  const remote = session.executionEnvironment === 'remote';
  if (remote && (!session.remoteProfileId || !session.cwd.startsWith('/'))) return null;
  return (remote ? remoteWorkspacePath(session.remoteProfileId!, session.cwd) : session.cwd).replace(/\/+$/, '');
}
/** Throws when `path` is outside `sessionRoot` and a running session works in the folder that holds it. */
export function assertSaveAllowed(path: string, sessionRoot: string, sessions: readonly WorkingSession[]): void {
  const within = (folder: string): boolean => folder !== '' && (path === folder || path.startsWith(`${folder}/`));
  if (within(sessionRoot.replace(/\/+$/, ''))) return;
  const holder = sessions.find((session) => within(runningSessionFolder(session) ?? ''));
  if (holder) throw new Error(`${holder.title ? `"${holder.title}"` : 'Another session'} is working in this folder, so this file can't be saved here.`);
}

const pathKeys = new Set(['root', 'path', 'directory', 'source', 'target', 'roots', 'paths', 'projectRoot', 'projectPath', 'worktreePath', 'checkoutPath', 'gitRoot', 'localRoot', 'projectFilter']);
/** Hosted PR commands wrap their root in one request object. */
const nestedRequestCommands = new Set(['list_github_pull_requests', 'read_github_pull_request_file', 'submit_github_pull_request_review', 'reply_github_pull_request_comment', 'comment_github_pull_request', 'merge_github_pull_request']);
export function remoteWorkspaceRequest(args: Record<string, unknown>, command = ''): { profileId: string; args: Record<string, unknown> } | null {
  let profileId: string | null = null;
  let localAbsolute = false;
  const decode = (value: unknown): unknown => {
    if (Array.isArray(value)) return value.map(decode);
    if (typeof value !== 'string') return value;
    const remote = parseRemoteWorkspacePath(value);
    if (!remote) { if (value.startsWith('/')) localAbsolute = true; return value; }
    if (profileId && profileId !== remote.profileId) throw new Error('A workspace operation cannot cross machines');
    profileId = remote.profileId;
    return remote.path;
  };
  const decodeKeys = (fields: Record<string, unknown>): Record<string, unknown> =>
    Object.fromEntries(Object.entries(fields).map(([key, value]) => [key, pathKeys.has(key) ? decode(value) : value]));
  const nested = nestedRequestCommands.has(command);
  const result = Object.fromEntries(Object.entries(decodeKeys(args)).map(([key, value]) => [
    key,
    nested && value && typeof value === 'object' && !Array.isArray(value) ? decodeKeys(value as Record<string, unknown>) : value
  ]));
  if (profileId && localAbsolute) throw new Error('A workspace operation cannot mix Mac and remote paths');
  return profileId ? { profileId, args: result } : null;
}
export function qualifyWorkspaceResult(value: unknown, profileId: string, key = ''): unknown {
  if (typeof value === 'string') return pathKeys.has(key) && value.startsWith('/') ? remoteWorkspacePath(profileId, value) : value;
  if (Array.isArray(value)) return value.map((item) => qualifyWorkspaceResult(item, profileId, key));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([name, item]) => [name.startsWith('/') ? remoteWorkspacePath(profileId, name) : name, qualifyWorkspaceResult(item, profileId, name)]));
  return value;
}

/**
 * SQLite keeps each path on the session's own machine (`profileId`, null for the Mac) native, so
 * older records load unchanged. A path on another machine keeps its `assembly-remote://` form, and
 * a Mac path in a remote session's record is saved as `file://<path>`. That record is kept by the
 * remote machine, which must not be able to open Mac files, so loading it drops every Mac path.
 */
export function mapWorkspaceSnapshotPaths(value: unknown, profileId: string | null, direction: 'load' | 'save' = 'load', key = ''): unknown {
  const keys = new Set(['openPaths', 'activePath', 'selectedPath', 'diffRoot', 'filesInspectionRoot', 'sourceControlInspectionRoot', 'root', 'path', 'viewedProjectRoot']);
  // Null marks a dropped Mac path: it clears a field and leaves arrays and objects.
  const mapPath = (path: string): string | null => {
    const remote = parseRemoteWorkspacePath(path);
    if (direction === 'save') return remote ? (remote.profileId === profileId ? remote.path : path) : profileId && path.startsWith('/') ? `file://${path}` : path;
    if (path.startsWith('file:///')) return profileId ? null : path.slice(7);
    return profileId && path.startsWith('/') ? remoteWorkspacePath(profileId, path) : path;
  };
  if (typeof value === 'string') {
    // Top-row editor tabs are keyed 'editor:<path>' and must follow openPaths.
    if ((key === 'order' || key === 'activeKey') && value.startsWith('editor:')) {
      const path = mapPath(value.slice(7));
      return path === null ? null : `editor:${path}`;
    }
    return keys.has(key) || key === 'expandedPathsByRoot' ? mapPath(value) : value;
  }
  if (Array.isArray(value)) return value
    .map((item) => mapWorkspaceSnapshotPaths(item, profileId, direction, key))
    .filter((item, index) => item !== null || typeof value[index] !== 'string');
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).flatMap(([name, item]) => {
    const mapped = name.startsWith('/') || name.startsWith(remotePrefix) || name.startsWith('file:///') ? mapPath(name) : name;
    return mapped === null ? [] : [[mapped, mapWorkspaceSnapshotPaths(item, profileId, direction, key === 'expandedPathsByRoot' ? key : name)]];
  }));
  return value;
}
