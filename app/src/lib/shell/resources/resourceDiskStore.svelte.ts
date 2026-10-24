import { readResourceDiskUsage, reclaimResourceDiskEntry } from './resourceSampleBackend.ts';
import type { DiskUsageEntry, DiskUsageReport } from './resourceDiskTypes.ts';
import { buildReclaimRequest } from './resourceDiskViewModel.ts';

/**
 * Disk sizes are measured on demand, never during process usage reads: a walk
 * over a workspace costs far more than reading process usage, and the backend
 * keeps each answer for five minutes anyway.
 */
export const resourceDiskState = $state<{
  report: DiskUsageReport | null;
  loading: boolean;
  error: string | null;
}>({
  report: null,
  loading: false,
  error: null
});

export const resourceReclaimState = $state<{
  entry: DiskUsageEntry | null;
  busy: boolean;
  error: string | null;
  receipt: string | null;
}>({
  entry: null,
  busy: false,
  error: null,
  receipt: null
});

let loadInFlight: Promise<DiskUsageReport | null> | null = null;

export async function loadResourceDiskUsage(refresh = false): Promise<DiskUsageReport | null> {
  if (loadInFlight) return loadInFlight;
  resourceDiskState.loading = true;
  resourceDiskState.error = null;
  loadInFlight = loadResourceDiskUsageOnce(refresh);
  return loadInFlight;
}

async function loadResourceDiskUsageOnce(refresh: boolean): Promise<DiskUsageReport | null> {
  try {
    const report = await readResourceDiskUsage(refresh);
    resourceDiskState.report = report;
    return report;
  } catch (error: unknown) {
    resourceDiskState.error =
      error instanceof Error ? error.message : 'Disk usage could not be measured.';
    return null;
  } finally {
    resourceDiskState.loading = false;
    loadInFlight = null;
  }
}

export function askToReclaimDiskEntry(entry: DiskUsageEntry): void {
  if (!buildReclaimRequest(entry)) return;
  resourceReclaimState.entry = entry;
  resourceReclaimState.error = null;
  resourceReclaimState.receipt = null;
}

export function cancelReclaimDiskEntry(): void {
  resourceReclaimState.entry = null;
  resourceReclaimState.busy = false;
}

export async function confirmReclaimDiskEntry(): Promise<void> {
  const entry = resourceReclaimState.entry;
  if (!entry || resourceReclaimState.busy) return;
  const request = buildReclaimRequest(entry);
  if (!request) return;
  resourceReclaimState.busy = true;
  resourceReclaimState.error = null;
  try {
    const receipt = await reclaimResourceDiskEntry(request);
    resourceReclaimState.receipt =
      receipt?.message ?? 'Reclaiming disk space is available in the desktop app.';
    resourceReclaimState.entry = null;
    await loadResourceDiskUsage(true);
  } catch (error: unknown) {
    resourceReclaimState.error =
      error instanceof Error ? error.message : String(error ?? 'That folder could not be removed.');
  } finally {
    resourceReclaimState.busy = false;
  }
}
