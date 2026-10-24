/**
 * gitDiscardConfirm.test.mjs — the exact words asked before work is thrown away.
 *
 * A discard is the one panel action git cannot undo, so the wording is checked
 * here rather than clicked through in the app. Nothing in this file runs git.
 */
import assert from 'node:assert/strict';

import {
  DISCARD_NAMES_SHOWN,
  describeDiscardQuestion,
  describeDiscardTargets
} from '../src/lib/shell/git/components/discardConfirm.ts';

// ── one tracked file ────────────────────────────────────────────────────────
const oneFile = describeDiscardQuestion({
  scope: 'file',
  targets: [{ relativePath: 'src/lib/shell/git/gitService.ts', untracked: false }]
});
assert.equal(oneFile.title, 'Discard changes to this file?');
assert.equal(oneFile.confirmLabel, 'Discard changes');
assert.equal(oneFile.cancelLabel, 'Keep my changes');
assert.equal(oneFile.destructive, true);
assert.equal(oneFile.deletesFiles, false, 'a tracked file is restored, not deleted');
assert.ok(
  oneFile.lines.includes('src/lib/shell/git/gitService.ts'),
  'the question names the file it is about'
);
assert.ok(
  oneFile.lines.some((line) => line.includes('cannot be undone')),
  'the question says plainly that git keeps no copy'
);

// ── an untracked file is deleted, and says so ───────────────────────────────
const untracked = describeDiscardQuestion({
  scope: 'file',
  targets: [{ relativePath: 'notes.md', untracked: true }]
});
assert.equal(untracked.deletesFiles, true);
assert.equal(untracked.confirmLabel, 'Delete the file');
assert.ok(
  untracked.lines.some((line) => line.includes('deleted from disk')),
  'deleting an untracked file is its own sentence, not the same as restoring one'
);

// ── a mix keeps both sentences ──────────────────────────────────────────────
const mixed = describeDiscardQuestion({
  scope: 'file',
  targets: [
    { relativePath: 'a.rs', untracked: false },
    { relativePath: 'new.md', untracked: true }
  ]
});
assert.equal(mixed.title, 'Discard changes to 2 files?');
assert.equal(mixed.confirmLabel, 'Discard changes');
assert.ok(mixed.lines.some((line) => line.includes('1 file goes back to how the last commit')));
assert.ok(mixed.lines.some((line) => line.includes('1 file git has never tracked')));

// ── long lists are counted, not cut ─────────────────────────────────────────
const many = Array.from({ length: DISCARD_NAMES_SHOWN + 3 }, (_unused, index) => ({
  relativePath: `file-${index}.ts`,
  untracked: false
}));
const named = describeDiscardTargets(many);
assert.equal(named.length, DISCARD_NAMES_SHOWN + 1);
assert.equal(named.at(-1), '…and 3 more files');

// ── everything ──────────────────────────────────────────────────────────────
const allTracked = describeDiscardQuestion({ scope: 'all', targets: [], untrackedCount: 0 });
assert.equal(allTracked.title, 'Discard every change?');
assert.equal(allTracked.confirmLabel, 'Discard everything');
assert.equal(allTracked.deletesFiles, false);
assert.equal(
  allTracked.lines.some((line) => line.includes('deleted from disk')),
  false,
  'with no untracked files nothing claims files will be deleted'
);

const allWithNew = describeDiscardQuestion({ scope: 'all', targets: [], untrackedCount: 4 });
assert.equal(allWithNew.confirmLabel, 'Discard and delete');
assert.equal(allWithNew.deletesFiles, true);
assert.ok(allWithNew.lines.some((line) => line.includes('4 new files')));

console.log('gitDiscardConfirm tests passed');
