import {
  findReferencesKeymap,
  formatKeymap,
  LSPClient,
  hoverTooltips,
  jumpToDefinitionKeymap,
  renameKeymap,
  serverCompletion,
  serverDiagnostics,
  signatureHelp,
  type Transport
} from '@codemirror/lsp-client';
import type { Extension } from '@codemirror/state';
import { keymap } from '@codemirror/view';

import {
  ensureNativeCsharpLanguageClientFromTauri,
  markNativeCsharpLanguageClientReadyFromTauri
} from '$lib/tauriSource';

type CodeMirrorCsharpSession = {
  root: string;
  extension(path: string): Promise<Extension>;
  references(path: string, line: number, column: number, limit: number): Promise<CsharpReferenceLocation[]>;
  dispose(): void;
};

export type CsharpReferenceLocation = {
  path: string;
  line: number;
  column: number;
};

let activeRoot: string | null = null;
let activeSession: CodeMirrorCsharpSession | null = null;

type CsharpClientState = {
  disposed: boolean;
  socket: WebSocket | null;
  client: LSPClient | null;
  detachSocketListeners: (() => void) | null;
};

function normalizedPath(path: string): string {
  return path.replaceAll('\\', '/').replace(/\/+$/, '');
}

function fileUri(path: string): string {
  return `file://${normalizedPath(path).split('/').map(encodeURIComponent).join('/')}`;
}

function pathFromFileUri(uri: string): string | null {
  try {
    const parsed = new URL(uri);
    return parsed.protocol === 'file:' ? normalizedPath(decodeURIComponent(parsed.pathname)) : null;
  } catch {
    return null;
  }
}

/** Roslyn requires a workspace folder even when the standard root URI is set. */
export function withCsharpWorkspaceFolder(message: string, root: string): string {
  let payload: unknown;
  try {
    payload = JSON.parse(message);
  } catch {
    return message;
  }
  if (!payload || typeof payload !== 'object') return message;
  const request = payload as Record<string, unknown>;
  if (request.method !== 'initialize') return message;
  const params = request.params && typeof request.params === 'object'
    ? request.params as Record<string, unknown>
    : {};
  const normalizedRoot = normalizedPath(root);
  return JSON.stringify({
    ...request,
    params: {
      ...params,
      workspaceFolders: [{
        uri: fileUri(normalizedRoot),
        name: normalizedRoot.split('/').filter(Boolean).at(-1) ?? normalizedRoot
      }]
    }
  });
}

async function startCsharpLanguageClient(
  requestedRoot: string,
  state: CsharpClientState,
  disposeOnFailure: () => void,
  onReady?: () => void
): Promise<LSPClient> {
  try {
    const endpoint = await ensureNativeCsharpLanguageClientFromTauri(requestedRoot);
    if (state.disposed) throw new Error('The C# client left the visible editor.');
    if (!endpoint || normalizedPath(endpoint.root) !== requestedRoot) {
      throw new Error('Rust did not provide a C# endpoint for the active project.');
    }

    const handlers = new Set<(message: string) => void>();
    let socketError: Error | null = null;
    const socket = new WebSocket(endpoint.wsUrl);
    state.socket = socket;
    await new Promise<void>((resolve, reject) => {
      function cleanup(): void {
        socket.removeEventListener('open', handleOpen);
        socket.removeEventListener('error', handleConnectionError);
        socket.removeEventListener('close', handleConnectionClose);
      }
      function handleOpen(): void {
        cleanup();
        resolve();
      }
      function handleConnectionError(): void {
        cleanup();
        reject(new Error('Could not connect to Roslyn.'));
      }
      function handleConnectionClose(): void {
        cleanup();
        reject(new Error('The Roslyn connection closed before it was ready.'));
      }
      socket.addEventListener('open', handleOpen, { once: true });
      socket.addEventListener('error', handleConnectionError, { once: true });
      socket.addEventListener('close', handleConnectionClose, { once: true });
    });
    if (state.disposed) throw new Error('The C# client left the visible editor.');
    function handleSocketError(): void {
      socketError = new Error('Could not connect to Roslyn.');
    }
    function handleSocketClose(): void {
      if (!state.disposed && !state.client) socketError = new Error('Could not connect to Roslyn.');
    }
    function handleSocketMessage(event: MessageEvent): void {
      const message = String(event.data);
      for (const handler of handlers) handler(message);
    }
    socket.addEventListener('error', handleSocketError, { once: true });
    socket.addEventListener('close', handleSocketClose, { once: true });
    socket.addEventListener('message', handleSocketMessage);
    state.detachSocketListeners = () => {
      socket.removeEventListener('error', handleSocketError);
      socket.removeEventListener('close', handleSocketClose);
      socket.removeEventListener('message', handleSocketMessage);
      handlers.clear();
      state.detachSocketListeners = null;
    };

    const transport: Transport = {
      send(message) {
        socket.send(withCsharpWorkspaceFolder(message, requestedRoot));
      },
      subscribe(handler) {
        handlers.add(handler);
      },
      unsubscribe(handler) {
        handlers.delete(handler);
      }
    };
    const client = new LSPClient({
      rootUri: fileUri(requestedRoot),
      timeout: 30_000,
      extensions: [
        serverCompletion({ override: true }),
        hoverTooltips(),
        keymap.of([...formatKeymap, ...renameKeymap, ...jumpToDefinitionKeymap, ...findReferencesKeymap]),
        signatureHelp(),
        serverDiagnostics()
      ]
    }).connect(transport);
    state.client = client;
    await client.initializing;
    if (socketError) throw socketError;
    if (state.disposed || activeRoot !== requestedRoot) {
      throw new Error('The C# client left the active Supercharged project.');
    }
    await markNativeCsharpLanguageClientReadyFromTauri(requestedRoot);
    onReady?.();
    return client;
  } catch (error) {
    disposeOnFailure();
    throw error instanceof Error ? error : new Error(String(error));
  }
}

