// The relay's rows (plan "Row contract") and their fold into what the
// conversation shows. The conversation keeps one View in $state and feeds it
// rows; nothing here parses text. Plain functions, so tests run them in node.
import type { TurnStats } from '../session/turns';

// Assembly's SafeInlinePart / SafeMarkdownBlock, as serde writes them
// (src-tauri/src/agent_conversation/safe_markdown.rs:5-69 there). The relay
// adds `lines` to a complete code block.
export type SafeInlinePart =
  | { kind: 'text'; value: string }
  | { kind: 'code'; value: string }
  | { kind: 'strong' | 'emphasis' | 'strike'; parts: SafeInlinePart[] }
  | { kind: 'link'; href: string; parts: SafeInlinePart[] }
  | { kind: 'file-link'; path: string; parts: SafeInlinePart[] };
export type SafeListItem = { task: boolean; checked: boolean; parts: SafeInlinePart[]; blocks: SafeMarkdownBlock[] };
export type CodeToken = { value: string; className: 'keyword' | 'string' | 'comment' | 'number' | 'type' | 'plain' };
export type CodeBlock = { kind: 'code'; language: string; value: string; complete: boolean; lines?: CodeToken[][] };
export type SafeMarkdownBlock =
  | { kind: 'paragraph'; parts: SafeInlinePart[] }
  | { kind: 'heading'; level: number; parts: SafeInlinePart[] }
  | CodeBlock
  | { kind: 'quote'; blocks: SafeMarkdownBlock[] }
  | { kind: 'list'; ordered: boolean; items: SafeListItem[] }
  | { kind: 'table'; headers: SafeInlinePart[][]; rows: SafeInlinePart[][][] }
  | { kind: 'rule' };

export type TurnState = 'started' | 'finished' | 'failed' | 'cancelled';
export type ToolPayload = {
  name: string;
  state: 'started' | 'completed' | 'failed';
  summary: string;
  path?: string;
  added?: number;
  removed?: number;
  hasDetail: boolean;
  // ACP's tool kind: read, edit, delete, move, search, execute, think, fetch or other.
  kind?: string;
};
// A card asking the user: a permission (one of the agent's options, by ACP kind) or a
// question (a flat JSON Schema form). `answer` is the option's `{ optionId }` or the
// form's `{ action, content }`.
export type AskOption = { optionId: string; name: string; kind: 'allow_once' | 'allow_always' | 'reject_once' | 'reject_always' | string };
export type AskPayload = {
  kind: 'permission' | 'question';
  state: 'open' | 'answered' | 'cancelled';
  title?: string;
  message?: string;
  toolCallId?: string;
  options?: AskOption[];
  schema?: { properties?: Record<string, AskField>; required?: string[] };
  answer?: { optionId?: string; action?: string; content?: Record<string, unknown> };
};
export type AskChoice = { const: string; title?: string; description?: string };
export type AskField = {
  type?: string;
  title?: string;
  description?: string;
  oneOf?: AskChoice[];
  enum?: string[];
  items?: { anyOf?: AskChoice[]; enum?: string[] };
};
// Background work: a backgrounded command or other task the agent runs past its turn.
export type TaskPayload = { name: string; state: 'running' | 'completed' | 'failed' | 'stopped'; canStop: boolean; toolCallId?: string };
// A subagent the agent runs as a child session, with a thread of its own (`threadId`), nested under the
// tool call that started it (`toolCallId`) when known.
export type SubagentPayload = {
  sessionId: string;
  title: string;
  description?: string;
  state: 'running' | 'requires_action' | 'idle';
  stopReason?: string;
  error?: string;
  canStop: boolean;
  toolCallId?: string;
};
export type PlanItem = { text: string; status: 'pending' | 'in_progress' | 'completed' };
export type RunError = { message: string; code: string | null };
// GET /threads/:id/tools/:itemId
// `input` is the call's arguments as JSON text.
export type ToolDetail = { summary: string; input: string | null; output: string | null; diff: string | null; path: string | null };

type Payloads = {
  // `reason` says why a run the agent started itself started.
  turn: { state: TurnState; reason?: string };
  // `from`: the thread of the session that sent it, when it's a message from another session.
  user: { text: string; from?: string };
  // `text` is only this delta's new raw text; the UI draws `blocks`.
  delta: { from: number; text: string; blocks: SafeMarkdownBlock[] };
  message: { blocks: SafeMarkdownBlock[] };
  thinking: { text: string; done: boolean };
  tool: ToolPayload;
  plan: { items: PlanItem[] };
  stats: TurnStats;
  error: RunError;
  ask: AskPayload;
  task: TaskPayload;
  subagent: SubagentPayload;
  // How full the agent's context window is, in tokens: one item per run.
  context: { used: number; size: number };
  // Claude's compact_boundary (CUSTOM `compaction` from Node).
  compaction: { trigger: 'manual' | 'auto'; preTokens: number; postTokens?: number };
};
export type Row = {
  [K in keyof Payloads]: {
    sessionId: string;
    seq: number;
    runId: string | null;
    itemId: string | null;
    kind: K;
    payload: Payloads[K];
    createdAt: number;
    // On page rows only: the seq of the item's first row, which places it.
    firstSeq?: number;
    // On live rows only: the seq the socket held before this row. Finished
    // messages' deltas are deleted, so it can be more than one below seq.
    prevSeq?: number;
  };
}[keyof Payloads];

