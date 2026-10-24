import type { ProcessOwner, ResourceDiskRoot, ResourceProcess, ResourceProjectGroup, ResourceSessionGroup, ResourceWorkspaceGroup } from './resourceTypes.ts';

/** Space needs scan roots even when the shell passes none. The owned-process
 * snapshot already knows every live workspace root, so those become the
 * default roots — marked active so the scan can never offer them for
 * deletion. */
export function deriveDiskRootsFromProcesses(processes: ResourceProcess[]): ResourceDiskRoot[] {
  const roots = new Map<string, ResourceDiskRoot>();
  for (const process of processes) {
    if (!process.root || process.owner === 'external') continue;
    if (roots.has(process.root)) continue;
    roots.set(process.root, {
      repositoryId: process.projectId || 'unassigned',
      workspaceId: process.workspaceId || process.root,
      path: process.root,
      kind: 'worktree',
      protection: 'active'
    });
  }
  return [...roots.values()].sort((left, right) => left.path.localeCompare(right.path));
}

const ownerLabels: Record<ProcessOwner, string> = {
  app: 'App process',
  'owned-session': 'Owned session',
  'language-server': 'Language server',
  'provider-sidecar': 'Provider sidecar',
  playwright: 'Browser automation',
  external: 'External process'
};

export function resourceOwnerLabel(resource: Pick<ResourceProcess, 'owner' | 'ownerId'>): string {
  const label = ownerLabels[resource.owner] ?? 'External process';
  return resource.ownerId ? `${label} · ${resource.ownerId}` : label;
}

export function resourceCanStop(resource: Pick<ResourceProcess, 'owner' | 'canStop'>): boolean {
  return resource.owner !== 'external' && resource.canStop;
}

export function countInactiveResourceWorkspaces(
  processes: Array<Pick<ResourceProcess, 'workspaceId'>>,
  workspaceIds: Iterable<string | null | undefined>
): number {
  const active = new Set(
    processes
      .map((process) => process.workspaceId?.trim())
      .filter((workspace): workspace is string => Boolean(workspace))
  );
  const known = new Set<string>();
  for (const workspaceId of workspaceIds) {
    const normalized = workspaceId?.trim();
    if (normalized) known.add(normalized);
  }
  return [...known].filter((workspaceId) => !active.has(workspaceId)).length;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = units[0];
  for (let index = 1; value >= 1024 && index < units.length; index += 1) {
    value /= 1024;
    unit = units[index];
  }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${unit}`;
}

function sumCpu(processes: ResourceProcess[]): number {
  return processes.reduce((total, process) => total + process.cpuPercent, 0);
}

function sumRss(processes: ResourceProcess[]): number {
  return processes.reduce((total, process) => total + process.rssBytes, 0);
}

function groupLabel(value: string | null | undefined, fallback: string): string {
  return value?.trim() || fallback;
}

export function groupResourceProcesses(processes: ResourceProcess[]): ResourceProjectGroup[] {
  const projects = new Map<string, Map<string, Map<string, ResourceProcess[]>>>();
  for (const process of processes) {
    const project = groupLabel(process.projectId, 'Unassigned project');
    const workspace = groupLabel(process.workspaceId, 'Unassigned workspace');
    const session = groupLabel(process.sessionName || process.ownerId, 'Owned session');
    const workspaces = projects.get(project) ?? new Map<string, Map<string, ResourceProcess[]>>();
    const sessions = workspaces.get(workspace) ?? new Map<string, ResourceProcess[]>();
    const rows = sessions.get(session) ?? [];
    rows.push(process);
    sessions.set(session, rows);
    workspaces.set(workspace, sessions);
    projects.set(project, workspaces);
  }
  return [...projects.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([project, workspaces]) => {
    const workspaceGroups: ResourceWorkspaceGroup[] = [...workspaces.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([workspace, sessions]) => {
      const sessionGroups: ResourceSessionGroup[] = [...sessions.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([session, rows]) => ({
        id: `${project}/${workspace}/${session}`,
        label: session,
        processes: rows.sort((left, right) => right.rssBytes - left.rssBytes || right.cpuPercent - left.cpuPercent),
        totalCpuPercent: sumCpu(rows),
        totalRssBytes: sumRss(rows)
      }));
      return {
        id: `${project}/${workspace}`,
        label: workspace,
        sessions: sessionGroups,
        totalCpuPercent: sessionGroups.reduce((total, session) => total + session.totalCpuPercent, 0),
        totalRssBytes: sessionGroups.reduce((total, session) => total + session.totalRssBytes, 0)
      };
    });
    return {
      id: project,
      label: project,
      workspaces: workspaceGroups,
      totalCpuPercent: workspaceGroups.reduce((total, workspace) => total + workspace.totalCpuPercent, 0),
      totalRssBytes: workspaceGroups.reduce((total, workspace) => total + workspace.totalRssBytes, 0)
    };
  });
}
