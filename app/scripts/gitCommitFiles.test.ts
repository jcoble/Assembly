/**
 * gitCommitFiles.test.ts — expanding a commit to see what it changed.
 *
 * The commit graph lets a row open in place and show the files that commit
 * touched, and clicking one of those files shows what changed inside it. Three
 * things about that are easy to get wrong and are pinned here:
 *
 *  1. Git is asked from the TOP of the repository. `git show <sha> -- <path>`
 *     matches the path against the folder git runs in, so asking from a
 *     subfolder returns nothing and reports success — a blank diff that reads
 *     as "nothing changed". The panel is often pointed at a session's working
 *     folder, which is not always the top.
 *  2. A merge commit lists no files. That is the answer, not a missing one, so
 *     it must end in a sentence rather than a spinner.
 *  3. A file whose name git could not print cleanly (a non-ASCII name comes
 *     back as octal escapes from the desktop app) cannot be asked about at all:
 *     git would match nothing and hand back a blank diff that looks clean. The
 *     row says so instead.
 *
 * Run: node --experimental-strip-types scripts/gitCommitFiles.test.ts
 */

globalThis.$state = (value) => value;

import assert from 'node:assert/strict';

const store = await import('../src/lib/shell/git/gitCommitFilesStore.svelte.ts');
const service = await import('../src/lib/shell/git/gitCommitFilesService.ts');

const {
  COMMIT_FILES_DESKTOP_ONLY_MESSAGE,
  MERGE_HAS_NO_CHANGES_MESSAGE,
  UNREADABLE_PATH_MESSAGE,
  commitFilesEntry,
  createGitCommitFilesState,
  describeCommitFiles,
  gitCommitFilesView,
  isCommitExpanded,
  isUnreadableGitPath,
  resetGitCommitFilesView,
  resetGitCommitFilesState,
  splitRepositoryPath,
  summarizeCommitFiles
} = store;
const { createGitCommitFilesService } = service;
const { GIT_DIFF_TIMEOUT_MESSAGE } = await import(
  '../src/lib/shell/git/gitBackendExtra.ts'
);

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/** The panel state the diff view reads, in the shape `gitPanelStore` makes it. */
function panelState(root) {
  return {
    root,
    selectedPath: '',
    selectedPaths: { compact: '', large: '' },
    diffOwner: null,
    diffRevision: 0,
    selectedDiff: null,
    diffLoading: false,
    diffError: ''
  };
}

function fileChange(relativePath, status = 'modified', badge = 'M') {
  return { relativePath, status, badge };
}

// ── pure helpers ────────────────────────────────────────────────────────────
{
  const state = createGitCommitFilesState();
  assert.equal(isCommitExpanded(state, 'abc'), false);
  assert.deepEqual(commitFilesEntry(state, 'abc').files, []);
  assert.equal(commitFilesEntry(state, 'abc').loaded, false);

  assert.equal(summarizeCommitFiles([]), 'No files');
  assert.equal(summarizeCommitFiles([fileChange('a.ts')]), '1 file');
  assert.equal(summarizeCommitFiles([fileChange('a.ts'), fileChange('b.ts')]), '2 files');

  assert.deepEqual(splitRepositoryPath('src/lib/git/thing.ts'), {
    name: 'thing.ts',
    folder: 'src/lib/git'
  });
  assert.deepEqual(splitRepositoryPath('README.md'), { name: 'README.md', folder: '' });
  // Git reports a whole folder of untracked files as the folder, with a
  // trailing slash. The row has to show a name, not an empty gap.
  assert.deepEqual(splitRepositoryPath('.vscode/'), { name: '.vscode/', folder: '' });
  assert.deepEqual(splitRepositoryPath('src/lib/notes/'), {
    name: 'notes/',
    folder: 'src/lib'
  });

  // Git prints a non-ASCII name as octal escapes unless it is told not to. The
  // desktop app does not tell it, so the name arrives unusable.
  assert.equal(isUnreadableGitPath('docs/r\\303\\251sum\\303\\251.md'), true);
  assert.equal(isUnreadableGitPath('"docs/plain.md"'), true);
  assert.equal(isUnreadableGitPath('docs/résumé.md'), false);
  assert.equal(isUnreadableGitPath('src/lib/git/thing.ts'), false);
  assert.equal(isUnreadableGitPath('C:\\\\windows\\\\path.ts'), false);
}

