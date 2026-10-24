/**
 * The Agents panel's view model.
 *
 * A session's subagents arrive as `ConversationChildAgent` records, which carry
 * a title, a provider state string, timestamps, and transcript availability. In
 * particular there is no message count and no path to a log on that record, so
 * this module is careful to say "not known" rather than invent either one.
 *
 * A message count exists only for an agent whose transcript has actually been
 * read, because the store keeps a timeline for the selected child alone. That
 * is why `messageCount` is `number | null`: `null` means nobody has looked yet,
 * and `0` means we looked and the transcript was empty. The panel shows blank
 * for the first and a real zero for the second.
 */
import type { UIMessage } from '@tanstack/ai/client';
import type { ConversationChildAgent } from '../../conversation/conversationTypes.ts';
import { conversationMessageDisplayItem } from '../../conversation/conversationMessages.ts';

export type AgentStatus = 'working' | 'waiting' | 'done' | 'failed' | 'disconnected' | 'stopped' | 'idle';

/** The word and chip tone each status shows in the Agents panel. */
export const AGENT_STATUS_WORD: Record<AgentStatus, string> = {
  working: 'Working',
  waiting: 'Needs you',
  done: 'Done',
  failed: 'Failed',
  disconnected: 'Disconnected',
  stopped: 'Stopped',
  idle: 'Idle'
};
export const AGENT_STATUS_TONE = {
  working: 'live',
  waiting: 'attention',
  done: 'good',
  failed: 'bad',
  disconnected: 'neutral',
  stopped: 'neutral',
  idle: 'neutral'
} as const satisfies Record<AgentStatus, string>;

export interface AgentActivityRow {
  childId: string;
  label: string;
  status: AgentStatus;
  /** One plain-English line about what this agent is doing, or '' when nothing is known. */
  activity: string;
  /** Message count, or null when it cannot be known for this agent. */
  messageCount: number | null;
  /** Absolute path of a log to open, or null when none is available. */
  logPath: string | null;
  updatedAtMs: number;
}

/** How long an activity line may run before it is cut short with an ellipsis. */
const ACTIVITY_LINE_LIMIT = 80;

const WORKING_STATES = new Set(['active', 'running', 'in_progress', 'in-progress', 'working', 'started']);
const DONE_STATES = new Set(['completed', 'complete', 'done', 'finished', 'succeeded', 'success']);
const FAILED_STATES = new Set(['failed', 'failure', 'error', 'errored']);
const STOPPED_STATES = new Set(['cancelled', 'canceled']);

/** Maps the provider's raw state string onto a status. Unknown states become 'idle'. */
export function agentStatus(state: string): AgentStatus {
  const normalized = state.trim().toLowerCase();
  if (WORKING_STATES.has(normalized)) return 'working';
  if (DONE_STATES.has(normalized)) return 'done';
  if (FAILED_STATES.has(normalized)) return 'failed';
  if (normalized === 'disconnected') return 'disconnected';
  if (STOPPED_STATES.has(normalized)) return 'stopped';
  return 'idle';
}

function firstLine(text: string): string {
  const line = text.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
  return line.length > ACTIVITY_LINE_LIMIT ? `${line.slice(0, ACTIVITY_LINE_LIMIT - 1).trimEnd()}…` : line;
}

/**
 * One line describing a single timeline entry, or '' when that entry says
 * nothing a person would want on a one-line summary.
 */
function lineFor(message: UIMessage): string {
  const entry = conversationMessageDisplayItem(message);
  if (!entry) return '';
  switch (entry.kind) {
    case 'user':
      return '';
    case 'assistant':
      return firstLine(entry.text);
    case 'tool':
      if (entry.state === 'failed') return firstLine(`${entry.title} failed`);
      if (entry.state === 'completed') return firstLine(`Finished ${entry.title}`);
      return firstLine(`Running ${entry.title}`);
    case 'approval':
      return entry.state === 'requested' ? 'Waiting for approval' : firstLine(entry.summary ?? '');
    case 'plan':
      return 'Working through a plan';
    case 'error':
      return firstLine(entry.text);
    default:
      return '';
  }
}

/** The newest entry that describes something, searching backwards. */
function activityFrom(timeline: readonly UIMessage[]): string {
  for (let index = timeline.length - 1; index >= 0; index -= 1) {
    const line = lineFor(timeline[index]);
    if (line) return line;
  }
  return '';
}

/** Rows for one session's children, most recently updated first.
 *  `timelineByChild` supplies a count only for children whose transcript has been read.
 *  `waiting` holds helper sessions waiting on the owner; a working one among them needs you. */
export function agentActivityRows(
  children: readonly ConversationChildAgent[],
  timelineByChild: Readonly<Record<string, readonly UIMessage[]>>,
  waiting: ReadonlySet<string> = new Set()
): AgentActivityRow[] {
  return [...children]
    .sort((left, right) => right.updatedAtMs - left.updatedAtMs)
    .map((child) => {
      const timeline = timelineByChild[child.childId] ?? null;
      const transcriptActivity = timeline ? activityFrom(timeline) : '';
      const status = agentStatus(child.state);
      return {
        childId: child.childId,
        label: child.title,
        status: status === 'working' && waiting.has(child.childId) ? 'waiting' : status,
        activity: transcriptActivity || firstLine(child.latestActivity ?? ''),
        messageCount: timeline ? timeline.length : null,
        // A child agent record carries no log path, so there is never one to
        // offer. This stays here rather than being dropped so the panel can say
        // so honestly, and so the day a path arrives there is one place to fill in.
        logPath: null,
        updatedAtMs: child.updatedAtMs
      };
    });
}
