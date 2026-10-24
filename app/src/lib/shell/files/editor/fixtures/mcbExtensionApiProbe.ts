/**
 * Internal API-first extension probe contract.
 *
 * This module deliberately does not import `vscode`, Tauri, a filesystem
 * implementation, a process API, the terminal service, or the Git service.
 * The controller supplies those adapters while retaining ownership of the
 * existing LocalWebWorker, Monaco services, owned PTY, and Rust Git authority.
 * Keeping the contract host-neutral makes the deterministic parts testable in
 * Node and makes a second runtime/backend impossible by construction.
 */

import { PRODUCT_NAME } from '../../../../productIdentity.ts';

export const MCB_EXTENSION_API_PROBE_ID = 'mcb.internal.extension-api-probe' as const;
export const MCB_EXTENSION_API_PROBE_VERSION = '0.1.0' as const;
export const MCB_EXTENSION_API_PROBE_COMMAND = `${MCB_EXTENSION_API_PROBE_ID}.ping` as const;
export const MCB_EXTENSION_API_PROBE_CONTEXT_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.currentContext` as const;
export const MCB_EXTENSION_API_PROBE_ADAPTERS_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.runBoundedAdapters` as const;
export const MCB_EXTENSION_API_PROBE_CONTEXT_CHANGED_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.contextChanged` as const;
export const MCB_EXTENSION_API_PROBE_TERMINAL_BRIDGE_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.bridge.terminal` as const;
export const MCB_EXTENSION_API_PROBE_SCM_BRIDGE_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.bridge.scm` as const;
export const MCB_EXTENSION_API_PROBE_DISPOSE_BRIDGE_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.bridge.dispose` as const;
export const MCB_EXTENSION_API_PROBE_REPORT_BRIDGE_COMMAND =
  `${MCB_EXTENSION_API_PROBE_ID}.bridge.report` as const;
export const MCB_EXTENSION_API_PROBE_BROWSER_ENTRY = './browser.cjs' as const;
export const MCB_EXTENSION_API_PROBE_MARKER_FILE = 'README.md' as const;

/** Manifest data for the controller's one existing `registerExtension` call. */
export const MCB_EXTENSION_API_PROBE_MANIFEST = Object.freeze({
  name: 'extension-api-probe',
  publisher: 'mac-command-bar',
  version: MCB_EXTENSION_API_PROBE_VERSION,
  engines: { vscode: '*' },
  browser: MCB_EXTENSION_API_PROBE_BROWSER_ENTRY,
  activationEvents: [
    `onCommand:${MCB_EXTENSION_API_PROBE_COMMAND}`,
    `onCommand:${MCB_EXTENSION_API_PROBE_ADAPTERS_COMMAND}`
  ],
  contributes: {
    commands: [
      {
        command: MCB_EXTENSION_API_PROBE_COMMAND,
        title: `${PRODUCT_NAME}: Extension API probe`
      },
      {
        command: MCB_EXTENSION_API_PROBE_ADAPTERS_COMMAND,
        title: `${PRODUCT_NAME}: Run bounded extension adapters`
      }
    ]
  }
} as const);

/** The only host tier this internal fixture is allowed to run in. */
export const MCB_EXTENSION_API_PROBE_HOST_KIND = 'LocalWebWorker' as const;

export const MCB_EXTENSION_API_PROBE_MAX_TERMINAL_WRITE_CHARS = 4_096 as const;
export const MCB_EXTENSION_API_PROBE_MAX_SCM_GROUPS = 128 as const;
export const MCB_EXTENSION_API_PROBE_MAX_SCM_RESOURCES_PER_GROUP = 256 as const;

export type McbProbeHostKind = typeof MCB_EXTENSION_API_PROBE_HOST_KIND;

export type McbProbeDisposable = {
  dispose(): void;
};

export type McbProbeWorkspaceRoot = {
  /** Canonical filesystem path, not a URI and never a display label. */
  path: string;
  /** Canonical `file://` URI used by the VS Code bridge. */
  uri: string;
};

export type McbProbeWorkspaceContext = {
  ownedId: string;
  generation: number;
  roots: readonly McbProbeWorkspaceRoot[];
  activeRoot: string;
};

export type McbProbeWorkspaceContextInput = {
  ownedId: string;
  generation: number;
  roots: readonly (string | McbProbeWorkspaceRoot)[];
  activeRoot?: string;
};

