import assert from 'node:assert/strict';
import { parseUnifiedDiff } from '../src/lib/shell/git/parseUnifiedDiff.ts';
import {
  changedFileTree,
  diffTextOf,
  splitDiffByFile,
  splitRows,
  unifiedRows,
  type DiffRow
} from '../src/lib/shell/git/diffRows.ts';

const text = (...lines: string[]) => lines.join('\n');

/** A 20-line file where line 5 changed and line 15 was removed. */
const twoHunks = parseUnifiedDiff(
  text(
    'diff --git a/a.ts b/a.ts',
    '--- a/a.ts',
    '+++ b/a.ts',
    '@@ -4,3 +4,3 @@',
    ' four',
    '-five',
    '+FIVE',
    ' six',
    '@@ -14,3 +14,2 @@',
    ' fourteen',
    '-fifteen',
    ' sixteen'
  )
);
const after = Array.from({ length: 19 }, (_, i) => `line ${i + 1}`);

function describe(rows: DiffRow[]): string[] {
  return rows.map((row) => {
    if (row.kind === 'gap') return `gap ${row.count ?? '?'}${row.expandable ? '' : ' fixed'}`;
    if (row.kind === 'label') return `label ${row.text}`;
    return `${row.line.kind} ${row.line.beforeLine ?? '-'} ${row.line.afterLine ?? '-'}`;
  });
}

// ── gaps between, before and after hunks ────────────────────────────────────
{
  const rows = unifiedRows(twoHunks, null, new Set(), true);
  assert.deepEqual(describe(rows), [
    'gap 3',
    'context 4 4',
    'removed 5 -',
    'added - 5',
    'context 6 6',
    'gap 7',
    'context 14 14',
    'removed 15 -',
    'context 16 15',
    'gap ?'
  ]);
}

// Without full text, the trailing gap is still offered when the caller can load it.
{
  const rows = unifiedRows(twoHunks, null, new Set(), false);
  assert.deepEqual(describe(rows).at(-1), 'gap ? fixed', 'nothing can load the rest of the file');
  assert.equal(describe(rows)[0], 'gap 3 fixed');
}

// With full text the trailing gap has a count, and expanded gaps become lines.
{
  const collapsed = unifiedRows(twoHunks, after, new Set(), false);
  assert.equal(describe(collapsed).at(-1), 'gap 4', 'lines 16..19 after the last hunk');
  assert.equal(describe(collapsed)[0], 'gap 3', 'full text makes every gap expandable');

  const gapKeys = collapsed.flatMap((row) => (row.kind === 'gap' ? [row.key] : []));
  const expanded = unifiedRows(twoHunks, after, new Set([gapKeys[1]]), false);
  const middle = expanded.filter(
    (row) => row.kind === 'line' && row.line.afterLine !== null && row.line.afterLine >= 7 && row.line.afterLine <= 13
  );
  assert.equal(middle.length, 7);
  const first = middle[0];
  assert.ok(first.kind === 'line');
  assert.equal(first.line.text, 'line 7');
  assert.equal(first.line.beforeLine, 7);

  // After the removed line the old side runs one ahead of the new side.
  const tail = unifiedRows(twoHunks, after, new Set([gapKeys[2]]), false);
  const last = tail.at(-1);
  assert.ok(last && last.kind === 'line');
  assert.equal(last.line.afterLine, 19);
  assert.equal(last.line.beforeLine, 20);
  assert.equal(last.line.text, 'line 19');
}

// A new file has no gaps at all.
{
  const added = parseUnifiedDiff(
    text('--- /dev/null', '+++ b/n.ts', '@@ -0,0 +1,2 @@', '+one', '+two')
  );
  assert.deepEqual(describe(unifiedRows(added, null, new Set(), true)), ['added - 1', 'added - 2']);
}

// A file with staged and unstaged work keeps both labels, and its gaps cannot be expanded.
{
  const both = parseUnifiedDiff(
    text('## Staged', '@@ -3,1 +3,1 @@', '-a', '+b', '## Working tree', '@@ -9,1 +9,1 @@', '-c', '+d')
  );
  assert.deepEqual(describe(unifiedRows(both, after, new Set(), true)), [
    'label Staged',
    'gap 2 fixed',
    'removed 3 -',
    'added - 3',
    'label Working tree',
    'gap 8 fixed',
    'removed 9 -',
    'added - 9'
  ]);
}

