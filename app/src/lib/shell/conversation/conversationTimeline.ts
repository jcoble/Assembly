import type {
  AgentCommandDescriptor,
  AgentConfigValue,
  AgentContent,
  AgentEvent,
  AgentItem,
  AgentItemType,
  AgentConversationEvent,
  AgentConversationTurnFacts,
  AgentPermissionOption,
  AgentUserInputField,
  ConversationAttachment
} from './conversationTypes.ts';

import type { SafeMarkdownBlock } from './conversationMessageSafety.ts';

export interface AgentPlanStep {
  id: string;
  title: string;
  detail: string | null;
  state: 'pending' | 'in-progress' | 'completed' | 'failed' | 'blocked';
  ownerAgentId: string | null;
  startedAt: string | null;
  completedAt: string | null;
}

export interface ConversationTask {
  id: string;
  title: string;
  state: AgentPlanStep['state'];
  detail?: string | null;
  ownerAgentId?: string | null;
}

export type ConversationToolKind = 'command' | 'file-edit' | 'search' | 'fetch' | 'tool';
export type ConversationToolState = 'pending' | 'running' | 'completed' | 'failed';

export type ConversationTextDisplayItem = {
  kind: 'user' | 'assistant' | 'reasoning' | 'command' | 'file' | 'error';
  itemId: string;
  text: string;
  timestampMs: number;
  completed?: boolean;
  label?: string;
  blocks?: readonly SafeMarkdownBlock[];
  metadata?: Record<string, AgentConfigValue>;
};

export type ConversationDisplayItem = (
  | (ConversationTextDisplayItem & { kind: 'user'; attachments?: readonly ConversationAttachment[] })
  | (ConversationTextDisplayItem & { kind: 'assistant' })
  | (ConversationTextDisplayItem & { kind: 'reasoning' })
  | (ConversationTextDisplayItem & { kind: 'command' })
  | (ConversationTextDisplayItem & { kind: 'file' })
  | (ConversationTextDisplayItem & { kind: 'error' })
  | { kind: 'plan'; itemId: string; title: string; steps: AgentPlanStep[]; timestampMs: number; text?: string }
  | { kind: 'tasks'; itemId: string; title: string; tasks: ConversationTask[]; timestampMs: number; text?: string }
  | {
      kind: 'tool';
      itemId: string;
      title: string;
      toolKind: ConversationToolKind;
      state: ConversationToolState;
      output?: string;
      diff?: string; hasDetail?: boolean; added?: number; removed?: number;
      path?: string;
      summary?: string;
      timestampMs: number;
      metadata?: Record<string, AgentConfigValue>;
    }
  | { kind: 'subagent'; itemId: string; childId: string; parentId: string; label: string; state: string; timestampMs: number; metadata?: Record<string, AgentConfigValue> }
  | {
      kind: 'approval';
      itemId: string;
      requestId: string;
      title: string;
      toolTitle: string;
      summary: string;
      state: string;
      options: AgentPermissionOption[];
      timestampMs: number;
    }
  | { kind: 'input'; itemId: string; requestId: string; title: string; description?: string; fields: AgentUserInputField[]; timestampMs: number }
  | { kind: 'fileEdits'; itemId: string; edits: readonly ConversationFileEdit[]; timestampMs: number }
  | {
      kind: 'toolRun';
      itemId: string;
      items: readonly ConversationDisplayItem[];
      summary: string;
      icon: 'pencil' | 'book' | 'terminal' | 'search' | 'sparkles';
      timestampMs: number;
    }
  | { kind: 'compaction'; itemId: string; trigger?: string; preTokens?: number; postTokens?: number; timestampMs: number }
  | { kind: 'unknown'; itemId: string; text: string; timestampMs: number; metadata?: Record<string, AgentConfigValue> }
) & {
  readonly turnId?: string | null;
  readonly completed?: boolean;
};

/** One file a turn changed, as the grouped row lists it. */
export interface ConversationFileEdit {
  /** The row this edit was folded out of, so the group keys by something stable. */
  readonly itemId: string;
  readonly path: string;
  readonly diff?: string;
  readonly added: number;
  readonly removed: number;
}

export interface ConversationTurnGroup {
  readonly turnId: string | null;
  readonly items: readonly ConversationDisplayItem[];
  readonly workItemIds: readonly string[];
  readonly tailItemIds: readonly string[];
  readonly completed: boolean;
  readonly elapsedMs: number | null;
  readonly startedAtMs: number | null;
  readonly running: boolean;
  readonly stopped?: boolean; // The owner stopped it; set on its last part, which ends with a Stopped line.
}

/**
 * The path information carried by the event that rendered a file link. A
 * provider may record a workspace root, or only the file the event concerned;
 * both are useful when the reader has since pointed the source panels at a
 * different checkout.
 */
export interface ConversationFileLinkProvenance {
  readonly root?: string;
  readonly path?: string;
}

