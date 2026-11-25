/**
 * `.NET` `/socket/terminal` RPC wrappers and live output tail watcher for the Run feature.
 *
 * All terminal calls and subscriptions travel over the dedicated `/socket/terminal` channel.
 * `startRunTerminal` omits `owner` because run configurations belong to the project folder
 * rather than a single Chat session (`TerminalHost.StartAsync` contract).
 */

import { BackendCallError, backend, isBackendAbort } from '$lib/infrastructure/backend';
import { appendOutputTail } from './runOutputTail';
import type { TerminalInfo, TerminalOutputPiece, TerminalOutputRow } from './types';

/** Read at most the newest 64 KiB of existing scrollback when opening an output watch. */
const SEED_MAX_BYTES = 64 * 1024;

export interface RunOutputWatch {
  stop(): void;
}

export interface RunOutputWatchCallbacks {
  onTail: (terminalId: string, tail: string[]) => void;
  onExit?: (terminalId: string, exitCode: number) => void;
  onMissing?: (terminalId: string) => void;
}

/** Start a project run command in its own `.NET` PTY without binding a Chat session owner. */
export function startRunTerminal(
  cwd: string,
  command: string,
  signal?: AbortSignal
): Promise<TerminalInfo> {
  return backend.call<TerminalInfo>(
    'terminal',
    'start',
    { cwd, command },
    { channel: 'terminal', signal }
  );
}

/** List all terminals currently held by the `.NET` `TerminalHost`. */
export function listRunTerminals(signal?: AbortSignal): Promise<TerminalInfo[]> {
  return backend.call<TerminalInfo[]>(
    'terminal',
    'list',
    {},
    { channel: 'terminal', signal, key: 'run:terminal-list' }
  );
}

/** Read a chunk of a terminal's retained output ring starting at byte `offset`. */
export function readRunTerminal(
  terminal: string,
  offset: number,
  signal?: AbortSignal
): Promise<TerminalOutputPiece> {
  return backend.call<TerminalOutputPiece>(
    'terminal',
    'read',
    { terminal, offset },
    { channel: 'terminal', signal }
  );
}

/** Close a terminal PTY and release its `.NET` output buffer. */
export async function closeRunTerminal(terminal: string, signal?: AbortSignal): Promise<void> {
  await backend.call('terminal', 'close', { terminal }, { channel: 'terminal', signal });
}

