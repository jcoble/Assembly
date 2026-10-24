/**
 * problemsStore.test.mjs — the shell Problems panel's store, run in plain node.
 *
 * `problemsStore.svelte.ts` is a runes module, so node cannot import it as it
 * stands: `$state` is compiler syntax, not a function. The test therefore does
 * what vite does — strip the TypeScript types, run the Svelte compiler over the
 * result, and import the compiled JavaScript. The compiled file is written
 * inside `node_modules` so that `svelte/internal/client` still resolves, and it
 * is deleted again at the end. Same trick as `contextStore.test.mjs`.
 *
 * This works only because the store has no runtime imports of its own (its one
 * import is type-only). Keep it that way, or this test has to grow a bundler.
 */
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import { compileModule } from 'svelte/compiler';

const storePath = fileURLToPath(
  new URL('../src/lib/shell/problems/problemsStore.svelte.ts', import.meta.url)
);
const outputDir = fileURLToPath(new URL('../node_modules/.mcb-test/', import.meta.url));
const outputPath = `${outputDir}problemsStore.compiled.mjs`;

mkdirSync(outputDir, { recursive: true });
const source = readFileSync(storePath, 'utf8');
const javascript = stripTypeScriptTypes(source, { mode: 'strip' });
const compiled = compileModule(javascript, {
  generate: 'client',
  filename: 'problemsStore.svelte.js'
});
writeFileSync(outputPath, compiled.js.code);

let store;
try {
  store = await import(outputPath);
} finally {
  rmSync(outputDir, { recursive: true, force: true });
}

const {
  applyProblems,
  beginProblemsLoad,
  countProblems,
  describeProblemCounts,
  describeProblemsEmptyState,
  describeProblemsSource,
  failProblemsLoad,
  fileNameOf,
  filterProblemRows,
  groupProblemsByFile,
  markProblemsActivated,
  markProblemsUnavailable,
  problemRowFromDiagnostic,
  problemRowKey,
  problemsEmptyKind,
  problemsState,
  relativeToRoot,
  resetProblems,
  setProblemsFilter,
  setProblemsRoot,
  severityRank,
  severityWord,
  sortProblemRows,
  worstSeverity
} = store;

let passed = 0;

function test(name, run) {
  resetProblems();
  try {
    run();
    passed += 1;
  } catch (error) {
    console.error(`FAILED: ${name}`);
    throw error;
  }
}

/** A diagnostic in the shape the backend returns, with the new optional path. */
function diagnostic(severity, message, line, path, column = 1) {
  return { severity, message, line, column, source: 'tsserver', path };
}

const ROOT = '/Users/dev/app';

// ── Severity vocabulary ───────────────────────────────────────────────────────

test('severity sorts errors first and hints last', () => {
  assert.equal(severityRank('error'), 0);
  assert.equal(severityRank('warning'), 1);
  assert.equal(severityRank('info'), 2);
  assert.equal(severityRank('hint'), 3);
  // Anything unrecognised sorts last rather than jumping the queue.
  assert.ok(severityRank('nonsense') > severityRank('hint'));
});

test('severities are said in plain words, singular and plural', () => {
  assert.equal(severityWord('error', 1), 'error');
  assert.equal(severityWord('error', 2), 'errors');
  assert.equal(severityWord('warning', 1), 'warning');
  assert.equal(severityWord('warning', 3), 'warnings');
  assert.equal(severityWord('info', 1), 'note');
  assert.equal(severityWord('info', 2), 'notes');
  assert.equal(severityWord('hint', 1), 'hint');
  assert.equal(severityWord('hint', 4), 'hints');
});

// ── Turning a backend diagnostic into a row ───────────────────────────────────

test('a diagnostic becomes a row with a path relative to the project', () => {
  const row = problemRowFromDiagnostic(
    diagnostic('error', 'Cannot find name x', 12, `${ROOT}/src/lib/app.ts`, 4),
    ROOT
  );
  assert.equal(row.path, `${ROOT}/src/lib/app.ts`);
  assert.equal(row.relativePath, 'src/lib/app.ts');
  assert.equal(row.fileName, 'app.ts');
  assert.equal(row.severity, 'error');
  assert.equal(row.line, 12);
  assert.equal(row.column, 4);
  assert.equal(row.source, 'tsserver');
});

test('a diagnostic with no path of its own takes the file it was read for', () => {
  const row = problemRowFromDiagnostic(
    { severity: 'warning', message: 'unused', line: 3, column: 1 },
    ROOT,
    `${ROOT}/src/main.ts`
  );
  assert.equal(row.path, `${ROOT}/src/main.ts`);
  assert.equal(row.relativePath, 'src/main.ts');
});

