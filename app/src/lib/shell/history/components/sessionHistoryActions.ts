/**
 * What a session card in the History panel offers, and what it cannot.
 *
 * This file is pure on purpose: it decides which actions a given session
 * supports and, for the ones it does not, writes the sentence the card shows
 * instead. Keeping that here means the answer can be tested without a browser,
 * and means the expanded action row and the more-actions menu cannot drift
 * apart — both read this one list.
 *
 * The rule the whole panel follows: an action is either backed by something
 * real or it is off with a plain-English reason. Nothing pretends.
 */
import { hasTranscriptReader, runningRuntimeStates } from '../sessionHistoryViewModel.ts';
import { normalizeProvider } from '../../ownedSessions.ts';
import { resumeCommand } from '../../resumeCommand.ts';
import type { AgentConversationTranscriptImport } from '../../../tauriSource.ts';
import type { SessionLibraryRecord } from '../library/sessionLibraryModel.ts';

export type SessionHistoryActionId =
  | 'resume-assembly'
  | 'view-log'
  | 'copy-resume-command'
  | 'open-log'
  | 'reveal-log'
  | 'open-working-directory'
  | 'copy-session-id'
  | 'copy-log-path'
  | 'delete';

export interface SessionHistoryAction {
  id: SessionHistoryActionId;
  label: string;
  enabled: boolean;
  /** Plain English, shown as the item's tooltip when it is off. Null when enabled. */
  disabledReason: string | null;
  destructive: boolean;
}

/** The order a person sees, in the menu and in the expanded action row. */
export const SESSION_HISTORY_ACTION_IDS: readonly SessionHistoryActionId[] = [
  'resume-assembly',
  'view-log',
  'copy-resume-command',
  'open-log',
  'reveal-log',
  'open-working-directory',
  'copy-session-id',
  'copy-log-path',
  'delete'
];

const NO_TRANSCRIPT = 'No transcript file was found for this session.';
const NO_FOLDER = 'This session has no folder recorded.';
const NO_RESUME_COMMAND = 'This session has no resume command to copy.';
const NOT_OURS = 'Only sessions started in this app can be deleted.';
const NO_SESSION_ID = 'This session has no provider session id to read a transcript for.';
const UNREADABLE_AGENT = 'This app cannot read transcripts written by this agent.';

/** The one resume command a card offers to copy: the same one the rail builds
 * for Claude and Codex, which also covers sessions started in this app (they
 * carry no scan details), and the scanner's own command for other agents. */
export function sessionResumeCommand(record: SessionLibraryRecord): string | null {
  const { agent } = normalizeProvider(record.provider);
  return (
    resumeCommand(agent, record.nativeSessionId, record.canonicalCwd ?? '') ??
    record.available?.resumeCommands?.[0] ??
    null
  );
}

/** What Copy Session ID puts on the clipboard: the provider's id when there is
 * one, and the row's own identity when there is not. */
export function sessionHistoryIdentity(record: SessionLibraryRecord): string {
  return record.nativeSessionId ?? record.key;
}

/**
 * What Resume as Assembly Session asks the backend for: which agent wrote the
 * transcript, the id that agent knew the session by, the file it wrote, and the
 * folder it ran in. Null when the row is missing any of the four, which is the
 * same answer that switches the action off.
 *
 * The folder is required because resuming is not only reading: the conversation
 * is handed back to its agent through the ACP adapter, and an adapter has to be
 * started somewhere.
 */
export function sessionTranscriptImport(
  record: SessionLibraryRecord
): AgentConversationTranscriptImport | null {
  // A session run through cmux still writes its agent's own transcript, so the
  // row's provider is read the way the rest of the shell reads it rather than
  // being matched literally — `cmux-codex` is a codex transcript.
  const { agent } = normalizeProvider(record.provider);
  const provider = hasTranscriptReader(agent) ? agent : null;
  const nativeSessionId = record.nativeSessionId?.trim() ?? '';
  const transcriptPath = record.logPath?.trim() ?? '';
  const cwd = record.canonicalCwd?.trim() ?? '';
  if (!provider || !nativeSessionId || !transcriptPath || !cwd) return null;
  // A session whose agent is running here is only selected: reading it again needs it closed first.
  if (record.ownedId && record.runtimeState && runningRuntimeStates.has(record.runtimeState)) return null;
  // The name travels with the import so the DATABASE gets it. Setting it on the
  // rail row after the fact looked right until the next reload, when the rail
  // read the row back and found no title on it.
  const title = record.title?.trim() || null;
  return { provider, nativeSessionId, transcriptPath, cwd, title };
}

function action(
  id: SessionHistoryActionId,
  label: string,
  available: boolean,
  reason: string,
  destructive = false
): SessionHistoryAction {
  return {
    id,
    label,
    enabled: available,
    disabledReason: available ? null : reason,
    destructive
  };
}

/**
 * The nine actions a session card offers, in the order a person sees them. `machineScanning` is true while the
 * record's machine, this Mac included, has not answered History's scan (a session Assembly holds learns its transcript from it),
 * and `runningElsewhere` once a resume was refused because its own CLI or app is still running it.
 */
export function sessionHistoryActions(record: SessionLibraryRecord, machineScanning = false, runningElsewhere = false): SessionHistoryAction[] {
  const hasLog = Boolean(record.logPath?.trim());
  const hasFolder = Boolean(record.canonicalCwd?.trim());
  // Resuming needs all four of the agent, the id it knew the session by, the
  // file it wrote, and the folder it ran in. Say which one is missing, most
  // specific first — a row with no id at all is a different problem from a row
  // whose agent this app cannot read.
  const canResumeAssembly = sessionTranscriptImport(record) !== null;
  const resumeLabel = 'Resume here';
  const waiting = (machineScanning && Boolean(record.ownedId)) || runningElsewhere;
  const resumeReason = runningElsewhere ? 'Running outside Assembly' : waiting ? 'Scanning…' : !record.nativeSessionId?.trim()
    ? NO_SESSION_ID
    : !hasLog
      ? NO_TRANSCRIPT
      : !hasFolder
        ? NO_FOLDER
        : UNREADABLE_AGENT;

  const actions = [
    action('resume-assembly', resumeLabel, !waiting && (Boolean(record.ownedId) || canResumeAssembly), resumeReason),
    action('view-log', 'View Log', hasLog, NO_TRANSCRIPT),
    action(
      'copy-resume-command',
      'Copy Resume Command',
      Boolean(sessionResumeCommand(record)),
      NO_RESUME_COMMAND
    ),
    action('open-log', 'Open Log', hasLog, NO_TRANSCRIPT),
    action('reveal-log', 'Reveal Log', hasLog, NO_TRANSCRIPT),
    action('open-working-directory', 'Open Working Directory', hasFolder, NO_FOLDER),
    // Every row has an identity of some kind, so this one is never off.
    action('copy-session-id', 'Copy Session ID', true, ''),
    action('copy-log-path', 'Copy Log Path', hasLog, NO_TRANSCRIPT),
    action('delete', 'Delete', Boolean(record.ownedId), NOT_OURS, true)
  ];
  return record.machine !== 'local'
    ? actions.filter((entry) => ['resume-assembly', 'copy-resume-command', 'copy-session-id', 'delete'].includes(entry.id))
    : actions;
}