// ── what the open row says when there is no list to show ────────────────────
{
  const loading = { files: [], loading: true, loaded: false, error: '' };
  assert.equal(describeCommitFiles(loading, false), 'Reading what this commit changed…');

  const failed = { files: [], loading: false, loaded: false, error: 'git said no' };
  assert.equal(describeCommitFiles(failed, false), 'git said no');

  const merge = { files: [], loading: false, loaded: true, error: '' };
  assert.equal(describeCommitFiles(merge, true), MERGE_HAS_NO_CHANGES_MESSAGE);
  assert.match(MERGE_HAS_NO_CHANGES_MESSAGE, /merge/i);

  const empty = { files: [], loading: false, loaded: true, error: '' };
  assert.equal(describeCommitFiles(empty, false), 'This commit changed no files.');

  const listed = { files: [fileChange('a.ts')], loading: false, loaded: true, error: '' };
  assert.equal(describeCommitFiles(listed, false), '', 'a list of files needs no sentence');
}

// ── the file list is read from the top of the repository ────────────────────
{
  const asked = [];
  const panel = panelState('/repo/tauri-svelte-preview');
  const state = createGitCommitFilesState();
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async (root, sha) => {
      asked.push([root, sha]);
      return [fileChange('tauri-svelte-preview/src/app.ts')];
    },
    readDiff: async () => null,
    clearPanelSelection: () => {}
  });

  commitFiles.activate('/repo/tauri-svelte-preview');
  await commitFiles.toggleCommit('abc123', false);
  await settle();

  assert.deepEqual(asked, [['/repo', 'abc123']], 'git is asked from the top, not the subfolder');
  assert.equal(isCommitExpanded(state, 'abc123'), true);
  const entry = commitFilesEntry(state, 'abc123');
  assert.equal(entry.loading, false);
  assert.equal(entry.loaded, true);
  assert.deepEqual(
    entry.files.map((file) => file.relativePath),
    ['tauri-svelte-preview/src/app.ts']
  );

  // Closing releases the file payload; opening again reads it on demand.
  await commitFiles.toggleCommit('abc123', false);
  assert.equal(commitFilesEntry(state, 'abc123').loaded, false);
  await commitFiles.toggleCommit('abc123', false);
  await settle();
  assert.equal(asked.length, 2);
}

// ── a merge ends in a sentence, never a spinner ─────────────────────────────
{
  const state = createGitCommitFilesState();
  const commitFiles = createGitCommitFilesService({
    state,
    panel: panelState('/repo'),
    resolveTop: async () => '/repo',
    readFiles: async () => [],
    readDiff: async () => null,
    clearPanelSelection: () => {}
  });

  commitFiles.activate('/repo');
  await commitFiles.toggleCommit('merge1', true);
  await settle();

  const entry = commitFilesEntry(state, 'merge1');
  assert.equal(entry.loading, false, 'the spinner is off');
  assert.equal(entry.loaded, true);
  assert.deepEqual(entry.files, []);
  assert.equal(describeCommitFiles(entry, true), MERGE_HAS_NO_CHANGES_MESSAGE);
}

// ── nothing to ask: no desktop app and no dev server ────────────────────────
{
  const state = createGitCommitFilesState();
  const commitFiles = createGitCommitFilesService({
    state,
    panel: panelState('/repo'),
    resolveTop: async () => '/repo',
    readFiles: async () => null,
    readDiff: async () => null,
    clearPanelSelection: () => {}
  });

  commitFiles.activate('/repo');
  await commitFiles.toggleCommit('abc', false);
  await settle();

  const entry = commitFilesEntry(state, 'abc');
  assert.equal(entry.loading, false);
  assert.equal(entry.error, COMMIT_FILES_DESKTOP_ONLY_MESSAGE);
}

