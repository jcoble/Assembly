// A finished turn folds its work under one heading above its final reply
// (Assembly's turnRows and summarizeCompletedWork, conversationTimeline.ts).
// The prompt, the final reply, errors and notes stay out; so does anything
// still waiting on the user or still running. Plain functions, for node tests.
import type { Item, SafeInlinePart, SafeMarkdownBlock } from '../rows/store';
import type { TurnStats } from './turns';

export type Fold = {
  runId: string;
  // The keys of the items the heading hides while closed.
  work: Set<string>;
  // The final reply's key: copy and time go under it.
  reply: string | null;
  summary: string;
  durationMs: number | null;
};

// The Assembly MCP server's tools, by name: Claude Code calls them mcp__assembly__<tool>, Codex mcp.assembly.<tool>.
export type HelperTool = 'start_session' | 'send_message' | 'message_status' | 'list_sessions';
const HELPER_TITLES: Record<HelperTool, string> = {
  start_session: 'Started a helper',
  message_status: "Checked a helper's answer",
  send_message: 'Sent a message',
  list_sessions: 'Listed sessions',
};

export function helperToolOf(name: string): HelperTool | null {
  const tool = /^mcp(?:__|\.)assembly(?:__|\.)(\w+)/.exec(name)?.[1];
  return tool && tool in HELPER_TITLES ? (tool as HelperTool) : null;
}

// The row's title: a helper tool's in words, any other's name.
export function toolTitle(name: string): string {
  const helper = helperToolOf(name);
  return helper ? HELPER_TITLES[helper] : name;
}

const isWork = (item: Item) =>
  item.kind === 'tool' ||
  item.kind === 'thinking' ||
  item.kind === 'message' ||
  (item.kind === 'task' && item.task.state !== 'running') ||
  (item.kind === 'subagent' && item.subagent.state === 'idle') ||
  (item.kind === 'ask' && item.ask.state !== 'open');

// Each finished run's fold, by run id. A run is finished once its stats row is
// in and it is not the one running. A run with nothing but its reply has no fold.
export function turnFolds(items: Item[], stats: Record<string, TurnStats>, activeRunId: string | null): Map<string, Fold> {
  const byRun = new Map<string, Item[]>();
  for (const item of items) {
    if (!item.runId || item.runId === activeRunId || !stats[item.runId]) continue;
    (byRun.get(item.runId) ?? byRun.set(item.runId, []).get(item.runId)!).push(item);
  }
  const folds = new Map<string, Fold>();
  for (const [runId, run] of byRun) {
    const reply = run.findLast((item) => item.kind === 'message')?.key ?? null;
    const work = run.filter((item) => isWork(item) && item.key !== reply);
    if (!work.length) {
      if (reply) folds.set(runId, { runId, work: new Set(), reply, summary: '', durationMs: null });
      continue;
    }
    folds.set(runId, {
      runId,
      work: new Set(work.map((item) => item.key)),
      reply,
      summary: workSummary(work),
      durationMs: stats[runId]?.durationMs ?? null,
    });
  }
  return folds;
}

