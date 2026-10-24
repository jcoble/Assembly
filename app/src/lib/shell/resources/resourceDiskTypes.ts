/** The shapes `read_resource_disk_usage` and `reclaim_resource_disk_entry` return. */

export type DiskUsageCategory =
  | 'cargo-target'
  | 'node-modules'
  | 'git-directory'
  | 'worktree'
  | 'transcript-store'
  | 'usage-store';

export type DiskUsageEntry = {
  id: string;
  label: string;
  path: string;
  category: DiskUsageCategory;
  categoryLabel: string;
  bytes: number;
  reclaimable: boolean;
  /** The size is a floor: the walk stopped at its budget. */
  truncated: boolean;
};

export type DiskUsageSection = {
  id: string;
  label: string;
  root: string;
  bytes: number;
  measuredAtMs: number;
  entries: DiskUsageEntry[];
};

export type DiskUsageReport = {
  generatedAtMs: number;
  totalBytes: number;
  sections: DiskUsageSection[];
};

export type DiskReclaimRequest = {
  path: string;
  category: DiskUsageCategory;
  expectedBytes: number;
};

export type DiskReclaimReceipt = {
  action: string;
  path: string;
  category: DiskUsageCategory;
  listedBytes: number;
  reclaimedBytes: number;
  message: string;
};