function decodeBase64Bytes(base64: string): Uint8Array {
  if (!base64) return new Uint8Array(0);
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

interface WatchedTerminal {
  id: string;
  hydrating: boolean;
  watermark: number;
  exitReported: boolean;
  tail: string[];
  queued: TerminalOutputRow[];
  decoder: TextDecoder;
  unsub: () => void;
}

/**
 * Subscribe to live output for the given `.NET` terminal IDs on `/socket/terminal`.
 *
 * Returns synchronously with a `stop()` handle so component cleanup always cancels in-flight
 * seed reads and unsubscribes every topic immediately with zero polling or timers.
 */
export function watchRunOutput(
  terminalIds: readonly string[],
  callbacks: RunOutputWatchCallbacks
): RunOutputWatch {
  const uniqueIds = [...new Set(terminalIds.filter((id) => id.trim().length > 0))];
  let stopped = false;
  const abortController = new AbortController();
  const entries = new Map<string, WatchedTerminal>();

  function applyRow(entry: WatchedTerminal, row: TerminalOutputRow): void {
    if (stopped) return;
    if (row.unavailable === 'terminal_missing') {
      if (!entry.exitReported) {
        entry.exitReported = true;
        callbacks.onMissing?.(entry.id);
      }
      return;
    }

    if (typeof row.data === 'string' && row.data.length > 0) {
      const seq = typeof row.seq === 'number' ? row.seq : null;
      const prevSeq = typeof row.prevSeq === 'number' ? row.prevSeq : null;
      if (seq !== null && seq <= entry.watermark) {
        // Already covered by the initial `terminal/read` snapshot.
      } else {
        let bytes = decodeBase64Bytes(row.data);
        if (prevSeq !== null && prevSeq < entry.watermark) {
          const skip = entry.watermark - prevSeq;
          bytes = skip < bytes.length ? bytes.subarray(skip) : new Uint8Array(0);
        }
        if (seq !== null) {
          entry.watermark = Math.max(entry.watermark, seq);
        } else {
          entry.watermark += bytes.length;
        }
        if (bytes.length > 0) {
          const chunk = entry.decoder.decode(bytes, { stream: true });
          if (chunk) {
            entry.tail = appendOutputTail(entry.tail, chunk);
            callbacks.onTail(entry.id, entry.tail);
          }
        }
      }
    }

    if (typeof row.exit === 'number' && !entry.exitReported) {
      entry.exitReported = true;
      const flushed = entry.decoder.decode();
      if (flushed) {
        entry.tail = appendOutputTail(entry.tail, flushed);
        callbacks.onTail(entry.id, entry.tail);
      }
      callbacks.onExit?.(entry.id, row.exit);
    }
  }

  async function seedAll(): Promise<void> {
    if (uniqueIds.length === 0) return;

    // Seed each terminal's newest suffix after subscriptions are armed so no bytes are missed.
    for (const id of uniqueIds) {
      if (stopped) return;
      const entry = entries.get(id);
      if (!entry) continue;
      try {
        let piece = await readRunTerminal(id, 0, abortController.signal);
        if (stopped) return;

        let bytes = decodeBase64Bytes(piece.data);
        let end = piece.end;

        // If the terminal ring already holds more than one read page, jump straight to its tail.
        if (end > piece.offset + bytes.length) {
          const tailOffset = Math.max(piece.offset, end - SEED_MAX_BYTES);
          piece = await readRunTerminal(id, tailOffset, abortController.signal);
          if (stopped) return;
          bytes = decodeBase64Bytes(piece.data);
          end = piece.end;
        }

        const suffix =
          bytes.length > SEED_MAX_BYTES ? bytes.subarray(bytes.length - SEED_MAX_BYTES) : bytes;
        const text = entry.decoder.decode(suffix, { stream: true });
        entry.watermark = end;
        entry.tail = appendOutputTail([], text);
        callbacks.onTail(id, entry.tail);

        entry.hydrating = false;
        const buffered = entry.queued;
        entry.queued = [];
        for (const row of buffered) {
          applyRow(entry, row);
        }
      } catch (err) {
        if (stopped || isBackendAbort(err)) return;
        entry.hydrating = false;
        entry.queued = [];
        if (err instanceof BackendCallError && err.code === 'terminal_missing') {
          if (!entry.exitReported) {
            entry.exitReported = true;
            callbacks.onMissing?.(id);
          }
        }
      }
    }

    // One-shot reconciliation after subscribing catches fast commands that exited before sub armed.
    if (stopped) return;
    try {
      const liveList = await listRunTerminals(abortController.signal);
      if (stopped) return;
      const byId = new Map(liveList.map((info) => [info.id, info]));
      for (const id of uniqueIds) {
        const entry = entries.get(id);
        if (!entry || entry.exitReported) continue;
        const info = byId.get(id);
        if (!info) {
          entry.exitReported = true;
          callbacks.onMissing?.(id);
        } else if (info.exitCode !== null && info.exitCode !== undefined) {
          entry.exitReported = true;
          callbacks.onExit?.(id, info.exitCode);
        }
      }
    } catch (err) {
      if (!stopped && !isBackendAbort(err)) {
        // Opportunistic reconciliation; live topic frames still flow if the terminal is active.
      }
    }
  }

  for (const id of uniqueIds) {
    const entry: WatchedTerminal = {
      id,
      hydrating: true,
      watermark: 0,
      exitReported: false,
      tail: [],
      queued: [],
      decoder: new TextDecoder('utf-8', { fatal: false }),
      unsub: () => {}
    };
    entry.unsub = backend.subscribe<TerminalOutputRow>(
      'terminal',
      (event) => {
        if (stopped) return;
        if (event.type !== 'change' || !event.item) return;
        if (entry.hydrating) {
          entry.queued.push(event.item);
        } else {
          applyRow(entry, event.item);
        }
      },
      id,
      'terminal'
    );
    entries.set(id, entry);
  }

  void seedAll();

  return {
    stop(): void {
      if (stopped) return;
      stopped = true;
      abortController.abort();
      for (const entry of entries.values()) {
        entry.unsub();
      }
      entries.clear();
    }
  };
}
