/**
 * runOutputTail.ts — bounded plain-text tail of a running command's output.
 *
 * Chunks may split mid-line, so the last entry is treated as the open line and
 * the next chunk continues it. ANSI escape sequences and carriage returns are
 * stripped so plain text stays readable.
 *
 * Pure module: no DOM, no state, no imports.
 */

/** Maximum number of lines kept per running command tail. */
export const RUN_OUTPUT_TAIL_LINES = 200;

/** Maximum character length kept so single-line progress rewrites cannot grow unbounded. */
export const RUN_OUTPUT_TAIL_CHARS = 64 * 1024;

/**
 * Terminal escape sequences stripped from output tails:
 *  - `ESC ] ... BEL` or `ESC ] ... ESC \` (OSC / window title)
 *  - `ESC [ ... final` (CSI / color, cursor movement, line clear)
 *  - `ESC <single byte>` (short 2-byte escapes)
 */
const ESCAPE_SEQUENCES = new RegExp(
  [
    '\\u001b\\][^\\u0007]*(?:\\u0007|\\u001b\\\\)',
    '\\u001b\\[[0-9;?]*[ -/]*[@-~]',
    '\\u001b[@-Z\\\\-_]'
  ].join('|'),
  'g'
);

/** Append a chunk to a tail immutably, keeping only the bounded visible suffix. */
export function appendOutputTail(tail: readonly string[], chunk: string): string[] {
  const text = String(chunk ?? '')
    .replace(ESCAPE_SEQUENCES, '')
    .replace(/\r/g, '');
  const lines = tail.length > 0 ? [...tail] : [''];
  const parts = text.split('\n');
  lines[lines.length - 1] += parts[0];
  for (let index = 1; index < parts.length; index += 1) {
    lines.push(parts[index]);
  }
  const lineBounded =
    lines.length > RUN_OUTPUT_TAIL_LINES ? lines.slice(-RUN_OUTPUT_TAIL_LINES) : lines;
  const joined = lineBounded.join('\n');
  return joined.length > RUN_OUTPUT_TAIL_CHARS
    ? joined.slice(-RUN_OUTPUT_TAIL_CHARS).split('\n')
    : lineBounded;
}
