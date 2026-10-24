/**
 * explorerStore.test.mjs — the file explorer's view maths.
 *
 * The rune store itself (`explorerStore.svelte.ts`) cannot be loaded here: it
 * uses `$state`, a Svelte compiler rune that does not exist in plain Node. So
 * every rule worth testing lives in `explorerTree.ts` — folder expansion, the
 * filter, the visible-row window, and revealing a file — and the store is a thin
 * wrapper that only assigns the values these functions return.
 */
import assert from 'node:assert/strict';

import {
  buildExplorerView,
  expandedForRecord,
  projectRootLabel,
  rowIndexForPath,
  scrollTopForPath,
  toggleFolder,
  EXPLORER_FALLBACK_VIEWPORT_HEIGHT,
  EXPLORER_ROW_HEIGHT
} from '../src/lib/shell/files/explorer/explorerTree.ts';

/** Minimal stand-in for one file the backend scan returned. */
function record(relativePath) {
  const fileName = relativePath.split('/').at(-1);
  return {
    path: `/repo/${relativePath}`,
    relativePath,
    fileName,
    language: fileName.split('.').at(-1),
    byteCount: 120
  };
}

const project = [
  record('README.md'),
  record('src/lib/shell/deep.ts'),
  record('src/lib/alpha.ts'),
  record('src/routes/page.svelte')
];

/** The same view options every small test uses. */
function view(overrides = {}) {
  return buildExplorerView({
    records: project,
    query: '',
    expandedFolderIds: new Set(),
    scrollTop: 0,
    viewportHeight: 400,
    ...overrides
  });
}

function rowLabels(rows) {
  return rows.map((row) => `${'  '.repeat(row.level)}${row.node.name}`);
}

// a closed tree shows the top level only: folders first, then files
{
  const result = view();
  assert.deepEqual(rowLabels(result.rows), ['src', 'README.md']);
  assert.equal(result.matchedRecords.length, 4, 'nothing is filtered out');
  assert.equal(result.autoExpandFolders, false);
}

// opening a folder adds its children one level deeper; closing it takes them away
{
  const opened = toggleFolder(new Set(), 'folder:src');
  assert.deepEqual([...opened], ['folder:src'], 'toggling a closed folder opens it');
  assert.deepEqual(rowLabels(view({ expandedFolderIds: opened }).rows), [
    'src',
    '  lib',
    '  routes',
    'README.md'
  ]);

  const closedAgain = toggleFolder(opened, 'folder:src');
  assert.deepEqual([...closedAgain], [], 'toggling an open folder closes it');
  assert.deepEqual([...opened], ['folder:src'], 'the original set is never mutated');
}

// opening every folder on the way down exposes the deepest file
{
  const opened = new Set(['folder:src', 'folder:src/lib', 'folder:src/lib/shell']);
  assert.deepEqual(rowLabels(view({ expandedFolderIds: opened }).rows), [
    'src',
    '  lib',
    '    shell',
    '      deep.ts',
    '    alpha.ts',
    '  routes',
    'README.md'
  ]);
}

// revealing a file opens exactly the folders above it, keeping what was open
{
  const target = project[1]; // src/lib/shell/deep.ts
  const opened = expandedForRecord(new Set(['folder:kept']), target);
  assert.deepEqual(
    [...opened].sort(),
    ['folder:kept', 'folder:src', 'folder:src/lib', 'folder:src/lib/shell'],
    'ancestors added, existing expansion kept'
  );
  const rows = view({ expandedFolderIds: opened }).rows;
  assert.ok(rowIndexForPath(rows, target.path) >= 0, 'the revealed file is now a visible row');
}

// filtering narrows the records AND opens every folder, so a deep match shows up
{
  const result = view({ query: 'deep' });
  assert.equal(result.autoExpandFolders, true);
  assert.deepEqual(
    result.matchedRecords.map((entry) => entry.relativePath),
    ['src/lib/shell/deep.ts']
  );
  assert.deepEqual(rowLabels(result.rows), ['src', '  lib', '    shell', '      deep.ts']);
}

// a filter that matches nothing leaves no rows at all
{
  assert.deepEqual(view({ query: 'nothing-here' }).rows, []);
}

