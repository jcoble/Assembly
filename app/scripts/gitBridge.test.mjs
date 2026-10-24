/**
 * gitBridge.test.mjs — the browser-side git bridge, run against THIS repository.
 *
 * In the desktop app the source-control panel talks to Rust. In a browser there
 * is no Rust, so the dev server answers the same questions itself by running
 * `git` (see `src/lib/server/gitBridge.ts`). The whole point of the bridge is
 * that the two give back the SAME SHAPES, so this test checks the real answers
 * from the real repository this file lives in: a status, a commit history, the
 * files one commit touched, and the changes inside one of those files.
 *
 * It only ever reads. Nothing here stages, commits or writes anything, so it is
 * safe to run while there is work in progress in the checkout.
 *
 * Run: node --experimental-strip-types scripts/gitBridge.test.mjs
 */

import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { assignGitGraphLanes } from '../src/lib/shell/git/gitGraphLanes.ts';
import {
  GIT_BRIDGE_ROUTE_PREFIX,
  handleGitBridgeRequest,
  readGitCommitFileDiff,
  readGitCommitFiles,
  readGitCommitHistory,
  readGitFileDiff,
  readGitStatus,
  repositoryTop
} from '../src/lib/server/gitBridge.ts';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
/** The repository this checkout is: two levels up from `scripts/`. */
const root = path.resolve(scriptsDir, '..', '..');

// ── the working copy, shaped like the desktop app's status ──────────────────
{
  const status = await readGitStatus(root);
  assert.equal(typeof status.branch, 'string');
  assert.ok(status.branch.length > 0, 'this checkout has a branch');
  assert.equal(typeof status.ahead, 'number');
  assert.equal(typeof status.behind, 'number');
  assert.equal(typeof status.hasUpstream, 'boolean');
  assert.ok(Array.isArray(status.files));
  for (const file of status.files) {
    assert.equal(typeof file.relativePath, 'string');
    assert.ok(file.relativePath.length > 0);
    assert.equal(typeof file.indexStatus, 'string');
    assert.equal(typeof file.worktreeStatus, 'string');
    assert.equal(typeof file.status, 'string');
    assert.equal(typeof file.badge, 'string');
    assert.ok(file.badge.length <= 1, 'the badge is a single letter, like git prints');
  }
}

// ── the commit history, and its parents ─────────────────────────────────────
const firstHistoryPage = await readGitCommitHistory(root);
const history = firstHistoryPage.commits;
{
  assert.equal(firstHistoryPage.root, root);
  assert.equal(firstHistoryPage.relativePath, null);
  assert.ok(history.length > 5, 'this repository has a history to read');
  for (const entry of history) {
    assert.match(entry.sha, /^[0-9a-f]{40}$/, 'a full commit id');
    assert.ok(entry.sha.startsWith(entry.shortSha), 'the short id is the start of the long one');
    assert.equal(typeof entry.subject, 'string');
    assert.equal(typeof entry.author, 'string');
    assert.match(entry.committedAt, /^\d{4}-\d{2}-\d{2}T/, 'a machine-readable date');
    assert.ok(Array.isArray(entry.parentShas));
    assert.equal(entry.parentCount, entry.parentShas.length);
    assert.ok(entry.taskID === null || /^TSK-\d+$/.test(entry.taskID));
  }

  assert.equal(history.length, 24, 'one bounded page is returned');
  assert.equal(firstHistoryPage.nextCursor, '24');
  const secondHistoryPage = await readGitCommitHistory(root, firstHistoryPage.nextCursor);
  assert.equal(secondHistoryPage.commits.length, 24);
  assert.notEqual(
    secondHistoryPage.commits[0].sha,
    history.at(-1).sha,
    'the continuation reads the next page instead of repeating the first'
  );
}

// ── the files one commit touched ────────────────────────────────────────────
{
  const ordinary = history.find((entry) => entry.parentCount === 1);
  assert.ok(ordinary, 'this repository has an ordinary commit');
  const files = await readGitCommitFiles(root, ordinary.sha);
  assert.ok(files.length > 0, 'an ordinary commit touched at least one file');
  for (const file of files) {
    assert.equal(typeof file.relativePath, 'string');
    assert.ok(file.relativePath.length > 0);
    assert.ok(
      ['modified', 'added', 'deleted', 'renamed', 'copied', 'unmerged', ''].includes(file.status),
      `plain-word status, got ${file.status}`
    );
    assert.match(file.badge, /^[MADRCU!?]?$/);
  }

  // A merge commit brings no changes of its own, so git lists no files for it.
  // That is an answer, not a missing one — the panel has to say so plainly
  // rather than sit on a spinner.
  const merge = history.find((entry) => entry.parentCount > 1);
  if (merge) {
    assert.deepEqual(await readGitCommitFiles(root, merge.sha), []);
  }
}

// ── what changed inside one file of one commit ──────────────────────────────
{
  const ordinary = history.find((entry) => entry.parentCount === 1);
  const files = await readGitCommitFiles(root, ordinary.sha);
  const target = files.find((file) => file.status === 'modified') ?? files[0];
  const diff = await readGitCommitFileDiff(root, ordinary.sha, target.relativePath);

  assert.equal(diff.relativePath, target.relativePath);
  assert.equal(typeof diff.status, 'string');
  assert.equal(typeof diff.isBinary, 'boolean');
  if (!diff.isBinary) {
    assert.ok(diff.diff.includes('@@'), 'a text file diff carries at least one hunk header');
  }
}

