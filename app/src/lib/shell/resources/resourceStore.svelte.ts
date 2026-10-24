import type { DiskScanReport, ResourceSnapshot } from './resourceTypes.ts';
import { resourceService } from './resourceService.ts';

export const resourceState = $state<{
  snapshot: ResourceSnapshot | null;
  disk: DiskScanReport | null;
  loading: boolean;
  diskLoading: boolean;
  error: string | null;
  unavailableReason: string | null;
  lastRefreshAt: number | null;
}>({
  snapshot: null,
  disk: null,
  loading: false,
  diskLoading: false,
  error: null,
  unavailableReason: null,
  lastRefreshAt: null
});

let refreshGeneration = 0;
let diskScanGeneration = 0;

export function releaseResourceRefresh(): void {
  refreshGeneration += 1;
  resourceState.loading = false;
  void cancelResourceRefresh();
}

async function cancelResourceRefresh(): Promise<void> {
  try {
    await resourceService.cancelRefresh();
  } catch {
    // The panel is already gone; cancellation is best-effort cleanup.
  }
}

export async function refreshResources(): Promise<ResourceSnapshot | null> {
  const generation = ++refreshGeneration;
  resourceState.loading = true;
  resourceState.error = null;
  try {
    const snapshot = await resourceService.refresh();
    if (generation !== refreshGeneration) return resourceState.snapshot;
    resourceState.snapshot = snapshot;
    resourceState.unavailableReason = snapshot ? null : 'Resource inventory is available in the desktop app.';
    resourceState.lastRefreshAt = snapshot ? Date.now() : resourceState.lastRefreshAt;
    return snapshot;
  } catch (error) {
    if (generation !== refreshGeneration) return resourceState.snapshot;
    resourceState.error = error instanceof Error ? error.message : 'Resource inventory failed.';
    return null;
  } finally {
    if (generation === refreshGeneration) resourceState.loading = false;
  }
}

export async function scanWorkspaceSpace(
  roots: Parameters<typeof resourceService.readDisk>[0]
): Promise<DiskScanReport | null> {
  const generation = ++diskScanGeneration;
  resourceState.diskLoading = true;
  resourceState.error = null;
  try {
    const disk = await resourceService.readDisk(roots);
    if (generation !== diskScanGeneration) return resourceState.disk;
    resourceState.disk = disk;
    resourceState.unavailableReason = disk ? null : 'Space scans are available in the desktop app.';
    return disk;
  } catch (error) {
    if (generation !== diskScanGeneration) return resourceState.disk;
    resourceState.error = error instanceof Error ? error.message : 'Space scan failed.';
    return null;
  } finally {
    if (generation === diskScanGeneration) resourceState.diskLoading = false;
  }
}