// ── clicking a file in a commit shows its changes ───────────────────────────
{
  const panel = panelState('/repo/app');
  const state = createGitCommitFilesState();
  const asked = [];
  let cleared = 0;
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => [fileChange('app/src/main.ts')],
    readDiff: async (root, sha, relativePath) => {
      asked.push([root, sha, relativePath]);
      return { relativePath, status: 'modified', diff: '@@ -1 +1 @@', isBinary: false };
    },
    clearPanelSelection: () => {
      cleared += 1;
    }
  });

  commitFiles.activate('/repo/app');
  await commitFiles.selectCommitFile('abc', fileChange('app/src/main.ts'));
  await settle();

  assert.equal(cleared, 1, 'the working-copy diff already on screen is let go of first');
  assert.deepEqual(asked, [['/repo', 'abc', 'app/src/main.ts']]);
  assert.equal(panel.selectedPath, 'app/src/main.ts');
  assert.equal(panel.diffLoading, false);
  assert.equal(panel.diffError, '');
  assert.equal(panel.selectedDiff.diff, '@@ -1 +1 @@');
  assert.equal(gitCommitFilesView(state).selectedCommitSha, 'abc');
  assert.equal(gitCommitFilesView(state).selectedRelativePath, 'app/src/main.ts');
}

// ── a file whose name git could not print is not asked about ────────────────
{
  const panel = panelState('/repo');
  const state = createGitCommitFilesState();
  let diffCalls = 0;
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => [],
    readDiff: async () => {
      diffCalls += 1;
      return null;
    },
    clearPanelSelection: () => {}
  });

  commitFiles.activate('/repo');
  await commitFiles.selectCommitFile('abc', fileChange('docs/r\\303\\251sum\\303\\251.md'));
  await settle();

  assert.equal(diffCalls, 0, 'git is never asked about a name it could not print');
  assert.equal(panel.diffLoading, false);
  assert.equal(panel.diffError, UNREADABLE_PATH_MESSAGE);
  assert.match(UNREADABLE_PATH_MESSAGE, /name/i);
}

// ── a diff nobody can read here says so instead of showing nothing ──────────
{
  const panel = panelState('/repo');
  const state = createGitCommitFilesState();
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => [],
    readDiff: async () => null,
    clearPanelSelection: () => {}
  });

  commitFiles.activate('/repo');
  await commitFiles.selectCommitFile('abc', fileChange('src/app.ts'));
  await settle();

  assert.equal(panel.diffLoading, false);
  assert.equal(panel.diffError, COMMIT_FILES_DESKTOP_ONLY_MESSAGE);
  assert.equal(panel.selectedDiff, null);
}

// ── a diff read that never answers ends in an error, not a spinner ───────
{
  const panel = panelState('/repo');
  const state = createGitCommitFilesState();
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => [],
    readDiff: async () => new Promise(() => {}),
    clearPanelSelection: () => {},
    diffTimeoutMs: 5
  });

  commitFiles.activate('/repo');
  // Node does not wait on AbortSignal.timeout, so hold the process open until it fires.
  const keepAlive = setInterval(() => {}, 1000);
  await commitFiles.selectCommitFile('abc', fileChange('src/app.ts'));
  clearInterval(keepAlive);

  assert.equal(panel.diffLoading, false);
  assert.equal(panel.diffError, GIT_DIFF_TIMEOUT_MESSAGE);
  assert.equal(panel.selectedDiff, null);
}

