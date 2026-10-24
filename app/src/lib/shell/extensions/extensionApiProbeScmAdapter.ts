import type { ProjectGitFileStatus, ProjectGitStatus } from '$lib/tauriSource';
import type { GitService } from '../git/gitService.ts';
import type {
  RustGitScmGroupSnapshot,
  RustGitScmOwnerContext,
  RustGitScmOwnerToken
} from './rustGitScmProvider.ts';

export interface ExtensionApiProbeScmLease {
  readonly ownedId: string;
  readonly generation: number;
  readonly root: string;
  readStatus(): ProjectGitStatus | null;
  groups(): readonly RustGitScmGroupSnapshot[];
  select(relativePath: string): Promise<void>;
  refresh(): Promise<ProjectGitStatus | null>;
  dispose(): void;
}

export interface ExtensionApiProbeScmProviderBridge {
  acquireOwner(context: RustGitScmOwnerContext): RustGitScmOwnerToken | null;
  isOwnerCurrent(owner: RustGitScmOwnerToken): boolean;
  releaseOwner(owner: RustGitScmOwnerToken): void;
  groups(owner: RustGitScmOwnerToken): readonly RustGitScmGroupSnapshot[];
}

const STALE_OWNER_MESSAGE = 'The source-control probe lease is no longer current.';

function canonicalRoot(root: string | null | undefined): string {
  return (root ?? '').trim().replace(/\/+$/, '');
}

function cloneStatus(status: ProjectGitStatus | null): ProjectGitStatus | null {
  if (!status) return null;
  return {
    ...status,
    files: status.files.map((file) => ({ ...file }))
  };
}

function cloneGroups(groups: readonly RustGitScmGroupSnapshot[]): RustGitScmGroupSnapshot[] {
  return groups.map((group) => ({
    id: group.id,
    label: group.label,
    files: group.files.map((file) => ({ ...file }))
  }));
}

function assertCurrent(
  service: Pick<GitService, 'state'>,
  provider: ExtensionApiProbeScmProviderBridge,
  owner: RustGitScmOwnerToken
): void {
  if (canonicalRoot(service.state.root) !== owner.root || !provider.isOwnerCurrent(owner)) {
    throw new Error(STALE_OWNER_MESSAGE);
  }
}

function findCurrentFile(status: ProjectGitStatus | null, relativePath: string): ProjectGitFileStatus {
  const path = relativePath.trim().replace(/^\/+/, '');
  if (!path) throw new Error('A repository-relative file path is required.');
  const file = (status?.files ?? []).find((entry) => entry.relativePath === path);
  if (!file) throw new Error('The requested file is not present in the current source-control status.');
  return file;
}

export function createExtensionApiProbeScmLease(
  service: Pick<GitService, 'state' | 'selectFile' | 'refreshStatus'>,
  provider: ExtensionApiProbeScmProviderBridge,
  context: RustGitScmOwnerContext
): ExtensionApiProbeScmLease | null {
  const root = canonicalRoot(context.root);
  if (!root || root !== canonicalRoot(service.state.root)) return null;

  const owner = provider.acquireOwner({ ...context, root });
  if (!owner) return null;
  const activeOwner = owner;
  let disposed = false;

  function guard(): void {
    if (disposed) throw new Error(STALE_OWNER_MESSAGE);
    assertCurrent(service, provider, activeOwner);
  }

  return {
    ownedId: activeOwner.ownedId,
    generation: activeOwner.generation,
    root: activeOwner.root,

    readStatus(): ProjectGitStatus | null {
      guard();
      return cloneStatus(service.state.status);
    },

    groups(): readonly RustGitScmGroupSnapshot[] {
      guard();
      return cloneGroups(provider.groups(activeOwner));
    },

    async select(relativePath: string): Promise<void> {
      guard();
      const file = findCurrentFile(service.state.status, relativePath);
      await service.selectFile(file);
      guard();
    },

    async refresh(): Promise<ProjectGitStatus | null> {
      guard();
      await service.refreshStatus();
      guard();
      return cloneStatus(service.state.status);
    },

    dispose(): void {
      if (disposed) return;
      disposed = true;
      provider.releaseOwner(activeOwner);
    }
  };
}

export async function acquireExtensionApiProbeScmLease(
  context: RustGitScmOwnerContext
): Promise<ExtensionApiProbeScmLease | null> {
  const [{ gitService }, providerModule] = await Promise.all([
    import('../git/gitService.ts'),
    import('./rustGitScmProvider.ts')
  ]);
  return createExtensionApiProbeScmLease(gitService, {
    acquireOwner: providerModule.acquireRustGitScmProbeOwner,
    isOwnerCurrent: providerModule.isRustGitScmProbeOwnerCurrent,
    releaseOwner: providerModule.releaseRustGitScmProbeOwner,
    groups: providerModule.rustGitScmProbeGroups
  }, context);
}
