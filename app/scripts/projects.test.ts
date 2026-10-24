import assert from 'node:assert/strict';
import test from 'node:test';

import {
  moveProjectDestinations,
  defaultDraftProjectId,
  filterProjects,
  isNewFolderPath,
  middleTruncatePath,
  otherProjectGroups,
  projectBadge,
  projectCountsByMachine,
  projectLastUsedLabel,
  projectMachineLabel,
  sessionWorkingIn,
  shortProjectPath,
  visibleProjects
} from '../src/lib/shell/projects/projects.ts';
import type { ProjectRecord } from '../src/lib/tauriSource.ts';

const project = (id: string, machine: string): ProjectRecord => ({
  id,
  machine,
  rootPath: `/work/${id}`,
  title: id,
  repoKey: '',
  createdAtMs: 0
});

// The badge is the first letters of the first and last word, or the first and
// last letter of a single word.
{
  assert.equal(projectBadge('mac-command-bar'), 'MB');
  assert.equal(projectBadge('rental-management'), 'RM');
  assert.equal(projectBadge('EdiPlatform'), 'EM');
  assert.equal(projectBadge('HealthAggregator'), 'HR');
  assert.equal(projectBadge('x'), 'X');
}

// Projects on a machine that is no longer saved are hidden, not deleted.
{
  const projects = [project('a', 'workbox'), project('b', 'local'), project('c', 'gone')];
  assert.deepEqual(visibleProjects(projects, ['workbox']).map((row) => row.id), ['a', 'b']);
}

// A new draft starts in the active session's project, else the most recent
// session's, else in no project.
{
  const projects = visibleProjects([project('a', 'local'), project('b', 'local'), project('hidden', 'gone')], []);
  const owned = [
    { ownedId: 'one', projectId: 'a', lastActivity: '2026-10-01T10:00:00.000Z' },
    { ownedId: 'two', projectId: 'b', lastActivity: '2026-10-02T10:00:00.000Z' },
    { ownedId: 'three', projectId: 'hidden', lastActivity: '2026-10-03T10:00:00.000Z' },
    { ownedId: 'four', projectId: null, lastActivity: '2026-10-04T10:00:00.000Z' }
  ];
  assert.equal(defaultDraftProjectId(projects, owned, 'one'), 'a');
  assert.equal(defaultDraftProjectId(projects, owned, 'three'), 'b');
  assert.equal(defaultDraftProjectId(projects, owned, null), 'b');
  assert.equal(defaultDraftProjectId(projects, [owned[3]], null), null);
  assert.equal(defaultDraftProjectId([], owned, 'one'), null);
}

{
  const profiles = [{ id: 'workbox', name: 'Workbox' }];
  assert.equal(projectMachineLabel('local', profiles), 'This Mac');
  assert.equal(projectMachineLabel('workbox', profiles), 'Workbox');
  assert.equal(projectMachineLabel('gone', profiles), 'Remote machine');
}

// A typed path names a folder to create when its parent is the listed folder
// and the direct path check confirms it is absent.
{
  const listing = { path: '/Users/me/dev', directories: ['app', 'site'], truncated: false };
  assert.equal(isNewFolderPath('/Users/me/dev/new-app', listing, false), true);
  assert.equal(isNewFolderPath(' /Users/me/dev/new-app ', listing, false), true);
  assert.equal(isNewFolderPath('/Users/me/dev/app', listing, true), false);
  assert.equal(isNewFolderPath('/Users/me/dev/new-app/', listing, false), true);
  assert.equal(isNewFolderPath('/Users/me/dev/app/', listing, true), false);
  assert.equal(isNewFolderPath('/Users/me/dev/', listing, false), false);
  assert.equal(isNewFolderPath('/Users/me/dev/..', listing, false), false);
  assert.equal(isNewFolderPath('/Users/me/other/new-app', listing, false), false);
  assert.equal(isNewFolderPath('/Users/me/dev/new-app', null, false), false);
  assert.equal(isNewFolderPath('/fresh', { path: '/', directories: ['Users'], truncated: false }, false), true);
  assert.equal(isNewFolderPath('/Users/me/dev/existing-past-cap', { ...listing, truncated: true }, true), false);
}

// The Projects screen narrows the list by the chosen machine and by a search
// that matches the name or the folder, ignoring case.
{
  const projects = [
    { ...project('EdiPlatform', 'workbox'), rootPath: '/home/me/dev/EdiPlatform' },
    { ...project('rental', 'workbox'), rootPath: '/home/me/dev/rental-management' },
    { ...project('assembly', 'local'), rootPath: '/Users/me/dev/mac-command-bar' }
  ];
  const ids = (rows: ProjectRecord[]) => rows.map((row) => row.id);
  assert.deepEqual(ids(filterProjects(projects, null, '')), ['EdiPlatform', 'rental', 'assembly']);
  assert.deepEqual(ids(filterProjects(projects, 'workbox', '')), ['EdiPlatform', 'rental']);
  assert.deepEqual(ids(filterProjects(projects, 'local', '')), ['assembly']);
  assert.deepEqual(ids(filterProjects(projects, null, 'edi')), ['EdiPlatform']);
  assert.deepEqual(ids(filterProjects(projects, null, ' MANAGEMENT ')), ['rental']);
  assert.deepEqual(ids(filterProjects(projects, 'local', 'edi')), []);
}

// Each machine's count of projects; a machine with none has no entry.
{
  const projects = [project('a', 'workbox'), project('b', 'workbox'), project('c', 'local')];
  assert.deepEqual(projectCountsByMachine(projects), { workbox: 2, local: 1 });
  assert.deepEqual(projectCountsByMachine([]), {});
}

