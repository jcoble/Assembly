/** The owner's answers to what waits on them, shared by Agents > Inbox and the Board's "Messages for you". */
import { SvelteMap, SvelteSet } from 'svelte/reactivity';
import { answerBoardQuestion } from '../board/boardSource.ts';
import { sessionRowJump } from '../components/sessionRowJump.ts';
import { adoptSession, sendStructuredMessage } from '../conversation/conversationService.ts';
import { ensureConversationSession } from '../conversation/conversationStore.svelte.ts';
import type { AgentConversationProvider } from '../conversation/conversationTypes.ts';
import { normalizeProvider, type OwnedSession } from '../ownedSessions.ts';
import { rail } from '../stores/sessionRailStore.svelte.ts';
import { answerWorkflowPause, approveWorkflowGate } from './workflowService.ts';
import { workflowMachine } from './workflowTypes.ts';

export type InboxDecision = 'approve' | 'another-round' | 'accept' | 'stop';

/**
 * Answers a waiting step on the machine that owns its run (`mac` for this Mac).
 * The decision itself is not cancelled: stopping it partway on the other machine
 * could leave a gate approved with its next step never started.
 */
export async function answerInboxStep(
  step: { machine: string; runId: string; nodeId: string },
  decision: InboxDecision
): Promise<void> {
  const machine = workflowMachine(step.machine);
  const idempotencyKey = `agents:inbox:${crypto.randomUUID()}`;
  if (decision === 'approve') {
    await approveWorkflowGate({ runId: step.runId, nodeId: step.nodeId, approval: { approved: true }, idempotencyKey }, machine);
  } else {
    await answerWorkflowPause({ runId: step.runId, nodeId: step.nodeId, decision, reply: null, idempotencyKey }, machine);
  }
}

/**
 * Opens a waiting session at its latest message, adopting it first. `current` turns false
 * when a newer click or the view closing made this open stale.
 */
export async function openInboxSession(ownedId: string, signal: AbortSignal, current: () => boolean): Promise<void> {
  const adopted = await adoptSession(ownedId, signal);
  if (!current()) return;
  if (!adopted) throw new Error('That session is not available yet.');
  await sessionRowJump(ownedId, 'session');
}

/** What a waiting session waits for, in words. */
export function waitingOn(session: OwnedSession): string {
  if (session.pendingPermission) return 'Waiting for permission';
  if (session.runtimeState === 'waiting-approval') return 'Waiting for approval';
  return 'Waiting for your answer';
}

/** Board answers sent to their session but not yet recorded, by `machine:id:askedAtMs`; Retry records them. */
const sentNotRecorded = new SvelteMap<string, string>();
/** Questions with an answer on its way; one at a time each. */
const answering = new SvelteSet<string>();

export function questionState(key: string): { busy: boolean; unrecorded: string | undefined } {
  return { busy: answering.has(key), unrecorded: sentNotRecorded.get(key) };
}

/**
 * Sends `text` to the session as a normal owner message, leaving the composer's screenshots
 * where they are; false when cancelled before the send.
 */
async function sendOwnerAnswer(ownedId: string, text: string, signal: AbortSignal): Promise<boolean> {
  const adopted = await adoptSession(ownedId, signal);
  // The lookup answers false when cancelled, so the signal is checked first.
  if (signal.aborted) return false;
  const owned = rail.owned.find((session) => session.ownedId === ownedId);
  if (!adopted || !owned) throw new Error('That session is not available yet.');
  // Only agent sessions can ask, through the board tool, so this is a conversation provider.
  ensureConversationSession(ownedId, normalizeProvider(owned.agent).agent as AgentConversationProvider);
  if (!await sendStructuredMessage(ownedId, text, undefined, false)) throw new Error('The message was not sent.');
  return true;
}

/**
 * Answers a board question: sends "Q<n>: `answer`" to its session, then records `answer` on the question's machine.
 * Only the send can be cancelled; once sent, the answer is always recorded. `record` false sends only.
 */
export async function answerQuestion(
  q: { machine: string; id: number; askedAtMs: number; ownedId: string },
  answer: string,
  signal: AbortSignal,
  record = true
): Promise<void> {
  // Asking again moves askedAtMs, so an answer kept for Retry never lands on the new question.
  const key = `${q.machine}:${q.id}:${q.askedAtMs}`;
  if (answering.has(key)) return;
  answering.add(key);
  try {
    let sent = sentNotRecorded.get(key);
    if (sent === undefined) {
      if (!await sendOwnerAnswer(q.ownedId, `Q${q.id}: ${answer}`, signal) || !record) return;
      sent = answer;
      sentNotRecorded.set(key, answer);
    }
    try {
      // False means the agent already marked it answered or asked it again, which is fine.
      await answerBoardQuestion(q.machine, q.id, q.askedAtMs, sent, Date.now());
      sentNotRecorded.delete(key);
    } catch {
      throw new Error('Sent, but not recorded. Retry records it only.');
    }
  } finally {
    answering.delete(key);
  }
}
