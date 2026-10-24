/**
 * gitDiffTimeout.test.ts — a selected diff read is stopped through its signal.
 *
 * `withGitDiffTimeout` hands the read one signal that aborts when its owner
 * stops or the time runs out, so the backend can cancel the git call instead
 * of the page only dropping the answer.
 *
 * Run: node --experimental-strip-types scripts/gitDiffTimeout.test.ts
 */

globalThis.$state = (value) => value;

import assert from 'node:assert/strict';

const { withGitDiffTimeout, GIT_DIFF_TIMEOUT_MESSAGE } = await import(
  '../src/lib/shell/git/gitBackendExtra.ts'
);

/** A read that only ends when its signal aborts, like a cancelled backend call.
 * Node does not wait on AbortSignal.timeout, so the test holds the process open. */
async function readUntilAborted(signal: AbortSignal): Promise<never> {
  signal.throwIfAborted();
  const keepAlive = setInterval(() => {}, 1000);
  try {
    await new Promise((_resolve, reject) => {
      signal.addEventListener('abort', () => reject(signal.reason), { once: true });
    });
    throw new Error('unreachable');
  } finally {
    clearInterval(keepAlive);
  }
}

// ── the timeout aborts the call's signal and reports the timeout message ──
{
  let seen: AbortSignal | null = null;
  await assert.rejects(
    withGitDiffTimeout((signal: AbortSignal) => {
      seen = signal;
      return readUntilAborted(signal);
    }, new AbortController().signal, 5),
    { message: GIT_DIFF_TIMEOUT_MESSAGE }
  );
  assert.equal(seen?.aborted, true, 'the call saw its signal abort on timeout');
}

// ── a read that ignores its signal still ends with the timeout message ──
{
  const keepAlive = setInterval(() => {}, 1000);
  try {
    await assert.rejects(
      withGitDiffTimeout(() => new Promise(() => {}), new AbortController().signal, 5),
      { message: GIT_DIFF_TIMEOUT_MESSAGE }
    );
  } finally {
    clearInterval(keepAlive);
  }
}

// ── the owner stopping aborts the call's signal, and is not called a timeout ──
{
  const owner = new AbortController();
  let seen: AbortSignal | null = null;
  const read = withGitDiffTimeout((signal: AbortSignal) => {
    seen = signal;
    return readUntilAborted(signal);
  }, owner.signal, 60_000);
  owner.abort();
  await assert.rejects(read, (error: unknown) => {
    assert.notEqual((error as Error).message, GIT_DIFF_TIMEOUT_MESSAGE);
    return true;
  });
  assert.equal(seen?.aborted, true, 'the call saw its signal abort when the owner stopped');
}

// ── a read that answers in time returns its answer ──
{
  const answer = await withGitDiffTimeout(async () => 'diff', new AbortController().signal, 1000);
  assert.equal(answer, 'diff');
}

console.log('gitDiffTimeout tests passed');