// ── side by side pairing ─────────────────────────────────────────────────────
{
  const uneven = parseUnifiedDiff(
    text('@@ -1,4 +1,3 @@', ' keep', '-old one', '-old two', '+new one', ' end', '\\ No newline at end of file')
  );
  const rows = splitRows(unifiedRows(uneven, null, new Set(), false));
  const shape = rows.map((row) =>
    row.kind === 'pair'
      ? `${row.left?.kind ?? '_'}:${row.left?.text ?? ''} | ${row.right?.kind ?? '_'}:${row.right?.text ?? ''}`
      : row.kind
  );
  assert.deepEqual(shape, [
    'context:keep | context:keep',
    'removed:old one | added:new one',
    'removed:old two | _:',
    'context:end | context:end',
    'note:\\ No newline at end of file | note:\\ No newline at end of file',
    'gap'
  ]);
}

// A changed last line with no final newline on either side still pairs old with new.
{
  const lastLine = parseUnifiedDiff(
    text('@@ -1 +1 @@', '-old', '\\ No newline at end of file', '+new', '\\ No newline at end of file')
  );
  const rows = splitRows(unifiedRows(lastLine, null, new Set(), false));
  const shape = rows.map((row) =>
    row.kind === 'pair'
      ? `${row.left?.kind ?? '_'}:${row.left?.text ?? ''} | ${row.right?.kind ?? '_'}:${row.right?.text ?? ''}`
      : row.kind
  );
  assert.deepEqual(shape, [
    'removed:old | added:new',
    'note:\\ No newline at end of file | note:\\ No newline at end of file',
    'gap'
  ]);
}

// An added run with nothing removed sits on the right only.
{
  const insert = parseUnifiedDiff(text('@@ -2,0 +3,2 @@', '+x', '+y'));
  const rows = splitRows(unifiedRows(insert, null, new Set(), false));
  const pairs = rows.filter((row) => row.kind === 'pair');
  assert.equal(pairs.length, 2);
  for (const row of pairs) {
    assert.ok(row.kind === 'pair');
    assert.equal(row.left, null);
    assert.equal(row.right?.kind, 'added');
  }
}

// ── the changed-file tree ────────────────────────────────────────────────────
{
  const tree = changedFileTree([
    'tauri-svelte-preview/src/lib/shell/controllers/workbenchController.svelte.ts',
    'tauri-svelte-preview/src/lib/shell/components/ShellFrame.svelte',
    'tauri-svelte-preview/node_modules',
    'README.md'
  ]);
  assert.deepEqual(
    tree.map((entry) => `${'  '.repeat(entry.depth)}${entry.kind === 'folder' ? '/' : ''}${entry.name}`),
    [
      '/tauri-svelte-preview',
      '  /src/lib/shell',
      '    /components',
      '      ShellFrame.svelte',
      '    /controllers',
      '      workbenchController.svelte.ts',
      '  node_modules',
      'README.md'
    ]
  );
  const shell = tree.find((entry) => entry.name === 'src/lib/shell');
  assert.equal(shell?.path, 'tauri-svelte-preview/src/lib/shell');
  assert.equal(tree.find((entry) => entry.name === 'README.md')?.path, 'README.md');
}

// ── untracked files arrive with content and no diff ─────────────────────────
{
  const base = { relativePath: 'new.txt', isBinary: false, originalContent: null };
  assert.equal(
    diffTextOf({ ...base, status: 'untracked', diff: '', modifiedContent: 'a\r\nb\n' }),
    text('diff --git a/new.txt b/new.txt', 'new file mode 100644', '--- /dev/null', '+++ b/new.txt', '@@ -0,0 +1,2 @@', '+a', '+b')
  );
  assert.equal(diffTextOf({ ...base, status: 'modified', diff: 'x', modifiedContent: 'y' }), 'x');
}

// ── one branch diff cut into files ───────────────────────────────────────────
{
  const files = splitDiffByFile(
    text(
      'diff --git a/src/a.ts b/src/a.ts',
      'index 1..2 100644',
      '--- a/src/a.ts',
      '+++ b/src/a.ts',
      '@@ -1 +1 @@',
      '-one',
      '+two',
      'diff --git a/gone.txt b/gone.txt',
      'deleted file mode 100644',
      '--- a/gone.txt',
      '+++ /dev/null',
      '@@ -1 +0,0 @@',
      '-bye',
      'diff --git a/logo.png b/logo.png',
      'new file mode 100644',
      'Binary files /dev/null and b/logo.png differ',
      ''
    )
  );
  assert.deepEqual(
    files.map((file) => [file.relativePath, file.status, file.isBinary]),
    [
      ['src/a.ts', 'modified', false],
      ['gone.txt', 'deleted', false],
      ['logo.png', 'added', true]
    ]
  );
  assert.equal(files[0].diff.split('\n').at(-1), '+two', 'each record holds only its own lines');
  assert.deepEqual(splitDiffByFile(''), []);
}

console.log('diffRows tests passed');