/** Keep the browser-side client aligned with Rust's one active Supercharged root. */
export function setNativeCsharpActiveRoot(root: string | null): void {
  const nextRoot = root ? normalizedPath(root) : null;
  if (activeRoot === nextRoot) return;
  activeRoot = nextRoot;
  if (activeSession?.root !== nextRoot) {
    activeSession?.dispose();
    activeSession = null;
  }
}

/** Connect the visible CodeMirror document to Rust's authenticated Roslyn WebSocket. */
export function connectCodeMirrorCsharpClient(
  root: string,
  onReady?: () => void
): CodeMirrorCsharpSession {
  const requestedRoot = normalizedPath(root);
  if (activeRoot !== requestedRoot) {
    throw new Error('This C# document belongs to an inactive Supercharged project.');
  }
  if (activeSession?.root === requestedRoot) return activeSession;

  activeSession?.dispose();
  const state: CsharpClientState = {
    disposed: false,
    socket: null,
    client: null,
    detachSocketListeners: null
  };
  let session: CodeMirrorCsharpSession | null = null;
  const ready = startCsharpLanguageClient(
    requestedRoot,
    state,
    () => session?.dispose(),
    onReady
  );

  session = {
    root: requestedRoot,
    async extension(path: string) {
      const documentPath = normalizedPath(path);
      if (documentPath !== requestedRoot && !documentPath.startsWith(`${requestedRoot}/`)) {
        throw new Error('The active C# document is outside its Roslyn workspace root.');
      }
      return (await ready).plugin(fileUri(documentPath), 'csharp');
    },
    async references(path: string, line: number, column: number, limit: number) {
      const documentPath = normalizedPath(path);
      if (documentPath !== requestedRoot && !documentPath.startsWith(`${requestedRoot}/`)) return [];
      const client = await ready;
      if (state.disposed) return [];
      client.sync();
      const result = await client.request<
        {
          textDocument: { uri: string };
          position: { line: number; character: number };
          context: { includeDeclaration: boolean };
        },
        { uri?: unknown; range?: { start?: { line?: unknown; character?: unknown } } }[] | null
      >('textDocument/references', {
        textDocument: { uri: fileUri(documentPath) },
        position: { line: Math.max(0, line - 1), character: Math.max(0, column - 1) },
        context: { includeDeclaration: true }
      });
      if (!Array.isArray(result)) return [];
      const locations: CsharpReferenceLocation[] = [];
      for (const location of result) {
        const targetPath = typeof location.uri === 'string' ? pathFromFileUri(location.uri) : null;
        const targetLine = location.range?.start?.line;
        const targetColumn = location.range?.start?.character;
        if (!targetPath || typeof targetLine !== 'number' || typeof targetColumn !== 'number') continue;
        locations.push({ path: targetPath, line: targetLine + 1, column: targetColumn + 1 });
        if (locations.length >= limit) break;
      }
      return locations;
    },
    dispose() {
      if (state.disposed) return;
      state.disposed = true;
      state.detachSocketListeners?.();
      state.client?.disconnect();
      state.socket?.close();
      if (activeSession === session) activeSession = null;
    }
  };
  activeSession = session;
  return session;
}

/** Ask the one active official C# client for reference locations. */
export async function findCsharpReferenceLocations(
  root: string,
  path: string,
  line: number,
  column: number,
  limit: number
): Promise<CsharpReferenceLocation[] | null> {
  const requestedRoot = normalizedPath(root);
  const session = activeSession;
  if (!session || activeRoot !== requestedRoot || session.root !== requestedRoot) return null;
  return await session.references(path, line, column, limit);
}
