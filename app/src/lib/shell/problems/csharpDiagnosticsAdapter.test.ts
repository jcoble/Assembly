/**
 * The native C# diagnostics the adapter holds per project root. The point of
 * this test is the cap: a run that visits many projects must not keep every
 * project's diagnostics alive.
 */
import assert from 'node:assert/strict';
import {
  nativeCsharpDiagnosticsForRoot,
  onNativeCsharpDiagnosticsChanged,
  recordNativeCsharpDiagnostics
} from './csharpDiagnosticsAdapter.ts';
import type { RawProblemDiagnostic } from './problemsStore.svelte.ts';

function diagnosticIn(root: string): RawProblemDiagnostic[] {
  return [
    {
      severity: 'error',
      message: `problem in ${root}`,
      line: 1,
      column: 1,
      path: `${root}/Program.cs`
    }
  ];
}

const notifiedRoots: string[] = [];
const stopListening = onNativeCsharpDiagnosticsChanged((root) => notifiedRoots.push(root));

// A trailing slash and Windows separators name the same project.
recordNativeCsharpDiagnostics('C:\\repo\\', diagnosticIn('/a'));
assert.deepEqual(nativeCsharpDiagnosticsForRoot('C:/repo'), diagnosticIn('/a'));
assert.deepEqual(notifiedRoots, ['C:/repo']);

// Only the three most recently reported projects are kept.
for (const root of ['/a', '/b', '/c', '/d']) recordNativeCsharpDiagnostics(root, diagnosticIn(root));
assert.deepEqual(nativeCsharpDiagnosticsForRoot('C:/repo'), [], 'the first project is evicted');
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/a'), [], 'the oldest project past the cap goes');
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/d'), diagnosticIn('/d'), 'the newest is kept');
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/b'), diagnosticIn('/b'));
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/c'), diagnosticIn('/c'));

// Reporting again for a project makes it the most recent, so the next project
// past the cap evicts whichever has gone longest without a report.
recordNativeCsharpDiagnostics('/b', diagnosticIn('/b'));
recordNativeCsharpDiagnostics('/e', diagnosticIn('/e'));
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/c'), [], 'the least recently reported goes');
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/b'), diagnosticIn('/b'), 'a fresh report keeps it');
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/d'), diagnosticIn('/d'));
assert.deepEqual(nativeCsharpDiagnosticsForRoot('/e'), diagnosticIn('/e'));

// Every report reaches the listeners, including the ones that evicted a root.
assert.deepEqual(notifiedRoots, ['C:/repo', '/a', '/b', '/c', '/d', '/b', '/e']);
stopListening();
recordNativeCsharpDiagnostics('/f', diagnosticIn('/f'));
assert.equal(notifiedRoots.length, 7, 'unsubscribing stops the notifications');

console.log('csharpDiagnosticsAdapter tests passed');
