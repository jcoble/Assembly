export type ProcessOwner =
  | 'app'
  | 'owned-session'
  | 'language-server'
  | 'provider-sidecar'
  | 'playwright'
  | 'external';

export type ResourceProcess = {
  pid: number;
  ppid: number;
  pgid: number;
  cpuPercent: number;
  rssBytes: number;
  elapsedSeconds: number;
  user: string;
  command: string;
  listeningPorts: number[];
  owner: ProcessOwner;
  ownerId: string | null;
  root: string | null;
  registryGeneration: number;
  canStop: boolean;
  projectId: string | null;
  workspaceId: string | null;
  sessionName: string | null;
};

export type ResourceSnapshot = {
  generation: number;
  capturedAtMs: number;
  processes: ResourceProcess[];
  totalCpuPercent: number;
  totalRssBytes: number;
};

export type ResourceSessionGroup = {
  id: string;
  label: string;
  processes: ResourceProcess[];
  totalCpuPercent: number;
  totalRssBytes: number;
};

export type ResourceWorkspaceGroup = {
  id: string;
  label: string;
  sessions: ResourceSessionGroup[];
  totalCpuPercent: number;
  totalRssBytes: number;
};

export type ResourceProjectGroup = {
  id: string;
  label: string;
  workspaces: ResourceWorkspaceGroup[];
  totalCpuPercent: number;
  totalRssBytes: number;
};

export type DiskProtection =
  | 'active'
  | 'dirty'
  | 'unmerged'
  | 'locked'
  | 'user-data'
  | 'safe-candidate'
  | 'unknown';

export type WorkspaceDiskKind =
  | 'worktree'
  | 'build-output'
  | 'dependency-cache'
  | 'agent-data'
  | 'other';

export type WorkspaceDiskItem = { path: string; bytes: number };

export type WorkspaceDiskEntry = {
  id: string;
  repositoryId: string;
  workspaceId: string;
  path: string;
  kind: WorkspaceDiskKind;
  bytes: number;
  reclaimableBytes: number;
  protection: DiskProtection;
  topLevelItems: WorkspaceDiskItem[];
};

export type DiskScanReport = {
  capturedAtMs: number;
  entries: WorkspaceDiskEntry[];
  scannedBytes: number;
  reclaimableBytes: number;
  truncated: boolean;
};

export type ResourceDiskRoot = Omit<WorkspaceDiskEntry, 'id' | 'bytes' | 'reclaimableBytes' | 'topLevelItems'>;

export type ResourceStopRequest = {
  pid: number;
  expectedPgid: number | null;
  expectedGeneration: number;
  ownerId: string | null;
  expectedRoot: string | null;
};

export type ResourceCommandReceipt = {
  action: string;
  pid: number;
  pgid: number;
  owner: ProcessOwner;
  ownerId: string | null;
  registryGeneration: number;
  signal: string;
};

export type ResourceUnavailable = {
  available: false;
  reason: string;
};

export type ResourceCleanupRequest = {
  repositoryId: string;
  workspaceId: string;
  repositoryRoot?: string | null;
  path: string;
  kind: WorkspaceDiskKind;
  protection: DiskProtection;
  expectedId: string;
  expectedBytes: number;
  maxDepth?: number;
  maxEntries?: number;
};

export type ResourceCleanupReceipt = {
  action: string;
  entryId: string;
  path: string;
  protection: DiskProtection;
  beforeBytes: number;
  afterBytes: number;
  reclaimedBytes: number;
  message: string;
};