function metadataString(
  metadata: Record<string, AgentConfigValue> | undefined,
  keys: readonly string[]
): string | undefined {
  for (const key of keys) {
    const value = metadata?.[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return undefined;
}

/** Preserve the event/provider path instead of borrowing the selected checkout. */
export function conversationFileLinkProvenance(
  item: ConversationDisplayItem
): ConversationFileLinkProvenance | undefined {
  const metadata = 'metadata' in item ? item.metadata : undefined;
  const root = metadataString(metadata, ['root', 'cwd', 'projectPath', 'worktree']);
  if (root) return { root };
  const path = item.kind === 'tool'
    ? item.path ?? metadataString(metadata, ['path'])
    : metadataString(metadata, ['path']);
  return path ? { path } : undefined;
}

/**
 * The file a tool row may open. Only a path the call carried counts: its
 * summary is a description ("Read slot 4 API log") or the command text, and
 * opening that as a file joins it onto the session folder and lands on a file
 * that does not exist.
 */
export function toolFilePath(item: Extract<ConversationDisplayItem, { kind: 'tool' }>): string {
  const meta = item.metadata as Record<string, unknown> | undefined;
  if (item.path?.trim()) return item.path.trim();
  if (!meta) return '';
  let rawArgs: unknown = meta.args ?? meta.arguments ?? meta.parameters ?? meta.input ?? meta.rawInput;
  if (typeof rawArgs === 'string') {
    try { rawArgs = JSON.parse(rawArgs); } catch { rawArgs = undefined; }
  }
  const direct = meta.AbsolutePath || meta.TargetFile || meta.filePath || meta.file_path || meta.path || meta.file || meta.target;
  if (typeof direct === 'string' && direct.trim()) return direct.trim();
  if (rawArgs && typeof rawArgs === 'object' && !Array.isArray(rawArgs)) {
    const args = rawArgs as Record<string, unknown>;
    const fromArgs = args.AbsolutePath || args.TargetFile || args.filePath || args.file_path || args.path || args.file || args.target;
    if (typeof fromArgs === 'string' && fromArgs.trim()) return fromArgs.trim();
  }
  return '';
}

const FOLDABLE_TURN_KINDS = new Set<ConversationDisplayItem['kind']>([
  'reasoning',
  'fileEdits',
  'toolRun',
  'tool',
  'command',
  'file',
  'subagent',
  'plan',
  'tasks',
  // Only a finished turn folds, so an approval still waiting for an answer is never hidden.
  'approval'
]);

/** Tool-run display state; native turn metadata separately owns turn completion. */
function turnItemSettled(item: ConversationDisplayItem): boolean {
  if (item.kind === 'toolRun') return Boolean(item.completed && item.items.every(turnItemSettled));
  if (item.kind === 'tool') return item.state === 'completed' || item.state === 'failed';
  if (item.kind === 'subagent') {
    return ['completed', 'complete', 'failed', 'cancelled', 'canceled', 'stopped', 'done']
      .includes(item.state.trim().toLowerCase());
  }
  if (item.kind === 'plan') {
    return item.steps.every((step) => !['pending', 'in-progress'].includes(step.state));
  }
  if (item.kind === 'tasks') {
    return item.tasks.every((task) => !['pending', 'in-progress'].includes(task.state));
  }
  if (item.kind === 'approval') {
    return !['requested', 'pending', 'running'].includes(item.state.trim().toLowerCase());
  }
  if (item.kind === 'input') return false;
  return true;
}

function turnGroup(
  turnId: string | null,
  items: readonly ConversationDisplayItem[],
  running: boolean,
  facts: AgentConversationTurnFacts | undefined
): ConversationTurnGroup {
  const finalItemId = facts?.finalAssistantItemId;
  const workItemIds = items
    .filter((item) => FOLDABLE_TURN_KINDS.has(item.kind)
      || (item.kind === 'assistant' && !!finalItemId && item.itemId !== finalItemId))
    .map((item) => item.itemId);
  const tailItemIds = items
    .filter((item) => item.kind === 'user'
      || (item.kind === 'assistant' && (!finalItemId || item.itemId === finalItemId)))
    .map((item) => item.itemId);
  const startedAtMs = facts?.startedAtMs ?? null;
  const endedAtMs = facts?.endedAtMs;
  const completed = !running && !!facts?.terminalState;
  return {
    turnId,
    items,
    workItemIds,
    tailItemIds,
    running,
    completed,
    startedAtMs,
    elapsedMs: completed && startedAtMs !== null && endedAtMs != null
      ? Math.max(0, endedAtMs - startedAtMs)
      : null
  };
}

/** The turn each row belongs to, filling in the ones a stored transcript lacks.
 *
 * A live turn is told to us: every row the agent sends carries the id of the
 * turn that produced it. A transcript read back out of the store carries none,
 * because the provider's own file never wrote one down. Those rows all arrived
 * with a null id, and grouping by that id put an entire resumed conversation
 * into a single turn — one fold covering everything, which is why a resumed
 * conversation read as a flat column of prose.
 *
 * A prompt is what starts a turn, so a user message is where a stored turn
 * begins and everything after it belongs to that turn until the next prompt.
 * The id is the first row's own id, which keeps it stable across re-renders —
 * the fold's open state is keyed by it.
 *
 * A row that carries no id of its own carries on the turn it sits in, whether
 * that turn was named by the agent or read off a prompt. Some rows are written
 * by this app rather than by the agent — a compaction marker is — and starting
 * a new turn at one of those cut the turn it interrupted in half. */
function turnIdsOf(items: readonly ConversationDisplayItem[]): (string | null)[] {
  let currentTurnId: string | null = null;
  return items.map((item) => {
    if (item.turnId) currentTurnId = item.turnId;
    else if (item.kind === 'user' || currentTurnId === null) currentTurnId = `stored-turn:${item.itemId}`;
    return currentTurnId;
  });
}

/** The file change a row carries, or null when the row is not one.
 *
 * Two rows can be a file change: the dedicated `file` row, which keeps the
 * path and the patch in its metadata, and a tool call that reported a patch
 * alongside whatever else it did. */
function fileEditOf(item: ConversationDisplayItem): ConversationFileEdit | null {
  const read = (path: unknown, diff: unknown): ConversationFileEdit | null => {
    if (typeof path !== 'string' || typeof diff !== 'string') return null;
    if (!path.trim() || !diff.trim()) return null;
    return { itemId: item.itemId, path, diff, ...diffLineCounts(diff) };
  };
  if (item.kind === 'file') return read(item.metadata?.path, item.metadata?.diff ?? item.text);
  if (item.kind !== 'tool' || item.toolKind !== 'file-edit') return null;
  // A history page sends an edit's line counts and no diff; the row reads the diff by id when opened.
  return item.diff || item.added == null || !item.path?.trim() ? read(item.path, item.diff) : { itemId: item.itemId, path: item.path, added: item.added, removed: item.removed ?? 0 };
}

/** Collapse each run of neighbouring file changes into one row.
 *
 * A turn that touches nine files used to draw nine cards, each with its own
 * heading and its own fold, and the reader had to scroll past all of them to
 * reach what the agent said next. One row naming the files, with the counts
 * beside them, says the same thing in nine lines and opens onto the same
 * diffs.
 *
 * Runs, not the whole turn: edits separated by a command or a message are
 * separate pieces of work and stay separate groups, which is also what keeps
 * the transcript in the order things happened. */
export function foldFileEdits(
  items: readonly ConversationDisplayItem[]
): readonly ConversationDisplayItem[] {
  const folded: ConversationDisplayItem[] = [];
  let run: ConversationFileEdit[] = [];
  let runTurnId: string | null | undefined;
  let runTimestampMs = 0;

  const closeRun = (): void => {
    if (!run.length) return;
    folded.push({
      kind: 'fileEdits',
      itemId: `file-edits:${run[0].itemId}`,
      edits: run,
      timestampMs: runTimestampMs,
      turnId: runTurnId,
      completed: true
    });
    run = [];
  };

  for (const item of items) {
    const edit = fileEditOf(item);
    if (!edit) {
      closeRun();
      folded.push(item);
      continue;
    }
    if (!run.length) {
      runTurnId = item.turnId;
      runTimestampMs = item.timestampMs;
    }
    run.push(edit);
  }
  closeRun();
  return folded;
}

export function summarizeToolRun(items: readonly ConversationDisplayItem[]): {
  summary: string;
  icon: 'pencil' | 'book' | 'terminal' | 'search' | 'sparkles';
} {
  let editCount = 0;
  const editedPaths = new Set<string>();
  let failedCount = 0;
  let readCount = 0;
  let commandCount = 0;
  let searchCount = 0;
  let reasoningCount = 0;
  let toolCount = 0;

  for (const item of items) {
    if (item.kind === 'tool' && item.state === 'failed') { failedCount += 1; continue; }
    if (item.kind === 'file' || item.kind === 'fileEdits' || (item.kind === 'tool' && item.toolKind === 'file-edit')) {
      const paths = item.kind === 'fileEdits' ? item.edits.map((edit) => edit.path)
        : [item.kind === 'tool' ? toolFilePath(item) : metadataString(item.metadata, ['path'])];
      for (const path of paths) {
        if (!path || !editedPaths.has(path)) editCount += 1;
        if (path) editedPaths.add(path);
      }
    } else if (item.kind === 'command' || (item.kind === 'tool' && item.toolKind === 'command')) {
      commandCount += 1;
    } else if (item.kind === 'tool' && item.toolKind === 'search') {
      searchCount += 1;
    } else if (item.kind === 'tool' && item.toolKind === 'fetch' && toolFilePath(item) && !/^https?:/i.test(toolFilePath(item))) {
      readCount += 1;
    } else if (item.kind === 'reasoning') {
      reasoningCount += 1;
    } else if (item.kind === 'tool') {
      toolCount += 1;
    }
  }

  const parts: string[] = [];
  if (editCount === 1) parts.push('Edited a file');
  else if (editCount > 1) parts.push(`Edited ${editCount} files`);

  if (readCount === 1) parts.push('read a file');
  else if (readCount > 1) parts.push('read files');

  if (commandCount === 1) parts.push('ran a command');
  else if (commandCount > 1) parts.push('ran commands');

  if (searchCount === 1) parts.push('searched codebase');
  else if (searchCount > 1) parts.push('searched files');

  if (toolCount === 1) parts.push('used a tool');
  else if (toolCount > 1) parts.push('used tools');

  if (failedCount > 0) parts.push(`${failedCount} failed ${failedCount === 1 ? 'call' : 'calls'}`);

  if (parts.length === 0) {
    if (reasoningCount > 0) return { summary: 'Thinking', icon: 'sparkles' };
    return { summary: 'Worked', icon: 'sparkles' };
  }

  parts[0] = parts[0].charAt(0).toUpperCase() + parts[0].slice(1);
  const summary = parts.join(', ');

  let icon: 'pencil' | 'book' | 'terminal' | 'search' | 'sparkles' = 'sparkles';
  if (editCount > 0) icon = 'pencil';
  else if (readCount > 0) icon = 'book';
  else if (commandCount > 0) icon = 'terminal';
  else if (searchCount > 0) icon = 'search';

  return { summary, icon };
}

/** Summarize a finished turn's recorded work, even a single action; native facts own turn completion. */
export function summarizeCompletedWork(items: readonly ConversationDisplayItem[]): string | null {
  const actions = items.flatMap((item) => item.kind === 'toolRun' ? item.items : [item])
    .filter((item) => item.kind === 'tool' ? item.state === 'completed' || item.state === 'failed'
      : item.kind === 'reasoning' || ((item.kind === 'command' || item.kind === 'file' || item.kind === 'fileEdits') && item.completed === true));
  return actions.length ? summarizeToolRun(actions).summary : null;
}

/** What a running turn is doing, read from the newest row of the transcript.
 *
 * Only a row carrying the active turn's id can speak for it: a bounded or
 * reselected transcript may end in an older turn's unfinished-looking text.
 * Open requests come from the session's pending maps rather than
 * the transcript, because an input row never records that it was answered.
 * Anything else says "Working" rather than guess. */
export function turnActivityLabel(
  items: readonly ConversationDisplayItem[],
  activeTurnId: string | null,
  pendingApprovals: number,
  pendingInputs: number
): string {
  if (pendingApprovals > 0) return 'Waiting for approval';
  if (pendingInputs > 0) return 'Waiting for your input';
  // The plan and task lists are pinned to the end of the transcript; they are
  // not what the agent is doing right now.
  const latest = items.findLast((item) => item.kind !== 'plan' && item.kind !== 'tasks');
  if (!latest || !activeTurnId || latest.turnId !== activeTurnId) return 'Working';
  if (latest.kind === 'reasoning' && latest.completed === false) return 'Thinking';
  if (latest.kind === 'assistant' && latest.completed === false) return 'Writing';
  if (latest.kind === 'tool' && (latest.state === 'pending' || latest.state === 'running')) {
    if (latest.toolKind === 'command') return 'Running a command';
    if (latest.toolKind === 'file-edit') return 'Editing';
    if (latest.toolKind === 'search') return 'Searching';
    if (latest.toolKind === 'fetch') return 'Reading';
    return 'Using a tool';
  }
  return 'Working';
}

export function foldToolRuns(
  items: readonly ConversationDisplayItem[]
): readonly ConversationDisplayItem[] {
  const folded: ConversationDisplayItem[] = [];
  let run: ConversationDisplayItem[] = [];
  let runTurnId: string | null | undefined;
  let runTimestampMs = 0;

  const closeRun = (): void => {
    if (!run.length) return;
    // A lone call is its own row, even beside thinking: a group heading over one call is a second disclosure.
    if (run.filter((item) => item.kind !== 'reasoning').length <= 1) { folded.push(...run); run = []; return; }
    const { summary, icon } = summarizeToolRun(run);
    const allCompleted = run.every(turnItemSettled);
    folded.push({
      kind: 'toolRun',
      itemId: `tool-run:${run[0].itemId}`,
      items: run,
      summary,
      icon,
      completed: allCompleted,
      turnId: runTurnId,
      timestampMs: runTimestampMs
    });
    run = [];
  };

  for (const item of items) {
    if (item.kind === 'tool' || item.kind === 'command' || item.kind === 'file' || item.kind === 'fileEdits' || item.kind === 'reasoning') {
      if (!run.length) {
        runTurnId = item.turnId;
        runTimestampMs = item.timestampMs;
      }
      run.push(item);
    } else {
      closeRun();
      folded.push(item);
    }
  }
  closeRun();
  return folded;
}

/** Group rows in transcript order; native facts own turn state and timing.
 * A history window ending in an older turn never makes that turn active. */
export function conversationTurnGroups(
  items: readonly ConversationDisplayItem[],
  activeTurnId: string | null = null,
  turnFacts: readonly AgentConversationTurnFacts[] = []
): readonly ConversationTurnGroup[] {
  const facts = new Map(turnFacts.map((turn) => [turn.turnId, turn]));
  const turnIds = turnIdsOf(items);
  const partitions: { turnId: string | null; items: ConversationDisplayItem[] }[] = [];
  items.forEach((item, index) => {
    const open = partitions[partitions.length - 1];
    if (open && turnIds[index] === open.turnId) open.items.push(item);
    else partitions.push({ turnId: turnIds[index], items: [item] });
  });
  // A stored turn that never recorded its end is over once a later prompt exists; its last
  // reply is the final one.
  let laterPrompt = false;
  const groups: ConversationTurnGroup[] = [];
  for (const partition of partitions.toReversed()) {
    const known = partition.turnId ? facts.get(partition.turnId) : undefined;
    const ended = known?.terminalState || !laterPrompt ? known : {
      ...known, turnId: partition.turnId ?? '', terminalState: 'completed',
      finalAssistantItemId: known?.finalAssistantItemId ?? partition.items.findLast((item) => item.kind === 'assistant')?.itemId
    };
    const group = turnGroup(partition.turnId, partition.items, activeTurnId !== null && partition.turnId === activeTurnId, ended);
    groups.push({ ...group, stopped: group.completed && ended?.terminalState === 'interrupted' && !groups.some((later) => later.turnId === group.turnId) });
    if (partition.items.some((item) => item.kind === 'user')) laterPrompt = true;
  }
  return groups.reverse();
}

/** What a turn shows, in order, with its heading before the first row that is not the prompt.
 * A completed turn hides its work under the heading until the reader opens it. `opened` is
 * only ever the reader's click: running is read from the group, never stored, so a turn
 * folds by itself the moment it completes. */
export function turnRows(
  group: ConversationTurnGroup,
  items: readonly ConversationDisplayItem[],
  opened: boolean | undefined
): readonly (ConversationDisplayItem | 'heading')[] {
  const folded = group.completed && !group.running && opened !== true;
  const work = new Set(group.workItemIds);
  const result: (ConversationDisplayItem | 'heading')[] = [];
  let heading = !(group.running || group.completed);
  for (const item of items) {
    if (!heading && item.kind !== 'user') { result.push('heading'); heading = true; }
    if (folded && (item.kind === 'toolRun' || work.has(item.itemId))) continue;
    result.push(item);
  }
  if (group.stopped) result.push({ kind: 'unknown', itemId: `stopped:${group.turnId}`, text: 'Stopped', turnId: group.turnId, timestampMs: items.at(-1)?.timestampMs ?? 0 });
  return result;
}

/** The row that restores a reading anchor: its own row, the tool run holding it, or the
 * heading of the folded turn hiding it. */
export function anchorRowIndex(
  rows: readonly { anchorItemId?: string; run?: { items: readonly { itemId: string }[] };
    folded?: boolean; group?: { workItemIds: readonly string[] } }[],
  itemId: string
): number {
  return rows.findIndex((row) => row.anchorItemId === itemId
    || row.run?.items.some((item) => item.itemId === itemId)
    || (row.folded === true && !!row.group?.workItemIds.includes(itemId)));
}

/** Whether a landed history page asks for the next one at once. A page that left the view
 * at its edge and the content height within 80 px added nothing the reader can see (it
 * landed inside a folded turn). A failed load, a moved view or a visible page stops. */
export function continueHistoryPaging(landed: boolean, atEdge: boolean, heightBefore: number, heightAfter: number): boolean {
  return landed && atEdge && Math.abs(heightAfter - heightBefore) <= 80;
}

/** Whether a live tool group stays pinned to its newest row after a scroll. Reaching the end
 * pins it and scrolling up unpins it. Anything else keeps it as it was, so our own scroll to
 * the end that lands just before a new row grows the list does not unpin it. */
export function nextToolGroupPinned(pinned: boolean, previousTop: number, top: number, distanceFromEnd: number): boolean {
  return distanceFromEnd <= 16 || (pinned && top >= previousTop);
}

export function formatWorkedFor(elapsedMs: number): string {
  const safeElapsedMs = Math.max(0, elapsedMs);
  if (safeElapsedMs < 10_000) return `${(safeElapsedMs / 1_000).toFixed(1)}s`;
  const seconds = Math.round(safeElapsedMs / 1_000);
  if (seconds < 60) return `${seconds}s`;
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

/** How many lines of a sent message the transcript shows before folding it. */
export const USER_MESSAGE_FOLD_LINES = 10;

/** Whether a sent message is long enough to be worth folding.
 *
 * Measured in lines — the height of the text over the height of one line —
 * rather than in characters, so the answer is the same however wide the column
 * happens to be and whatever the message is made of. A pixel over the limit is
 * a rounded-off line rather than an extra one, so the last line has a pixel of
 * room before anything folds. */
export function userMessageOverflowsFold(
  contentHeightPx: number,
  lineHeightPx: number,
  maxLines: number = USER_MESSAGE_FOLD_LINES
): boolean {
  if (!Number.isFinite(contentHeightPx) || !(lineHeightPx > 0)) return false;
  return contentHeightPx > maxLines * lineHeightPx + 1;
}

/*
 * There is no render window here any more, and adding one back is a decision,
 * not a tidy-up.
 *
 * What stood here kept the last 120 items and put the rest behind a "Show
 * earlier" button. The count was of stored events, not of messages, and a
 * transcript carries far more of the former than the latter — usage and config
 * updates outnumber the writing several times over. So the button hid whole
 * conversations while the window filled with rows that draw nothing, and it
 * hid them by default: what the reader saw first was the boundary.
 *
 * What the database holds, the transcript shows. If a transcript ever needs a
 * budget, it belongs in a virtualizer that renders every item and only mounts
 * the visible ones — never in a button that asks the reader to request their
 * own history back.
 *
 * "Load more" means one thing now: fetching older turns that are NOT in the
 * database yet, from the provider's own transcript on disk. That is
 * `extendAgentConversationImportFromTauri`, and it is a different act from
 * drawing a row this app already stores.
 */

type ConversationEvent = AgentEvent | AgentConversationEvent;
type StringRecord = Record<string, unknown>;

function recordOf(value: unknown): StringRecord | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  return value as StringRecord;
}

function metadataRecord(value: unknown): Record<string, AgentConfigValue> | undefined {
  return recordOf(value) as Record<string, AgentConfigValue> | undefined;
}

function stringOf(value: unknown, fallback = ''): string {
  if (typeof value === 'string') return value;
  const path = recordOf(value)?.path;
  return typeof path === 'string' ? path : fallback;
}

/** A token count from metadata, or nothing when the provider did not say. */
function positiveNumberOf(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : undefined;
}

function numberOf(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function booleanOf(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback;
}

function normalizedPayloadKind(payload: StringRecord): string {
  const raw = stringOf(payload.kind, stringOf(payload.sessionUpdate));
  return raw.replace(/_([a-z])/g, (_, letter: string) => letter.toUpperCase());
}

function textFromValue(value: unknown): string {
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) return value.map(textFromValue).filter(Boolean).join('');
  const record = recordOf(value);
  if (!record) return '';
  for (const field of ['text', 'output', 'delta', 'message']) {
    const text = textFromValue(record[field]);
    if (text) return text;
  }
  return textFromValue(record.content);
}

function firstNestedString(value: unknown, keys: readonly string[]): string {
  if (Array.isArray(value)) {
    for (const entry of value) {
      const found = firstNestedString(entry, keys);
      if (found) return found;
    }
    return '';
  }
  const record = recordOf(value);
  if (!record) return '';
  for (const key of keys) {
    const direct = stringOf(record[key]);
    if (direct) return direct;
  }
  for (const nested of Object.values(record)) {
    const found = firstNestedString(nested, keys);
    if (found) return found;
  }
  return '';
}

const textOf = (content: readonly AgentContent[] | undefined): string =>
  (content ?? []).map((entry) => entry.text).join('');

function planStateOf(status: string): AgentPlanStep['state'] {
  const normalized = status.trim().toLowerCase().replaceAll('_', '-');
  if (normalized === 'in-progress' || normalized === 'completed' || normalized === 'failed' || normalized === 'blocked') {
    return normalized;
  }
  return 'pending';
}

function stepsOf(value: unknown): AgentPlanStep[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry, index) => {
    const row = recordOf(entry);
    if (!row) return [];
    return [{
      id: stringOf(row.id, `step-${index + 1}`),
      title: stringOf(row.title, stringOf(row.text, stringOf(row.content, stringOf(row.name, `Step ${index + 1}`)))),
      detail: typeof row.detail === 'string' ? row.detail : null,
      state: planStateOf(stringOf(row.state, stringOf(row.status))),
      ownerAgentId: typeof row.ownerAgentId === 'string' ? row.ownerAgentId : null,
      startedAt: typeof row.startedAt === 'string' ? row.startedAt : null,
      completedAt: typeof row.completedAt === 'string' ? row.completedAt : null
    }];
  });
}