test('a diagnostic with no path anywhere is dropped rather than shown as nowhere', () => {
  assert.equal(problemRowFromDiagnostic({ severity: 'error', message: 'x', line: 1, column: 1 }, ROOT), null);
});

test('a file: URI is read as a path', () => {
  const row = problemRowFromDiagnostic(
    diagnostic('error', 'boom', 1, `file://${ROOT}/src/a.ts`),
    ROOT
  );
  assert.equal(row.path, `${ROOT}/src/a.ts`);
  assert.equal(row.relativePath, 'src/a.ts');
});

test('a file outside the project keeps its whole path', () => {
  assert.equal(relativeToRoot('/elsewhere/lib/x.ts', ROOT), '/elsewhere/lib/x.ts');
  assert.equal(relativeToRoot(`${ROOT}/src/x.ts`, ROOT), 'src/x.ts');
  assert.equal(relativeToRoot(`${ROOT}/src/x.ts`, ''), `${ROOT}/src/x.ts`);
  assert.equal(fileNameOf(`${ROOT}/src/x.ts`), 'x.ts');
});

// ── Counting ──────────────────────────────────────────────────────────────────

const rows = [
  problemRowFromDiagnostic(diagnostic('warning', 'unused import', 4, `${ROOT}/src/a.ts`), ROOT),
  problemRowFromDiagnostic(diagnostic('error', 'missing semicolon', 9, `${ROOT}/src/b.ts`), ROOT),
  problemRowFromDiagnostic(diagnostic('error', 'cannot find name', 2, `${ROOT}/src/b.ts`), ROOT),
  problemRowFromDiagnostic(diagnostic('hint', 'prefer const', 7, `${ROOT}/src/a.ts`), ROOT),
  problemRowFromDiagnostic(diagnostic('info', 'consider naming this', 1, `${ROOT}/src/c.ts`), ROOT)
];

test('counts are per severity plus a total', () => {
  const counts = countProblems(rows);
  assert.equal(counts.error, 2);
  assert.equal(counts.warning, 1);
  assert.equal(counts.info, 1);
  assert.equal(counts.hint, 1);
  assert.equal(counts.total, 5);
  const none = countProblems([]);
  assert.equal(none.total, 0);
  assert.equal(none.error, 0);
});

test('counts are said in plain words', () => {
  assert.equal(describeProblemCounts(countProblems([])), 'No problems');
  assert.equal(describeProblemCounts(countProblems(rows)), '2 errors, 1 warning, 1 note, 1 hint');
  assert.equal(
    describeProblemCounts(countProblems(rows.filter((row) => row.severity === 'error'))),
    '2 errors'
  );
  assert.equal(
    describeProblemCounts(countProblems(rows.filter((row) => row.severity === 'warning'))),
    '1 warning'
  );
});

test('the worst severity in a list is the one that decides its badge', () => {
  assert.equal(worstSeverity(rows), 'error');
  assert.equal(worstSeverity(rows.filter((row) => row.severity !== 'error')), 'warning');
  assert.equal(worstSeverity([]), null);
});

// ── Sorting and grouping ──────────────────────────────────────────────────────

test('rows sort by severity, then by line, then by column', () => {
  const sorted = sortProblemRows([
    problemRowFromDiagnostic(diagnostic('hint', 'h', 1, `${ROOT}/src/a.ts`), ROOT),
    problemRowFromDiagnostic(diagnostic('error', 'later', 30, `${ROOT}/src/a.ts`), ROOT),
    problemRowFromDiagnostic(diagnostic('error', 'earlier', 5, `${ROOT}/src/a.ts`, 9), ROOT),
    problemRowFromDiagnostic(diagnostic('error', 'earliest', 5, `${ROOT}/src/a.ts`, 2), ROOT),
    problemRowFromDiagnostic(diagnostic('info', 'i', 2, `${ROOT}/src/a.ts`), ROOT),
    problemRowFromDiagnostic(diagnostic('warning', 'w', 2, `${ROOT}/src/a.ts`), ROOT)
  ]);
  assert.deepEqual(
    sorted.map((row) => row.message),
    ['earliest', 'earlier', 'later', 'w', 'i', 'h']
  );
});

test('sorting leaves the list it was given alone', () => {
  const input = [...rows];
  sortProblemRows(input);
  assert.deepEqual(input.map((row) => row.message), rows.map((row) => row.message));
});

test('problems group by file, worst file first, rows sorted inside each file', () => {
  const groups = groupProblemsByFile(rows);
  assert.deepEqual(groups.map((group) => group.relativePath), ['src/b.ts', 'src/a.ts', 'src/c.ts']);

  const [worst] = groups;
  assert.equal(worst.path, `${ROOT}/src/b.ts`);
  assert.equal(worst.fileName, 'b.ts');
  assert.equal(worst.counts.error, 2);
  assert.equal(worst.counts.total, 2);
  assert.equal(worst.worstSeverity, 'error');
  assert.deepEqual(worst.rows.map((row) => row.line), [2, 9]);

  const middle = groups[1];
  assert.equal(middle.counts.warning, 1);
  assert.equal(middle.counts.hint, 1);
  assert.equal(middle.worstSeverity, 'warning');
});

