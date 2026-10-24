import type {
  ResourceSample,
  ResourceSampleAppPart,
  ResourceSampleGroup,
  ResourceSampleProcess,
  ResourceSampleSession
} from './resourceSampleTypes.ts';

export type ResourceTotals = {
  cpuPercent: number;
  physicalFootprintBytes: number;
  rssBytes: number;
  processCount: number;
};

export type ResourceSessionView = ResourceSampleSession & {
  id: string;
  totals: ResourceTotals;
};

export type ResourceWorkspaceView = Omit<ResourceSampleGroup, 'sessions'> & {
  id: string;
  sessions: ResourceSessionView[];
  totals: ResourceTotals;
};

export type ResourceSampleView = {
  groups: ResourceWorkspaceView[];
  appParts: ResourceSampleAppPart[];
  appTotals: ResourceTotals;
};

function totalsFor(
  rows: Array<Pick<ResourceSampleProcess, 'cpuPercent' | 'physicalFootprintBytes' | 'rssBytes'>>
): ResourceTotals {
  return {
    cpuPercent: rows.reduce((total, row) => total + row.cpuPercent, 0),
    physicalFootprintBytes: rows.reduce((total, row) => total + row.physicalFootprintBytes, 0),
    rssBytes: rows.reduce((total, row) => total + row.rssBytes, 0),
    processCount: rows.length
  };
}

function stableId(value: string): string {
  return value.trim().toLocaleLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || 'resource';
}

export function shapeResourceSample(sample: ResourceSample): ResourceSampleView {
  const groups = sample.groups
    .map((group, groupIndex) => {
      const sessions = group.sessions
        .map((session, sessionIndex) => {
          const processes = [...session.processes].sort(
            (left, right) => right.physicalFootprintBytes - left.physicalFootprintBytes || left.pid - right.pid
          );
          return {
            ...session,
            id: `${stableId(group.workspace)}-${stableId(session.ownedId ?? session.label)}-${sessionIndex}`,
            processes,
            totals: totalsFor(processes)
          } satisfies ResourceSessionView;
        })
        .sort((left, right) => left.label.localeCompare(right.label));
      return {
        workspace: group.workspace,
        id: `${stableId(group.workspace)}-${groupIndex}`,
        sessions,
        totals: totalsFor(sessions.flatMap((session) => session.processes))
      } satisfies ResourceWorkspaceView;
    })
    .sort((left, right) => left.workspace.localeCompare(right.workspace));

  const appParts = [...sample.app.parts].sort(
    (left, right) =>
      Number(left.label !== 'Main process') - Number(right.label !== 'Main process') ||
      right.physicalFootprintBytes - left.physicalFootprintBytes ||
      left.pid - right.pid
  );
  return {
    groups,
    appParts,
    appTotals: totalsFor(appParts)
  };
}

export function formatResourceBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const unitIndex = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / 1024 ** unitIndex;
  const precision = unitIndex >= 3 && value < 10 ? 2 : unitIndex >= 2 && value < 100 ? 1 : 0;
  return `${value.toFixed(precision)} ${units[unitIndex]}`;
}

export function formatResourceCpu(cpuPercent: number): string {
  return `${Math.max(0, cpuPercent).toFixed(1)}%`;
}

export function formatResourceUpdatedAgo(generatedAtMs: number | null, nowMs: number): string {
  if (generatedAtMs === null) return 'waiting for first sample';
  const seconds = Math.max(0, Math.floor((nowMs - generatedAtMs) / 1000));
  if (seconds < 2) return 'updated just now';
  if (seconds < 60) return `updated ${seconds}s ago`;
  return `updated ${Math.floor(seconds / 60)}m ago`;
}

export function resourceSessionKindLabel(kind: ResourceSampleSession['kind']): string {
  if (kind === 'terminal') return 'Terminal';
  if (kind === 'conversation') return 'Conversation';
  return 'Other';
}