export type McbProbeWorkspaceEvent = {
  kind: 'workspaceFolders';
  generation: number;
  ownedId: string;
  roots: readonly string[];
  activeRoot: string;
};

export type McbProbeFileEvent = {
  kind: 'files';
  generation: number;
  ownedId: string;
  root: string;
  paths: readonly string[];
};

export type McbProbeEvent = McbProbeWorkspaceEvent | McbProbeFileEvent;

export type McbProbeProgressReport = {
  message?: string;
  increment?: number;
};

export type McbProbeProgressReporter = {
  report(value: McbProbeProgressReport): void;
};

export type McbProbeProgressToken = {
  readonly isCancellationRequested: boolean;
};

export type McbProbeWindow = {
  showInformationMessage(
    message: string,
    ...actions: readonly string[]
  ): Promise<string | undefined>;
  withProgress<T>(
    options: {
      location: 'notification';
      title: string;
      cancellable: false;
    },
    task: (
      progress: McbProbeProgressReporter,
      token: McbProbeProgressToken
    ) => Promise<T> | T
  ): Promise<T>;
};

export type McbProbeWorkspaceFsStat = {
  type: 'file' | 'directory';
  size: number;
};

export type McbProbeWorkspaceFsEntry = {
  name: string;
  type: 'file' | 'directory';
};

/** The existing native filesystem adapter receives a checked root + relative path. */
export type McbProbeWorkspaceFs = {
  readFile(root: string, relativePath: string): Promise<string>;
  stat(root: string, relativePath: string): Promise<McbProbeWorkspaceFsStat>;
  readDirectory(
    root: string,
    relativePath: string
  ): Promise<readonly McbProbeWorkspaceFsEntry[]>;
};

export type McbProbeWorkspaceApi = {
  /** Return the active session's canonical roots, not arbitrary user input. */
  currentContext(): McbProbeWorkspaceContext;
  fs: McbProbeWorkspaceFs;
  onDidChangeWorkspaceFolders(
    listener: (event: McbProbeWorkspaceContext) => void
  ): McbProbeDisposable;
  onDidChangeFiles(listener: (event: McbProbeFileEvent) => void): McbProbeDisposable;
};

export type McbProbeCommandHandler<TArguments = unknown, TResult = unknown> = (
  arguments_: TArguments
) => TResult | Promise<TResult>;

export type McbProbeCommands = {
  registerCommand<TArguments, TResult>(
    command: string,
    handler: McbProbeCommandHandler<TArguments, TResult>
  ): McbProbeDisposable;
  executeCommand<TResult>(command: string, ...arguments_: readonly unknown[]): Promise<TResult>;
};

/** Exact identity carried into the existing owned PTY service. */
export type McbProbeTerminalRequest = {
  ownedId: string;
  generation: number;
  root: string;
  cwd: string;
  cols?: number;
  rows?: number;
};

export type McbProbeTerminalLease = {
  sessionId: string;
  ownedId: string;
  generation: number;
  root: string;
};

/**
 * A deliberately small adapter over `nextTerminalService`/`TerminalService`.
 * There is no command, executable, shell, or process field here: the
 * controller maps this request to the already-owned PTY and its lifecycle.
 */
export type McbProbeTerminalAdapter = {
  request(request: McbProbeTerminalRequest): Promise<McbProbeTerminalLease>;
  show(lease: McbProbeTerminalLease): void;
  write(lease: McbProbeTerminalLease, data: string): Promise<void>;
  resize(lease: McbProbeTerminalLease, cols: number, rows: number): Promise<void>;
  dispose(lease: McbProbeTerminalLease): Promise<void>;
};

export type McbProbeScmRequest = {
  ownedId: string;
  generation: number;
  root: string;
};

export type McbProbeScmResource = {
  relativePath: string;
  status: string;
  selected: boolean;
};

export type McbProbeScmGroup = {
  id: string;
  label: string;
  resources: readonly McbProbeScmResource[];
};

export type McbProbeScmSnapshot = {
  root: string;
  generation: number;
  groups: readonly McbProbeScmGroup[];
  selectedRelativePath: string | null;
};

/** Read-only bridge over the Rust-backed SCM provider. There are no mutation methods. */
export type McbProbeScmAdapter = {
  readStatus(request: McbProbeScmRequest): Promise<McbProbeScmSnapshot | null>;
  selectResource(request: McbProbeScmRequest, relativePath: string): Promise<void>;
  refresh(request: McbProbeScmRequest): Promise<McbProbeScmSnapshot | null>;
  dispose(request: McbProbeScmRequest): Promise<void>;
};

