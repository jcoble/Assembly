import assert from 'node:assert/strict';
import {
  parseUnifiedDiff,
  summarizeParsedDiff
} from '../src/lib/shell/git/parseUnifiedDiff.ts';

/** Build diff text from lines, the way git writes it. */
const diffText = (...lines) => lines.join('\n');

// ── a plain one-hunk modification ────────────────────────────────────────────
{
  const parsed = parseUnifiedDiff(
    diffText(
      'diff --git a/src/app.ts b/src/app.ts',
      'index 1111111..2222222 100644',
      '--- a/src/app.ts',
      '+++ b/src/app.ts',
      '@@ -10,4 +10,5 @@ export function start() {',
      ' const before = 1;',
      '-const gone = 2;',
      '+const added = 2;',
      '+const alsoAdded = 3;',
      ' const after = 4;'
    )
  );

  assert.equal(parsed.sections.length, 1);
  assert.equal(parsed.sections[0].label, '', 'a diff with no marker lines is unlabeled');
  assert.equal(parsed.hunks.length, 1);
  assert.equal(parsed.oldPath, 'src/app.ts');
  assert.equal(parsed.newPath, 'src/app.ts');
  assert.equal(parsed.addedCount, 2);
  assert.equal(parsed.removedCount, 1);
  assert.equal(parsed.isEmpty, false);
  assert.equal(parsed.isBinary, false);

  const hunk = parsed.hunks[0];
  assert.equal(hunk.heading, 'export function start() {');
  assert.equal(hunk.beforeStart, 10);
  assert.equal(hunk.beforeCount, 4);
  assert.equal(hunk.afterStart, 10);
  assert.equal(hunk.afterCount, 5);
  assert.equal(hunk.lines.length, 5);

  assert.deepEqual(
    hunk.lines.map((line) => [line.kind, line.text, line.beforeLine, line.afterLine]),
    [
      ['context', 'const before = 1;', 10, 10],
      ['removed', 'const gone = 2;', 11, null],
      ['added', 'const added = 2;', null, 11],
      ['added', 'const alsoAdded = 3;', null, 12],
      ['context', 'const after = 4;', 12, 13]
    ],
    'line numbers advance separately on each side'
  );

  assert.equal(parsed.before, 'const before = 1;\nconst gone = 2;\nconst after = 4;');
  assert.equal(
    parsed.after,
    'const before = 1;\nconst added = 2;\nconst alsoAdded = 3;\nconst after = 4;'
  );
  assert.equal(summarizeParsedDiff(parsed), '2 lines added · 1 line removed');
}

// ── a combined staged + working-tree diff (what the backend concatenates) ────
{
  const parsed = parseUnifiedDiff(
    diffText(
      '## Staged',
      'diff --git a/notes.md b/notes.md',
      '--- a/notes.md',
      '+++ b/notes.md',
      '@@ -1,2 +1,2 @@',
      '-old title',
      '+staged title',
      ' body',
      '',
      '## Working tree',
      'diff --git a/notes.md b/notes.md',
      '--- a/notes.md',
      '+++ b/notes.md',
      '@@ -1,2 +1,2 @@',
      '-staged title',
      '+working title',
      ' body'
    )
  );

  assert.deepEqual(
    parsed.sections.map((section) => section.label),
    ['Staged', 'Working tree'],
    'the two marker lines split the text into two labeled sections'
  );
  assert.equal(parsed.hunks.length, 2, 'hunks from both sections are listed in order');
  assert.equal(parsed.addedCount, 2);
  assert.equal(parsed.removedCount, 2);
  assert.equal(parsed.sections[0].after, 'staged title\nbody');
  assert.equal(parsed.sections[1].before, 'staged title\nbody');
  assert.equal(parsed.before, 'old title\nbody', 'before comes from the first section');
  assert.equal(parsed.after, 'working title\nbody', 'after comes from the last section');
}

