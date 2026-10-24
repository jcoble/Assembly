/**
 * resourceDiskViewModel.test.mjs — which disk rows may be removed, and what the
 * dialog says before one is.
 *
 * The rule under test is the only one that matters here: a Rust build folder
 * and an installed dependency folder can be removed, and nothing else can —
 * not a repository's history, not another checkout, not anything the app
 * stores. The backend enforces the same rule; this is the half a person sees.
 */
import assert from 'node:assert/strict';
import test from 'node:test';

import {
  buildReclaimRequest,
  describeReclaimQuestion,
  diskEntryIsReclaimable,
  formatDiskMeasuredAgo,
  shapeDiskReport
} from '../src/lib/shell/resources/resourceDiskViewModel.ts';

function entry(overrides) {
  return {
    id: overrides.path,
    label: overrides.label ?? overrides.path,
    path: overrides.path,
    category: overrides.category,
    categoryLabel: overrides.categoryLabel ?? 'Something',
    bytes: overrides.bytes ?? 1024,
    reclaimable: overrides.reclaimable ?? true,
    truncated: false
  };
}

const report = {
  generatedAtMs: 1_000,
  totalBytes: 70e9,
  sections: [
    {
      id: '/repo',
      label: 'mac-command-bar',
      root: '/repo',
      bytes: 68e9,
      measuredAtMs: 1_000,
      entries: [
        entry({ path: '/repo/.git', category: 'git-directory', bytes: 3e9 }),
        entry({
          path: '/repo/target',
          category: 'cargo-target',
          categoryLabel: 'Rust build output',
          bytes: 60e9
        }),
        entry({ path: '/repo/web/node_modules', category: 'node-modules', bytes: 5e9 })
      ]
    },
    {
      id: 'app-stores',
      label: 'App stores',
      root: '',
      bytes: 2e9,
      measuredAtMs: 1_000,
      entries: [
        entry({ path: '/home/.claude/projects', category: 'transcript-store', bytes: 2e9 })
      ]
    }
  ]
};

test('only build output and installed dependencies may be removed', () => {
  assert.equal(diskEntryIsReclaimable(entry({ path: '/a', category: 'cargo-target' })), true);
  assert.equal(diskEntryIsReclaimable(entry({ path: '/a', category: 'node-modules' })), true);
  for (const category of ['git-directory', 'worktree', 'transcript-store', 'usage-store']) {
    assert.equal(diskEntryIsReclaimable(entry({ path: '/a', category })), false, category);
    assert.equal(buildReclaimRequest(entry({ path: '/a', category })), null, category);
  }
});

test('a backend that says a history folder is reclaimable is still refused', () => {
  const rogue = entry({ path: '/repo/.git', category: 'git-directory', reclaimable: true });

  assert.equal(diskEntryIsReclaimable(rogue), false);
  assert.equal(buildReclaimRequest(rogue), null);
});

test('sections list the biggest folder first and add up what can be reclaimed', () => {
  const view = shapeDiskReport(report);

  assert.deepEqual(
    view.sections.map((section) => section.label),
    ['mac-command-bar', 'App stores']
  );
  assert.deepEqual(
    view.sections[0].entries.map((row) => row.path),
    ['/repo/target', '/repo/web/node_modules', '/repo/.git']
  );
  assert.equal(view.sections[0].reclaimableBytes, 65e9);
  assert.equal(view.sections[1].reclaimableBytes, 0);
  assert.equal(view.reclaimableBytes, 65e9);
  assert.equal(view.reclaimableLabel, '60.5 GB');
  // The share drives the bar behind the row, so it never exceeds the section.
  assert.ok(view.sections[0].entries.every((row) => row.share >= 0 && row.share <= 1));
});

test('the dialog names the exact folder and says what puts it back', () => {
  const question = describeReclaimQuestion(
    entry({
      path: '/repo/target',
      category: 'cargo-target',
      categoryLabel: 'Rust build output',
      bytes: 60e9
    })
  );

  assert.equal(question.lines[0], '/repo/target');
  assert.equal(question.lines[1], 'A build puts it back, slower the first time.');
  assert.equal(question.intro, 'Rust build output, 55.9 GB.');
  assert.equal(question.confirmLabel, 'Remove 55.9 GB');
});

test('the request carries the path and size the dialog showed', () => {
  const row = entry({ path: '/repo/web/node_modules', category: 'node-modules', bytes: 5e9 });

  assert.deepEqual(buildReclaimRequest(row), {
    path: '/repo/web/node_modules',
    category: 'node-modules',
    expectedBytes: 5e9
  });
});

test('a measurement says how old it is', () => {
  assert.equal(formatDiskMeasuredAgo(1_000, 31_000), 'measured just now');
  assert.equal(formatDiskMeasuredAgo(1_000, 181_000), 'measured 3m ago');
});