// the rendered window is a slice around the scroll position, with spacers that
// add up to the full tree height
{
  const many = Array.from({ length: 200 }, (_, index) =>
    record(`file-${String(index).padStart(3, '0')}.ts`)
  );
  const result = buildExplorerView({
    records: many,
    query: '',
    expandedFolderIds: new Set(),
    scrollTop: 480, // row 20 at 24px rows
    viewportHeight: 120, // five rows visible
    rowHeight: 24,
    overscanRows: 2
  });

  assert.equal(result.rows.length, 200, 'every file is a row');
  assert.equal(result.virtual.startIndex, 18, 'two overscan rows above the first visible row');
  assert.equal(result.virtual.endIndex, 27, 'five visible rows plus two overscan below');
  assert.equal(result.virtual.rows.length, 9, 'only the window is rendered');
  assert.equal(result.virtual.topSpacerHeight, 18 * 24);
  assert.equal(result.virtual.bottomSpacerHeight, (200 - 27) * 24);
  assert.equal(
    result.virtual.topSpacerHeight + result.virtual.rows.length * 24 + result.virtual.bottomSpacerHeight,
    result.virtual.totalHeight,
    'spacers plus rendered rows equal the full tree height'
  );
}

// expansion and windowing interact: opening a folder pushes later rows down, and
// the window follows the scroll position rather than the record order
{
  const many = Array.from({ length: 60 }, (_, index) =>
    record(`src/lib/file-${String(index).padStart(2, '0')}.ts`)
  );
  const closed = buildExplorerView({
    records: many,
    query: '',
    expandedFolderIds: new Set(),
    scrollTop: 0,
    viewportHeight: 240,
    rowHeight: 24,
    overscanRows: 0
  });
  assert.equal(closed.rows.length, 1, 'a closed root folder is a single row');

  const open = buildExplorerView({
    records: many,
    query: '',
    expandedFolderIds: new Set(['folder:src', 'folder:src/lib']),
    scrollTop: 24 * 30,
    viewportHeight: 240,
    rowHeight: 24,
    overscanRows: 0
  });
  assert.equal(open.rows.length, 62, 'two folder rows plus sixty files');
  assert.equal(open.virtual.startIndex, 30);
  assert.equal(open.virtual.rows[0].node.name, 'file-28.ts', 'row 30 is the 29th file');
}

// an unmeasured panel (height zero, because it is parked or behind another tab)
// falls back to a sensible window instead of rendering a single row
{
  const many = Array.from({ length: 100 }, (_, index) => record(`file-${index}.ts`));
  const result = buildExplorerView({
    records: many,
    query: '',
    expandedFolderIds: new Set(),
    scrollTop: 0,
    viewportHeight: 0,
    rowHeight: EXPLORER_ROW_HEIGHT,
    overscanRows: 0
  });
  assert.equal(
    result.virtual.endIndex,
    Math.ceil(EXPLORER_FALLBACK_VIEWPORT_HEIGHT / EXPLORER_ROW_HEIGHT),
    'the fallback height decides the window'
  );
}

// scrolling a file into view
{
  const many = Array.from({ length: 100 }, (_, index) =>
    record(`file-${String(index).padStart(3, '0')}.ts`)
  );
  const rows = buildExplorerView({
    records: many,
    query: '',
    expandedFolderIds: new Set(),
    scrollTop: 0,
    viewportHeight: 120,
    rowHeight: 24,
    overscanRows: 0
  }).rows;

  assert.equal(rowIndexForPath(rows, '/repo/file-020.ts'), 20);
  assert.equal(rowIndexForPath(rows, '/repo/not-scanned.ts'), -1);

  assert.equal(
    scrollTopForPath(rows, '/repo/file-020.ts', 0, 120, 24),
    504 - 120,
    'a row below the viewport scrolls up to its bottom edge'
  );
  assert.equal(
    scrollTopForPath(rows, '/repo/file-002.ts', 0, 120, 24),
    0,
    'a row already in view does not move the tree'
  );
  assert.equal(
    scrollTopForPath(rows, '/repo/not-scanned.ts', 96, 120, 24),
    96,
    'an unknown path leaves the scroll position alone'
  );
}

// the heading shows the project folder's own name
{
  assert.equal(projectRootLabel('/Users/me/dev/work/mac-command-bar'), 'mac-command-bar');
  assert.equal(projectRootLabel('/Users/me/dev/work/mac-command-bar/'), 'mac-command-bar');
  assert.equal(projectRootLabel(null), '');
}

console.log('explorerStore: all tests passed');