function tasksOf(value: unknown): ConversationTask[] {
  return stepsOf(value).map((step) => ({
    id: step.id,
    title: step.title,
    state: step.state,
    detail: step.detail,
    ownerAgentId: step.ownerAgentId
  }));
}

function kindFor(item: AgentItem): ConversationDisplayItem['kind'] {
  switch (item.type) {
    case 'user-message': return 'user';
    case 'assistant-message': return 'assistant';
    case 'reasoning': return 'reasoning';
    case 'plan': return 'plan';
    case 'task-list': return 'tasks';
    case 'command':
    case 'file-change': return 'tool';
    case 'mcp-tool':
    case 'web-search':
    case 'image-view':
    case 'image-generation': return 'tool';
    case 'subagent': return 'subagent';
    case 'context-compaction': return 'compaction';
    case 'error': return 'error';
    default: return 'unknown';
  }
}

function toolKindOf(...values: unknown[]): ConversationToolKind {
  for (const value of values) {
    if (typeof value !== 'string') continue;
    switch (value.trim().toLowerCase().replaceAll('_', '-')) {
      case 'command':
      case 'execute':
      case 'terminal':
      case 'shell':
      case 'run':
      case 'bash':
      case 'sh':
      case 'zsh':
      case 'run-command':
      case 'exec-command':
      case 'execute-command':
        return 'command';
      case 'file':
      case 'file-change':
      case 'file-edit':
      case 'edit':
      case 'patch':
      case 'diff':
      case 'write':
      case 'delete':
      case 'move':
      case 'write-to-file':
      case 'replace-file-content':
      case 'apply-patch':
        return 'file-edit';
      case 'search':
      case 'grep':
      case 'find':
      case 'query':
      case 'glob':
      case 'ripgrep':
      case 'grep-search':
      case 'find-by-name':
        return 'search';
      case 'fetch':
      case 'read':
      case 'view':
      case 'open':
      case 'http':
      case 'web':
      case 'web-search':
      case 'websearch':
      case 'image-view':
      case 'file-read':
      case 'read-file':
      case 'view-file':
      case 'read-url-content':
      case 'cat':
        return 'fetch';
    }
  }
  return 'tool';
}

