/**
 * Covers the remembering behind the editor's "N references"
 * margin numbers. The rules live in `referenceCountBatcher.ts` (no Svelte
 * state, no backend calls) precisely so this test can run under plain Node —
 * `sourceIntelligence.ts` only wires them to the real backend.
 *
 * A count comes back as `{ count, atLeast }`. `atLeast: false` means the pass
 * read every file and the number is exact; `atLeast: true` means it did not,
 * so the margin says "at least N" rather than stating a number nobody took.
 */
import assert from 'node:assert/strict';
import {
  countFromResult,
  createCountMemory,
  createReferenceCountBatcher,
  createReferenceCountStore,
  createSemanticReferenceCountScheduler
} from '../src/lib/shell/files/editor/referenceCountBatcher.ts';

const resultFor = (counts, approximate = false) => ({
  counts,
  approximate,
  scannedFiles: 10,
  elapsedMs: 5
});

/** A number the pass counted in full. */
const exactly = (count) => ({ count, atLeast: false });
/** A number the pass could only put a floor under. */
const atLeast = (count) => ({ count, atLeast: true });
async function settle(): Promise<void> {
  for (let turn = 0; turn < 5; turn += 1) {
    await Promise.resolve();
  }
}

// every unremembered symbol is asked about immediately
{
  const asked = [];
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences(symbolNames) {
      asked.push(symbolNames);
      return Promise.resolve(resultFor({ Alpha: 3, Beta: 7, Gamma: 0 }));
    }
  });

  const counts = await Promise.all([
    batcher.count('Alpha'),
    batcher.count('Beta'),
    batcher.count('Gamma')
  ]);

  assert.deepEqual(counts, [exactly(3), exactly(7), exactly(0)]);
  assert.deepEqual(asked, [['Alpha'], ['Beta'], ['Gamma']]);
}

// the same symbol asked about twice while pending is asked about once
{
  const asked = [];
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences(symbolNames) {
      asked.push(symbolNames);
      return Promise.resolve(resultFor({ Alpha: 4 }));
    }
  });

  const counts = await Promise.all([batcher.count('Alpha'), batcher.count('Alpha')]);

  assert.deepEqual(counts, [exactly(4), exactly(4)]);
  assert.deepEqual(asked, [['Alpha']]);
}

// a counted symbol is remembered, so typing does not re-ask for it
{
  let requests = 0;
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences() {
      requests += 1;
      return Promise.resolve(resultFor({ Alpha: 2 }));
    }
  });

  assert.deepEqual(await batcher.count('Alpha'), exactly(2));
  assert.deepEqual(await batcher.count('Alpha'), exactly(2));
  assert.equal(requests, 1);

  // …until the project changes underneath it
  batcher.forget();
  assert.deepEqual(await batcher.count('Alpha'), exactly(2));
  assert.equal(requests, 2);
}

// counts above the margin's ceiling are cut down to it, and reported as a floor
// rather than as the exact number the margin has room for
{
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences: () => Promise.resolve(resultFor({ Alpha: 812 }))
  });

  assert.deepEqual(await batcher.count('Alpha'), atLeast(50));
}

// a pass that ran out of time reports "not counted", not "no references"
{
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences: () => Promise.resolve(resultFor({ Alpha: 6, Beta: 0 }, true))
  });

  const [alpha, beta] = await Promise.all([batcher.count('Alpha'), batcher.count('Beta')]);
  assert.deepEqual(
    alpha,
    atLeast(6),
    'a symbol the pass did reach gets its number, marked as a floor'
  );
  assert.equal(beta, null, 'a zero from a pass that gave up early means "unknown"');
}

// nothing was counted at all: every waiting symbol hears "unknown"
{
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences: () => Promise.resolve(null)
  });

  assert.deepEqual(await Promise.all([batcher.count('Alpha'), batcher.count('Beta')]), [
    null,
    null
  ]);
}

// a failed request answers "unknown" rather than leaving the margin hanging
{
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences: () => Promise.reject(new Error('backend is gone'))
  });

  assert.equal(await batcher.count('Alpha'), null);
}

// an unknown answer is not remembered, so the next look can still find one
{
  let requests = 0;
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences() {
      requests += 1;
      return Promise.resolve(requests === 1 ? null : resultFor({ Alpha: 9 }));
    }
  });

  assert.equal(await batcher.count('Alpha'), null);
  assert.deepEqual(await batcher.count('Alpha'), exactly(9));
  assert.equal(requests, 2);
}