// One thing the conversation draws, keyed by its item (or, for a row with no
// item, by its seq). Deltas and the final message share one item.
type Base = { key: string; runId: string | null };
export type Item =
  // `at`: when the message was sent, or its reply last written.
  | (Base & { kind: 'user'; text: string; at: number; from?: string })
  | (Base & { kind: 'message'; blocks: SafeMarkdownBlock[]; done: boolean; at: number })
  | (Base & { kind: 'thinking'; text: string; done: boolean })
  | (Base & { kind: 'tool'; itemId: string; tool: ToolPayload })
  | (Base & { kind: 'error'; error: RunError })
  | (Base & { kind: 'ask'; askId: string; ask: AskPayload })
  | (Base & { kind: 'task'; taskId: string; task: TaskPayload })
  | (Base & { kind: 'subagent'; acpSessionId: string; subagent: SubagentPayload })
  | (Base & { kind: 'note'; text: string })
  | (Base & { kind: 'stats'; stats: TurnStats })
  | (Base & { kind: 'compaction'; trigger: 'manual' | 'auto'; preTokens: number; postTokens?: number })
  | (Base & { kind: 'other'; type: string });

export type View = {
  items: Item[];
  // Each run's stats, for the checkout line above its prompt.
  stats: Record<string, TurnStats>;
  // The newest plan row and the newest turn row seen, with when it was written.
  plan: { seq: number; items: PlanItem[] } | null;
  turn: { seq: number; runId: string | null; state: TurnState; at: number } | null;
  // The newest context window report, for the composer's ring.
  context: { seq: number; used: number; size: number } | null;
  // The lowest seq loaded (the next page's `before`), and whether older rows exist.
  oldest: number | null;
  hasBefore: boolean;
};

export const emptyView = (): View => ({ items: [], stats: {}, plan: null, turn: null, context: null, oldest: null, hasBefore: false });

// Assembly's live-event rule (conversationService.ts:715-745 there), keyed on
// prevSeq instead of seq + 1: a row applies when it follows the watermark.
export function liveStep(watermark: number, row: { seq: number; prevSeq?: number }): 'apply' | 'ignore' | 'reload' {
  if (row.seq <= watermark) return 'ignore';
  return row.prevSeq === watermark ? 'apply' : 'reload';
}

export const keyOf = (row: Row) =>
  row.itemId === null ? `seq:${row.seq}` : `${row.kind === 'delta' ? 'message' : row.kind}:${row.itemId}`;

// Applies a row newer than every row applied so far: it updates its item in
// place, or adds the item at the end.
export function applyRow(view: View, row: Row): void {
  const { runId } = row;
  if (row.kind === 'turn') {
    // A run the agent started itself has no prompt above it, so its reason stands in for one.
    if (row.payload.reason && !view.items.some((i) => i.key === keyOf(row)))
      view.items.push({ key: keyOf(row), runId, kind: 'note', text: row.payload.reason });
    if (!view.turn || row.seq > view.turn.seq) view.turn = { seq: row.seq, runId, state: row.payload.state, at: row.createdAt };
    return;
  }
  if (row.kind === 'plan') {
    if (!view.plan || row.seq > view.plan.seq) view.plan = { seq: row.seq, items: row.payload.items };
    return;
  }
  if (row.kind === 'context') {
    if (!view.context || row.seq > view.context.seq) view.context = { seq: row.seq, ...row.payload };
    return;
  }
  const key = keyOf(row);
  const item = view.items.findLast((i) => i.key === key);
  switch (row.kind) {
    case 'delta':
      if (item?.kind === 'message') {
        if (!item.done) item.blocks.splice(row.payload.from, Infinity, ...row.payload.blocks);
        item.at = row.createdAt;
        return;
      }
      view.items.push({ key, runId, kind: 'message', blocks: [...row.payload.blocks], done: false, at: row.createdAt });
      return;
    case 'message':
      if (item?.kind === 'message') {
        item.blocks = row.payload.blocks;
        item.done = true;
        item.at = row.createdAt;
        return;
      }
      view.items.push({ key, runId, kind: 'message', blocks: row.payload.blocks, done: true, at: row.createdAt });
      return;
    case 'thinking':
      if (item?.kind === 'thinking') {
        item.text = row.payload.text;
        item.done = row.payload.done;
        return;
      }
      view.items.push({ key, runId, kind: 'thinking', ...row.payload });
      return;
    case 'tool':
      if (item?.kind === 'tool') {
        item.tool = row.payload;
        return;
      }
      view.items.push({ key, runId, kind: 'tool', itemId: row.itemId ?? key, tool: row.payload });
      return;
    case 'stats':
      view.stats[row.payload.runId] = row.payload;
      if (!item) view.items.push({ key, runId, kind: 'stats', stats: row.payload });
      return;
    case 'user':
      if (!item) view.items.push({ key, runId, kind: 'user', text: row.payload.text, at: row.createdAt, from: row.payload.from });
      return;
    case 'error':
      if (!item) view.items.push({ key, runId, kind: 'error', error: row.payload });
      return;
    case 'compaction':
      if (!item) view.items.push({ key, runId, kind: 'compaction', ...row.payload });
      return;
    case 'ask':
      if (item?.kind === 'ask') {
        item.ask = row.payload;
        return;
      }
      view.items.push({ key, runId, kind: 'ask', askId: (row.itemId ?? '').replace(/^ask:/, ''), ask: row.payload });
      return;
    case 'task':
      if (item?.kind === 'task') {
        item.task = row.payload;
        return;
      }
      view.items.push({ key, runId, kind: 'task', taskId: (row.itemId ?? '').replace(/^task:/, ''), task: row.payload });
      return;
    case 'subagent':
      if (item?.kind === 'subagent') {
        item.subagent = row.payload;
        return;
      }
      view.items.push({ key, runId, kind: 'subagent', acpSessionId: (row.itemId ?? '').replace(/^subagent:/, ''), subagent: row.payload });
      return;
    default:
      // A kind this build does not know: shown by name, so nothing silently disappears.
      if (!item) view.items.push({ key, runId, kind: 'other', type: (row as { kind: string }).kind });
  }
}