test('two files with the same worst severity sort by name', () => {
  const groups = groupProblemsByFile([
    problemRowFromDiagnostic(diagnostic('error', 'x', 1, `${ROOT}/src/z.ts`), ROOT),
    problemRowFromDiagnostic(diagnostic('error', 'y', 1, `${ROOT}/src/a.ts`), ROOT)
  ]);
  assert.deepEqual(groups.map((group) => group.relativePath), ['src/a.ts', 'src/z.ts']);
});

test('a file with more errors comes before a file with fewer', () => {
  const groups = groupProblemsByFile([
    problemRowFromDiagnostic(diagnostic('error', 'x', 1, `${ROOT}/src/a.ts`), ROOT),
    problemRowFromDiagnostic(diagnostic('error', 'y', 1, `${ROOT}/src/z.ts`), ROOT),
    problemRowFromDiagnostic(diagnostic('error', 'y2', 2, `${ROOT}/src/z.ts`), ROOT)
  ]);
  assert.deepEqual(groups.map((group) => group.relativePath), ['src/z.ts', 'src/a.ts']);
});

test('grouping nothing gives nothing', () => {
  assert.deepEqual(groupProblemsByFile([]), []);
});

test('every row has a key that stays put', () => {
  const key = problemRowKey(rows[1]);
  assert.equal(problemRowKey(rows[1]), key);
  assert.notEqual(problemRowKey(rows[2]), key);
});

// ── Filtering ─────────────────────────────────────────────────────────────────

test('an empty filter keeps everything', () => {
  assert.equal(filterProblemRows(rows, '').length, rows.length);
  assert.equal(filterProblemRows(rows, '   ').length, rows.length);
});

test('the filter matches the message, whatever the letter case', () => {
  const matched = filterProblemRows(rows, 'SEMICOLON');
  assert.equal(matched.length, 1);
  assert.equal(matched[0].message, 'missing semicolon');
});

test('the filter matches the file name and the path', () => {
  assert.equal(filterProblemRows(rows, 'b.ts').length, 2);
  assert.equal(filterProblemRows(rows, 'src/').length, rows.length);
});

test('the filter matches the severity word people actually type', () => {
  assert.equal(filterProblemRows(rows, 'error').length, 2);
  assert.equal(filterProblemRows(rows, 'errors').length, 2);
  assert.equal(filterProblemRows(rows, 'warning').length, 1);
  // "note" is what the panel calls an info diagnostic, so it has to find one.
  assert.equal(filterProblemRows(rows, 'note').length, 1);
  assert.equal(filterProblemRows(rows, 'hint').length, 1);
});

test('a filter that matches nothing returns nothing', () => {
  assert.deepEqual(filterProblemRows(rows, 'zzzz'), []);
});

// ── The state and its superseded-load guard ───────────────────────────────────

test('the panel starts with nothing loaded and nothing claimed', () => {
  assert.equal(problemsState.activated, false);
  assert.deepEqual(problemsState.rows, []);
  assert.equal(problemsState.loading, false);
  assert.equal(problemsState.source, null);
  assert.equal(problemsState.error, null);
  assert.equal(problemsState.unavailableReason, null);
  assert.equal(problemsState.filter, '');
});

test('a finished load replaces the rows and says where they came from', () => {
  markProblemsActivated();
  setProblemsRoot(ROOT);
  const ticket = beginProblemsLoad();
  assert.equal(problemsState.loading, true);
  assert.equal(applyProblems(ticket, rows, 'workspace', 3), true);
  assert.equal(problemsState.loading, false);
  assert.equal(problemsState.rows.length, 5);
  assert.equal(problemsState.source, 'workspace');
  assert.equal(problemsState.filesConsidered, 3);
  assert.equal(problemsState.root, ROOT);
  assert.ok(problemsState.loadedAt !== null);
});

test('a slow first load cannot overwrite a fast second one', () => {
  const first = beginProblemsLoad();
  const second = beginProblemsLoad();
  assert.equal(applyProblems(second, rows, 'workspace', 3), true);
  assert.equal(applyProblems(first, [], 'workspace', 0), false);
  assert.equal(problemsState.rows.length, 5);
  assert.equal(failProblemsLoad(first, 'too late'), false);
  assert.equal(problemsState.error, null);
});

