import { TrackedWebSocket } from './debug.svelte';
import { BASE_WS, TOKEN } from './host/backend';

// The page's one socket to the backend (plan 5.1): `hello` with the token, then `call` frames answered by `result` or
// `error`, `cancel` for a call nobody wants any more, and `sub`/`unsub` for a topic's `state` and `change` frames
// (subscribe). The backend's side is
// dotnet/Assembly.Infrastructure/Socket/. The socket opens on the first call; if it can't, it's tried once more and
// the call fails. An open socket that closes is reopened once (reopen). There are no timers here: the backend ends every call within 30 s.

const PROTOCOL = 1;

// Where a failed call is told, once for the whole page; the console until then.
let report = (message: string) => console.warn(message);
export const onFailure = (handler: (message: string) => void) => (report = handler);

// What a call comes back with: its result, or why there's none. 'failed' was already told through onFailure;
// 'aborted' (a newer call took its place, its owner closed, the socket closed) isn't an error and shows nothing.
export type Outcome<T> = { ok: true; result: T } | { ok: false; why: 'failed' | 'aborted' };

type Frame = { t: string; id?: number; result?: unknown; code?: string; message?: string; sub?: number; items?: unknown[]; item?: unknown };
type Pending = { name: string; settle: (outcome: Outcome<unknown>) => void };
type Apply<T> = { state: (items: T[]) => void; change: (item: T) => void };
type Subscription = { topic: string; key?: string; apply: Apply<unknown> };

let connecting: Promise<WebSocket> | undefined;
let current: WebSocket | undefined;
let lastId = 0;
const pending = new Map<number, Pending>();
const running = new Map<string, number>();
const subscriptions = new Map<number, Subscription>();

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
        // Every subscription, again after a reopen: its state comes back current.
        for (const id of subscriptions.keys()) sendSub(socket, id);
        return resolve(socket);
      }
      if (frame.t === 'state') return subscriptions.get(frame.sub!)?.apply.state(frame.items!);
      if (frame.t === 'change') return subscriptions.get(frame.sub!)?.apply.change(frame.item);
      const call = pending.get(frame.id!);
      if (!call) return failSub(frame);
      pending.delete(frame.id!);
      if (frame.t === 'result') return call.settle({ ok: true, result: frame.result });
      if (frame.code === 'cancelled') return call.settle({ ok: false, why: 'aborted' });
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
        reopen();
      },
      { once: true },
    );
  });
}

// After an open socket closes: one try to open it again, and the failure told if that fails. B9 adds what comes after
// (the shell's backend-ready, "Disconnected" and Reconnect; plan 5.10); until then the next call tries again.
function reopen() {
  connecting = open();
  connecting.catch((e) => {
    connecting = undefined;
    report(`the socket closed and couldn't reopen: ${e instanceof Error ? e.message : e}`);
  });
}

// One of a feature's calls. With `key`, a newer call for the same key cancels the one still running, so a fast switch
// never waits on a stale load: the older one comes back 'aborted' at once, and its result, if the backend had
// already finished it, never reaches its caller. `signal` (its owner closing) cancels it too, and that call settles on
// the backend's answer: 'aborted' for `cancelled`, or its result if it had already finished.
export async function call<T>(feature: string, method: string, args: Record<string, unknown> = {}, { key, signal }: { key?: string; signal?: AbortSignal } = {}): Promise<Outcome<T>> {
  const name = `${feature}/${method}`;
  const mine = new AbortController();
  if (key) {
    const older = running.get(key);
    if (older !== undefined) supersede(older);
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
      if (key && running.get(key) !== id) return { ok: false, why: 'aborted' };
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

// Tells the backend to stop a call. The call still settles on the backend's answer: `cancelled`, or its result if it
// had already finished, so a write that committed is never taken for a lost one.
function cancel(id: number) {
  if (pending.has(id)) current?.send(JSON.stringify({ t: 'cancel', id }));
}

// A newer call took this one's key: tell the backend, and answer 'aborted' now, so a stale result can't land after
// the newer one. Keys are for loads.
function supersede(id: number) {
  const call = pending.get(id);
  if (!call) return;
  cancel(id);
  pending.delete(id);
  call.settle({ ok: false, why: 'aborted' });
}

// A topic's state, then each change to it (plan 5.7). With no key, every change on the topic; with a key, only that
// key's. It stays subscribed until `signal` aborts, across reopens, each of which sends the current state again.
export function subscribe<T>(topic: string, key: string | undefined, apply: Apply<T>, signal?: AbortSignal): void {
  if (signal?.aborted) return;
  const id = ++lastId;
  subscriptions.set(id, { topic, key, apply: apply as Apply<unknown> });
  signal?.addEventListener('abort', () => subscriptions.delete(id) && current?.send(JSON.stringify({ t: 'unsub', id })), { once: true });
  // An open socket takes it now; otherwise the welcome sends it.
  if (current) sendSub(current, id);
  else connect().catch((e) => report(`${topic} failed: ${e instanceof Error ? e.message : e}`));
}

function sendSub(socket: WebSocket, id: number) {
  const { topic, key } = subscriptions.get(id)!;
  socket.send(JSON.stringify({ t: 'sub', id, topic, key }));
}

// An error for a subscription, such as a topic the backend doesn't have: told, and the subscription dropped.
function failSub(frame: Frame) {
  const subscription = subscriptions.get(frame.id!);
  if (frame.t !== 'error' || !subscription) return;
  subscriptions.delete(frame.id!);
  report(`${subscription.topic} failed: ${frame.message}`);
}
