import assert from 'node:assert/strict';

import { boardNotifications } from '../src/lib/shell/board/boardNotifications.ts';
import type { BoardCard, BoardQuestion, MachineBoard } from '../src/lib/shell/board/boardSource.ts';

const card = (key: string, state: BoardCard['state'], summary = 'Merge PR #1', steps: string[] = []) =>
  ({ key, title: `Card ${key}`, state, summary, steps, note: null }) as BoardCard;
const question = (id: number, askedAtMs: number, answer: string | null = null) =>
  ({ id, title: `Question ${id}`, body: 'Ship it?', asked_at_ms: askedAtMs, answer, answered_at_ms: answer ? askedAtMs + 1 : null }) as BoardQuestion;
const board = (cards: BoardCard[], questions: BoardQuestion[] = [], machine = 'mac') =>
  ({ machine, value: { cards, runs: [], steps: [], counts: {}, session_notes: [], questions } }) as MachineBoard;

{ // a_card_entering_needs_you_notifies_once
  const sent = boardNotifications([board([card('a', 'running')])], [board([card('a', 'needs_you')])]);
  assert.deepEqual(sent, [{ title: 'Build Board · Card a', body: 'Merge PR #1' }]);
  assert.equal(boardNotifications([board([])], [board([card('b', 'needs_you')])]).length, 1, 'a new needs_you card');
}

{ // an_unchanged_card_does_not_notify_again
  assert.deepEqual(boardNotifications([board([card('a', 'needs_you')])], [board([card('a', 'needs_you', 'Edited')])]), []);
}

{ // a_re_asked_question_notifies_and_an_answer_does_not
  assert.equal(boardNotifications([board([], [question(1, 10)])], [board([], [question(1, 20)])]).length, 1);
  assert.deepEqual(boardNotifications([board([], [question(1, 10)])], [board([], [question(1, 10, 'yes')])]), []);
  const sent = boardNotifications([board([])], [board([], [question(2, 5)])]);
  assert.deepEqual(sent, [{ title: 'Build Board · Question 2', body: 'Ship it?' }]);
}

{ // running_to_done_does_not_notify
  assert.deepEqual(boardNotifications([board([card('a', 'running')])], [board([card('a', 'done')])]), []);
}

{ // the_first_read_of_a_machine_notifies_nothing_so_a_reload_or_reconnect_does_not_repeat
  assert.deepEqual(boardNotifications(null, [board([card('a', 'needs_you')], [question(1, 1)])]), []);
  assert.deepEqual(boardNotifications([board([])], [board([card('a', 'needs_you')], [], 'workbox')]), []);
}

{ // notification_body_is_what_is_needed: the first step, else the question or summary, at most 120 characters
  const [steps] = boardNotifications([board([])], [board([card('a', 'needs_you', 'Context', ['Approve PR #1', 'Merge'])])]);
  assert.equal(steps.body, 'Approve PR #1');
  const [long] = boardNotifications([board([])], [board([card('a', 'needs_you', 'x'.repeat(400))])]);
  assert.equal(long.body.length, 120);
}
