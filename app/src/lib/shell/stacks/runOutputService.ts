/**
 * runOutputService.ts — the live output tail the Run panel shows under a
 * running action.
 *
 * The panel is not a terminal. It shows the last couple of hundred lines a
 * running command printed so a glance answers "is it up yet, and did it
 * complain"; the terminal itself is the session in the rail, one click away.
 *
 * THE DISCIPLINE, same as the rest of this lane: nothing polls. A watch is
 * opened when the panel is on screen and closed the moment it is not. Each
 * watch does exactly one read per running action (its saved scrollback) plus
 * one subscription to the output event that the app is already emitting for the
 * terminals themselves — after that, no backend call happens until the user
 * opens the panel again.
 *
 * WHY IT LOOKS SESSIONS UP. Output events are keyed by the backend's own
 * terminal id, and everything else in this lane is keyed by the shell's
 * `ownedId`. `list_terminal_sessions` reports both, so one read at the start of
 * a watch is enough to translate between them for its lifetime.
 */
import { countInvoke } from '../devInvokeCounter.svelte.ts';
import {
  listTerminalSessionsFromTauri,
  readTerminalSessionScrollbackFromTauri,
  type TerminalOutputEnvelope
} from '../../tauriSource.ts';
import { appendOutputTail } from '../panels/run/runOutputTail.ts';
import {
  subscribeToTerminalOutput,
  type TerminalOutputSubscription
} from '../terminal/terminalService.ts';

/**
 * How much of a saved scrollback is read into a fresh tail. The ring behind it
 * holds megabytes and the tail keeps 200 lines, so everything older than this
 * would be parsed only to be thrown away.
 */
const SEED_MAX_CHARS = 64 * 1024;

/** A watch in progress. Calling `stop` is safe at any time, including twice. */
export interface RunOutputWatch {
  stop(): void;
}

/**
 * Follow the output of the given sessions until `stop`.
 *
 * `onTail` is called once per session with its seeded tail, then again on every
 * chunk that session prints. A session the desktop app knows nothing about is
 * skipped silently: outside the desktop app there are no terminals to follow,
 * and the panel says so elsewhere rather than through an empty box here.
 */
export async function watchRunOutput(
  ownedIds: readonly string[],
  onTail: (ownedId: string, tail: string[]) => void
): Promise<RunOutputWatch> {
  let stopped = false;
  const stopController = new AbortController();
  let unlisten: TerminalOutputSubscription | null = null;

  const watch: RunOutputWatch = {
    stop(): void {
      stopped = true;
      stopController.abort();
      unlisten?.unsubscribe();
      unlisten = null;
    }
  };

  const wanted = new Set(ownedIds);
  if (wanted.size === 0) return watch;

  countInvoke('list_terminal_sessions');
  const sessions = (await listTerminalSessionsFromTauri()) ?? [];
  if (stopped) return watch;

  /** Backend terminal id -> the shell session it belongs to. */
  const ownedByTerminal = new Map<string, string>();
  for (const session of sessions) {
    const ownedId = session.ownedId ?? null;
    if (ownedId && wanted.has(ownedId)) ownedByTerminal.set(session.sessionId, ownedId);
  }
  if (ownedByTerminal.size === 0) return watch;

  const tails = new Map<string, string[]>();
  const watermarks = new Map<string, { generation: number; sequence: number }>();
  const queued: TerminalOutputEnvelope[] = [];
  let hydrating = true;

  function applyOutput(envelope: TerminalOutputEnvelope): void {
    const payload = envelope.chunk;
    const ownedId = ownedByTerminal.get(payload.sessionId);
    if (!ownedId || !payload.data) return;
    const watermark = watermarks.get(payload.sessionId);
    if (
      watermark &&
      envelope.generation === watermark.generation &&
      envelope.sequence <= watermark.sequence
    ) {
      return;
    }
    const next = appendOutputTail(tails.get(ownedId) ?? [], payload.data);
    tails.set(ownedId, next);
    onTail(ownedId, next);
  }

  const stopListening = subscribeToTerminalOutput((envelope) => {
    if (hydrating) queued.push(envelope);
    else applyOutput(envelope);
  }, stopController.signal);
  try {
    await stopListening.ready;
  } catch (error) {
    stopController.abort();
    throw error;
  }
  if (stopped) return watch;
  unlisten = stopListening;

  for (const [terminalId, ownedId] of ownedByTerminal) {
    countInvoke('read_terminal_session_scrollback');
    const scrollback = await readTerminalSessionScrollbackFromTauri(terminalId, SEED_MAX_CHARS);
    if (stopped) return watch;
    const seed = appendOutputTail([], scrollback?.data ?? '');
    if (scrollback) {
      watermarks.set(terminalId, {
        generation: scrollback.generation,
        sequence: scrollback.sequence
      });
    }
    tails.set(ownedId, seed);
    onTail(ownedId, seed);
  }
  hydrating = false;
  for (const envelope of queued) applyOutput(envelope);
  queued.length = 0;
  return watch;
}