test('a failed load says why in plain words and keeps nothing made up', () => {
  const ticket = beginProblemsLoad();
  assert.equal(failProblemsLoad(ticket, 'Could not read problems: the language server is not running'), true);
  assert.equal(problemsState.loading, false);
  assert.equal(
    problemsState.error,
    'Could not read problems: the language server is not running'
  );
});

test('a load that could never run marks the panel unavailable and shows no rows', () => {
  const ticket = beginProblemsLoad();
  applyProblems(ticket, rows, 'workspace', 3);
  const second = beginProblemsLoad();
  assert.equal(markProblemsUnavailable(second, 'This runs in the desktop app only.'), true);
  assert.deepEqual(problemsState.rows, []);
  assert.equal(problemsState.unavailableReason, 'This runs in the desktop app only.');
});

test('the filter is remembered on the store, trimmed of nothing but kept as typed', () => {
  setProblemsFilter('  Error ');
  assert.equal(problemsState.filter, '  Error ');
});

// ── The status line and the empty states ──────────────────────────────────────

test('the status line says which route answered, in plain words', () => {
  assert.equal(describeProblemsSource(null, 0), '');
  assert.equal(
    describeProblemsSource('workspace', 3),
    'These are every problem the language server has reported for this project.'
  );
  assert.equal(
    describeProblemsSource('open-files', 1),
    'This desktop app cannot list problems for a whole project yet, so these come from the 1 file you have open.'
  );
  assert.equal(
    describeProblemsSource('open-files', 4),
    'This desktop app cannot list problems for a whole project yet, so these come from the 4 files you have open.'
  );
});

function snapshot(overrides = {}) {
  return {
    activated: true,
    loading: false,
    root: ROOT,
    rows: [],
    filter: '',
    error: null,
    unavailableReason: null,
    source: 'workspace',
    filesConsidered: 0,
    ...overrides
  };
}

test('a panel that has never been asked to load says so', () => {
  assert.equal(problemsEmptyKind(snapshot({ activated: false, source: null })), 'not-loaded');
});

test('a panel with rows to show has no empty state', () => {
  assert.equal(problemsEmptyKind(snapshot({ rows })), null);
});

test('rows that the filter hides are their own empty state', () => {
  assert.equal(problemsEmptyKind(snapshot({ rows, filter: 'zzz' })), 'no-match');
});

test('the desktop-only, failed, and no-project states each win over the rest', () => {
  assert.equal(
    problemsEmptyKind(snapshot({ unavailableReason: 'This runs in the desktop app only.' })),
    'desktop-only'
  );
  assert.equal(problemsEmptyKind(snapshot({ error: 'boom' })), 'load-failed');
  assert.equal(problemsEmptyKind(snapshot({ root: null, source: null })), 'no-project');
});

test('an empty whole-project answer is honest about a language server that may still be starting', () => {
  assert.equal(problemsEmptyKind(snapshot({ source: 'workspace' })), 'nothing-reported-yet');
  const state = describeProblemsEmptyState('nothing-reported-yet');
  assert.equal(state.headline, 'No problems found');
  assert.match(state.hint, /still starting/);
});

test('the open-file route tells apart clean files from no files at all', () => {
  assert.equal(problemsEmptyKind(snapshot({ source: 'open-files', filesConsidered: 0 })), 'no-open-files');
  assert.equal(problemsEmptyKind(snapshot({ source: 'open-files', filesConsidered: 2 })), 'clean');
  assert.equal(describeProblemsEmptyState('clean').headline, 'No problems found');
  assert.match(describeProblemsEmptyState('no-open-files').hint, /open a file/i);
});

test('every empty state has a headline and a hint anyone can read', () => {
  for (const kind of [
    'not-loaded',
    'no-project',
    'desktop-only',
    'load-failed',
    'no-open-files',
    'nothing-reported-yet',
    'clean',
    'no-match'
  ]) {
    const state = describeProblemsEmptyState(kind);
    assert.ok(state.headline.length > 0, `${kind} has no headline`);
    assert.ok(state.hint.length > 0, `${kind} has no hint`);
    // Plain English only: no invented vocabulary, no bare command names.
    assert.doesNotMatch(state.hint, /_|\bLSP\b/, `${kind} says something only the code explains`);
  }
});

test('resetting puts the panel back where it launched', () => {
  markProblemsActivated();
  setProblemsRoot(ROOT);
  const ticket = beginProblemsLoad();
  applyProblems(ticket, rows, 'workspace', 3);
  setProblemsFilter('error');
  resetProblems();
  assert.equal(problemsState.activated, false);
  assert.deepEqual(problemsState.rows, []);
  assert.equal(problemsState.root, null);
  assert.equal(problemsState.filter, '');
  assert.equal(problemsState.source, null);
});

console.log(`\nproblemsStore: ${passed} checks passed`);
