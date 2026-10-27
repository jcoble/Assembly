import { TrackedWebSocket } from './debug.svelte';
import { BASE_WS, TOKEN } from './host/backend';

// The page's one socket to the backend (plan 5.1): `hello` with the token, then `call` frames answered by `result` or
// `error`, and `cancel` for a call nobody wants any more. The backend's side is
// dotnet/Assembly.Infrastructure/Socket/. The socket opens on the first call; if it can't, it's tried once more and
// the call fails. There are no timers here: the backend ends every call within 30 s.

const PROTOCOL = 1;

// Where a failed call is told, once for the whole page; the console until then.
let report = (message: string) => console.warn(message);
export const onFailure = (handler: (message: string) => void) => (report = handler);

// What a call comes back with: its result, or why there's none. 'failed' was already told through onFailure;
// 'aborted' (a newer call took its place, its owner closed, the socket closed) isn't an error and shows nothing.
export type Outcome<T> = { ok: true; result: T } | { ok: false; why: 'failed' | 'aborted' };

type Frame = { t: string; id?: number; result?: unknown; message?: string };
type Pending = { name: string; settle: (outcome: Outcome<unknown>) => void };

let connecting: Promise<WebSocket> | undefined;
let current: WebSocket | undefined;
let lastId = 0;
const pending = new Map<number, Pending>();
const running = new Map<string, number>();

// The open socket, opening it (and once more if that fails) when there's none.
function connect(): Promise<WebSocket> {
  connecting ??= open()
    .catch(open)
    .catch((e) => {
      connecting = undefined;
      throw e;
    });
  return connecting;
}

function open(): Promise<WebSocket> {
  return new Promise((resolve, reject) => {
    const socket = new TrackedWebSocket(`${BASE_WS}/socket`);
    let welcomed = false;
    socket.addEventListener('open', () => socket.send(JSON.stringify({ t: 'hello', token: TOKEN ?? '', protocol: PROTOCOL })));
    socket.addEventListener('message', (event) => {
      const frame = JSON.parse(String((event as MessageEvent).data)) as Frame;
      if (frame.t === 'welcome') {
        welcomed = true;
        current = socket;
        return resolve(socket);
      }
      const call = pending.get(frame.id!);
      if (!call) return;
      pending.delete(frame.id!);
      if (frame.t === 'result') return call.settle({ ok: true, result: frame.result });
      report(`${call.name} failed: ${frame.message}`);
      call.settle({ ok: false, why: 'failed' });
    });
    socket.addEventListener(
      'close',
      (event) => {
        if (!welcomed) return reject(new Error(`the socket closed (${(event as CloseEvent).code})`));
        connecting = current = undefined;
        for (const call of pending.values()) call.settle({ ok: false, why: 'aborted' });
        pending.clear();
      },
      { once: true },
    );
  });
}

// One of a feature's calls. With `key`, a newer call for the same key cancels the one still running, so a fast switch
// never waits on a stale load; `signal` (its owner closing) cancels it too.
export async function call<T>(feature: string, method: string, args: Record<string, unknown> = {}, { key, signal }: { key?: string; signal?: AbortSignal } = {}): Promise<Outcome<T>> {
  const name = `${feature}/${method}`;
  const mine = new AbortController();
  if (key) {
    const older = running.get(key);
    if (older !== undefined) cancel(older);
    running.set(key, ++lastId);
  }
  const id = key ? running.get(key)! : ++lastId;
  const abort = () => mine.abort();
  signal?.addEventListener('abort', abort, { once: true });
  try {
    let socket: WebSocket;
    try {
      socket = await connect();
    } catch (e) {
      report(`${name} failed: ${e instanceof Error ? e.message : e}`);
      return { ok: false, why: 'failed' };
    }
    if (mine.signal.aborted || signal?.aborted || (key && running.get(key) !== id)) return { ok: false, why: 'aborted' };
    const outcome = new Promise<Outcome<unknown>>((settle) => pending.set(id, { name, settle }));
    socket.send(JSON.stringify({ t: 'call', id, feature, method, args }));
    mine.signal.addEventListener('abort', () => cancel(id), { once: true });
    return (await outcome) as Outcome<T>;
  } finally {
    signal?.removeEventListener('abort', abort);
    if (key && running.get(key) === id) running.delete(key);
  }
}

// Tells the backend to stop a call, and answers it as aborted now rather than waiting for its `cancelled`.
function cancel(id: number) {
  const call = pending.get(id);
  if (!call) return;
  pending.delete(id);
  current?.send(JSON.stringify({ t: 'cancel', id }));
  call.settle({ ok: false, why: 'aborted' });
}
