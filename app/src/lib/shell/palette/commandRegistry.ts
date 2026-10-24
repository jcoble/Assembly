/**
 * commandRegistry.ts — the action list behind the /next command palette.
 *
 * Pure and free of IO: it holds command descriptions and searches them. It never
 * calls the backend, never touches the DOM, and knows nothing about the panels
 * that supply commands — each panel (or the page) hands its commands in under a
 * source name, and the palette renders whatever is registered.
 *
 * Why a registry instead of one big list: the old shell built 131 commands inline
 * inside the page, each one closing over page-local state, so none of them could
 * move. Here a command is a plain value, so any part of the shell can add one
 * without the palette importing that part.
 *
 * NOTE: explicit `.ts` specifiers elsewhere in `src/lib/shell` exist so modules
 * load under `node --experimental-strip-types`. This file imports nothing, so it
 * loads there as-is.
 */

/** One action the palette can show and run. */
export interface PaletteCommand {
  /** Stable unique id. Two commands with the same id: the first one registered wins. */
  id: string;
  /** Short name shown in bold, e.g. "Reset layout". */
  label: string;
  /** One line of context under the name, e.g. which project it acts on. */
  detail: string;
  /** Optional check run at search time. `true` = shown greyed out and not runnable. */
  disabled?: () => boolean;
  /** What the command does when chosen. May be async. */
  perform: () => void | Promise<void>;
}

/**
 * A command prepared for display: `disabled` has been resolved to a plain boolean
 * so the overlay (which takes `disabled?: boolean`) can render it directly.
 */
export interface PaletteCommandRow {
  id: string;
  label: string;
  detail: string;
  disabled: boolean;
  perform: () => void | Promise<void>;
}

/**
 * Commands by source name. A Map keeps insertion order, so the palette lists
 * sources in the order they first registered and commands in the order given.
 * Re-registering an existing source replaces its commands and keeps its place.
 */
const commandsBySource = new Map<string, PaletteCommand[]>();

/**
 * Replace everything registered under `source` with `commands`.
 *
 * Idempotent per source: calling it twice with the same source leaves one copy,
 * so a component that re-registers on every mount never duplicates its rows.
 * Returns the function that removes this source again (for component teardown).
 */
export function registerCommands(source: string, commands: PaletteCommand[]): () => void {
  commandsBySource.set(source, [...commands]);
  return () => unregisterCommands(source);
}

/** Remove every command registered under `source`. Unknown source: does nothing. */
export function unregisterCommands(source: string): void {
  commandsBySource.delete(source);
}

/** Forget every registered command. Used by tests; the app never calls it. */
export function resetCommandRegistry(): void {
  commandsBySource.clear();
}

/**
 * Every registered command, in registration order, with duplicate ids dropped
 * (first registration wins). The de-duplication is not cosmetic: the palette list
 * is keyed by id and a repeated key would break rendering.
 */
export function allCommands(): PaletteCommand[] {
  const seen = new Set<string>();
  const result: PaletteCommand[] = [];
  for (const commands of commandsBySource.values()) {
    for (const command of commands) {
      if (seen.has(command.id)) continue;
      seen.add(command.id);
      result.push(command);
    }
  }
  return result;
}

/**
 * Is this command currently unavailable?
 *
 * A check that throws counts as unavailable: if we cannot tell whether an action
 * is safe to run, we show it greyed out rather than run it and hope.
 */
function commandIsDisabled(command: PaletteCommand): boolean {
  if (!command.disabled) return false;
  try {
    return command.disabled() === true;
  } catch {
    return true;
  }
}

/**
 * Does every word of the search text appear somewhere in these values?
 *
 * Copied from `textMatchesSearchTokens` in `src/lib/sourceData.ts:1577` (the old
 * shell's palette used it) so the palette keeps the same feel: case-insensitive,
 * space-separated words that may match in any order and in any field. Copied
 * rather than imported to keep this module dependency-free — it is mounted on
 * every page view, and `sourceData.ts` is a 2,877-line module.
 */
function textMatchesSearchWords(
  filter: string,
  ...values: Array<string | number | boolean | null | undefined>
): boolean {
  const words = String(filter ?? '')
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean);
  if (words.length === 0) return true;

  const haystack = values.map((value) => String(value ?? '').toLowerCase()).join(' ');
  return words.every((word) => haystack.includes(word));
}

/**
 * The rows to show for a search: commands whose name or detail matches every word
 * of `query`, in registration order, at most `limit` of them (12 like the old
 * shell). An empty query returns the first `limit` commands.
 *
 * Unavailable commands stay in the list, greyed out, so the palette explains why
 * an expected action cannot run instead of silently hiding it.
 */
export function filterCommands(query: string, limit = 12): PaletteCommandRow[] {
  const maxRows = Math.max(0, Math.floor(Number.isFinite(limit) ? limit : 0));
  if (maxRows === 0) return [];

  const rows: PaletteCommandRow[] = [];
  for (const command of allCommands()) {
    if (!textMatchesSearchWords(query, command.label, command.detail)) continue;
    rows.push({
      id: command.id,
      label: command.label,
      detail: command.detail,
      disabled: commandIsDisabled(command),
      perform: command.perform
    });
    if (rows.length >= maxRows) break;
  }
  return rows;
}

/**
 * Run a chosen row. Unavailable rows do nothing. Errors are returned to the
 * caller (the palette host reports them) rather than thrown into a keyboard
 * handler where nothing would catch them.
 */
export async function runPaletteCommand(row: PaletteCommandRow): Promise<void> {
  if (row.disabled) return;
  await row.perform();
}
