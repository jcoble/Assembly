import assert from 'node:assert/strict';
import {
  groupResourceProcesses,
  formatBytes,
  resourceCanStop,
  resourceOwnerLabel,
  countInactiveResourceWorkspaces
} from '../src/lib/shell/resources/resourceViewModel.ts';

assert.equal(resourceOwnerLabel({ owner: 'owned-session', ownerId: 'session-1' }), 'Owned session · session-1');
assert.equal(resourceCanStop({ owner: 'external', canStop: false }), false);
assert.equal(resourceCanStop({ owner: 'owned-session', canStop: true }), true);

const process = (pid, projectId, workspaceId, sessionName, rssBytes, cpuPercent) => ({
  pid, ppid: pid - 1, pgid: 1, cpuPercent, rssBytes, elapsedSeconds: 1, user: 'test', command: `/bin/tool-${pid}`,
  listeningPorts: [], owner: 'owned-session', ownerId: sessionName, root: '/tmp', projectId, workspaceId, sessionName,
  registryGeneration: 1, canStop: true
});

const groups = groupResourceProcesses([
  process(1, 'repo-a', 'workspace-a', 'Terminal 1', 2048, 2),
  process(2, 'repo-a', 'workspace-a', 'Terminal 1', 1024, 1),
  process(3, 'repo-a', 'workspace-b', 'Agent 1', 4096, 3)
]);
assert.equal(groups.length, 1);
assert.equal(groups[0].workspaces.length, 2);
assert.equal(groups[0].workspaces[0].sessions[0].processes.length, 2);
assert.equal(groups[0].totalRssBytes, 7168);
assert.equal(groups[0].totalCpuPercent, 6);
assert.equal(formatBytes(1024 * 1024), '1.0 MB');
assert.equal(countInactiveResourceWorkspaces([{ workspaceId: 'workspace-a' }], ['workspace-a', 'workspace-b', 'workspace-b']), 1);
console.log('resource view-model tests passed');
{
  const { deriveDiskRootsFromProcesses } = await import('../src/lib/shell/resources/resourceViewModel.ts');
  const roots = deriveDiskRootsFromProcesses([
    process(1, 'repo-a', 'workspace-a', 'Terminal 1', 2048, 2),
    process(2, 'repo-a', 'workspace-a', 'Terminal 1', 1024, 1),
    { ...process(3, 'repo-b', 'workspace-b', 'Agent 1', 4096, 3), root: null },
    { ...process(4, 'repo-c', 'workspace-c', 'Agent 2', 4096, 3), owner: 'external' }
  ]);
  assert.equal(roots.length, 1);
  assert.equal(roots[0].path, '/tmp');
  assert.equal(roots[0].repositoryId, 'repo-a');
  assert.equal(roots[0].protection, 'active');
  console.log('deriveDiskRootsFromProcesses tests passed');
}