export type McbExtensionApiProbeHost = {
  commands: McbProbeCommands;
  workspace: McbProbeWorkspaceApi;
  window: McbProbeWindow;
  terminal: McbProbeTerminalAdapter;
  scm: McbProbeScmAdapter;
};

export type McbProbeAllowedPath = {
  root: string;
  relativePath: string;
};

export type McbExtensionApiProbeOptions = {
  /** Exact, workspace-relative files the probe may read/stat. */
  allowedPaths?: readonly McbProbeAllowedPath[];
};

export type McbProbeCommandResult = {
  extensionId: typeof MCB_EXTENSION_API_PROBE_ID;
  version: typeof MCB_EXTENSION_API_PROBE_VERSION;
  hostKind: McbProbeHostKind;
  ownedId: string;
  generation: number;
  workspaceFolderCount: number;
  activeRoot: string;
};

export type McbProbeRegistrationCounts = {
  commands: number;
  workspaceListeners: number;
  fileListeners: number;
};

export type McbProbeSnapshot = {
  active: boolean;
  activationCount: number;
  deactivationCount: number;
  context: McbProbeWorkspaceContext | null;
  registrations: McbProbeRegistrationCounts;
  events: readonly McbProbeEvent[];
  staleEventCount: number;
  terminalRequestCount: number;
  terminalShowCount: number;
  terminalWriteCount: number;
  terminalResizeCount: number;
  terminalDisposeCount: number;
  scmReadCount: number;
  scmSelectCount: number;
  scmRefreshCount: number;
  scmDisposeCount: number;
  notificationCount: number;
  progressCount: number;
};

export type McbProbeActivationReceipt = {
  extensionId: typeof MCB_EXTENSION_API_PROBE_ID;
  version: typeof MCB_EXTENSION_API_PROBE_VERSION;
  hostKind: McbProbeHostKind;
  generation: number;
  registrations: McbProbeRegistrationCounts;
};

export type McbExtensionApiProbe = {
  readonly id: typeof MCB_EXTENSION_API_PROBE_ID;
  readonly version: typeof MCB_EXTENSION_API_PROBE_VERSION;
  readonly hostKind: McbProbeHostKind;
  activate(
    host: McbExtensionApiProbeHost,
    context?: McbProbeWorkspaceContext
  ): Promise<McbProbeActivationReceipt>;
  deactivate(): void;
  switchWorkspace(context: McbProbeWorkspaceContext): void;
  ping(arguments_?: { generation?: number }): McbProbeCommandResult;
  showInformationMessage(message: string, ...actions: readonly string[]): Promise<string | undefined>;
  withProgress<T>(
    title: string,
    task: (
      progress: McbProbeProgressReporter,
      token: McbProbeProgressToken
    ) => Promise<T> | T
  ): Promise<T>;
  workspaceFs: {
    readFile(request: McbProbeWorkspaceFsRequest): Promise<string>;
    stat(request: McbProbeWorkspaceFsRequest): Promise<McbProbeWorkspaceFsStat>;
    readDirectory(
      request: McbProbeWorkspaceFsRequest
    ): Promise<readonly McbProbeWorkspaceFsEntry[]>;
  };
  terminal: {
    request(request: McbProbeTerminalRequest): Promise<McbProbeTerminalLease>;
    show(lease: McbProbeTerminalLease): void;
    write(lease: McbProbeTerminalLease, data: string): Promise<void>;
    resize(lease: McbProbeTerminalLease, cols: number, rows: number): Promise<void>;
    dispose(lease: McbProbeTerminalLease): Promise<void>;
  };
  scm: {
    readStatus(request: McbProbeScmRequest): Promise<McbProbeScmSnapshot | null>;
    selectResource(request: McbProbeScmRequest, relativePath: string): Promise<void>;
    refresh(request: McbProbeScmRequest): Promise<McbProbeScmSnapshot | null>;
    dispose(request: McbProbeScmRequest): Promise<void>;
  };
  snapshot(): McbProbeSnapshot;
};

export type McbProbeWorkspaceFsRequest = {
  generation: number;
  root: string;
  path: string;
};

