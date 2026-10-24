import { untrack } from 'svelte';

// Debug counters (spec 6.1). Each must return to its baseline after a
// conversation closes; the status bar shows them and the soak samples them.
// `frames` is a pending animation frame (the streaming reveal).
export const counters = $state({ sockets: 0, chats: 0, fetches: 0, timers: 0, frames: 0, rows: 0, tauri: 0 });

// Row measurements since launch, and how many moved a row by 1px or more from
// the size the virtualizer had (rows/heights.ts). Not lifecycle counters: they only grow.
export const measures = $state({ measured: 0, moved: 0 });

// Open conversations, kept in a plain number and copied to the counter. A
// thread switch makes the next conversation inside the switch's batch, so
// `counters.chats++` there was lost when the old one's `--` ran at commit.
let chats = 0;
export const chatOpened = () => untrack(() => (counters.chats = ++chats));
export const chatClosed = () => (counters.chats = --chats);

// Timeline rows mounted and not yet destroyed, counted the same way. More than
// the rows in the DOM means a row was never destroyed.
let rows = 0;
export const rowMounted = () => untrack(() => (counters.rows = ++rows));
export const rowDestroyed = () => (counters.rows = --rows);

// Tauri listeners: counted once listen() resolves, and uncounted when the stop
// it gave is called.
let tauri = 0;
export const trackedListen = (listening: Promise<() => void>): Promise<() => void> =>
  listening.then((stop) => {
    counters.tauri = ++tauri;
    return () => {
      counters.tauri = --tauri;
      stop();
    };
  });

// A WebSocket counted from creation until its close event (untracked, like
// delay's count, for a socket opened inside an effect).
export class TrackedWebSocket extends WebSocket {
  constructor(url: string | URL) {
    super(url);
    untrack(() => counters.sockets++);
    this.addEventListener('close', () => counters.sockets--, { once: true });
  }
}

// A counted timer bound to an owner's signal: aborting clears it and rejects.
// The count is untracked, so an effect that starts a delay does not rerun on it,
// and kept in a plain number like `chats`: a `++` on the state inside a batch was
// lost when a later `--` ran, which took the counter below 0.
let timers = 0;
export function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    untrack(() => (counters.timers = ++timers));
    const finish = () => {
      counters.timers = --timers;
      signal.removeEventListener('abort', onAbort);
    };
    const onAbort = () => {
      clearTimeout(id);
      finish();
      reject(signal.reason);
    };
    const id = setTimeout(() => {
      finish();
      resolve();
    }, ms);
    signal.addEventListener('abort', onAbort, { once: true });
  });
}