// a symbol asked about after the first count resolves opens a new request
{
  const asked = [];
  const batcher = createReferenceCountBatcher({
    memory: createCountMemory({ cacheMs: 30_000 }),
    maxCount: 50,
    countReferences(symbolNames) {
      asked.push(symbolNames);
      return Promise.resolve(resultFor({ Alpha: 1, Beta: 2 }));
    }
  });

  assert.deepEqual(await batcher.count('Alpha'), exactly(1));
  assert.deepEqual(await batcher.count('Beta'), exactly(2));
  assert.deepEqual(asked, [['Alpha'], ['Beta']]);
}

// a remembered count is let go once it is old enough
{
  let clock = 1_000;
  const memory = createCountMemory({ cacheMs: 30_000, now: () => clock });

  memory.remember('Alpha', exactly(5));
  assert.deepEqual(memory.get('Alpha'), exactly(5));

  clock += 29_999;
  assert.deepEqual(memory.get('Alpha'), exactly(5), 'still good just before it expires');

  clock += 1;
  assert.equal(memory.get('Alpha'), undefined, 'let go once it is old enough');
  assert.equal(memory.size, 0, 'and dropped rather than kept around');
}

// a floor is remembered as a floor, not quietly turned into an exact number
{
  const memory = createCountMemory({ cacheMs: 30_000 });
  memory.remember('Alpha', atLeast(12));
  assert.deepEqual(memory.get('Alpha'), atLeast(12));
}

// forgetting empties the memory
{
  const memory = createCountMemory({ cacheMs: 30_000 });
  memory.remember('Alpha', exactly(5));
  memory.forget();
  assert.equal(memory.get('Alpha'), undefined);
}

// reading one symbol out of a pass's result
{
  assert.deepEqual(countFromResult(resultFor({ Alpha: 3 }), 'Alpha', 50), exactly(3));
  assert.equal(countFromResult(resultFor({ Alpha: 3 }), 'Beta', 50), null);
  assert.equal(countFromResult(null, 'Alpha', 50), null);
  assert.deepEqual(countFromResult(resultFor({ Alpha: 0 }), 'Alpha', 50), exactly(0));
  assert.equal(countFromResult(resultFor({ Alpha: 0 }, true), 'Alpha', 50), null);
  assert.deepEqual(
    countFromResult(resultFor({ Alpha: 4 }, true), 'Alpha', 50),
    atLeast(4),
    'a number from a pass that did not read everything is a floor'
  );
  assert.deepEqual(
    countFromResult(resultFor({ Alpha: 812 }), 'Alpha', 50),
    atLeast(50),
    'a number cut down to the ceiling is a floor too'
  );
}

// ── The queue behind the language server's own counts ───────────────────────
//
// These cover the other way of asking, the one the desktop app uses: the
// language server is asked about one symbol at a time, so the queue decides how
// many questions are allowed to be outstanding and in what order they go out.

/** A promise whose ending this test gets to choose, plus the key it was for. */
function pendingAnswers() {
  const settle = new Map();
  const waitFactory = Promise as PromiseConstructor & {
    withResolvers<T>(): {
      promise: Promise<T>;
      resolve(value: T | PromiseLike<T>): void;
      reject(reason?: unknown): void;
    };
  };
  return {
    countFor(key) {
      const { promise, resolve } = waitFactory.withResolvers();
      settle.set(key, resolve);
      return promise;
    },
    /** Let the answer for one key arrive. */
    answer(key, value) {
      const resolve = settle.get(key);
      assert.ok(resolve, `nothing was asked about ${key}`);
      settle.delete(key);
      resolve(value);
    },
    get outstanding() {
      return [...settle.keys()];
    }
  };
}

// the queue works down the list it was given and never has more than the
// allowed number of questions outstanding at once
{
  const answers = pendingAnswers();
  const landed = [];
  const scheduler = createSemanticReferenceCountScheduler({
    maxInFlight: 4,
    countFor: answers.countFor,
    onCounted: (key, count) => landed.push([key, count])
  });

  scheduler.request(['a', 'b', 'c', 'd', 'e', 'f']);
  await settle();

  assert.deepEqual(answers.outstanding, ['a', 'b', 'c', 'd'], 'four at once, in the order given');
  assert.equal(scheduler.inFlight, 4);
  assert.equal(scheduler.waiting, 2);

  answers.answer('b', exactly(7));
  await settle();
  assert.deepEqual(landed, [['b', exactly(7)]]);
  assert.deepEqual(
    answers.outstanding,
    ['a', 'c', 'd', 'e'],
    'one answer lands, the next symbol in the document goes out'
  );

  answers.answer('a', exactly(1));
  answers.answer('c', null);
  answers.answer('d', exactly(2));
  answers.answer('e', exactly(3));
  await settle();
  assert.deepEqual(answers.outstanding, ['f']);

  answers.answer('f', exactly(4));
  await settle();
  assert.equal(scheduler.inFlight, 0);
  assert.equal(scheduler.waiting, 0);
  assert.deepEqual(
    landed.map(([key]) => key),
    ['b', 'a', 'c', 'd', 'e', 'f'],
    'every symbol got an answer'
  );
}