// ── leaving the owning surface cannot strand the shared diff spinner ─────
{
  const panel = panelState('/repo');
  const state = createGitCommitFilesState();
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => [],
    readDiff: async () => new Promise(() => {}),
    clearPanelSelection: () => {
      panel.selectedPath = '';
      panel.selectedDiff = null;
      panel.diffLoading = false;
      panel.diffError = '';
    },
    diffTimeoutMs: 5
  });

  commitFiles.activate('/repo');
  const pending = commitFiles.selectCommitFile('abc', fileChange('src/app.ts'));
  assert.equal(panel.diffLoading, true);
  commitFiles.release();
  assert.equal(panel.diffLoading, false, 'release clears an invalidated read immediately');
  assert.equal(panel.selectedPath, '');
  await pending;
  assert.equal(panel.diffLoading, false, 'the stale timeout cannot restore the spinner');
}

// ── pointing the panel at another repository forgets the old one ────────────
{
  const state = createGitCommitFilesState();
  gitCommitFilesView(state).expanded['abc'] = true;
  state.byCommit['abc'] = { files: [fileChange('a.ts')], loading: false, loaded: true, error: '' };
  gitCommitFilesView(state).selectedCommitSha = 'abc';

  resetGitCommitFilesState(state, '/other');
  assert.deepEqual(gitCommitFilesView(state).expanded, {});
  assert.deepEqual(state.byCommit, {});
  assert.equal(gitCommitFilesView(state).selectedCommitSha, '');
  assert.equal(state.root, '/other');
  assert.equal(state.repositoryTop, null);
}

// ── compact and large retain independent interaction over one payload store ──
{
  const state = createGitCommitFilesState();
  const panel = panelState('/repo');
  let fileReads = 0;
  let diffReads = 0;
  const options = {
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => {
      fileReads += 1;
      return [fileChange('shared.ts')];
    },
    readDiff: async (_root, _sha, relativePath) => {
      diffReads += 1;
      const mine = diffReads;
      await new Promise((resolve) => setTimeout(resolve, relativePath === 'compact.ts' ? 20 : 0));
      return { relativePath, status: 'modified', diff: `diff ${mine}`, isBinary: false };
    },
    clearPanelSelection: () => {}
  };
  const compact = createGitCommitFilesService({ ...options, owner: 'compact' });
  const large = createGitCommitFilesService({ ...options, owner: 'large' });

  compact.activate('/repo');
  await compact.toggleCommit('abc', false);
  await large.toggleCommit('abc', false);
  assert.equal(fileReads, 1, 'both surfaces reuse one loaded commit payload');
  assert.equal(isCommitExpanded(state, 'abc', 'compact'), true);
  assert.equal(isCommitExpanded(state, 'abc', 'large'), true);

  await compact.toggleCommit('abc', false);
  assert.equal(commitFilesEntry(state, 'abc').loaded, true, 'large still owns the shared payload');

  const slow = compact.selectCommitFile('abc', fileChange('compact.ts'));
  const quick = large.selectCommitFile('abc', fileChange('large.ts'));
  await Promise.all([slow, quick]);
  assert.equal(gitCommitFilesView(state, 'compact').selectedRelativePath, 'compact.ts');
  assert.equal(gitCommitFilesView(state, 'large').selectedRelativePath, 'large.ts');
  assert.equal(panel.selectedDiff.relativePath, 'large.ts', 'only the current surface materializes');

  compact.release();
  assert.deepEqual(gitCommitFilesView(state, 'compact').expanded, {});
  assert.deepEqual(gitCommitFilesView(state, 'large').expanded, {});
  assert.deepEqual(state.byCommit, {}, 'session release drops the shared payload');
}

