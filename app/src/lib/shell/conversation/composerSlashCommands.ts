import {
  filterConversationCommandCatalog,
  type ConversationCommand
} from './conversationCommandCatalog.ts';

export interface SlashMenuInput {
  /** Row the keyboard is currently on. */
  activeIndex: number;
  /** True after Escape, until the draft changes again. */
  dismissed: boolean;
}

export interface SlashMenuState {
  open: boolean;
  query: string;
  commands: ConversationCommand[];
  activeIndex: number;
  /** Short line shown instead of rows; empty when there are rows. */
  emptyText: string;
}

export function snapshotConversationCommands(
  catalog: readonly ConversationCommand[]
): ConversationCommand[] {
  return catalog.map((command) => ({ ...command }));
}

/**
 * The command word the composer is typing, or null when the draft is not a
 * command. The menu belongs to the first word only: as soon as arguments
 * begin, the user is writing the command rather than picking it.
 */
export function slashCommandQuery(draft: string): string | null {
  const trimmed = draft.trimStart();
  if (!trimmed.startsWith('/')) return null;
  const rest = trimmed.slice(1);
  if (/\s/.test(rest)) return null;
  return rest;
}

export function remainingContextPercent(
  usedTokens: number | null | undefined,
  contextWindow: number | null | undefined
): number | null {
  if (!Number.isFinite(usedTokens) || !Number.isFinite(contextWindow) || (contextWindow ?? 0) <= 0) {
    return null;
  }
  const remaining = ((contextWindow as number) - (usedTokens as number)) / (contextWindow as number) * 100;
  return Math.max(0, Math.min(100, Math.round(remaining)));
}

export type ContextMeterState =
  | {
      kind: 'percent';
      /** Share of the window still free, rounded. The hint line reads this. */
      remaining: number;
      /** Share of the window spent, rounded. Always 100 - remaining. */
      used: number;
      /** What the ring paints, with the small-usage floor applied. */
      arc: number;
      warm: boolean;
      hot: boolean;
      usedTokens: number;
      contextWindow: number;
      /** Null when the adapter did not report the split. */
      inputTokens: number | null;
      outputTokens: number | null;
    }
  | { kind: 'unknown' };

/**
 * The smallest arc the ring will paint for a window that has been touched at
 * all. A true 0.5% arc is a couple of pixels of stroke and reads as a ring
 * that has stopped working, so everything between nothing and the floor
 * paints the same sliver. Above the floor the arc is the honest share.
 */
export const MIN_VISIBLE_ARC_PERCENT = 10;

/** Tokens as a short label: 940, 9.4k, 398k, 1.2m. */
export function formatContextTokens(value: number): string {
  if (value < 1_000) return String(Math.round(value));
  if (value < 10_000) return `${Math.floor(value / 100) / 10}k`;
  if (value < 1_000_000) return `${Math.round(value / 1_000)}k`;
  return `${Math.floor(value / 100_000) / 10}m`;
}

/**
 * What the composer should show for context: the one object the ring and the
 * hint text both read, so they can't disagree. `unknown` when there is
 * nothing trustworthy to show — the window is missing, or the reported usage
 * exceeds it (a percentage would then be nonsense: agents that report a
 * lifetime token total rather than current occupancy would otherwise sit at
 * "0% left" forever).
 */
export function contextMeterState(
  usedTokens: number | null | undefined,
  contextWindow: number | null | undefined,
  tokens?: { inputTokens?: number | null; outputTokens?: number | null }
): ContextMeterState {
  const remaining = remainingContextPercent(usedTokens, contextWindow);
  if (remaining === null || (usedTokens as number) > (contextWindow as number)) {
    return { kind: 'unknown' };
  }
  const spent = usedTokens as number;
  const window = contextWindow as number;
  const used = 100 - remaining;
  // The floor is decided on the unrounded share: a session 0.1% of the way
  // through rounds to 0% used and would otherwise paint nothing.
  const exactUsed = Math.max(0, Math.min(100, (spent / window) * 100));
  const arc = exactUsed <= 0 ? 0 : Math.max(MIN_VISIBLE_ARC_PERCENT, exactUsed);
  return {
    kind: 'percent',
    remaining,
    used,
    arc,
    warm: remaining <= 25,
    hot: remaining <= 10,
    usedTokens: spent,
    contextWindow: window,
    inputTokens: Number.isFinite(tokens?.inputTokens) ? (tokens?.inputTokens as number) : null,
    outputTokens: Number.isFinite(tokens?.outputTokens) ? (tokens?.outputTokens as number) : null
  };
}

export interface ContextMeterPopoverRow {
  label: string;
  value: string;
}

export interface ContextMeterPopoverView {
  /** "51,285 of 1,000,000 (5%)" */
  headline: string;
  rows: ContextMeterPopoverRow[];
}

/** Full numbers with thousands separators: 51285 -> "51,285". */
function exactTokens(value: number): string {
  return Math.round(value).toLocaleString('en-US');
}

/**
 * What the ring's popup says. Null when there is no percentage to show, so the
 * ring and its popup appear and disappear together. A field the adapter never
 * sent names the provider rather than showing a zero it did not report.
 */
export function contextMeterPopover(
  meter: ContextMeterState,
  providerName: string
): ContextMeterPopoverView | null {
  if (meter.kind !== 'percent') return null;
  const absent = `not reported by ${providerName}`;
  return {
    headline: `${exactTokens(meter.usedTokens)} of ${exactTokens(meter.contextWindow)} (${meter.used}%)`,
    rows: [
      { label: 'In', value: meter.inputTokens === null ? absent : exactTokens(meter.inputTokens) },
      { label: 'Out', value: meter.outputTokens === null ? absent : exactTokens(meter.outputTokens) }
    ]
  };
}

/** Dash length for a ring whose full circumference reads 100%: the visible
 * arc is the remaining share, so it empties as context is used. */
export function ringDash(remaining: number, circumference: number): number {
  return (remaining / 100) * circumference;
}

function rank(command: ConversationCommand, query: string): number {
  const name = command.name.toLowerCase();
  if (name.startsWith(query)) return 0;
  if (name.includes(query)) return 1;
  return 2;
}

export function slashMenuState(
  draft: string,
  catalog: readonly ConversationCommand[],
  input: SlashMenuInput
): SlashMenuState {
  const query = slashCommandQuery(draft);
  if (query === null || input.dismissed) {
    return { open: false, query: '', commands: [], activeIndex: 0, emptyText: '' };
  }
  const normalized = query.trim().toLowerCase();
  const matches = filterConversationCommandCatalog(catalog, query);
  if (normalized) {
    matches.sort((left, right) => rank(left, normalized) - rank(right, normalized));
  }
  const emptyText = matches.length
    ? ''
    : catalog.length
      ? 'No commands match'
      : 'No commands';
  const activeIndex = matches.length ? Math.min(Math.max(input.activeIndex, 0), matches.length - 1) : 0;
  return { open: true, query, commands: matches, activeIndex, emptyText };
}

export function moveSlashMenuIndex(current: number, length: number, key: string): number {
  if (length <= 0) return 0;
  if (key === 'ArrowDown') return (current + 1) % length;
  if (key === 'ArrowUp') return (current - 1 + length) % length;
  return Math.min(Math.max(current, 0), length - 1);
}

/** Replace the typed command word with the chosen one, ready for arguments. */
export function draftAfterSlashCommand(_draft: string, command: ConversationCommand): string {
  return `/${command.name} `;
}