// the same symbol asked about twice is only asked about once
{
  const answers = pendingAnswers();
  const scheduler = createSemanticReferenceCountScheduler({
    maxInFlight: 4,
    countFor: answers.countFor,
    onCounted: () => {}
  });

  scheduler.request(['a', 'b']);
  scheduler.request(['b', 'a', 'c']);
  await settle();

  assert.deepEqual(answers.outstanding, ['a', 'b', 'c']);
}

// a duplicate stays ignored while its question is outstanding, but the same
// symbol may be asked again after its answer has been delivered.
{
  const answers = pendingAnswers();
  const landed = [];
  const scheduler = createSemanticReferenceCountScheduler({
    maxInFlight: 1,
    countFor: answers.countFor,
    onCounted: (key, count) => landed.push([key, count])
  });

  scheduler.request(['a']);
  scheduler.request(['a']);
  await settle();
  assert.deepEqual(answers.outstanding, ['a'], 'an outstanding question must stay deduplicated');

  answers.answer('a', null);
  await settle();
  scheduler.request(['a']);
  await settle();
  assert.deepEqual(answers.outstanding, ['a'], 'a delivered question may be asked again');

  answers.answer('a', exactly(6));
  await settle();
  assert.deepEqual(landed, [
    ['a', null],
    ['a', exactly(6)]
  ]);
}

// the reader opened another file: everything still waiting is dropped, and the
// answers to the questions already sent are ignored rather than drawn over the
// new file
{
  const answers = pendingAnswers();
  const landed = [];
  const scheduler = createSemanticReferenceCountScheduler({
    maxInFlight: 2,
    countFor: answers.countFor,
    onCounted: (key, count) => landed.push([key, count])
  });

  scheduler.request(['a', 'b', 'c', 'd']);
  await settle();
  assert.deepEqual(answers.outstanding, ['a', 'b']);

  scheduler.clear();
  assert.equal(scheduler.waiting, 0, 'nothing is still queued');

  answers.answer('a', exactly(9));
  answers.answer('b', exactly(3));
  await settle();
  assert.deepEqual(landed, [], 'answers about the file that was closed are not reported');
  assert.deepEqual(answers.outstanding, [], 'and nothing new went out for it either');

  // a fresh file starts cleanly, including symbols that share a name
  scheduler.request(['a']);
  await settle();
  assert.deepEqual(answers.outstanding, ['a']);
  answers.answer('a', exactly(5));
  await settle();
  assert.deepEqual(landed, [['a', exactly(5)]]);
}

// opening another file does not forget the old questions are still physically
// running, so the four-request ceiling survives the switch
{
  const answers = pendingAnswers();
  const landed = [];
  const oldKeys = ['old-a', 'old-b', 'old-c', 'old-d'];
  const newKeys = ['new-a', 'new-b', 'new-c', 'new-d'];
  const scheduler = createSemanticReferenceCountScheduler({
    maxInFlight: 4,
    countFor: answers.countFor,
    onCounted: (key, count) => landed.push([key, count])
  });

  scheduler.request(oldKeys);
  await settle();
  assert.deepEqual(answers.outstanding, oldKeys);

  scheduler.clear();
  scheduler.request(newKeys);
  await settle();
  assert.deepEqual(
    answers.outstanding,
    oldKeys,
    'the new file must wait while four old questions are still physically running'
  );

  for (const [index, oldKey] of oldKeys.entries()) {
    answers.answer(oldKey, exactly(1));
    await settle();
    assert.deepEqual(
      answers.outstanding,
      [...oldKeys.slice(index + 1), ...newKeys.slice(0, index + 1)],
      'one new question must start when one old question ends'
    );
  }

  assert.deepEqual(landed, [], 'answers from the cleared file must not be reported');
  for (const key of newKeys) {
    answers.answer(key, exactly(2));
  }
  await settle();
  assert.deepEqual(
    landed.map(([key]) => key),
    newKeys,
    'answers for the new file must be reported'
  );
}

// a question that fails answers "unknown" instead of blocking everything behind it
{
  const scheduler = createSemanticReferenceCountScheduler({
    maxInFlight: 1,
    countFor: (key) => (key === 'a' ? Promise.reject(new Error('gone')) : Promise.resolve(exactly(2))),
    onCounted: (key, count) => landed.push([key, count])
  });
  const landed = [];

  scheduler.request(['a', 'b']);
  await settle();

  assert.equal(JSON.stringify(landed), JSON.stringify([
    ['a', null],
    ['b', exactly(2)]
  ]));
  assert.equal(scheduler.inFlight, 0);
}

