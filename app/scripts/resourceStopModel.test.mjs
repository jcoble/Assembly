/**
 * resourceStopModel.test.mjs — what the stop dialog says, and what it sends.
 *
 * The point of these tests is that the sentence a person reads and the request
 * the backend receives are built from the same list of process ids. A dialog
 * that says "three processes" while the request names four is the failure this
 * guards against.
 */
import assert from 'node:assert/strict';
import test from 'node:test';

import {
  buildStopRequest,
  describeStopQuestion,
  stopTargetPids
} from '../src/lib/shell/resources/resourceStopModel.ts';

const session = {
  scope: 'session',
  label: 'Terminal 1 in mac-command-bar',
  ownedId: 'terminal-a',
  rootPid: 4100,
  pids: [4100, 4211, 4212]
};

test('the process id list keeps the root first and drops repeats', () => {
  assert.deepEqual(stopTargetPids(session), [4100, 4211, 4212]);
  assert.deepEqual(
    stopTargetPids({ ...session, pids: [4212, 4100, 4212, 0, -3] }),
    [4100, 4212]
  );
});

test('the dialog names every process id it is about to signal', () => {
  const question = describeStopQuestion(session);

  assert.equal(question.title, 'Stop Terminal 1 in mac-command-bar?');
  assert.equal(question.lines[0], '3 processes will be asked to stop: PIDs 4100, 4211 and 4212.');
  assert.equal(question.lines[1], 'Anything still running five seconds later is forced to quit.');
  assert.equal(question.confirmLabel, 'Stop processes');
  assert.equal(question.cancelLabel, 'Leave it running');
});

test('one process reads as one process', () => {
  const question = describeStopQuestion({
    scope: 'process',
    label: 'cargo (PID 5150)',
    rootPid: 5150,
    pids: [5150]
  });

  assert.equal(question.lines[0], '1 process will be asked to stop: PID 5150.');
  assert.equal(question.confirmLabel, 'Stop process');
  assert.equal(question.intro, 'This ends this process and anything it started.');
});

test('the request carries exactly the ids the dialog read out', () => {
  const question = describeStopQuestion(session);
  const request = buildStopRequest(session);

  assert.deepEqual(request, {
    rootPid: 4100,
    ownedId: 'terminal-a',
    expectedPids: [4100, 4211, 4212]
  });
  for (const pid of request.expectedPids) {
    assert.ok(question.lines[0].includes(String(pid)), `the dialog must name PID ${pid}`);
  }
});

test('a row with no session identity sends none', () => {
  assert.deepEqual(buildStopRequest({ scope: 'process', label: 'node', rootPid: 77, pids: [77] }), {
    rootPid: 77,
    expectedPids: [77]
  });
});