// "Edited 2 files, ran 3 commands": what the hidden work did, by ACP tool kind.
export function workSummary(work: Item[]): string {
  const edited = new Set<string>();
  let edits = 0, reads = 0, commands = 0, searches = 0, fetches = 0, others = 0, failed = 0, thinking = 0, subagents = 0;
  const helper: Record<HelperTool, number> = { start_session: 0, message_status: 0, send_message: 0, list_sessions: 0 };
  for (const item of work) {
    if (item.kind === 'thinking') thinking++;
    if (item.kind === 'subagent') subagents++;
    if (item.kind !== 'tool') continue;
    const { kind, path, state } = item.tool;
    // A failed call still counts as what it was (a stopped command is still a command run).
    if (state === 'failed') failed++;
    const helperTool = helperToolOf(item.tool.name);
    if (helperTool) {
      helper[helperTool]++;
      continue;
    }
    if (kind === 'edit' || kind === 'delete' || kind === 'move') {
      if (!path || !edited.has(path)) edits++;
      if (path) edited.add(path);
    } else if (kind === 'read') reads++;
    else if (kind === 'execute') commands++;
    else if (kind === 'search') searches++;
    else if (kind === 'fetch') fetches++;
    else if (kind !== 'think') others++;
  }
  const parts = [
    edits && (edits === 1 ? 'edited a file' : `edited ${edits} files`),
    reads && (reads === 1 ? 'read a file' : `read ${reads} files`),
    commands && (commands === 1 ? 'ran a command' : `ran ${commands} commands`),
    searches && (searches === 1 ? 'searched once' : `searched ${searches} times`),
    fetches && (fetches === 1 ? 'fetched a page' : `fetched ${fetches} pages`),
    others && (others === 1 ? 'used a tool' : `used ${others} tools`),
    subagents && (subagents === 1 ? 'ran a subagent' : `ran ${subagents} subagents`),
    helper.start_session && (helper.start_session === 1 ? 'started a helper' : `started ${helper.start_session} helpers`),
    helper.send_message && (helper.send_message === 1 ? 'sent a message' : `sent ${helper.send_message} messages`),
    helper.message_status &&
      (helper.message_status === 1 ? "checked a helper's answer" : `checked answers ${helper.message_status} times`),
    helper.list_sessions && (helper.list_sessions === 1 ? 'listed sessions' : `listed sessions ${helper.list_sessions} times`),
    failed && (failed === 1 ? '1 call failed' : `${failed} calls failed`),
  ].filter((part): part is string => !!part);
  if (!parts.length) return thinking ? 'Thought' : 'Worked';
  parts[0] = parts[0]!.charAt(0).toUpperCase() + parts[0]!.slice(1);
  return parts.join(', ');
}

// The reply as markdown again, for the copy button: the rows carry only its blocks.
export function markdownOf(blocks: SafeMarkdownBlock[]): string {
  return blocks.map((block) => blockText(block, '')).join('\n\n');
}

function blockText(block: SafeMarkdownBlock, indent: string): string {
  switch (block.kind) {
    case 'paragraph':
      return indent + inline(block.parts);
    case 'heading':
      return `${indent}${'#'.repeat(block.level)} ${inline(block.parts)}`;
    case 'code':
      return `${indent}\`\`\`${block.language}\n${block.value.replace(/\n$/, '')}\n${indent}\`\`\``;
    case 'quote':
      return block.blocks.map((b) => blockText(b, '')).join('\n\n').replace(/^/gm, `${indent}> `);
    case 'list':
      return block.items
        .map((item, i) => {
          const mark = block.ordered ? `${i + 1}.` : '-';
          const box = item.task ? (item.checked ? '[x] ' : '[ ] ') : '';
          const rest = item.blocks.map((b) => '\n' + blockText(b, indent + '  ')).join('');
          return `${indent}${mark} ${box}${inline(item.parts)}${rest}`;
        })
        .join('\n');
    case 'table': {
      const row = (cells: SafeInlinePart[][]) => `| ${cells.map(inline).join(' | ')} |`;
      return [row(block.headers), `|${block.headers.map(() => ' --- |').join('')}`, ...block.rows.map(row)].map((l) => indent + l).join('\n');
    }
    case 'rule':
      return indent + '---';
  }
}

function inline(parts: SafeInlinePart[]): string {
  return parts
    .map((part) => {
      switch (part.kind) {
        case 'text':
          return part.value;
        case 'code':
          return `\`${part.value}\``;
        case 'strong':
          return `**${inline(part.parts)}**`;
        case 'emphasis':
          return `*${inline(part.parts)}*`;
        case 'strike':
          return `~~${inline(part.parts)}~~`;
        case 'link':
          return `[${inline(part.parts)}](${part.href})`;
        case 'file-link':
          return `[${inline(part.parts)}](${part.path})`;
      }
    })
    .join('');
}

const WEEK_MS = 7 * 24 * 60 * 60 * 1000;

// "Thursday 12:02 PM" within the last 7 days, "Oct 17, 12:02 PM" before that.
export function messageTime(at: number, now: number, locale?: string): string {
  const date = new Date(at);
  const time = date.toLocaleTimeString(locale, { hour: 'numeric', minute: '2-digit' });
  if (now - at < WEEK_MS) return `${date.toLocaleDateString(locale, { weekday: 'long' })} ${time}`;
  return `${date.toLocaleDateString(locale, { month: 'short', day: 'numeric' })}, ${time}`;
}

// A sent message folds past this many lines; a diff past DIFF_FOLD shows its first DIFF_SHOWN.
export const MESSAGE_FOLD_LINES = 10;
export const DIFF_FOLD = 20;
export const DIFF_SHOWN = 12;
