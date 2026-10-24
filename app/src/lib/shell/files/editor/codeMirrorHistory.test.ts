import assert from 'node:assert/strict';
import test from 'node:test';

import { history, isolateHistory, redo, redoDepth, undo, undoDepth } from '@codemirror/commands';
import { EditorState } from '@codemirror/state';

import { limitSavedEditorHistory, savedHistoryDepthLimit } from './codeMirrorHistory.ts';

test('retained editor states keep only five undo and redo groups', () => {
  const extensions = [history({ minDepth: savedHistoryDepthLimit })];
  let state = EditorState.create({ doc: '', extensions });

  for (let index = 0; index < 10; index += 1) {
    state = state.update({
      changes: { from: state.doc.length, insert: String(index) },
      annotations: isolateHistory.of('full')
    }).state;
  }

  state = limitSavedEditorHistory(state, extensions);
  assert.equal(undoDepth(state), savedHistoryDepthLimit);
  assert.equal(redoDepth(state), 0);

  for (let index = 0; index < savedHistoryDepthLimit; index += 1) {
    let next = state;
    assert.equal(undo({ state, dispatch: (transaction) => { next = transaction.state; } }), true);
    state = next;
  }
  assert.equal(undoDepth(state), 0);
  assert.equal(redoDepth(state), savedHistoryDepthLimit);

  state = limitSavedEditorHistory(state, extensions);
  assert.equal(redoDepth(state), savedHistoryDepthLimit);
  for (let index = 0; index < savedHistoryDepthLimit; index += 1) {
    let next = state;
    assert.equal(redo({ state, dispatch: (transaction) => { next = transaction.state; } }), true);
    state = next;
  }
  assert.equal(redoDepth(state), 0);
});