// ── in-flight payloads follow whichever view still owns the expansion ───────
{
  const state = createGitCommitFilesState();
  const panel = panelState('/repo');
  const finishReads = [];
  const options = {
    state,
    panel,
    resolveTop: async () => '/repo',
    readFiles: async () => new Promise((resolve) => { finishReads.push(resolve); }),
    readDiff: async () => null,
    clearPanelSelection: () => {}
  };
  const compact = createGitCommitFilesService({ ...options, owner: 'compact' });
  const large = createGitCommitFilesService({ ...options, owner: 'large' });

  compact.activate('/repo');
  const pending = compact.toggleCommit('shared', false);
  await settle();
  await large.toggleCommit('shared', false);
  await compact.toggleCommit('shared', false);
  finishReads.shift()([fileChange('shared.ts')]);
  await pending;

  assert.equal(isCommitExpanded(state, 'shared', 'large'), true);
  assert.equal(commitFilesEntry(state, 'shared').loading, false);
  assert.equal(commitFilesEntry(state, 'shared').loaded, true);

  await large.toggleCommit('shared', false);
  assert.equal(state.byCommit.shared, undefined, 'the last owner releases the settled payload');
  assert.deepEqual(gitCommitFilesView(state, 'compact').expanded, {});
  assert.deepEqual(gitCommitFilesView(state, 'large').expanded, {});

  const reopened = compact.toggleCommit('shared', false);
  await settle();
  finishReads.shift()([fileChange('reopened.ts')]);
  await reopened;
  assert.equal(commitFilesEntry(state, 'shared').files[0].relativePath, 'reopened.ts');

  await compact.toggleCommit('shared', false);
  // Resetting the request origin must still finish for the other open view.
  const transferred = large.toggleCommit('transferred', false);
  await settle();
  await compact.toggleCommit('transferred', false);
  resetGitCommitFilesView(state, 'large');
  finishReads.shift()([fileChange('transferred.ts')]);
  await transferred;

  assert.equal(isCommitExpanded(state, 'transferred', 'compact'), true);
  assert.equal(commitFilesEntry(state, 'transferred').loading, false);
  assert.equal(commitFilesEntry(state, 'transferred').loaded, true);

  await compact.toggleCommit('transferred', false);
  // With no remaining owner, reset drops the payload and rejects the late answer.
  const released = large.toggleCommit('released', false);
  await settle();
  resetGitCommitFilesView(state, 'large');
  finishReads.shift()([fileChange('late.ts')]);
  await released;

  assert.equal(state.byCommit.released, undefined);

  // An old request cannot replace a new opposite-owner request for the same SHA.
  const old = compact.toggleCommit('race', false);
  await settle();
  await large.toggleCommit('race', false);
  await compact.toggleCommit('race', false);
  await large.toggleCommit('race', false);
  const newer = large.toggleCommit('race', false);
  await settle();
  finishReads[1]([fileChange('new.ts')]);
  await newer;
  finishReads[0]([fileChange('old.ts')]);
  await old;

  assert.equal(commitFilesEntry(state, 'race').files[0].relativePath, 'new.ts');
}

// ── a slow answer never overwrites a newer one ──────────────────────────────
{
  const state = createGitCommitFilesState();
  const panel = panelState('/repo');
  const commitFiles = createGitCommitFilesService({
    state,
    panel,
    resolveTop: async () => '/repo',
    readDiff: async (root, sha, relativePath) => {
      // The superseded read may stop before it reaches git, so tell them apart by path.
      const mine = relativePath === 'slow.ts' ? 1 : 2;
      await new Promise((resolve) => setTimeout(resolve, mine === 1 ? 20 : 0));
      return { relativePath, status: 'modified', diff: `diff ${mine}`, isBinary: false };
    },
    readFiles: async () => [],
    clearPanelSelection: () => {}
  });

  commitFiles.activate('/repo');
  const slow = commitFiles.selectCommitFile('abc', fileChange('slow.ts'));
  const quick = commitFiles.selectCommitFile('abc', fileChange('quick.ts'));
  await Promise.all([slow, quick]);
  await new Promise((resolve) => setTimeout(resolve, 40));

  assert.equal(panel.selectedPath, 'quick.ts');
  assert.equal(panel.selectedDiff.diff, 'diff 2', 'the slow answer was dropped');
  assert.equal(panel.diffLoading, false);
}

console.log('gitCommitFiles.test.ts: all checks passed');
