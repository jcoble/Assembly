/**
 * devInvokeCounter.svelte.ts — dev-only tally of backend `invoke` calls.
 *
 * A runes `$state` counter the /next shell bumps on every Tauri command it
 * issues, so the dev HUD can show "are we hammering the backend?" at a glance.
 * Pure bookkeeping: no IO, no persistence, no backend calls. Reads/writes are
 * tracked by Svelte's runes runtime.
 *
 * The tally is split into TWO buckets because they answer different questions.
 * `write_terminal_session` carries bytes the terminal owes the program —
 * keystrokes, a pasted line, and (because a full-screen TUI like `claude`
 * enables DECSET 1004 focus reporting) an `\x1b[I` / `\x1b[O` on every focus
 * change. Those are keystroke-class events: one per session switch is CORRECT,
 * and counting them in the headline made the HUD read "+1 invoke per switch"
 * and look like a backend storm. So input gets its own bucket and the headline
 * number — the shell's own IPC overhead, the thing worth watching — excludes it.
 */

/**
 * Live invoke tallies. `total` is the HEADLINE: every counted call that is NOT
 * interactive PTY input. `input` is the excluded bucket. `byCommand` is
 * untouched by the split — it still breaks down EVERY counted call, input
 * included, per Tauri command name.
 */
export const invokeCounts = $state<{
  total: number;
  input: number;
  byCommand: Record<string, number>;
}>({
  total: 0,
  input: 0,
  byCommand: {}
});

/** Record one backend invoke of `command`, routed to its bucket. */
export function countInvoke(command: string): void {
  // if (isTerminalInputCommand(command)) {
  //   invokeCounts.input += 1;
  // } else {
  //   invokeCounts.total += 1;
  // }
  // invokeCounts.byCommand[command] = (invokeCounts.byCommand[command] ?? 0) + 1;
}