const firstSeq = (row: Row) => row.firstSeq ?? row.seq;

// Puts an older page above what is loaded, each item placed by its first seq.
// What is loaded is newer, so it wins: no loaded item, plan, turn, context or stats is
// replaced. A page holds one row per item and kind, so its order is free.
export function prependPage(view: View, rows: Row[], hasBefore: boolean): View {
  const page = emptyView();
  for (const row of [...rows].sort((a, b) => firstSeq(a) - firstSeq(b))) applyRow(page, row);
  const loaded = new Set(view.items.map((item) => item.key));
  view.items.unshift(...page.items.filter((item) => !loaded.has(item.key)));
  view.stats = { ...page.stats, ...view.stats };
  view.plan ??= page.plan;
  view.turn ??= page.turn;
  view.context ??= page.context;
  if (rows.length) view.oldest = Math.min(view.oldest ?? Infinity, ...rows.map(firstSeq));
  view.hasBefore = hasBefore;
  return view;
}

// The run going now: the newest turn row's while it is started; else `known`
// (the page's activeRunId on open, or the run just sent) until a turn row for
// it arrives.
export function activeRun(view: View, known: string | null): string | null {
  if (view.turn?.state === 'started') return view.turn.runId;
  // A sent run counts until its first turn row; one with stats has ended, whatever run ended after it.
  return known !== null && view.turn?.runId !== known && !view.stats[known] ? known : null;
}

// When the run going now started: its started turn row's time, or null when
// no such row is loaded (a run just sent, or one already ended).
export function runStartedAt(view: View, runId: string): number | null {
  return view.turn?.runId === runId && view.turn.state === 'started' ? view.turn.at : null;
}

export const partChars = (part: SafeInlinePart): number =>
  'value' in part ? part.value.length : part.parts.reduce((n, p) => n + partChars(p), 0);
export const partsChars = (parts: SafeInlinePart[]) => parts.reduce((n, p) => n + partChars(p), 0);
export function blockChars(block: SafeMarkdownBlock): number {
  switch (block.kind) {
    case 'paragraph':
    case 'heading':
      return partsChars(block.parts);
    case 'code':
      return block.value.length;
    case 'quote':
      return block.blocks.reduce((n, b) => n + blockChars(b), 0);
    case 'list':
      return block.items.reduce((n, i) => n + partsChars(i.parts) + i.blocks.reduce((m, b) => m + blockChars(b), 0), 0);
    case 'table':
      return [...block.headers, ...block.rows.flat()].reduce((n, cell) => n + partsChars(cell), 0);
    case 'rule':
      return 0;
  }
}

export const blocksChars = (blocks: SafeMarkdownBlock[]) => blocks.reduce((n, b) => n + blockChars(b), 0);

// The text and thinking characters the conversation shows (the soak's rendered_chars).
export function renderedChars(view: View): number {
  let sum = 0;
  for (const item of view.items) {
    if (item.kind === 'message') sum += blocksChars(item.blocks);
    else if (item.kind === 'user' || item.kind === 'thinking') sum += item.text.length;
  }
  return sum;
}