// ── Numbers remembered per project, per file, per symbol ────────────────────

// switching to another project and back keeps the first project's numbers,
// rather than throwing them away and counting everything a second time
{
  let clock = 1_000;
  const store = createReferenceCountStore({ cacheMs: 30_000, now: () => clock });

  store.remember('/projects/alpha', 'App.cs', 'RunAsync', exactly(4));
  store.remember('/projects/beta', 'App.cs', 'RunAsync', exactly(11));

  assert.deepEqual(store.get('/projects/alpha', 'App.cs', 'RunAsync'), exactly(4));
  assert.deepEqual(
    store.get('/projects/beta', 'App.cs', 'RunAsync'),
    exactly(11),
    'the same name in another project is its own number'
  );
  assert.equal(store.get('/projects/gamma', 'App.cs', 'RunAsync'), undefined);
}

// a number is let go once it is old enough, wherever it was kept
{
  let clock = 1_000;
  const store = createReferenceCountStore({ cacheMs: 30_000, now: () => clock });

  store.remember('/projects/alpha', 'App.cs', 'RunAsync', exactly(4));
  clock += 29_999;
  assert.deepEqual(store.get('/projects/alpha', 'App.cs', 'RunAsync'), exactly(4));
  clock += 1;
  assert.equal(store.get('/projects/alpha', 'App.cs', 'RunAsync'), undefined);
}

// the reader edited one file: only that file's numbers are counted again
{
  const store = createReferenceCountStore({ cacheMs: 30_000 });

  store.remember('/projects/alpha', 'App.cs', 'RunAsync', exactly(4));
  store.remember('/projects/alpha', 'App.cs', 'Stop', exactly(2));
  store.remember('/projects/alpha', 'Other.cs', 'RunAsync', exactly(9));
  store.remember('/projects/beta', 'App.cs', 'RunAsync', exactly(11));

  store.forgetFile('/projects/alpha', 'App.cs');

  assert.equal(store.get('/projects/alpha', 'App.cs', 'RunAsync'), undefined);
  assert.equal(store.get('/projects/alpha', 'App.cs', 'Stop'), undefined);
  assert.deepEqual(
    store.get('/projects/alpha', 'Other.cs', 'RunAsync'),
    exactly(9),
    'a file nobody touched keeps its numbers'
  );
  assert.deepEqual(store.get('/projects/beta', 'App.cs', 'RunAsync'), exactly(11));
}

// a project whose files all moved underneath us starts again from nothing
{
  const store = createReferenceCountStore({ cacheMs: 30_000 });
  store.remember('/projects/alpha', 'App.cs', 'RunAsync', exactly(4));
  store.remember('/projects/beta', 'App.cs', 'RunAsync', exactly(11));

  store.forgetProject('/projects/alpha');
  assert.equal(store.get('/projects/alpha', 'App.cs', 'RunAsync'), undefined);
  assert.deepEqual(store.get('/projects/beta', 'App.cs', 'RunAsync'), exactly(11));

  store.forgetEverything();
  assert.equal(store.get('/projects/beta', 'App.cs', 'RunAsync'), undefined);
}

// a page with no project open still has somewhere to put its numbers
{
  const store = createReferenceCountStore({ cacheMs: 30_000 });
  store.remember(null, 'App.cs', 'RunAsync', exactly(4));
  assert.deepEqual(store.get(null, 'App.cs', 'RunAsync'), exactly(4));
}

// the plain-text batcher writes into a drawer of this same store, and the
// drawer follows the reader from one project to the next
{
  let requests = 0;
  let project = '/projects/alpha';
  const store = createReferenceCountStore({ cacheMs: 30_000 });
  const batcher = createReferenceCountBatcher({
    memory: store.drawer(() => project, 'the whole project'),
    maxCount: 50,
    countReferences() {
      requests += 1;
      return Promise.resolve(resultFor({ Alpha: 2 }));
    }
  });

  assert.deepEqual(await batcher.count('Alpha'), exactly(2));
  assert.equal(requests, 1);
  assert.deepEqual(
    store.get('/projects/alpha', 'the whole project', 'Alpha'),
    exactly(2),
    'the number went into the one store, filed under the project it was counted in'
  );

  project = '/projects/beta';
  assert.deepEqual(await batcher.count('Alpha'), exactly(2));
  assert.equal(requests, 2, 'another project is counted on its own');

  project = '/projects/alpha';
  assert.deepEqual(await batcher.count('Alpha'), exactly(2));
  assert.equal(requests, 2, 'coming back finds the first project’s number still there');
}

console.log('referenceCountBatcher tests passed');