// A folder under a home directory is shown from `~`; anything else is unchanged.
{
  assert.equal(shortProjectPath('/Users/me/dev/mac-command-bar'), '~/dev/mac-command-bar');
  assert.equal(shortProjectPath('/home/blackcolours/dev/work/EdiPlatform'), '~/dev/work/EdiPlatform');
  assert.equal(shortProjectPath('/home/me'), '~');
  assert.equal(shortProjectPath('/srv/app'), '/srv/app');
  assert.equal(shortProjectPath('/homework/app'), '/homework/app');
}

// A long path is cut in the middle, keeping its start and its last folder whole; a short one is unchanged.
{
  assert.equal(middleTruncatePath('~/dev/work/EdiPlatform', 40), '~/dev/work/EdiPlatform');
  assert.equal(
    middleTruncatePath('~/dev/work/worktrees/mac-command-bar/tsk-1556-project-picker', 40),
    '~/dev/work/work…/tsk-1556-project-picker'
  );
  assert.equal(middleTruncatePath('~/a/a-very-long-final-folder-name-here', 20), '…/a-very-long-final-folder-name-here');
}

// Last used reads as the app's short age; a project never used shows nothing.
{
  const now = new Date('2026-10-10T12:00:00Z');
  assert.equal(projectLastUsedLabel(null, now), '');
  assert.equal(projectLastUsedLabel(now.getTime() - 30_000, now), 'now');
  assert.equal(projectLastUsedLabel(now.getTime() - 2 * 3_600_000, now), '2h');
  assert.equal(projectLastUsedLabel(now.getTime() - 3 * 86_400_000, now), '3d');
}

// Other projects: This Mac, then each connected saved machine, each project followed by its other worktrees.
{
  const groups = otherProjectGroups(
    [project('A', 'local'), project('B', 'workbox'), project('C', 'sd'), project('D', 'gone')],
    [{ id: 'workbox', name: 'Workbox Test' }, { id: 'sd', name: 'SD' }],
    { workbox: 'connected', sd: 'disconnected' },
    {
      '/work/A': [{ path: '/work/A/', branch: 'main', isMain: true }, { path: '/work/A-wt', branch: 'feature', isMain: false }, { path: '/work/fix', branch: 'fix', isMain: false }],
      'assembly-remote://workbox/work/B': [{ path: 'assembly-remote://workbox/work/B-wt', branch: 'fix', isMain: false }]
    }
  );
  // `folder` is the path on the option's own machine: where a new session there starts.
  assert.deepEqual(groups, [
    { machine: 'local', label: 'This Mac', options: [
      { projectId: 'A', path: '/work/A', folder: '/work/A', label: 'A', detail: '/work/A', worktree: false },
      { projectId: 'A', path: '/work/A-wt', folder: '/work/A-wt', label: 'A-wt — feature', detail: '/work/A-wt', worktree: true },
      // A branch named like its folder is not repeated.
      { projectId: 'A', path: '/work/fix', folder: '/work/fix', label: 'fix', detail: '/work/fix', worktree: true }
    ] },
    { machine: 'workbox', label: 'Workbox Test', options: [
      { projectId: 'B', path: 'assembly-remote://workbox/work/B', folder: '/work/B', label: 'B', detail: '/work/B', worktree: false },
      { projectId: 'B', path: 'assembly-remote://workbox/work/B-wt', folder: '/work/B-wt', label: 'B-wt — fix', detail: '/work/B-wt', worktree: true }
    ] }
  ]);
}

// A folder is read-only only while a running session works in it, on the same machine.
{
  const session = (cwd: string, runtimeState: string | undefined, state = 'live', remoteProfileId: string | null = null) =>
    ({ cwd, runtimeState, state, executionEnvironment: remoteProfileId ? 'remote' : 'local', remoteProfileId });
  const working = session('/work/A/sub', 'working');
  assert.equal(sessionWorkingIn('/work/A', [working]), working);
  assert.equal(sessionWorkingIn('/work/A/', [working]), working);
  assert.equal(sessionWorkingIn('/work/A', [session('/work/A', 'suspended'), session('/work/A', 'failed'), session('/work/A', 'closed'), session('/work/A', 'ready', 'exited')]), null);
  assert.equal(sessionWorkingIn('/work/A-wt', [session('/work/A', 'working')]), null);
  // A viewed folder inside a running session's folder is read-only too.
  assert.equal(sessionWorkingIn('/work/A/sub/package', [working]), working);
  assert.equal(sessionWorkingIn('/work/A/sub-2', [working]), null);
  const remote = session('/work/B', 'working', 'live', 'workbox');
  assert.equal(sessionWorkingIn('assembly-remote://workbox/work/B', [remote]), remote);
  assert.equal(sessionWorkingIn('/work/B', [remote]), null);
  assert.equal(sessionWorkingIn('assembly-remote://workbox/work/B/package', [remote]), remote);
  assert.equal(sessionWorkingIn('/work/B/package', [remote]), null);
  const terminal = session('/work/C', undefined);
  assert.equal(sessionWorkingIn('/work/C', [terminal]), terminal);
}

console.log('projects.test.ts passed');

test('move destinations stay on the session machine', () => {
  const projects = [project('a', 'local'), project('b', 'box'), project('c', 'other')];
  assert.deepEqual(moveProjectDestinations(projects, { executionEnvironment: 'local' }).map(p => p.id), ['a']);
  assert.deepEqual(moveProjectDestinations(projects, { executionEnvironment: 'remote', remoteProfileId: 'box' }).map(p => p.id), ['b']);
  assert.deepEqual(moveProjectDestinations(projects, { executionEnvironment: 'remote', remoteProfileId: null }), []);
});
