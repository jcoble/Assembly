/**
 * Files feature RPC wrappers over `.NET` `/socket` and `/socket/bulk`.
 * Uses shared `backend` and `runChunkedUpload` primitives so transport logic stays centralized.
 */

import { backend } from '$lib/infrastructure/backend';
import { runChunkedUpload } from '$lib/infrastructure/bulkTransfer';

export interface FileEntry {
  path: string;
  name: string;
  isDirectory: boolean;
  excluded: boolean;
}

export interface FileListing {
  entries: FileEntry[];
  truncated: boolean;
}

export interface FileChunk {
  content: string;
  size: number;
  revision: string;
  lineCount: number;
  binary: boolean;
  nextOffset: number | null;
}

export interface SaveResult {
  state: string;
  revision: string | null;
  error: { code: string; message: string } | null;
}

export function listSessionFiles(
  session: string,
  dir = '',
  signal?: AbortSignal,
  key = 'files:list'
): Promise<FileListing> {
  return backend.call<FileListing>('files', 'list', { session, dir }, { signal, key });
}

export async function readSessionFile(
  session: string,
  path: string,
  signal?: AbortSignal,
  key = 'files:read'
): Promise<FileChunk> {
  const first = await backend.call<FileChunk>(
    'files',
    'read',
    { session, path, offset: 0 },
    { bulk: true, signal, key }
  );
  if (first.binary || first.nextOffset === null || first.nextOffset === undefined) {
    return first;
  }

  let content = first.content;
  let nextOffset: number | null = first.nextOffset;
  let last = first;
  try {
    while (nextOffset !== null && nextOffset !== undefined) {
      const chunk: FileChunk = await backend.call<FileChunk>(
        'files',
        'read',
        { session, path, offset: nextOffset, revision: first.revision },
        { bulk: true, signal, key }
      );
      content += chunk.content;
      nextOffset = chunk.nextOffset;
      last = chunk;
    }
  } finally {
    void backend
      .call('files', 'release', { session, path, revision: first.revision }, { bulk: true })
      .catch(() => {});
  }
  return { ...last, content, nextOffset: null };
}

export function saveSessionFile(
  session: string,
  path: string,
  content: string,
  expectedRevision: string,
  signal?: AbortSignal
): Promise<SaveResult> {
  return runChunkedUpload<SaveResult>(content, {
    begin: async (size) => {
      const res = await backend.call<{ upload: string }>(
        'files',
        'beginSave',
        { session, path, expectedRevision, size },
        { bulk: true, signal }
      );
      return res.upload;
    },
    sendChunk: async (upload, offset, data) => {
      await backend.call('files', 'saveChunk', { upload, offset, data }, { bulk: true, signal });
    },
    commit: (upload) => backend.call<SaveResult>('files', 'commitSave', { upload }, { bulk: true, signal }),
    drop: async (upload) => {
      await backend.call('files', 'dropSave', { upload }, { bulk: true });
    }
  });
}