function toolKindForAgentItem(item: AgentItem): ConversationToolKind {
  const metadata = item.providerMetadata;
  const explicit = toolKindOf(item.type,
    metadata?.toolKind,
    metadata?.kind,
    metadata?.nativeType,
    metadata?.category,
    metadata?.name
  );
  if (explicit !== 'tool') return explicit;
  if (typeof metadata?.diff === 'string' && metadata.diff.trim()) return 'file-edit';
  switch (item.type) {
    case 'web-search': return 'search';
    case 'image-view': return 'fetch';
    default: return 'tool';
  }
}

function payloadHasFileChange(payload: StringRecord): boolean {
  return (typeof payload.diff === 'string' && payload.diff.trim().length > 0) || typeof payload.added === 'number'
    || firstNestedString(payload.content, ['diff', 'patch']).length > 0;
}

function toolKindForPayload(payload: StringRecord, payloadKind: string): ConversationToolKind {
  const kind = toolKindOf(payload.toolKind, payload.nativeType, payload.category, payload.type, payload.kind, payload.name);
  if (kind === 'command') return kind;
  if (payloadKind === 'turnDiff' || payloadHasFileChange(payload)) return 'file-edit';
  return kind;
}

function toolTitleOf(value: unknown): string | null {
  const title = stringOf(value).split('```')[0].trim();
  return title || null;
}

