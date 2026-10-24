import { remoteWorkspacePath } from '../workspacePaths.ts';
import type { OwnedSession } from './ownedSessions.ts';
import type { ConversationFileLinkProvenance } from './conversation/conversationTimeline.ts';
import { normalizeConversationFileHref, splitConversationFileReference } from './conversation/conversationMessageSafety.ts';

/**
 * openFileBus.ts — cross-panel "open this file in the editor" requests.
 *
 * Pure fan-out, no IO, no Svelte: the explorer, palette, and git panels call
 * `requestOpenFile`; the editor subscribes with `onOpenFile`. A request made
 * before the editor exists is parked (last one wins) and delivered to the
 * first subscriber, so early clicks are never dropped.
 */

export interface OpenFileRequest {
  /** Absolute path of the file to open. */
  path: string;
  /** Workspace that owns the file, when the producer already knows it. */
  projectRoot?: string;
  /** Open for reading only: no editing, no saving. Set by producers that know
   * the file is not the active workspace's to change. */
  readOnly?: boolean;
  /** Single-click explorer open: reuse the one unpinned preview tab. */
  preview?: boolean;
  /** Double-click or explicit open: keep this file as a durable tab. */
  pin?: boolean;
  /** 1-based line to reveal, if any. */
  line?: number;
  /** 1-based column to reveal, if any. */
  column?: number;
}

type Listener = (request: OpenFileRequest) => void;

const listeners = new Set<Listener>();
let pending: OpenFileRequest | null = null;
let unavailableRoot: string | null = null;

export function setUnavailableOpenFileRoot(root: string | null): void {
  unavailableRoot = root?.trim().replace(/\/+$/, '') || null;
}

export function resolveConversationFilePath(path: string, sessionCwd: string): string {
  const candidate = path.trim();
  if (candidate.startsWith('/') || candidate.startsWith('~')) return candidate;
  return `${sessionCwd.replace(/\/+$/, '')}/${candidate.replace(/^\.\//, '')}`;
}

export function requestOpenFile(request: OpenFileRequest): void {
  if (
    unavailableRoot &&
    (request.path === unavailableRoot || request.path.startsWith(`${unavailableRoot}/`))
  ) {
    return;
  }
  if (listeners.size === 0) {
    pending = request;
    return;
  }
  for (const listener of listeners) listener(request);
}

/** Subscribe; returns the unsubscribe. A parked request is delivered at once. */
export function onOpenFile(listener: Listener): () => void {
  listeners.add(listener);
  if (pending) {
    const request = pending;
    pending = null;
    listener(request);
  }
  return () => {
    listeners.delete(listener);
  };
}

/** Open a transcript file using its saved workspace and file provenance. */
export function requestOpenConversationFile(
  active: OwnedSession,
  reference: string,
  provenance?: ConversationFileLinkProvenance
): string | null {
  const sessionRoot = active.cwd.replace(/\/+$/, '');
  const { path, line } = splitConversationFileReference(reference);
  const candidate = normalizeConversationFileHref(path);
  const recordedPath = provenance?.path ? normalizeConversationFileHref(provenance.path) : '';
  const linkIsRecordedFile = Boolean(recordedPath && (
    recordedPath === candidate
    || recordedPath.endsWith(`/${candidate.replace(/^\.\//, '')}`)
  ));
  const recordedRoot = provenance?.root
    ? normalizeConversationFileHref(provenance.root)
    : !linkIsRecordedFile && recordedPath.includes('/')
      ? recordedPath.slice(0, recordedPath.lastIndexOf('/'))
      : '';
  const recordedRootPath = recordedRoot
    ? resolveConversationFilePath(recordedRoot, sessionRoot).replace(/\/+$/, '')
    : '';
  const root = recordedRootPath || sessionRoot;
  if (!root || !candidate || candidate.includes('\0') || candidate.split('/').includes('..')) {
    // Nothing here resolves to a file, so there is nothing to open.
    return 'That file link could not be opened.';
  }
  const absolute = linkIsRecordedFile && (recordedPath.startsWith('/') || recordedPath.startsWith('~'))
    ? recordedPath
    : resolveConversationFilePath(candidate, root);
  // A link that lands outside the workspace still opens, read-only: reading a
  // file this session does not own is safe, and refusing it left the reader
  // with a notice and no way to see what the link pointed at.
  const outside = absolute !== root && !absolute.startsWith(`${root}/`);
  const readOnly = outside || Boolean(recordedRootPath && recordedRootPath !== sessionRoot);
  if (active.executionEnvironment === 'remote' && !active.remoteProfileId) {
    return 'Connect to this conversation’s saved machine first.';
  }
  const qualify = (path: string): string => active.executionEnvironment === 'remote'
    ? remoteWorkspacePath(active.remoteProfileId!, path) : path;
  requestOpenFile({
    path: qualify(absolute),
    projectRoot: qualify(readOnly ? sessionRoot : root),
    readOnly,
    line
  });
  return null;
}
