import type { ProjectGitFileStatus, ProjectGitStatus } from '$lib/tauriSource';

export type RustGitScmSnapshot = {
  root: string | null;
  status: ProjectGitStatus | null;
  generation?: number;
};

export type RustGitScmOwnerToken = {
  readonly ownedId: string;
  readonly generation: number;
  readonly scmGeneration: number;
  readonly root: string;
};

export type RustGitScmOwnerContext = {
  readonly ownedId: string;
  readonly generation: number;
  readonly root: string;
};

export type RustGitScmGroupSnapshot = {
  readonly id: 'staged' | 'workingTree' | 'untracked';
  readonly label: string;
  readonly files: readonly ProjectGitFileStatus[];
};

let apiReady = false;
let pending: RustGitScmSnapshot = { root: null, status: null };
let activeRoot: string | null = null;
let activeStatus: ProjectGitStatus | null = null;
let activeScmGeneration = 0;
const activeProbeOwners = new Map<string, RustGitScmOwnerToken>();

function canonicalRoot(root: string | null | undefined): string | null {
  const trimmed = root?.trim() ?? '';
  return trimmed === '' ? null : trimmed.replace(/\/+$/, '');
}

function clearActiveSnapshot(): void {
  activeRoot = null;
  activeStatus = null;
  activeProbeOwners.clear();
}

function applySnapshot(snapshot: RustGitScmSnapshot): void {
  if (!apiReady) return;
  if (snapshot.generation !== undefined && snapshot.generation !== activeScmGeneration) return;
  const root = canonicalRoot(snapshot.root);
  if (!root) {
    clearActiveSnapshot();
    return;
  }

  if (activeRoot !== root) {
    clearActiveSnapshot();
    activeRoot = root;
  }

  activeStatus = snapshot.status;
}

/** Called once, after the singleton Monaco/VS Code wrapper has started. */
export function markRustGitScmApiReady(): void {
  if (apiReady) return;
  apiReady = true;
  applySnapshot(pending);
}

/**
 * Retain the app's already-loaded Git status for bounded extension adapters.
 * No Git command is issued here; gitService and Rust remain the authority.
 */
export function syncRustGitSourceControl(snapshot: RustGitScmSnapshot): void {
  const root = canonicalRoot(snapshot.root);
  if (snapshot.generation !== undefined) {
    if (snapshot.generation !== activeScmGeneration || root !== activeRoot) return;
    pending = { ...snapshot, root };
    applySnapshot(pending);
    return;
  }

  const sameActiveRoot = root === activeRoot && activeScmGeneration > 0;
  const generation = sameActiveRoot ? activeScmGeneration : activeScmGeneration + 1;
  if (!sameActiveRoot) {
    activeScmGeneration = generation;
    activeProbeOwners.clear();
  }
  pending = { ...snapshot, root, generation };
  applySnapshot(pending);
}

export function rustGitScmStatus(): {
  apiReady: boolean;
  root: string | null;
  generation: number;
  scmGeneration: number;
  probeOwnerCount: number;
  resourceCount: number;
} {
  return {
    apiReady,
    root: activeRoot,
    generation: activeScmGeneration,
    scmGeneration: activeScmGeneration,
    probeOwnerCount: activeProbeOwners.size,
    resourceCount: activeStatus?.files.length ?? 0
  };
}

export function acquireRustGitScmProbeOwner(context: RustGitScmOwnerContext): RustGitScmOwnerToken | null {
  const wanted = canonicalRoot(context.root);
  const ownedId = context.ownedId.trim();
  if (!wanted || !ownedId || wanted !== activeRoot || !activeStatus) return null;
  const token = {
    ownedId,
    generation: context.generation,
    scmGeneration: activeScmGeneration,
    root: wanted
  };
  activeProbeOwners.clear();
  activeProbeOwners.set(token.ownedId, token);
  return token;
}

export function isRustGitScmProbeOwnerCurrent(owner: RustGitScmOwnerToken): boolean {
  const active = activeProbeOwners.get(owner.ownedId);
  return Boolean(
    active &&
      active.root === owner.root &&
      active.generation === owner.generation &&
      active.scmGeneration === owner.scmGeneration &&
      activeRoot === owner.root &&
      activeScmGeneration === owner.scmGeneration
  );
}

export function releaseRustGitScmProbeOwner(owner: RustGitScmOwnerToken): void {
  const active = activeProbeOwners.get(owner.ownedId);
  if (
    active?.generation === owner.generation &&
    active.scmGeneration === owner.scmGeneration &&
    active.root === owner.root
  ) {
    activeProbeOwners.delete(owner.ownedId);
  }
}

export function rustGitScmProbeStatus(owner: RustGitScmOwnerToken): ProjectGitStatus | null {
  if (!isRustGitScmProbeOwnerCurrent(owner)) return null;
  return activeStatus;
}

export function rustGitScmProbeGroups(owner: RustGitScmOwnerToken): RustGitScmGroupSnapshot[] {
  const files = rustGitScmProbeStatus(owner)?.files ?? [];
  if (!isRustGitScmProbeOwnerCurrent(owner)) return [];
  const groups: RustGitScmGroupSnapshot[] = [
    {
      id: 'staged',
      label: 'Staged Changes',
      files: files.filter((file) => file.indexStatus.trim() !== '' && file.indexStatus !== '?')
    },
    {
      id: 'workingTree',
      label: 'Changes',
      files: files.filter((file) => file.worktreeStatus.trim() !== '' && file.worktreeStatus !== '?')
    },
    {
      id: 'untracked',
      label: 'Untracked Files',
      files: files.filter((file) => file.indexStatus === '?' || file.worktreeStatus === '?')
    }
  ];
  return groups.filter((group) => group.files.length > 0);
}