function toolStateOf(value: unknown): ConversationToolState {
  const normalized = stringOf(value, 'pending').toLowerCase().replaceAll('_', '-');
  if (['completed', 'complete', 'success', 'succeeded'].includes(normalized)) return 'completed';
  if (['failed', 'error', 'declined', 'cancelled', 'canceled'].includes(normalized)) return 'failed';
  if (['started', 'updated', 'running', 'in-progress', 'working'].includes(normalized)) return 'running';
  return 'pending';
}

function displayTimestamp(item: AgentItem, fallback: number): number {
  return numberOf(item.providerMetadata?.startedAtMs, fallback);
}

export function displayItemFromAgentItem(item: AgentItem, timestampMs = Date.now()): ConversationDisplayItem {
  const metadata = item.providerMetadata;
  const kind = kindFor(item);
  const startedAt = displayTimestamp(item, timestampMs);
  const turnId = item.turnId ?? null;
  if (kind === 'plan') return {
    kind,
    itemId: item.id,
    turnId,
    title: stringOf(metadata?.title, 'Plan'),
    steps: stepsOf(metadata?.steps ?? metadata?.entries),
    text: textOf(item.content),
    timestampMs: startedAt
  };
  if (kind === 'tasks') return {
    kind,
    itemId: item.id,
    turnId,
    title: stringOf(metadata?.title, 'Tasks'),
    tasks: tasksOf(metadata?.tasks ?? metadata?.steps),
    text: textOf(item.content),
    timestampMs: startedAt
  };
  if (kind === 'tool') {
    const title = toolTitleOf(metadata?.title) ?? toolTitleOf(metadata?.name) ?? 'Tool';
    const summary = stringOf(metadata?.summary) || undefined;
    const toolKind = toolKindForAgentItem(item);
    const output = toolKind === 'file-edit' ? '' : textOf(item.content) || stringOf(metadata?.output);
    const diff = stringOf(metadata?.diff);
    return {
      kind,
      itemId: item.id,
      turnId,
      title,
      toolKind,
      state: toolStateOf(metadata?.state ?? metadata?.status),
      output: output || undefined,
      diff: diff || undefined, hasDetail: metadata?.hasDetail === true || undefined, added: metadata?.added as number | undefined, removed: metadata?.removed as number | undefined,
      path: stringOf(metadata?.path) || undefined,
      summary,
      metadata,
      timestampMs: startedAt
    };
  }
  if (kind === 'subagent') return {
    kind,
    itemId: item.id,
    turnId,
    childId: stringOf(metadata?.childId, item.id),
    parentId: stringOf(metadata?.parentId, item.turnId ?? ''),
    label: stringOf(metadata?.label, 'Sub-agent'),
    state: stringOf(metadata?.state, 'working'),
    metadata,
    timestampMs: startedAt
  };
  if (kind === 'compaction') return {
    kind,
    itemId: item.id,
    turnId,
    trigger: stringOf(metadata?.trigger) || undefined,
    preTokens: positiveNumberOf(metadata?.preTokens),
    postTokens: positiveNumberOf(metadata?.postTokens),
    timestampMs: startedAt
  };
  if (kind === 'unknown') return { kind, itemId: item.id, turnId, text: textOf(item.content), metadata, timestampMs: startedAt };
  const completed = typeof metadata?.completed === 'boolean'
    ? metadata.completed
    : metadata?.streaming === true ? false : true;
  const blocks = Array.isArray(metadata?.blocks) ? (metadata.blocks as unknown as readonly SafeMarkdownBlock[]) : undefined;
  return {
    kind: kind as ConversationTextDisplayItem['kind'],
    itemId: item.id,
    turnId,
    text: textOf(item.content),
    completed,
    blocks,
    timestampMs: startedAt,
    metadata
  } as ConversationDisplayItem;
}

