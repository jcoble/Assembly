import assert from 'node:assert/strict';

import { FOLDS_KEY, MAX_FOLDS, isFoldOpen, readFolds, writeFolds } from '../src/lib/shell/board/boardQuestionFolds.ts';

const memory = (): Storage => {
  const data = new Map<string, string>();
  return {
    get length() { return data.size; },
    clear: () => data.clear(),
    getItem: (key) => data.get(key) ?? null,
    key: (index) => [...data.keys()][index] ?? null,
    removeItem: (key) => void data.delete(key),
    setItem: (key, value) => void data.set(key, value)
  };
};

{ // an_open_question_starts_expanded_and_an_answered_one_collapsed
  const folds = readFolds(memory());
  assert.equal(isFoldOpen(folds, 'mac:1', false), true);
  assert.equal(isFoldOpen(folds, 'mac:2', true), false);
}
{ // a_stored_fold_wins_over_the_default
  const storage = memory();
  writeFolds(storage, { 'mac:1': false, 'mac:2': true });
  const folds = readFolds(storage);
  assert.equal(isFoldOpen(folds, 'mac:1', false), false);
  assert.equal(isFoldOpen(folds, 'mac:2', true), true);
}
{ // only_the_newest_folds_are_kept
  const storage = memory();
  const folds = Object.fromEntries(Array.from({ length: MAX_FOLDS + 2 }, (_, i) => [`mac:${i}`, false]));
  writeFolds(storage, folds);
  const kept = readFolds(storage);
  assert.equal(Object.keys(kept).length, MAX_FOLDS);
  assert.equal('mac:0' in kept || 'mac:1' in kept, false);
  assert.equal(kept[`mac:${MAX_FOLDS + 1}`], false);
}
{ // a_damaged_value_reads_as_no_folds
  const storage = memory();
  storage.setItem(FOLDS_KEY, 'not json');
  assert.deepEqual(readFolds(storage), {});
}
console.log('board question fold tests passed');
