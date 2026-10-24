import {
  readResourceSample,
  readResourceTotals,
  stopResourceProcessTree
} from './resourceSampleBackend.ts';
import type { ResourceSample, ResourceSampleTotals } from './resourceSampleTypes.ts';
import { buildStopRequest, type ResourceStopTarget } from './resourceStopModel.ts';
import { resourceDiagnostics } from '../resourceDiagnostics.svelte.ts';

/** One current native sample plus the frontend counters at that same read.
 * Neither side stores prior readings or a history list. */
export type ResourceSnapshot = {
  capturedAtMs: number;
  native: ResourceSample;
  frontend: typeof resourceDiagnostics;
};

export const resourceSampleState = $state<{
  snapshot: ResourceSnapshot | null;
  totals: ResourceSampleTotals | null;
  loading: boolean;
  error: string | null;
}>({
  snapshot: null,
  totals: null,
  loading: false,
  error: null
});

export const resourceManagerState = $state({ open: false });

let refreshInFlight: Promise<ResourceSnapshot | null> | null = null;

export function setResourceManagerOpen(open: boolean): void {
  resourceManagerState.open = open;
}

export function toggleResourceManager(): void {
  resourceManagerState.open = !resourceManagerState.open;
}

export async function refreshResourceSample(): Promise<ResourceSnapshot | null> {
  if (refreshInFlight) return refreshInFlight;
  resourceSampleState.loading = true;
  resourceSampleState.error = null;
  refreshInFlight = refreshResourceSampleOnce();
  return refreshInFlight;
}

async function refreshResourceSampleOnce(): Promise<ResourceSnapshot | null> {
  try {
    const native = await readResourceSample();
    const snapshot = native
      ? ({
          capturedAtMs: native.generatedAtMs,
          native,
          frontend: resourceDiagnostics
        } satisfies ResourceSnapshot)
      : null;
    resourceSampleState.snapshot = snapshot;
    resourceSampleState.totals = native?.totals ?? null;
    return snapshot;
  } catch (error: unknown) {
      resourceSampleState.error =
        error instanceof Error ? error.message : 'Resource usage could not be read.';
      return null;
  } finally {
    resourceSampleState.loading = false;
    refreshInFlight = null;
  }
}

let totalsRefreshInFlight: Promise<ResourceSampleTotals | null> | null = null;

export async function refreshResourceTotals(): Promise<ResourceSampleTotals | null> {
  if (totalsRefreshInFlight) return totalsRefreshInFlight;
  totalsRefreshInFlight = refreshResourceTotalsOnce();
  return totalsRefreshInFlight;
}

async function refreshResourceTotalsOnce(): Promise<ResourceSampleTotals | null> {
  try {
    const totals = await readResourceTotals();
    if (totals) resourceSampleState.totals = totals;
    return totals;
  } catch (error: unknown) {
      resourceSampleState.error =
        error instanceof Error
          ? error.message
          : 'Resource usage could not be read.';
      return null;
  } finally {
    totalsRefreshInFlight = null;
  }
}

/**
 * The stop flow, which has exactly one shape: a button opens a question, and
 * nothing is signalled until the question is answered. There is no path from a
 * sample to a signal — the app never stops anything by itself.
 */
export const resourceStopState = $state<{
  target: ResourceStopTarget | null;
  busy: boolean;
  error: string | null;
  receipt: string | null;
}>({
  target: null,
  busy: false,
  error: null,
  receipt: null
});

export function askToStopResource(target: ResourceStopTarget): void {
  resourceStopState.target = target;
  resourceStopState.error = null;
  resourceStopState.receipt = null;
}

export function cancelStopResource(): void {
  resourceStopState.target = null;
  resourceStopState.busy = false;
}

export async function confirmStopResource(): Promise<void> {
  const target = resourceStopState.target;
  if (!target || resourceStopState.busy) return;
  resourceStopState.busy = true;
  resourceStopState.error = null;
  try {
    const receipt = await stopResourceProcessTree(buildStopRequest(target));
    resourceStopState.receipt = receipt?.message ?? 'Stopping is available in the desktop app.';
    resourceStopState.target = null;
    await refreshResourceSample();
  } catch (error: unknown) {
    resourceStopState.error =
      error instanceof Error ? error.message : String(error ?? 'That could not be stopped.');
  } finally {
    resourceStopState.busy = false;
  }
}