/**
 * What an error card says. An error that arrives without a message still has
 * to say something: a card with an empty body tells the reader only that the
 * app is confused.
 */
export function conversationErrorText(message: string | null | undefined): string {
  return (message ?? '').trim() || 'The agent reported an error without saying what went wrong.';
}

/** The plan the session is working to, or nothing when it has none.
 *
 * A transcript holds every plan update it was ever sent, and the chip above
 * the composer shows one plan: the newest, whichever turn produced it. */
export function latestPlan(
  items: readonly ConversationDisplayItem[]
): Extract<ConversationDisplayItem, { kind: 'plan' }> | null {
  return items.findLast(
    (item): item is Extract<ConversationDisplayItem, { kind: 'plan' }> => item.kind === 'plan'
  ) ?? null;
}

/** How many lines a unified diff adds and removes.
 *
 * The `+++` and `---` lines name the file the hunks belong to rather than
 * change a line in it, so they are read past. They only appear ahead of the
 * first hunk: once one has started, a line of three dashes is a removed line
 * that began with a comment marker, and skipping it lost the change. */
export function diffLineCounts(diff: string): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  let seenHunk = false;
  for (const line of diff.split('\n')) {
    if (line.startsWith('@@')) {
      seenHunk = true;
      continue;
    }
    if (!seenHunk && (line.startsWith('+++') || line.startsWith('---'))) continue;
    if (line.startsWith('+')) added += 1;
    else if (line.startsWith('-')) removed += 1;
  }
  return { added, removed };
}

/** File totals for the current turn, whether or not the provider supplied a plan. */
export function turnFileChanges(
  items: readonly ConversationDisplayItem[]
): { files: number; added: number; removed: number } | null {
  const paths = new Set<string>();
  let added = 0;
  let removed = 0;
  for (let index = items.length - 1; index >= 0; index -= 1) {
    const item = items[index];
    if (item.kind === 'user') break;
    let counts: { added: number; removed: number };
    if (item.kind === 'file') {
      paths.add(typeof item.metadata?.path === 'string' ? item.metadata.path : item.itemId);
      counts = diffLineCounts(typeof item.metadata?.diff === 'string' ? item.metadata.diff : item.text);
    } else if (item.kind === 'tool' && item.state === 'completed' && (item.diff || item.added != null || (item.toolKind === 'file-edit' && item.path))) {
      paths.add(item.path ?? item.itemId);
      counts = item.diff || item.added == null ? diffLineCounts(item.diff ?? '') : { added: item.added, removed: item.removed ?? 0 };
    } else continue;
    added += counts.added;
    removed += counts.removed;
  }
  return paths.size ? { files: paths.size, added, removed } : null;
}

/** Whether two plans say the same thing: the same steps, in the same order,
 * each in the same state. */
function samePlanSteps(left: readonly AgentPlanStep[], right: readonly AgentPlanStep[]): boolean {
  return left.length === right.length
    && left.every((step, index) => step.title === right[index].title && step.state === right[index].state);
}

/** Drops a plan update that repeats the one before it.
 *
 * A plan update arrives with no id of its own, so each one is filed under the
 * sequence it came in on and takes a row of its own. A provider that re-sends
 * an unchanged plan therefore wrote the same plan into the transcript twice.
 * The first of a run keeps its place, which holds the row still while the
 * repeats arrive; anything that actually moved on keeps its own row. */
export function withoutRepeatedPlans(items: readonly ConversationDisplayItem[]): ConversationDisplayItem[] {
  let previousSteps: readonly AgentPlanStep[] | null = null;
  return items.filter((item) => {
    if (item.kind !== 'plan') return true;
    const repeat = previousSteps !== null && samePlanSteps(previousSteps, item.steps);
    previousSteps = item.steps;
    return !repeat;
  });
}

function shallowRecordEqual(
  left: Readonly<Record<string, unknown>> | undefined,
  right: Readonly<Record<string, unknown>> | undefined
): boolean {
  if (left === right) return true;
  if (!left || !right) return false;
  const leftKeys = Object.keys(left);
  if (leftKeys.length !== Object.keys(right).length) return false;
  return leftKeys.every((key) => Object.is(left[key], right[key]));
}

