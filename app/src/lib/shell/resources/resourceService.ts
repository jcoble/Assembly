import {
  applyResourceMemoryPressure,
  cancelResourceDiskScan,
  cancelResourceSnapshot,
  cleanupWorkspaceDiskEntry,
  readLanguageServerLog,
  readResourceDiskScan,
  readResourceSnapshot,
  restartLanguageServerRoot,
  setActiveSourceRoot,
  stopOwnedResource
} from './resourceBackend.ts';
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

export const resourceService = {
  async refresh(): Promise<ResourceSnapshot | null> {
    return readResourceSnapshot();
  },
  async cancelRefresh(): Promise<void> {
    await cancelResourceSnapshot();
  },
  async readDisk(
    roots: ResourceDiskRoot[],
    options?: { maxDepth?: number; maxEntries?: number }
  ): Promise<DiskScanReport | null> {
    const report = await readResourceDiskScan(roots, options);
    return report;
  },
  async cancelDisk(): Promise<void> {
    await cancelResourceDiskScan();
  },
  async stop(request: ResourceStopRequest): Promise<ResourceCommandReceipt | null> {
    const receipt = await stopOwnedResource(request);
    return receipt;
  },
  async restartLanguageServer(root: string): Promise<ResourceUnavailable | null> {
    const unavailable = await restartLanguageServerRoot(root);
    return unavailable;
  },
  async applyMemoryPressure(level: string): Promise<ResourceUnavailable | null> {
    const unavailable = await applyResourceMemoryPressure(level);
    return unavailable;
  },
  async readLanguageServerLog(root: string): Promise<ResourceUnavailable | null> {
    const unavailable = await readLanguageServerLog(root);
    return unavailable;
  },
  async cleanup(request: ResourceCleanupRequest): Promise<ResourceCleanupReceipt | null> {
    const receipt = await cleanupWorkspaceDiskEntry(request);
    return receipt;
  },
  async setActiveRoot(root: string): Promise<string | null> {
    const activeRoot = await setActiveSourceRoot(root);
    return activeRoot;
  }
};
