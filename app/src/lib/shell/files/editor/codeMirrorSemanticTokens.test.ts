import assert from 'node:assert/strict';
import test from 'node:test';

import { EditorState } from '@codemirror/state';

import { semanticTokenDecorations } from './codeMirrorSemanticTokens.ts';

test('maps one-based semantic token positions onto the CodeMirror document', () => {
  const state = EditorState.create({ doc: 'public Widget Build()' });
  const decorations = semanticTokenDecorations(state, [
    { tokenType: 'class', line: 1, startColumn: 8, length: 6 },
    { tokenType: 'method', line: 1, startColumn: 15, length: 5 }
  ]);
  const ranges: Array<[number, number, string, string]> = [];
  decorations.between(0, state.doc.length, (from, to, value) => {
    ranges.push([
      from,
      to,
      String(value.spec.class),
      String(value.spec.attributes?.style)
    ]);
  });

  assert.deepEqual(ranges, [
    [7, 13, 'cm-semantic-type', 'color: #ffcb6b'],
    [14, 19, 'cm-semantic-function', 'color: #82aaff']
  ]);
});

test('drops semantic tokens outside the current document', () => {
  const state = EditorState.create({ doc: 'short' });
  const decorations = semanticTokenDecorations(state, [
    { tokenType: 'class', line: 2, startColumn: 1, length: 5 },
    { tokenType: 'method', line: 1, startColumn: 20, length: 5 }
  ]);
  let count = 0;
  decorations.between(0, state.doc.length, () => { count += 1; });
  assert.equal(count, 0);
});