// ── a file in the working copy, asked for by its full path on disk ──────────
{
  // Mirrors the desktop command exactly, including the surprising part: the
  // per-commit diff takes a path RELATIVE to the repository, while this one
  // takes the whole path on disk.
  const diff = await readGitFileDiff(root, path.join(root, 'README.md'));
  assert.equal(diff.relativePath, 'README.md');
  assert.equal(typeof diff.diff, 'string');
  assert.ok(
    ['clean', 'modified', 'added', 'deleted', 'renamed', 'copied', 'unmerged'].includes(diff.status)
  );
}

// ── asked from a subfolder, it still answers about the whole repository ─────
{
  // This is the trap the bridge exists to avoid: `git show <sha> -- <path>`
  // matches the path against the folder git is run in, so asking from a
  // subfolder with a repository-relative path quietly returns NOTHING and
  // reports success. Every read resolves the top of the repository first.
  const subfolder = path.join(root, 'tauri-svelte-preview');
  assert.equal(await repositoryTop(subfolder), root);
  assert.equal(await repositoryTop(root), root);

  const fromTop = await readGitStatus(root);
  const fromSubfolder = await readGitStatus(subfolder);
  assert.deepEqual(fromSubfolder, fromTop, 'the same status either way');

  const ordinary = history.find((entry) => entry.parentCount === 1);
  const files = await readGitCommitFiles(subfolder, ordinary.sha);
  assert.ok(files.length > 0, 'the file list is not empty just because we asked from inside');

  const diff = await readGitCommitFileDiff(subfolder, ordinary.sha, files[0].relativePath);
  assert.ok(diff.diff.length > 0, 'and neither is the diff');
  assert.deepEqual(diff, await readGitCommitFileDiff(root, ordinary.sha, files[0].relativePath));
}

// ── the graph drawn over this repository's real history ─────────────────────
{
  const layout = assignGitGraphLanes(history);
  assert.equal(layout.rows.length, history.length, 'every commit gets a row');
  assert.ok(layout.laneCount >= 1);

  const bySha = new Map(layout.rows.map((row) => [row.sha, row]));
  for (const entry of history) {
    const row = bySha.get(entry.sha);
    assert.ok(row, `${entry.shortSha} is on the graph`);
    assert.equal(row.isMerge, entry.parentCount > 1);
    const outgoing = row.edges.filter((edge) => edge.kind === 'parent');
    assert.equal(
      outgoing.length,
      new Set(entry.parentShas).size,
      `${entry.shortSha} draws one line per parent`
    );
  }

  const merge = layout.rows.find((row) => row.isMerge);
  if (merge) {
    assert.ok(merge.laneCount >= 2, 'a merge is drawn across at least two columns');
  }
}

// ── things it refuses to do ─────────────────────────────────────────────────
{
  await assert.rejects(
    () => readGitCommitFiles(root, '--upload-pack=evil'),
    /commit id/i,
    'an argument dressed up as a commit id is refused'
  );
  await assert.rejects(
    () => readGitCommitFileDiff(root, history[0].sha, '../outside.txt'),
    /relative/i,
    'a path climbing out of the repository is refused'
  );
  await assert.rejects(
    () => readGitFileDiff(root, path.join(tmpdir(), 'not-in-this-repo.txt')),
    /outside|metadata|not a file/i
  );

  const plainFolder = await mkdtemp(path.join(tmpdir(), 'mcb-git-bridge-'));
  try {
    await assert.rejects(() => readGitStatus(plainFolder), /not a git repository/i);
  } finally {
    await rm(plainFolder, { recursive: true, force: true });
  }
}

// ── the routes the dev server exposes ───────────────────────────────────────
{
  assert.equal(GIT_BRIDGE_ROUTE_PREFIX, '/__mcb/git/');

  const status = await handleGitBridgeRequest('/__mcb/git/status', { root });
  assert.equal(status.handled, true);
  assert.equal(status.statusCode, 200);
  assert.equal(typeof status.body.branch, 'string');

  const commitFiles = await handleGitBridgeRequest('/__mcb/git/commit-files', {
    root,
    sha: history[0].sha
  });
  assert.equal(commitFiles.statusCode, 200);
  assert.ok(Array.isArray(commitFiles.body));

  const unknown = await handleGitBridgeRequest('/__mcb/git/does-not-exist', { root });
  assert.equal(unknown.handled, false);

  const broken = await handleGitBridgeRequest('/__mcb/git/commit-files', { root, sha: '-evil' });
  assert.equal(broken.statusCode, 500);
  assert.match(broken.body.error, /commit id/i);

  // Writing is deliberately not on offer here: the browser preview reads the
  // repository and nothing else, so a stray click can never commit.
  const write = await handleGitBridgeRequest('/__mcb/git/commit', { root, message: 'nope' });
  assert.equal(write.handled, false);
}

console.log('gitBridge.test.mjs: all checks passed');