// ── a brand new file ─────────────────────────────────────────────────────────
{
  const parsed = parseUnifiedDiff(
    diffText(
      'diff --git a/new.txt b/new.txt',
      'new file mode 100644',
      'index 0000000..3333333',
      '--- /dev/null',
      '+++ b/new.txt',
      '@@ -0,0 +1,2 @@',
      '+first',
      '+second',
      '\\ No newline at end of file'
    )
  );

  assert.equal(parsed.isNewFile, true);
  assert.equal(parsed.isDeletedFile, false);
  assert.equal(parsed.oldPath, null, '/dev/null is not a path');
  assert.equal(parsed.newPath, 'new.txt');
  assert.equal(parsed.before, '');
  assert.equal(parsed.after, 'first\nsecond');
  assert.equal(parsed.addedCount, 2);
  assert.equal(parsed.removedCount, 0);
  assert.equal(
    parsed.hunks[0].lines.at(-1).kind,
    'note',
    "git's no-newline marker is kept as a note, not counted as a change"
  );
  assert.equal(summarizeParsedDiff(parsed), '2 lines added · new file');
}

// ── a deleted file ───────────────────────────────────────────────────────────
{
  const parsed = parseUnifiedDiff(
    diffText(
      'diff --git a/gone.txt b/gone.txt',
      'deleted file mode 100644',
      '--- a/gone.txt',
      '+++ /dev/null',
      '@@ -1,2 +0,0 @@',
      '-first',
      '-second'
    )
  );

  assert.equal(parsed.isDeletedFile, true);
  assert.equal(parsed.isNewFile, false);
  assert.equal(parsed.before, 'first\nsecond');
  assert.equal(parsed.after, '');
  assert.equal(parsed.removedCount, 2);
  assert.equal(summarizeParsedDiff(parsed), '2 lines removed · file deleted');
}

// ── a binary file ────────────────────────────────────────────────────────────
{
  const parsed = parseUnifiedDiff(
    diffText(
      'diff --git a/logo.png b/logo.png',
      'index 4444444..5555555 100644',
      'Binary files a/logo.png and b/logo.png differ'
    )
  );

  assert.equal(parsed.isBinary, true);
  assert.equal(parsed.hunks.length, 0);
  assert.equal(parsed.isEmpty, false, 'a binary change is a change, not an empty diff');
  assert.equal(
    summarizeParsedDiff(parsed),
    'This is a binary file, so there is no line-by-line comparison.'
  );
}

// ── a renamed file ───────────────────────────────────────────────────────────
{
  const parsed = parseUnifiedDiff(
    diffText(
      'diff --git a/old/name.ts b/new/name.ts',
      'similarity index 96%',
      'rename from old/name.ts',
      'rename to new/name.ts',
      '@@ -1 +1 @@',
      '-one',
      '+two'
    )
  );

  assert.equal(parsed.oldPath, 'old/name.ts');
  assert.equal(parsed.newPath, 'new/name.ts');
  assert.equal(parsed.hunks[0].beforeCount, 1, 'a hunk header with no count means one line');
  assert.equal(parsed.hunks[0].afterCount, 1);
}

// ── an empty diff, and blank context lines inside a hunk ─────────────────────
{
  const empty = parseUnifiedDiff('');
  assert.deepEqual(empty.sections, []);
  assert.equal(empty.isEmpty, true);
  assert.equal(empty.before, '');
  assert.equal(empty.after, '');
  assert.equal(summarizeParsedDiff(empty), 'No line changes to show.');

  // Some producers emit a truly empty line for a blank context line.
  const blanks = parseUnifiedDiff(
    diffText('@@ -1,3 +1,3 @@', ' first', '', '-third', '+changed third')
  );
  assert.deepEqual(
    blanks.hunks[0].lines.map((line) => line.kind),
    ['context', 'context', 'removed', 'added'],
    'a bare empty line inside a hunk counts as blank context'
  );
  assert.equal(blanks.before, 'first\n\nthird');
}

// ── junk after the hunk budget is ignored, not folded into the hunk ──────────
{
  const parsed = parseUnifiedDiff(
    diffText('@@ -1,1 +1,1 @@', '-one', '+two', 'trailing noise that is not diff content')
  );
  assert.equal(parsed.hunks[0].lines.length, 2, 'the hunk stops when its line budget runs out');
  assert.equal(parsed.addedCount, 1);
  assert.equal(parsed.removedCount, 1);
}

console.log('parseUnifiedDiff: all tests passed');
