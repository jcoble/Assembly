import type { OwnedSession } from '../../ownedSessions.ts';
import type { OwnedTerminalProbe, TerminalService } from '../../terminal/terminalService.ts';
import {
  acquireExtensionApiProbeScmLease,
  type ExtensionApiProbeScmLease
} from '../../extensions/extensionApiProbeScmAdapter.ts';
import {
  createProbeWorkspaceContext,
  type McbProbeWorkspaceContext
} from './fixtures/mcbExtensionApiProbe.ts';

export type ExtensionApiProbeOwnerRequest = {
  ownedId: string;
  generation: number;
  root: string;
};

export type ExtensionApiProbeTerminalRequest = ExtensionApiProbeOwnerRequest & {
  cwd?: string;
};

export type ExtensionApiProbeTerminalReceipt = {
  ownedId: string;
  generation: number;
};

export type ExtensionApiProbeScmReceipt = {
  ownedId: string;
  generation: number;
  root: string;
};

type ProbeRuntimeBinding = {
  terminalService: TerminalService;
  terminalHost: HTMLElement;
  acquireScmLease?: typeof acquireExtensionApiProbeScmLease;
};

type ContextListener = (
  context: McbProbeWorkspaceContext,
  previous: McbProbeWorkspaceContext | null
) => void;

export type ExtensionApiProbeObservation = {
  kind: 'activation' | 'adapters';
  summary: string;
  detail: string;
};

type ObservationListener = (observation: ExtensionApiProbeObservation) => void;

let context: McbProbeWorkspaceContext | null = null;
let contextGeneration = 0;
let runtime: ProbeRuntimeBinding | null = null;
let terminalLease: OwnedTerminalProbe | null = null;
let scmLease: ExtensionApiProbeScmLease | null = null;
const contextListeners = new Set<ContextListener>();
const observationListeners = new Set<ObservationListener>();
let observation: ExtensionApiProbeObservation | null = null;

function normalizedRoot(value: string): string {
  return value.trim().replace(/\/+$/, '');
}

function copiedContext(value: McbProbeWorkspaceContext): McbProbeWorkspaceContext {
  return {
    ownedId: value.ownedId,
    generation: value.generation,
    activeRoot: value.activeRoot,
    roots: value.roots.map((root) => ({ ...root }))
  };
}

function requireCurrent(request: ExtensionApiProbeOwnerRequest): McbProbeWorkspaceContext {
  const current = context;
  if (
    !current ||
    request.ownedId !== current.ownedId ||
    request.generation !== current.generation ||
    normalizedRoot(request.root) !== current.activeRoot
  ) {
    throw new Error('The extension API probe request is stale or belongs to another session.');
  }
  return current;
}

async function disposeTerminalLease(): Promise<boolean> {
  const lease = terminalLease;
  terminalLease = null;
  if (!lease) return false;
  await lease.dispose();
  return true;
}

function disposeScmLease(): boolean {
  const lease = scmLease;
  scmLease = null;
  if (!lease) return false;
  lease.dispose();
  return true;
}

export function currentExtensionApiProbeContext(): McbProbeWorkspaceContext | null {
  return context ? copiedContext(context) : null;
}

export function onExtensionApiProbeContextChanged(listener: ContextListener): () => void {
  contextListeners.add(listener);
  return () => contextListeners.delete(listener);
}

export function onExtensionApiProbeObservation(listener: ObservationListener): () => void {
  observationListeners.add(listener);
  if (observation) listener({ ...observation });
  return () => observationListeners.delete(listener);
}

export function recordExtensionApiProbeObservation(
  value: ExtensionApiProbeObservation
): ExtensionApiProbeObservation {
  const kind = value.kind;
  if (kind !== 'activation' && kind !== 'adapters') {
    throw new Error('The extension API probe observation kind is not allowed.');
  }
  observation = {
    kind,
    summary: value.summary.trim().slice(0, 160),
    detail: value.detail.trim().slice(0, 512)
  };
  if (!observation.summary) throw new Error('The extension API probe observation is empty.');
  for (const listener of observationListeners) listener({ ...observation });
  return { ...observation };
}

export async function setExtensionApiProbeWorkspace(input: {
  ownedId: string;
  root: string;
}): Promise<McbProbeWorkspaceContext> {
  const ownedId = input.ownedId.trim();
  const root = normalizedRoot(input.root);
  if (!ownedId || !root) {
    throw new Error('The extension API probe requires an owned session and canonical root.');
  }
  if (context?.ownedId === ownedId && context.activeRoot === root) {
    return copiedContext(context);
  }

  const previous = context ? copiedContext(context) : null;
  await disposeTerminalLease();
  disposeScmLease();
  context = createProbeWorkspaceContext({
    ownedId,
    generation: ++contextGeneration,
    roots: [root],
    activeRoot: root
  });
  const next = copiedContext(context);
  for (const listener of contextListeners) listener(next, previous);
  return next;
}

export async function ensureExtensionApiProbeWorkspaceRoot(
  root: string
): Promise<McbProbeWorkspaceContext> {
  const normalized = normalizedRoot(root);
  if (context?.activeRoot === normalized) return copiedContext(context);
  return setExtensionApiProbeWorkspace({ ownedId: 'native-csharp-editor', root: normalized });
}

