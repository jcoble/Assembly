import { isNativeTauriRuntime } from '$lib/tauriSource';

import type { ResourceSample, ResourceSampleTotals } from './resourceSampleTypes.ts';
import type { ResourceStopRequestBody } from './resourceStopModel.ts';
import type {
  DiskReclaimReceipt,
  DiskReclaimRequest,
  DiskUsageReport
} from './resourceDiskTypes.ts';

export type ResourceStopReceipt = {
  action: string;
  rootPid: number;
  ownedId?: string;
  signal: string;
  followUpSignal: string;
  graceSeconds: number;
  stoppedPids: number[];
  alreadyGonePids: number[];
  message: string;
};

export async function readResourceSample(): Promise<ResourceSample | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<ResourceSample>('read_resource_sample');
}

export async function readResourceTotals(): Promise<ResourceSampleTotals | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<ResourceSampleTotals>('read_resource_totals');
}

/**
 * Only ever called after a person has confirmed a dialog naming the process
 * ids in `expectedPids`.
 */
export async function stopResourceProcessTree(
  request: ResourceStopRequestBody
): Promise<ResourceStopReceipt | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<ResourceStopReceipt>('stop_resource_process_tree', { request });
}

export async function readResourceDiskUsage(refresh = false): Promise<DiskUsageReport | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<DiskUsageReport>('read_resource_disk_usage', { request: { refresh } });
}

/** Only ever called after a person has confirmed a dialog naming the folder. */
export async function reclaimResourceDiskEntry(
  request: DiskReclaimRequest
): Promise<DiskReclaimReceipt | null> {
  if (!isNativeTauriRuntime()) return null;
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<DiskReclaimReceipt>('reclaim_resource_disk_entry', { request });
}