type ProbeState = {
  host: McbExtensionApiProbeHost | null;
  context: McbProbeWorkspaceContext | null;
  active: boolean;
  activationCount: number;
  deactivationCount: number;
  registrations: McbProbeRegistrationCounts;
  disposables: McbProbeDisposable[];
  events: McbProbeEvent[];
  staleEventCount: number;
  terminalRequestCount: number;
  terminalShowCount: number;
  terminalWriteCount: number;
  terminalResizeCount: number;
  terminalDisposeCount: number;
  scmReadCount: number;
  scmSelectCount: number;
  scmRefreshCount: number;
  scmDisposeCount: number;
  notificationCount: number;
  progressCount: number;
  allowedPaths: Set<string>;
};

function nonEmpty(value: string, name: string): string {
  const trimmed = value.trim();
  if (!trimmed) throw new Error(`${name} must not be empty.`);
  return trimmed;
}

function pathWithoutUri(path: string): string {
  const trimmed = path.trim();
  if (trimmed.startsWith('file://')) {
    return decodeURIComponent(trimmed.slice('file://'.length));
  }
  return trimmed;
}

/** Normalize a POSIX/macOS path without touching the filesystem. */
export function normalizeProbePath(path: string): string {
  const raw = pathWithoutUri(path).replaceAll('\\', '/');
  if (!raw) return '/';
  const absolute = raw.startsWith('/');
  const segments: string[] = [];
  for (const segment of raw.split('/')) {
    if (!segment || segment === '.') continue;
    if (segment === '..') {
      if (segments.length > 0 && segments.at(-1) !== '..') {
        segments.pop();
      } else if (!absolute) {
        segments.push('..');
      }
      continue;
    }
    segments.push(segment);
  }
  const normalized = segments.join('/');
  return absolute ? `/${normalized}`.replace(/\/$/, '') || '/' : normalized || '.';
}

export function isProbePathWithinRoot(root: string, candidate: string): boolean {
  const normalizedRoot = normalizeProbePath(root);
  const normalizedCandidate = normalizeProbePath(candidate);
  return (
    normalizedCandidate === normalizedRoot ||
    (normalizedRoot === '/' && normalizedCandidate.startsWith('/')) ||
    normalizedCandidate.startsWith(`${normalizedRoot}/`)
  );
}

export function relativeProbePath(root: string, candidate: string): string | null {
  if (!isProbePathWithinRoot(root, candidate)) return null;
  const normalizedRoot = normalizeProbePath(root);
  const normalizedCandidate = normalizeProbePath(candidate);
  if (normalizedCandidate === normalizedRoot) return '';
  return normalizedCandidate.slice(normalizedRoot === '/' ? 1 : normalizedRoot.length + 1);
}

function rootUri(path: string): string {
  const normalized = normalizeProbePath(path);
  return `file://${encodeURI(normalized)}`;
}

function normalizeWorkspaceRoot(root: string | McbProbeWorkspaceRoot): McbProbeWorkspaceRoot {
  if (typeof root === 'string') {
    const path = normalizeProbePath(nonEmpty(root, 'workspace root'));
    return { path, uri: rootUri(path) };
  }
  const path = normalizeProbePath(nonEmpty(root.path, 'workspace root'));
  const uri = nonEmpty(root.uri, 'workspace root URI');
  if (!uri.startsWith('file://') || normalizeProbePath(uri) !== path) {
    throw new Error('workspace root URI must be the canonical file URI for its path.');
  }
  return { path, uri };
}

export function createProbeWorkspaceContext(
  input: McbProbeWorkspaceContextInput
): McbProbeWorkspaceContext {
  const ownedId = nonEmpty(input.ownedId, 'ownedId');
  if (!Number.isSafeInteger(input.generation) || input.generation < 1) {
    throw new Error('workspace generation must be a positive integer.');
  }
  const byPath = new Map<string, McbProbeWorkspaceRoot>();
  for (const root of input.roots) {
    const normalized = normalizeWorkspaceRoot(root);
    byPath.set(normalized.path, normalized);
  }
  if (byPath.size === 0) throw new Error('at least one workspace root is required.');
  const roots = [...byPath.values()].sort((left, right) => left.path.localeCompare(right.path));
  const activeRoot = normalizeProbePath(input.activeRoot ?? roots[0].path);
  if (!byPath.has(activeRoot)) throw new Error('activeRoot must be one of the workspace roots.');
  return Object.freeze({
    ownedId,
    generation: input.generation,
    roots: Object.freeze(roots),
    activeRoot
  });
}

function contextRoots(context: McbProbeWorkspaceContext): Set<string> {
  return new Set(context.roots.map((root) => root.path));
}

