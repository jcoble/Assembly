/**
 * commitSuggestion.test.mjs — the cheap suggested commit subject.
 *
 * No model is involved and none may be: this is a function of the staged file
 * names and their status letters. The test reads every branch of that, and the
 * "unstaged files are ignored" case is the one that matters most — a commit
 * records what is staged, so a suggestion drawn from anything else would
 * describe a commit that is not about to happen.
 */
import assert from 'node:assert/strict';

import {
  COMMIT_SUBJECT_LIMIT,
  commonPathPrefix,
  describeCommitSuggestion,
  suggestCommitMessage,
  suggestCommitVerb
} from '../src/lib/shell/git/components/commitSuggestion.ts';

function file(relativePath, badge, indexStatus = 'modified') {
  return { relativePath, indexStatus, worktreeStatus: '', status: indexStatus, badge };
}

assert.equal(suggestCommitMessage([]), '', 'nothing staged suggests nothing');
assert.equal(
  describeCommitSuggestion([]),
  'Stage something first — a suggestion is read from the staged files.'
);

assert.equal(
  suggestCommitMessage([file('src/lib/shell/git/gitService.ts', 'M')]),
  'Update src/lib/shell/git/gitService.ts'
);
assert.equal(suggestCommitMessage([file('src/new.ts', 'A', 'added')]), 'Add src/new.ts');
assert.equal(suggestCommitMessage([file('src/old.ts', 'D', 'deleted')]), 'Remove src/old.ts');

assert.equal(
  suggestCommitMessage([
    file('src/lib/shell/git/a.ts', 'M'),
    file('src/lib/shell/git/b.ts', 'M'),
    file('src/lib/shell/git/c.ts', 'M')
  ]),
  'Update 3 files in src/lib/shell/git'
);

assert.equal(
  suggestCommitMessage([file('src/a.ts', 'M'), file('docs/b.md', 'M')]),
  'Update 2 files',
  'files with nothing in common say so rather than inventing a folder'
);

assert.equal(
  suggestCommitMessage([file('docs/one.md', 'M'), file('docs/two.md', 'A', 'added')]),
  'Update the docs',
  'a mix of letters is always Update, and a docs-only change says docs'
);

assert.equal(
  suggestCommitMessage([file('scripts/gitPanel.test.mjs', 'M')]),
  'Update the tests'
);

// An unstaged or untracked row is not part of the next commit, so it is ignored.
const untracked = { ...file('notes.md', '?', ''), worktreeStatus: 'untracked' };
assert.equal(suggestCommitMessage([untracked]), '');
assert.equal(
  suggestCommitMessage([file('src/a.ts', 'M'), untracked]),
  'Update src/a.ts',
  'only the staged file reaches the suggestion'
);

assert.equal(suggestCommitVerb([]), 'Update');
assert.equal(suggestCommitVerb([file('a', 'R', 'renamed')]), 'Rename');
assert.equal(suggestCommitVerb([file('a', 'A'), file('b', 'D')]), 'Update');

assert.equal(commonPathPrefix([]), '');
assert.equal(commonPathPrefix(['a/b/c.ts', 'a/b/d.ts']), 'a/b');
assert.equal(commonPathPrefix(['a/b/c.ts', 'x/y.ts']), '');
assert.equal(commonPathPrefix(['top.ts', 'other.ts']), '');

const long = suggestCommitMessage([file(`src/${'deep/'.repeat(30)}file.ts`, 'M')]);
assert.ok(long.length <= COMMIT_SUBJECT_LIMIT, 'a subject stays readable in a git log');
assert.ok(long.endsWith('…'));

console.log('commitSuggestion tests passed');