function sameDisplayItem(left: ConversationDisplayItem, right: ConversationDisplayItem): boolean {
  if (left === right) return true;
  if (left.kind !== right.kind || left.itemId !== right.itemId || left.timestampMs !== right.timestampMs) return false;
  const leftRecord = left as unknown as Readonly<Record<string, unknown>>;
  const rightRecord = right as unknown as Readonly<Record<string, unknown>>;
  const keys = Object.keys(leftRecord);
  if (keys.length !== Object.keys(rightRecord).length) return false;
  return keys.every((key) => {
    if (key === 'metadata') {
      return shallowRecordEqual(
        leftRecord[key] as Readonly<Record<string, unknown>> | undefined,
        rightRecord[key] as Readonly<Record<string, unknown>> | undefined
      );
    }
    return Object.is(leftRecord[key], rightRecord[key]) || (key === 'steps' && left.kind === 'plan' && right.kind === 'plan' && samePlanSteps(left.steps, right.steps));
  });
}

/** Reuse unchanged keyed rows so Svelte only invalidates the touched subtree. */
export function reuseConversationDisplayItems(
  next: readonly ConversationDisplayItem[],
  previous: readonly ConversationDisplayItem[]
): ConversationDisplayItem[] {
  if (previous.length === 0) return next.slice();
  const previousById = new Map(previous.map((item) => [item.itemId, item]));
  return next.map((item) => {
    const prior = previousById.get(item.itemId);
    return prior && sameDisplayItem(item, prior) ? prior : item;
  });
}

function eventIdentity(event: ConversationEvent, payload: StringRecord, prefix: string): string {
  const fromEvent = 'itemId' in event ? event.itemId : undefined;
  // A journal event's turn says which turn it belongs to, not which row it
  // is: two id-less events of one turn must stay two rows.
  const turnId = 'type' in event ? event.turnId : undefined;
  return fromEvent
    || stringOf(payload.itemId)
    || stringOf(payload.toolCallId)
    || stringOf(payload.messageId)
    || `${prefix}:${turnId ?? stringOf(payload.turnId, String(event.sequence))}`;
}

function eventStartedAt(event: ConversationEvent, payload: StringRecord): number {
  const meta = recordOf(payload._meta);
  return numberOf(meta?.timestampMs, event.timestampMs);
}

function eventReplay(payload: StringRecord): boolean {
  return booleanOf(recordOf(payload._meta)?.replay);
}

function eventMetadata(
  event: ConversationEvent,
  payload: StringRecord,
  extra: Record<string, AgentConfigValue | undefined> = {}
): Record<string, AgentConfigValue> {
  const values = {
    ...(metadataRecord('providerMetadata' in event ? event.providerMetadata : undefined) ?? {}),
    ...(metadataRecord(payload.providerMetadata) ?? {}),
    ...(metadataRecord(payload._meta) ?? {}),
    ...extra,
    startedAtMs: eventStartedAt(event, payload),
    replay: eventReplay(payload)
  };
  const compact: Record<string, AgentConfigValue> = {};
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined) compact[key] = value;
  }
  return compact;
}

/** What a work row calls itself.
 *
 * A provider names its own functions — `apply_patch`, `web_search`, `Edit` —
 * and that is the provider's vocabulary, not the reader's. A row says what was
 * done instead, and the provider's own detail stays in the line beside it.
 *
 * Only a structured file-edit kind is renamed. A shell call still names the
 * command it ran, and a `Grep` still says `Grep`, because that is the work and
 * any broader guess would mislabel it. */
function plainToolTitle(toolKind: ConversationToolKind, name: string): string {
  if (toolKind === 'file-edit') return 'Edited a file';
  return name;
}

function toolItemFromPayload(event: ConversationEvent, payload: StringRecord, payloadKind: string): AgentItem {
  // A row that was handed its own title keeps it: the only caller that does so
  // is the whole-turn file change, which already knows it covers several files.
  const givenTitle = toolTitleOf(payload.title) ?? '';
  const name = stringOf(payload.name, stringOf(payload.toolName, stringOf(payload.command, stringOf(payload.path))));
  // A summary is the row's one-line preview, and nothing else. It used to
  // stand in for the body as well, which printed the same sentence twice —
  // once on the row and again inside it when it was opened — and, on a file
  // edit with no patch attached, offered the file's path as though it were the
  // change. Only real content is a body.
  // `output` is what a stored call answered; `content` is what a live ACP
  // update carries. A row reads whichever it was given.
  const contentText = textFromValue(payload.output) || textFromValue(payload.content);
  const toolKind = toolKindForPayload(payload, payloadKind);
  const title = givenTitle || (name ? plainToolTitle(toolKind, name) : undefined);
  const diff = stringOf(payload.diff) || firstNestedString(payload.content, ['diff', 'patch']);
  const output = toolKind === 'file-edit' ? '' : contentText;
  const path = stringOf(payload.path)
    || firstNestedString(payload.locations, ['path', 'uri'])
    || firstNestedString(payload.content, ['path', 'uri']);
  const status = payloadKind === 'toolCall' ? stringOf(payload.status, 'started') : stringOf(payload.status, stringOf(payload.state, 'updated'));
  return {
    id: eventIdentity(event, payload, 'tool'),
    type: 'mcp-tool',
    turnId: 'turnId' in event ? event.turnId : stringOf(payload.turnId) || undefined,
    content: output ? [{ channel: 'command-output', text: output }] : [],
    providerMetadata: eventMetadata(event, payload, {
      title,
      name: name || undefined,
      rawInput: (payload.rawInput ?? payload.input ?? payload.arguments) as AgentConfigValue | undefined,
      toolKind: toolKind === 'tool' && !name ? undefined : toolKind,
      state: status,
      status,
      summary: stringOf(payload.summary) || undefined,
      output: output || undefined,
      diff: diff || undefined, hasDetail: payload.hasDetail === true || undefined, added: payload.added as AgentConfigValue | undefined, removed: payload.removed as AgentConfigValue | undefined,
      path: path || undefined,
      completed: toolStateOf(status) === 'completed'
    })
  };
}

function planItemFromPayload(event: ConversationEvent, payload: StringRecord): AgentItem {
  const entries = Array.isArray(payload.items) ? payload.items : Array.isArray(payload.entries) ? payload.entries : [];
  return {
    id: eventIdentity(event, { ...payload, itemId: stringOf(payload.planId) || payload.itemId }, 'plan'),
    type: 'plan',
    turnId: 'turnId' in event ? event.turnId : stringOf(payload.turnId) || undefined,
    content: [],
    providerMetadata: eventMetadata(event, payload, { title: stringOf(payload.title, 'Plan'), steps: entries as AgentConfigValue })
  };
}

