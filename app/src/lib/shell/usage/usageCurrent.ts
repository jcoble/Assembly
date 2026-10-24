import type { ProviderUsageSnapshot } from './usageTypes.ts';
import {
  readAssemblySettingFromTauri,
  writeAssemblySettingFromTauri
} from '../../tauriSource.ts';

type ProviderUsageWindowInput = {
  label?: string;
  name?: string;
  usedPercent?: number | null;
  percentConsumed?: number | null;
  percentRemaining?: number | null;
  resetsAt?: string | null;
  resetAt?: string | null;
  windowMinutes?: number | null;
  semantics?: string;
};

type ProviderUsageInput = Omit<Partial<ProviderUsageSnapshot>, 'windows'> & {
  windows?: ProviderUsageWindowInput[];
  inputTokens?: number;
};

export function usageWindowDurationLabel(windowMinutes: number | null | undefined): string {
  if (!Number.isFinite(windowMinutes) || !windowMinutes || windowMinutes <= 0) return 'Provider window';
  if (windowMinutes >= 6 * 24 * 60 && windowMinutes <= 8 * 24 * 60) return 'Weekly';
  if (windowMinutes < 60) return `${windowMinutes}-minute`;
  const hours = windowMinutes / 60;
  return `${Number.isInteger(hours) ? hours : hours.toFixed(1)}-hour`;
}

export function usageQuotaWindowLabel(window: { label: string; windowMinutes?: number | null }): string {
  const minutes = window.windowMinutes;
  const label = window.label.trim() || 'Provider';
  if (minutes && minutes >= 6 * 24 * 60 && minutes <= 8 * 24 * 60 && label === 'Weekly') return 'Weekly (7-day) window';
  if (minutes === 5 * 60 && (label === 'Session' || label === '5-hour')) return 'Session (5-hour) window';
  return /window$/i.test(label) ? label : `${label} window`;
}

function reportedWindowMinutes(window: ProviderUsageWindowInput): number | null {
  if (Number.isFinite(window.windowMinutes) && window.windowMinutes && window.windowMinutes > 0) return window.windowMinutes;
  const semanticsMatch = window.semantics?.match(/\b(\d+)-minute\b/);
  return semanticsMatch ? Number(semanticsMatch[1]) : null;
}

function normalizeProviderWindow(window: ProviderUsageWindowInput) {
  const windowMinutes = reportedWindowMinutes(window);
  const usedPercent = window.usedPercent ?? window.percentConsumed ?? (window.percentRemaining == null ? 0 : 100 - window.percentRemaining);
  return {
    label: window.label?.trim() || (windowMinutes == null ? window.name?.trim() || 'Provider window' : usageWindowDurationLabel(windowMinutes)),
    usedPercent: Math.max(0, Math.min(100, usedPercent)),
    resetsAt: window.resetsAt ?? window.resetAt ?? null,
    windowMinutes
  };
}

export function normalizeProviderUsage(input: ProviderUsageInput): ProviderUsageSnapshot {
  const state = input.state === 'available' && (input.windows?.length ?? 0) > 0 ? 'available' : input.state === 'error' ? 'error' : 'unavailable';
  return {
    provider: input.provider?.trim() || 'unknown',
    account: input.account ?? null,
    instanceId: input.instanceId?.trim() || 'unknown',
    state,
    windows: state === 'available' ? (input.windows ?? []).map(normalizeProviderWindow) : [],
    capturedAt: input.capturedAt ?? Date.now(),
    source: input.source ?? null,
    sourceVersion: input.sourceVersion ?? null,
    unavailableReason: state === 'available' ? null : input.unavailableReason ?? 'No provider quota data is available.'
  };
}

export function usageWindowLabel(window: { label: string; usedPercent: number }): string {
  return `${window.label}: ${Math.round(window.usedPercent)}% used`;
}

export function usageProviderLabel(provider: string): string {
  return provider.trim() ? provider.trim().replace(/[-_]+/g, ' ').replace(/\b\w/g, (character) => character.toUpperCase()) : 'Provider';
}

export function usageResetLabel(resetsAt: string | null): string {
  if (!resetsAt) return 'Reset unavailable';
  const parsed = Number(resetsAt);
  const target = Number.isFinite(parsed) ? (parsed < 10_000_000_000 ? parsed * 1000 : parsed) : Date.parse(resetsAt);
  if (!Number.isFinite(target)) return 'Reset unavailable';
  const delta = Math.max(0, target - Date.now());
  const minutes = Math.round(delta / 60_000);
  if (minutes < 60) return `Resets in ${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  return `Resets in ${hours}h${remainder ? ` ${remainder}m` : ''}`;
}

export function usagePercent(window: { usedPercent: number }): number {
  return Math.max(0, Math.min(100, window.usedPercent));
}

/** Which way round a quota is read: how much has gone, or how much is left. */
export type UsageDisplayMode = 'used' | 'remaining';

export const USAGE_DISPLAY_SETTING_KEY = 'usage.display-mode';

/** The number a quota shows, 0–100, in whichever direction is being read. */
export function usageDisplayPercent(
  window: { usedPercent: number },
  mode: UsageDisplayMode
): number {
  const used = usagePercent(window);
  return mode === 'remaining' ? 100 - used : used;
}

export function usageDisplayLabel(
  window: { usedPercent: number },
  mode: UsageDisplayMode
): string {
  return `${Math.round(usageDisplayPercent(window, mode))}% ${mode === 'remaining' ? 'left' : 'used'}`;
}

export async function readUsageDisplayMode(): Promise<UsageDisplayMode> {
  try {
    const stored = await readAssemblySettingFromTauri(USAGE_DISPLAY_SETTING_KEY);
    return stored === 'remaining' ? 'remaining' : 'used';
  } catch {
    return 'used';
  }
}

export async function writeUsageDisplayMode(mode: UsageDisplayMode): Promise<void> {
  try {
    await writeAssemblySettingFromTauri(USAGE_DISPLAY_SETTING_KEY, mode);
  } catch {
    // Display mode is non-critical; the next launch falls back to the default.
  }
}