export async function configureExtensionApiProbeRuntime(
  binding: ProbeRuntimeBinding
): Promise<void> {
  if (
    runtime?.terminalService === binding.terminalService &&
    runtime.terminalHost === binding.terminalHost
  ) {
    runtime = binding;
    return;
  }
  await disposeTerminalLease();
  disposeScmLease();
  runtime = binding;
}

export async function createExtensionApiProbeTerminal(
  request: ExtensionApiProbeTerminalRequest
): Promise<ExtensionApiProbeTerminalReceipt> {
  const current = requireCurrent(request);
  if (!runtime?.terminalHost.isConnected) {
    throw new Error('The extension API probe terminal host is not available.');
  }
  await disposeTerminalLease();
  const probeOwnedId = `mcb-extension-probe:${current.ownedId}:${current.generation}`;
  const owned: OwnedSession = {
    ownedId: probeOwnedId,
    executionEnvironment: 'local',
    agent: 'other',
    viaCmux: false,
    source: 'fresh',
    title: 'Extension API probe',
    projectId: null,
    projectGroupKey: 'none',
    projectGroupLabel: 'No project',
    cwd: normalizedRoot(request.cwd ?? current.activeRoot),
    resumeCommand: null,
    nativeSessionId: null,
    ptySessionId: null,
    state: 'background',
    completedAt: null,
    settledAt: null,
    branch: null,
    taskId: null,
    pullRequest: null,
    messageCount: null,
    latestTurnPreview: null,
    lastActivity: null
  };
  if (
    owned.cwd !== current.activeRoot &&
    !owned.cwd.startsWith(`${current.activeRoot}/`)
  ) {
    throw new Error('The extension API probe terminal cwd is outside the active root.');
  }
  const lease = await runtime.terminalService.createProbe(owned, runtime.terminalHost);
  try {
    requireCurrent(request);
  } catch (error) {
    await lease?.dispose();
    throw error;
  }
  if (!lease) throw new Error('The extension API probe terminal could not be created.');
  terminalLease = lease;
  return { ownedId: lease.ownedId, generation: lease.generation };
}

export function showExtensionApiProbeTerminal(request: ExtensionApiProbeOwnerRequest): boolean {
  requireCurrent(request);
  if (!terminalLease) return false;
  terminalLease.show();
  return true;
}

export async function writeExtensionApiProbeTerminal(
  request: ExtensionApiProbeOwnerRequest,
  data: string
): Promise<boolean> {
  requireCurrent(request);
  const lease = terminalLease;
  if (!lease) return false;
  const written = await lease.write(data);
  requireCurrent(request);
  return written;
}

export async function resizeExtensionApiProbeTerminal(
  request: ExtensionApiProbeOwnerRequest,
  cols: number,
  rows: number
): Promise<boolean> {
  requireCurrent(request);
  const lease = terminalLease;
  if (!lease) return false;
  const resized = await lease.resize(cols, rows);
  requireCurrent(request);
  return resized;
}

export async function disposeExtensionApiProbeTerminal(
  request?: ExtensionApiProbeOwnerRequest
): Promise<boolean> {
  if (request) requireCurrent(request);
  return disposeTerminalLease();
}

export async function acquireExtensionApiProbeScm(
  request: ExtensionApiProbeOwnerRequest
): Promise<ExtensionApiProbeScmReceipt | null> {
  const current = requireCurrent(request);
  disposeScmLease();
  const acquire = runtime?.acquireScmLease ?? acquireExtensionApiProbeScmLease;
  const lease = await acquire({
    ownedId: current.ownedId,
    generation: current.generation,
    root: current.activeRoot
  });
  try {
    requireCurrent(request);
  } catch (error) {
    lease?.dispose();
    throw error;
  }
  scmLease = lease;
  return lease
    ? { ownedId: lease.ownedId, generation: lease.generation, root: lease.root }
    : null;
}

export function readExtensionApiProbeScm(request: ExtensionApiProbeOwnerRequest) {
  requireCurrent(request);
  return scmLease?.readStatus() ?? null;
}

export function groupsExtensionApiProbeScm(request: ExtensionApiProbeOwnerRequest) {
  requireCurrent(request);
  return scmLease?.groups() ?? [];
}

export async function selectExtensionApiProbeScm(
  request: ExtensionApiProbeOwnerRequest,
  relativePath: string
): Promise<boolean> {
  requireCurrent(request);
  if (!scmLease) return false;
  await scmLease.select(relativePath);
  requireCurrent(request);
  return true;
}

export async function refreshExtensionApiProbeScm(request: ExtensionApiProbeOwnerRequest) {
  requireCurrent(request);
  if (!scmLease) return null;
  const status = await scmLease.refresh();
  requireCurrent(request);
  return status;
}

export function disposeExtensionApiProbeScm(request?: ExtensionApiProbeOwnerRequest): boolean {
  if (request) requireCurrent(request);
  return disposeScmLease();
}

export async function disposeExtensionApiProbeResources(): Promise<void> {
  await disposeTerminalLease();
  disposeScmLease();
}

export async function disposeExtensionApiProbeRuntime(): Promise<void> {
  await disposeExtensionApiProbeResources();
  runtime = null;
}
