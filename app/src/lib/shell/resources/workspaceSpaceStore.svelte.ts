import type { DiskScanReport, ResourceDiskRoot } from './resourceTypes.ts';
import { workspaceSpaceService } from './workspaceSpaceService.ts';

export const workspaceSpaceState = $state<{
  report: DiskScanReport | null;
  scanning: boolean;
  error: string | null;
}>({ report: null, scanning: false, error: null });

let scanGeneration = 0;

export function releaseWorkspaceSpaceScan(): void {
  scanGeneration += 1;
  workspaceSpaceState.scanning = false;
  void cancelWorkspaceSpaceScan();
}

async function cancelWorkspaceSpaceScan(): Promise<void> {
  try {
    await workspaceSpaceService.cancel();
  } catch {
    // The workspace is already gone; cancellation is best-effort cleanup.
  }
}

export async function scanWorkspaceSpaceRoots(roots: ResourceDiskRoot[]): Promise<DiskScanReport | null> {
  const generation = ++scanGeneration;
  workspaceSpaceState.scanning = true;
  workspaceSpaceState.error = null;
  try {
    const report = await workspaceSpaceService.scan(roots);
    if (generation !== scanGeneration) return workspaceSpaceState.report;
    workspaceSpaceState.report = report;
    return report;
  } catch (error) {
    if (generation !== scanGeneration) return workspaceSpaceState.report;
    workspaceSpaceState.error = error instanceof Error ? error.message : 'Space scan failed.';
    return null;
  } finally {
    if (generation === scanGeneration) workspaceSpaceState.scanning = false;
  }
}
