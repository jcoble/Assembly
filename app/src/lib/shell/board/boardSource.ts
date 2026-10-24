import { invokeCancellable } from '../../tauriSource.ts';

export type BoardNote = { id: string; body: string; created_at_ms: number };
export type BoardCard = {
  key: string; title: string; state: 'needs_you' | 'running' | 'in_review' | 'done';
  summary: string; steps: string[]; note: string | null;
  task_key: string | null; task_url: string | null; pr_url: string | null;
  run_id: string | null; session_owned_id: string | null;
  written_by: string; created_by: string; writer_name: string | null;
  created_at_ms: number; updated_at_ms: number; snoozed_until_ms: number | null;
  run_state: string | null; run_step: string | null; session_state: string | null;
  notes: BoardNote[]; stale: number;
};
export type BoardRun = {
  id: string; name: string | null; task_title: string | null; task_key: string | null;
  state: string; current_step: string | null; steps_done: number; steps_total: number;
  round: number | null; findings: number | null; notes: BoardNote[];
};
export type BoardStep = {
  run_id: string; node_id: string; state: 'waiting-input' | 'waiting-approval';
  reason: string | null; owned_id: string | null; code: string | null; attempt: number;
  workflow_name: string | null; task_key: string | null; updated_at_ms: number;
  notes: BoardNote[];
};
export type BoardQuestion = {
  id: number; key: string; title: string; body: string; recommended: string | null;
  session_owned_id: string; session_name: string | null; session_state: string | null;
  answer: string | null; asked_at_ms: number; answered_at_ms: number | null;
};
export type MachineBoard = {
  machine: string;
  value: {
    cards: BoardCard[]; runs: BoardRun[]; steps: BoardStep[];
    /** Every card per state, including those past the 40-card limit. */
    counts: Partial<Record<BoardCard['state'], number>>;
    session_notes: Array<{ target: string; notes: BoardNote[] }>;
    /** Missing from a machine on an older build. */
    questions?: BoardQuestion[];
  };
};
export type BoardCount = { count: number; snoozed_until_ms: number | null };
export type BoardCardAction = 'done' | 'snooze' | 'drop' | 'undo' | 'clear_done';

/** A remote read can finish after cancellation; its result must not reach the board. */
async function board<T>(op: string, args: Record<string, unknown>, signal: AbortSignal): Promise<T | null> {
  const result = await invokeCancellable<T>('board', { op, args }, signal);
  return signal.aborted ? null : result;
}

/** The desktop and each reachable machine's bounded board document. */
export async function readBoard(todayMs: number, nowMs: number, signal: AbortSignal): Promise<MachineBoard[] | null> {
  return board('board', { today_ms: todayMs, now_ms: nowMs }, signal);
}

/** The combined badge count and earliest snooze end across machines. */
export async function countBoard(nowMs: number, signal: AbortSignal): Promise<BoardCount | null> {
  return board('board_count', { now_ms: nowMs }, signal);
}

/** Owner actions go only to the machine that owns the card. */
export async function changeBoardCard(
  machine: string, key: string, action: BoardCardAction, at: number, signal: AbortSignal
): Promise<boolean | null> {
  return board('board_card', { machine, key, action, at }, signal);
}

export async function addBoardNote(
  machine: string, target: string, body: string, nowMs: number, signal: AbortSignal
): Promise<boolean | null> {
  return board('board_note', { machine, target, body, now_ms: nowMs }, signal);
}

/** Records the owner's answer to the question as asked at `askedAtMs`; false when already answered or asked again. */
export async function answerBoardQuestion(machine: string, id: number, askedAtMs: number, answer: string, nowMs: number): Promise<boolean | null> {
  // A recorded answer must not be cancelled halfway, so this takes no signal.
  return invokeCancellable('board', { op: 'board_question', args: { machine, id, asked_at_ms: askedAtMs, answer, now_ms: nowMs } }, new AbortController().signal);
}
