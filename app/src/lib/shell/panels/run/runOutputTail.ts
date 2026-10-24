/**
 * runOutputTail.ts — the last few hundred lines of a running command's output.
 *
 * The Run panel shows a peek at what a running action is printing, not a
 * terminal: the terminal is the session in the rail, and clicking the row goes
 * there. So the panel keeps a bounded tail and nothing else — no scrollback, no
 * search, and no growth over a long-running dev server's lifetime.
 *
 * WHAT IT DOES TO THE TEXT. Output arrives in chunks that split anywhere,
 * including mid-line, so the tail's last entry is treated as the line still
 * being written and the next chunk continues it. Escape sequences (colour,
 * cursor moves) and carriage returns are removed: this is a plain text list, and
 * a raw escape sequence in it reads as garbage rather than as colour.
 *
 * PURE: no DOM, no state, no imports. `scripts/runActions.test.ts` covers it.
 */

/** How many lines of a run's output the panel keeps. */
export const RUN_OUTPUT_TAIL_LINES = 200;
/** A progress renderer may rewrite one line forever, so bytes need a ceiling too. */
export const RUN_OUTPUT_TAIL_CHARS = 64 * 1024;

/**
 * Escape sequences a terminal program emits. The pattern is built from strings
 * so the escape byte is spelled out rather than sitting invisibly in the source:
 *
 *  - `ESC ] … BEL` or `ESC ] … ESC \` — the window-title family;
 *  - `ESC [ … letter` — colour, cursor moves, line clears;
 *  - `ESC <one character>` — the short two-character sequences.
 */
const ESCAPE_SEQUENCES = new RegExp(
  ['\\u001b\\][^\\u0007]*(?:\\u0007|\\u001b\\\\)', '\\u001b\\[[0-9;?]*[ -/]*[@-~]', '\\u001b[@-Z\\\\-_]'].join('|'),
  'g'
);

/** Appends a chunk to a tail, keeping only the bounded visible suffix. */
export function appendOutputTail(tail: readonly string[], chunk: string): string[] {
  const text = String(chunk ?? '')
    .replace(ESCAPE_SEQUENCES, '')
    .replace(/\r/g, '');
  const lines = tail.length > 0 ? [...tail] : [''];
  const parts = text.split('\n');
  lines[lines.length - 1] += parts[0];
  for (let index = 1; index < parts.length; index += 1) lines.push(parts[index]);
  const lineBounded = lines.length > RUN_OUTPUT_TAIL_LINES ? lines.slice(-RUN_OUTPUT_TAIL_LINES) : lines;
  const joined = lineBounded.join('\n');
  return joined.length > RUN_OUTPUT_TAIL_CHARS
    ? joined.slice(-RUN_OUTPUT_TAIL_CHARS).split('\n')
    : lineBounded;
}