function assertGeneration(context: McbProbeWorkspaceContext, generation: number): void {
  if (generation !== context.generation) {
    throw new Error(`stale extension probe generation ${generation}; current is ${context.generation}.`);
  }
}

function assertActiveRoot(context: McbProbeWorkspaceContext, root: string): string {
  const normalized = normalizeProbePath(root);
  if (normalized !== context.activeRoot) {
    throw new Error('extension probe request must target the active workspace root.');
  }
  return normalized;
}

function assertWorkspaceRoot(context: McbProbeWorkspaceContext, root: string): string {
  const normalized = normalizeProbePath(root);
  if (!contextRoots(context).has(normalized)) {
    throw new Error('extension probe request targets a stale or unknown workspace root.');
  }
  return normalized;
}

function assertPathRequest(
  context: McbProbeWorkspaceContext,
  request: McbProbeWorkspaceFsRequest,
  allowedPaths: Set<string>
): { root: string; relativePath: string } {
  assertGeneration(context, request.generation);
  const root = assertWorkspaceRoot(context, request.root);
  const path = normalizeProbePath(request.path);
  const relativePath = relativeProbePath(root, path);
  if (relativePath === null) {
    throw new Error('extension probe filesystem request is outside the active workspace roots.');
  }
  const key = `${root}\0${relativePath}`;
  if (!allowedPaths.has(key)) {
    throw new Error('extension probe filesystem request is not allow-listed.');
  }
  return { root, relativePath };
}

function assertTerminalRequest(
  context: McbProbeWorkspaceContext,
  request: McbProbeTerminalRequest
): McbProbeTerminalRequest {
  assertGeneration(context, request.generation);
  if (request.ownedId !== context.ownedId) {
    throw new Error('extension probe terminal request is not owned by the active session.');
  }
  const root = assertActiveRoot(context, request.root);
  const cwd = normalizeProbePath(request.cwd);
  if (!isProbePathWithinRoot(root, cwd)) {
    throw new Error('extension probe terminal cwd is outside the active workspace root.');
  }
  for (const [value, label] of [
    [request.cols ?? 80, 'terminal cols'],
    [request.rows ?? 24, 'terminal rows']
  ] as const) {
    if (!Number.isSafeInteger(value) || value < 1 || value > 500) {
      throw new Error(`${label} must be an integer between 1 and 500.`);
    }
  }
  return { ...request, root, cwd, cols: request.cols ?? 80, rows: request.rows ?? 24 };
}

function assertTerminalLease(
  context: McbProbeWorkspaceContext,
  lease: McbProbeTerminalLease
): void {
  assertGeneration(context, lease.generation);
  if (lease.ownedId !== context.ownedId) {
    throw new Error('extension probe terminal lease belongs to a different session.');
  }
  assertActiveRoot(context, lease.root);
  nonEmpty(lease.sessionId, 'terminal sessionId');
}

function assertScmRequest(context: McbProbeWorkspaceContext, request: McbProbeScmRequest): McbProbeScmRequest {
  assertGeneration(context, request.generation);
  if (request.ownedId !== context.ownedId) {
    throw new Error('extension probe SCM request is not owned by the active session.');
  }
  return { ...request, root: assertActiveRoot(context, request.root) };
}

function validateScmSnapshot(
  context: McbProbeWorkspaceContext,
  request: McbProbeScmRequest,
  snapshot: McbProbeScmSnapshot | null
): McbProbeScmSnapshot | null {
  if (snapshot === null) return null;
  if (
    normalizeProbePath(snapshot.root) !== request.root ||
    snapshot.generation !== context.generation
  ) {
    throw new Error('Rust Git SCM response does not match the active root generation.');
  }
  if (snapshot.groups.length > MCB_EXTENSION_API_PROBE_MAX_SCM_GROUPS) {
    throw new Error('Rust Git SCM response exceeded the bounded group limit.');
  }
  for (const group of snapshot.groups) {
    nonEmpty(group.id, 'SCM group id');
    nonEmpty(group.label, 'SCM group label');
    if (group.resources.length > MCB_EXTENSION_API_PROBE_MAX_SCM_RESOURCES_PER_GROUP) {
      throw new Error('Rust Git SCM response exceeded the bounded resource limit.');
    }
    for (const resource of group.resources) {
      const rawRelativePath = resource.relativePath.replaceAll('\\', '/');
      if (rawRelativePath.startsWith('/') || rawRelativePath.split('/').includes('..')) {
        throw new Error('Rust Git SCM response contains a path outside the active root.');
      }
      const relative = relativeProbePath(request.root, `${request.root}/${rawRelativePath}`);
      if (relative === null || relative === '') {
        throw new Error('Rust Git SCM response contains a path outside the active root.');
      }
      nonEmpty(resource.status, 'SCM resource status');
    }
  }
  if (snapshot.selectedRelativePath !== null) {
    const selected = snapshot.selectedRelativePath.replaceAll('\\', '/');
    if (
      !selected ||
      selected.startsWith('/') ||
      selected.split('/').includes('..') ||
      relativeProbePath(request.root, `${request.root}/${selected}`) === null
    ) {
      throw new Error('Rust Git SCM response contains an invalid selected path.');
    }
  }
  return snapshot;
}

