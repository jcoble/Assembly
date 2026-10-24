import assert from 'node:assert/strict';
import test from 'node:test';

import { matchNotionProject } from '../src/lib/shell/notionProjectMatch.ts';
import { normalizeTasksView, projectToShow } from '../src/lib/shell/panels/tasks/tasksView.ts';

const PROJECTS = ['MacCommandBar', 'EdiPlatform'];

test("the session's Notion project wins, otherwise the saved project is kept", () => {
  const shown = (root: string) => projectToShow(matchNotionProject(root, PROJECTS), 'EdiPlatform');

  assert.equal(shown('/home/me/dev/work/mac-command-bar'), 'MacCommandBar');
  assert.equal(shown('/home/me/dev/work/TestEmptyFolderWGit'), 'EdiPlatform');
  assert.equal(shown(''), 'EdiPlatform');
});

test('junk in a stored tasks view becomes its default', () => {
  assert.deepEqual(
    normalizeTasksView({
      search: 42,
      filters: { status: ['Doing', 7, 'Doing'], priority: 'High' },
      projectFilter: null,
      sortBy: 'createdAt',
      sortDirection: 'sideways'
    }),
    {
      search: '',
      filters: { status: ['Doing'], priority: [] },
      projectFilter: '',
      sortBy: 'taskNumber',
      sortDirection: 'desc'
    }
  );
  assert.equal(normalizeTasksView('all'), null);
});

// Pins 7f64fd07f (one Tasks view shared by every session): a valid saved view must come back as saved.
test('a saved tasks view comes back exactly as it was saved', () => {
  const saved = {
    search: 'login',
    filters: { status: ['Doing', 'Blocked'], priority: ['High'] },
    projectFilter: 'MacCommandBar',
    sortBy: 'title',
    sortDirection: 'asc'
  };
  assert.deepEqual(normalizeTasksView(structuredClone(saved)), saved);
});

test('a missing or malformed stored view is not a view, so the panel keeps its defaults', () => {
  for (const value of [null, undefined, [], ['Doing'], 7, true]) {
    assert.equal(normalizeTasksView(value), null, JSON.stringify(value));
  }
});

test('stored filters keep only real values, and missing filters mean unfiltered', () => {
  assert.deepEqual(normalizeTasksView({ filters: null })?.filters, { status: [], priority: [] });
  assert.deepEqual(normalizeTasksView({})?.filters, { status: [], priority: [] });
  assert.deepEqual(
    normalizeTasksView({ filters: { status: ['', 'Todo', null], priority: ['Low', 'Low', ''] } })?.filters,
    { status: ['Todo'], priority: ['Low'] }
  );
});

// Pins 6704d4fdf and 71a124879: an empty saved project ("All projects") must not block the session's project.
test("an empty saved project does not hide the session's own project", () => {
  assert.equal(projectToShow('MacCommandBar', ''), 'MacCommandBar');
  assert.equal(projectToShow('', ''), '');
});
