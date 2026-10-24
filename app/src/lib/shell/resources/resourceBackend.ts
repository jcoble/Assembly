import {
  applyResourceMemoryPressureFromTauri,
  cancelResourceDiskScanFromTauri,
  cancelResourceSnapshotFromTauri,
  cleanupWorkspaceDiskEntryFromTauri,
  readResourceDiskScanFromTauri,
  readLanguageServerLogFromTauri,
  readResourceSnapshotFromTauri,
  restartLanguageServerRootFromTauri,
  setActiveSourceRootFromTauri,
  stopOwnedResourceFromTauri
} from '../../tauriSource.ts';
import type {
  DiskScanReport,
  ResourceCommandReceipt,
  ResourceCleanupReceipt,
  ResourceCleanupRequest,
  ResourceDiskRoot,
  ResourceSnapshot,
  ResourceStopRequest
} from './resourceTypes.ts';
import type { ResourceUnavailable } from './resourceTypes.ts';

export async function readResourceSnapshot(): Promise<ResourceSnapshot | null> {
  return readResourceSnapshotFromTauri();
}

export async function cancelResourceSnapshot(): Promise<void> {
  await cancelResourceSnapshotFromTauri();
}

export async function readResourceDiskScan(
  roots: ResourceDiskRoot[],
  options: { maxDepth?: number; maxEntries?: number } = {}
): Promise<DiskScanReport | null> {
  return readResourceDiskScanFromTauri(roots, options.maxDepth ?? 3, options.maxEntries ?? 2000);
}

export async function cancelResourceDiskScan(): Promise<void> {
  await cancelResourceDiskScanFromTauri();
}

export async function stopOwnedResource(
  request: ResourceStopRequest
): Promise<ResourceCommandReceipt | null> {
  return stopOwnedResourceFromTauri(request);
}

export async function restartLanguageServerRoot(root: string): Promise<ResourceUnavailable | null> {
  return restartLanguageServerRootFromTauri(root);
}

export async function applyResourceMemoryPressure(level: string): Promise<ResourceUnavailable | null> {
  return applyResourceMemoryPressureFromTauri(level);
}

export async function readLanguageServerLog(root: string): Promise<ResourceUnavailable | null> {
  return readLanguageServerLogFromTauri(root);
}

export async function cleanupWorkspaceDiskEntry(
  request: ResourceCleanupRequest
): Promise<ResourceCleanupReceipt | null> {
  return cleanupWorkspaceDiskEntryFromTauri(request);
}

export async function setActiveSourceRoot(root: string): Promise<string | null> {
  return setActiveSourceRootFromTauri(root);
}