function cloneContext(context: McbProbeWorkspaceContext | null): McbProbeWorkspaceContext | null {
  if (!context) return null;
  return {
    ownedId: context.ownedId,
    generation: context.generation,
    activeRoot: context.activeRoot,
    roots: context.roots.map((root) => ({ ...root }))
  };
}

function cloneEvent(event: McbProbeEvent): McbProbeEvent {
  if (event.kind === 'workspaceFolders') {
    return { ...event, roots: [...event.roots] };
  }
  return { ...event, paths: [...event.paths] };
}

function disposeOnce(disposable: McbProbeDisposable): void {
  let disposed = false;
  const original = disposable.dispose.bind(disposable);
  disposable.dispose = () => {
    if (disposed) return;
    disposed = true;
    original();
  };
}

function requireActive(state: ProbeState): {
  host: McbExtensionApiProbeHost;
  context: McbProbeWorkspaceContext;
} {
  if (!state.active || !state.host || !state.context) {
    throw new Error('extension API probe is not active.');
  }
  return { host: state.host, context: state.context };
}

export function createMcbExtensionApiProbe(
  options: McbExtensionApiProbeOptions = {}
): McbExtensionApiProbe {
  const state: ProbeState = {
    host: null,
    context: null,
    active: false,
    activationCount: 0,
    deactivationCount: 0,
    registrations: { commands: 0, workspaceListeners: 0, fileListeners: 0 },
    disposables: [],
    events: [],
    staleEventCount: 0,
    terminalRequestCount: 0,
    terminalShowCount: 0,
    terminalWriteCount: 0,
    terminalResizeCount: 0,
    terminalDisposeCount: 0,
    scmReadCount: 0,
    scmSelectCount: 0,
    scmRefreshCount: 0,
    scmDisposeCount: 0,
    notificationCount: 0,
    progressCount: 0,
    allowedPaths: new Set(
      (options.allowedPaths ?? []).map((allowed) => {
        const root = normalizeProbePath(allowed.root);
        const relativePath = normalizeProbePath(allowed.relativePath).replace(/^\//, '');
        return `${root}\0${relativePath}`;
      })
    )
  };

  const registration = (disposable: McbProbeDisposable, kind: keyof McbProbeRegistrationCounts) => {
    disposeOnce(disposable);
    state.disposables.push(disposable);
    state.registrations[kind] += 1;
  };

  const disposeAll = (): void => {
    for (const disposable of state.disposables.splice(0).reverse()) disposable.dispose();
    state.registrations = { commands: 0, workspaceListeners: 0, fileListeners: 0 };
  };

  const switchWorkspace = (context: McbProbeWorkspaceContext): void => {
    if (!state.active) throw new Error('extension API probe is not active.');
    const normalized = createProbeWorkspaceContext(context);
    if (!state.context) {
      state.context = normalized;
      return;
    }
    if (
      normalized.ownedId !== state.context.ownedId ||
      normalized.generation < state.context.generation
    ) {
      state.staleEventCount += 1;
      return;
    }
    if (normalized.generation === state.context.generation) {
      if (normalized.activeRoot !== state.context.activeRoot) {
        throw new Error('a workspace root change must advance the probe generation.');
      }
      return;
    }
    state.context = normalized;
    state.events.push({
      kind: 'workspaceFolders',
      generation: normalized.generation,
      ownedId: normalized.ownedId,
      roots: normalized.roots.map((root) => root.path),
      activeRoot: normalized.activeRoot
    });
  };

  const requireCurrentContext = (context?: McbProbeWorkspaceContext): McbProbeWorkspaceContext => {
    const current = requireActive(state).context;
    if (context) {
      if (context.ownedId !== current.ownedId || context.generation !== current.generation) {
        throw new Error('extension probe context is stale.');
      }
    }
    return current;
  };

  const probe: McbExtensionApiProbe = {
    id: MCB_EXTENSION_API_PROBE_ID,
    version: MCB_EXTENSION_API_PROBE_VERSION,
    hostKind: MCB_EXTENSION_API_PROBE_HOST_KIND,

    async activate(host, context = host.workspace.currentContext()): Promise<McbProbeActivationReceipt> {
      if (state.active) {
        if (state.host !== host) throw new Error('extension API probe is already bound to another host.');
        requireCurrentContext(context);
        return {
          extensionId: MCB_EXTENSION_API_PROBE_ID,
          version: MCB_EXTENSION_API_PROBE_VERSION,
          hostKind: MCB_EXTENSION_API_PROBE_HOST_KIND,
          generation: state.context!.generation,
          registrations: { ...state.registrations }
        };
      }
      const initial = createProbeWorkspaceContext(context);
      state.host = host;
      state.context = initial;
      state.active = true;
      state.activationCount += 1;
      try {
        registration(
          host.commands.registerCommand(
            MCB_EXTENSION_API_PROBE_COMMAND,
            (arguments_?: { generation?: number }) => probe.ping(arguments_)
          ),
          'commands'
        );
        registration(
          host.workspace.onDidChangeWorkspaceFolders((next) => {
            try {
              switchWorkspace(createProbeWorkspaceContext(next));
            } catch {
              state.staleEventCount += 1;
            }
          }),
          'workspaceListeners'
        );
        registration(
          host.workspace.onDidChangeFiles((event) => {
            if (!state.context) return;
            try {
              assertGeneration(state.context, event.generation);
              if (event.ownedId !== state.context.ownedId) throw new Error('stale ownedId');
              const root = assertWorkspaceRoot(state.context, event.root);
              const paths = event.paths.map((path) => {
                const checked = assertPathRequest(
                  state.context!,
                  { generation: event.generation, root, path },
                  state.allowedPaths
                );
                return checked.relativePath;
              });
              state.events.push({
                kind: 'files',
                generation: event.generation,
                ownedId: event.ownedId,
                root,
                paths
              });
            } catch {
              state.staleEventCount += 1;
            }
          }),
          'fileListeners'
        );
      } catch (error) {
        disposeAll();
        state.host = null;
        state.context = null;
        state.active = false;
        throw error;
      }
      return {
        extensionId: MCB_EXTENSION_API_PROBE_ID,
        version: MCB_EXTENSION_API_PROBE_VERSION,
        hostKind: MCB_EXTENSION_API_PROBE_HOST_KIND,
        generation: initial.generation,
        registrations: { ...state.registrations }
      };
    },

    deactivate(): void {
      if (!state.active) return;
      disposeAll();
      state.host = null;
      state.context = null;
      state.active = false;
      state.deactivationCount += 1;
    },

    switchWorkspace,

    ping(arguments_ = {}): McbProbeCommandResult {
      const { context } = requireActive(state);
      if (arguments_.generation !== undefined) assertGeneration(context, arguments_.generation);
      return {
        extensionId: MCB_EXTENSION_API_PROBE_ID,
        version: MCB_EXTENSION_API_PROBE_VERSION,
        hostKind: MCB_EXTENSION_API_PROBE_HOST_KIND,
        ownedId: context.ownedId,
        generation: context.generation,
        workspaceFolderCount: context.roots.length,
        activeRoot: context.activeRoot
      };
    },

    async showInformationMessage(message, ...actions): Promise<string | undefined> {
      const { host } = requireActive(state);
      const text = nonEmpty(message, 'notification message');
      state.notificationCount += 1;
      return await host.window.showInformationMessage(text, ...actions);
    },

    withProgress(title, task) {
      const { host } = requireActive(state);
      const text = nonEmpty(title, 'progress title');
      state.progressCount += 1;
      return host.window.withProgress(
        { location: 'notification', title: text, cancellable: false },
        task
      );
    },

    workspaceFs: {
      async readFile(request) {
        const { host, context } = requireActive(state);
        const checked = assertPathRequest(context, request, state.allowedPaths);
        return host.workspace.fs.readFile(checked.root, checked.relativePath);
      },
      async stat(request) {
        const { host, context } = requireActive(state);
        const checked = assertPathRequest(context, request, state.allowedPaths);
        return host.workspace.fs.stat(checked.root, checked.relativePath);
      },
      async readDirectory(request) {
        const { host, context } = requireActive(state);
        const checked = assertPathRequest(context, request, state.allowedPaths);
        return host.workspace.fs.readDirectory(checked.root, checked.relativePath);
      }
    },

    terminal: {
      async request(request) {
        const { host, context } = requireActive(state);
        const checked = assertTerminalRequest(context, request);
        state.terminalRequestCount += 1;
        const lease = await host.terminal.request(checked);
        if (
          lease.ownedId !== context.ownedId ||
          lease.generation !== context.generation ||
          normalizeProbePath(lease.root) !== context.activeRoot
        ) {
          throw new Error('owned PTY adapter returned a mismatched lease.');
        }
        return lease;
      },
      show(lease) {
        const { host, context } = requireActive(state);
        assertTerminalLease(context, lease);
        state.terminalShowCount += 1;
        host.terminal.show(lease);
      },
      async write(lease, data) {
        const { host, context } = requireActive(state);
        assertTerminalLease(context, lease);
        if (data.length > MCB_EXTENSION_API_PROBE_MAX_TERMINAL_WRITE_CHARS) {
          throw new Error('extension probe terminal input exceeded the bounded write limit.');
        }
        state.terminalWriteCount += 1;
        return host.terminal.write(lease, data);
      },
      async resize(lease, cols, rows) {
        const { host, context } = requireActive(state);
        assertTerminalLease(context, lease);
        if (
          !Number.isSafeInteger(cols) ||
          !Number.isSafeInteger(rows) ||
          cols < 1 ||
          cols > 500 ||
          rows < 1 ||
          rows > 500
        ) {
          throw new Error('extension probe terminal resize is outside the bounded grid.');
        }
        state.terminalResizeCount += 1;
        return host.terminal.resize(lease, cols, rows);
      },
      async dispose(lease) {
        const { host, context } = requireActive(state);
        assertTerminalLease(context, lease);
        state.terminalDisposeCount += 1;
        return host.terminal.dispose(lease);
      }
    },

    scm: {
      async readStatus(request) {
        const { host, context } = requireActive(state);
        const checked = assertScmRequest(context, request);
        state.scmReadCount += 1;
        const snapshot = await host.scm.readStatus(checked);
        return validateScmSnapshot(context, checked, snapshot);
      },
      async selectResource(request, relativePath) {
        const { host, context } = requireActive(state);
        const checked = assertScmRequest(context, request);
        const rawRelativePath = relativePath.replaceAll('\\', '/');
        if (
          !rawRelativePath ||
          rawRelativePath.startsWith('/') ||
          rawRelativePath.split('/').includes('..') ||
          relativeProbePath(checked.root, `${checked.root}/${rawRelativePath}`) === null
        ) {
          throw new Error('extension probe SCM selection is outside the active root.');
        }
        state.scmSelectCount += 1;
        return host.scm.selectResource(checked, rawRelativePath);
      },
      async refresh(request) {
        const { host, context } = requireActive(state);
        const checked = assertScmRequest(context, request);
        state.scmRefreshCount += 1;
        const snapshot = await host.scm.refresh(checked);
        return validateScmSnapshot(context, checked, snapshot);
      },
      async dispose(request) {
        const { host, context } = requireActive(state);
        const checked = assertScmRequest(context, request);
        state.scmDisposeCount += 1;
        return host.scm.dispose(checked);
      }
    },

    snapshot(): McbProbeSnapshot {
      return {
        active: state.active,
        activationCount: state.activationCount,
        deactivationCount: state.deactivationCount,
        context: cloneContext(state.context),
        registrations: { ...state.registrations },
        events: state.events.map(cloneEvent),
        staleEventCount: state.staleEventCount,
        terminalRequestCount: state.terminalRequestCount,
        terminalShowCount: state.terminalShowCount,
        terminalWriteCount: state.terminalWriteCount,
        terminalResizeCount: state.terminalResizeCount,
        terminalDisposeCount: state.terminalDisposeCount,
        scmReadCount: state.scmReadCount,
        scmSelectCount: state.scmSelectCount,
        scmRefreshCount: state.scmRefreshCount,
        scmDisposeCount: state.scmDisposeCount,
        notificationCount: state.notificationCount,
        progressCount: state.progressCount
      };
    }
  };

  return probe;
}