export function agentItemFromEvent(event: ConversationEvent): AgentItem | null {
  const payload = event.payload as unknown as StringRecord;
  if ('type' in event && event.type === 'plan.updated') return planItemFromPayload(event, payload);
  if ('type' in event && event.type === 'tasks.updated') return {
    id: eventIdentity(event, { ...payload, itemId: payload.itemId ?? `tasks:${event.generation}` }, 'tasks'),
    type: 'task-list', content: [],
    providerMetadata: eventMetadata(event, payload, { title: stringOf(payload.title, 'Tasks'), tasks: payload.tasks as AgentConfigValue })
  };
  const raw = recordOf(payload.item);
  if (raw && typeof raw.id === 'string' && typeof raw.type === 'string') {
    return {
      id: raw.id,
      type: raw.type as AgentItemType,
      turnId: typeof raw.turnId === 'string' ? raw.turnId : ('turnId' in event ? event.turnId : undefined),
      content: Array.isArray(raw.content)
        ? raw.content.flatMap((content) => {
          const row = recordOf(content);
          return row && typeof row.channel === 'string' && typeof row.text === 'string'
            ? [{ channel: row.channel as AgentContent['channel'], text: row.text, mimeType: typeof row.mimeType === 'string' ? row.mimeType : undefined }]
            : [];
        })
        : [],
      providerMetadata: eventMetadata(event, payload, metadataRecord(raw.providerMetadata) ?? {})
    };
  }

  const payloadKind = normalizedPayloadKind(payload);
  if (payloadKind === 'permissionRequest' || payloadKind === 'approval') return null;
  if (payloadKind === 'connection' || payloadKind === 'turn' || payloadKind === 'usage' || payloadKind === 'sessionCompleted') return null;
  // A sub-agent's progress belongs to the agent tree, which reads it from the
  // session's children. As a transcript row it had nothing to show.
  if (payloadKind === 'childUpdate') return null;
  if (payloadKind === 'checkoutChanged') return {
    id: `checkout:${event.sequence}`, type: 'unknown',
    content: [{ channel: 'assistant', text: `Checkout changed\n${stringOf(payload.fromCwd)}\n${stringOf(payload.toCwd)}` }],
    providerMetadata: eventMetadata(event, payload, { completed: true })
  };
  if (payloadKind === 'contextCompaction') {
    return {
      id: `compaction:${event.sequence}`,
      type: 'context-compaction',
      turnId: 'turnId' in event ? event.turnId : stringOf(payload.turnId) || undefined,
      content: [],
      providerMetadata: eventMetadata(event, payload, {
        completed: true,
        trigger: stringOf(payload.trigger) || undefined,
        preTokens: positiveNumberOf(payload.preTokens),
        postTokens: positiveNumberOf(payload.postTokens)
      })
    };
  }
  if (payloadKind === 'plan') return planItemFromPayload(event, payload);
  if (payloadKind === 'toolCall' || payloadKind === 'toolCallUpdate' || payloadKind === 'tool') {
    return toolItemFromPayload(event, payload, payloadKind);
  }
  if (payloadKind === 'turnDiff') {
    return toolItemFromPayload(event, { ...payload, title: stringOf(payload.title, 'Edited files'), toolKind: 'file-edit', status: stringOf(payload.status, 'completed') }, payloadKind);
  }
  // The command catalog reads this event separately; it is not a transcript row.
  if (payloadKind === 'availableCommandsUpdate') return null;

  if (payloadKind === 'error') {
    // Error text lives under `message`, unlike the ordinary content events.
    return {
      id: `error:${event.generation}:${event.sequence}`,
      type: 'error',
      turnId: 'turnId' in event ? event.turnId : stringOf(payload.turnId) || undefined,
      content: [{ channel: 'assistant', text: conversationErrorText(stringOf(payload.message)) }],
      providerMetadata: eventMetadata(event, payload, {
        completed: true,
        code: stringOf(payload.code) || undefined,
        recoverable: booleanOf(payload.recoverable, true)
      })
    };
  }

  const id = eventIdentity(event, payload, payloadKind || 'item');
  const text = textFromValue(payload.content) || stringOf(payload.text, stringOf(payload.delta));
  const eventType = 'type' in event ? event.type : '';
  const channel = stringOf(payload.channel, eventType === 'content.delta' ? 'assistant' : 'assistant') as AgentContent['channel'];
  let type: AgentItemType = 'unknown';
  let contentChannel = channel;
  let completed = true;
  if (payloadKind === 'userMessage' || payloadKind === 'userMessageChunk') {
    type = 'user-message';
    contentChannel = 'assistant';
    completed = payloadKind === 'userMessage' ? booleanOf(payload.completed, true) : eventReplay(payload);
  } else if (payloadKind === 'assistantMessage' || payloadKind === 'assistantDelta' || payloadKind === 'agentMessageChunk') {
    type = 'assistant-message';
    contentChannel = 'assistant';
    completed = payloadKind === 'assistantMessage' && booleanOf(payload.completed, true);
  } else if (payloadKind === 'agentThoughtChunk' || channel.startsWith('reasoning')) {
    type = 'reasoning';
    contentChannel = 'reasoning';
    completed = false;
  } else if (channel === 'plan') {
    type = 'plan';
  } else if (channel === 'command-output') {
    type = 'command';
  } else if (channel === 'file-change-output') {
    type = 'file-change';
  }
  return {
    id,
    type,
    turnId: 'turnId' in event ? event.turnId : stringOf(payload.turnId) || undefined,
    content: text ? [{ channel: contentChannel, text }] : [],
    providerMetadata: eventMetadata(event, payload, {
      completed,
      streaming: !completed,
      blocks: Array.isArray(payload.blocks) ? (payload.blocks as unknown as AgentConfigValue) : undefined
    })
  };
}

export function availableCommandsFromEvent(event: ConversationEvent): AgentCommandDescriptor[] | null {
  const payload = event.payload as unknown as StringRecord;
  if (normalizedPayloadKind(payload) !== 'availableCommandsUpdate') return null;
  if (!Array.isArray(payload.availableCommands)) return [];
  return payload.availableCommands.flatMap((entry) => {
    const row = recordOf(entry);
    const id = stringOf(row?.id, stringOf(row?.name));
    if (!row || !id) return [];
    const input = recordOf(row.input);
    return [{
      id,
      label: stringOf(row.label, id),
      description: stringOf(row.description) || undefined,
      inputHint: stringOf(row.inputHint, stringOf(input?.hint)) || undefined,
      providerMetadata: metadataRecord(row.providerMetadata)
    }];
  });
}
