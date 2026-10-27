import { TrackedWebSocket } from './debug.svelte';
import { BASE_WS, TOKEN } from './host/backend';

// The page's one socket to the backend (plan 5.1): `hello` with the token, then `call` frames answered by `result` or
// `error`, `cancel` for a call nobody wants any more, and `sub`/`unsub` for a topic's `state` and `change` frames
// (subscribe), and `save` for a feature's settings. The backend's side is
// dotnet/Assembly.Infrastructure/Socket/. The socket opens on the first call; if it can't, it's tried once more and
// the call fails. An open socket that closes is reopened once (reopen). There are no timers here: the backend ends every call within 30 s.

const PROTOCOL = 1;

// Where a failed call is told, once for the whole page; the console until then.
let report = (message: string) => console.warn(message);
export const onFailure = (handler: (message: string) => void) => (report = handler);

// What a call comes back with: its result, or why there's none. 'failed' was already told through onFailure;
// 'aborted' (a newer call took its place, its owner closed, the socket closed) isn't an error and shows nothing.
export type Outcome<T> = { ok: true; result: T } | { ok: false; why: 'failed' | 'aborted' };

type Entry = { key: string; item: unknown };
type Frame = { t: string; id?: number; result?: unknown; code?: string; message?: string; sub?: number; items?: Entry[]; key?: string; item?: unknown };
type Pending = { name: string; settle: (outcome: Outcome<unknown>) => void };
// state upserts its items (no topic removes yet; the first one that does makes state replace, keeping held keys).
type Apply<T> = { state: (items: T[]) => void; change: (item: T) => void };
type Subscription = { topic: string; key?: string; apply: Apply<unknown> };

let connecting: Promise<WebSocket> | undefined;
let current: WebSocket | undefined;
let lastId = 0;
const pending = new Map<number, Pending>();
const running = new Map<string, number>();
const subscriptions = new Map<number, Subscription>();
// Per topic and key: this window's saves still waiting on their answer, and the newest value that arrived meanwhile.
// A save sent on a socket that closes is answered 'aborted' then, so its count goes down with it; one still waiting
// to connect stays counted and goes out on the reopened socket. Held values go with the socket: its reopen's state is
// current.
const saving = new Map<string, number>();
const held = new Map<string, unknown>();

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
      if (frame.t === 'state') return deliver(frame.sub!, frame.items!, false);
      if (frame.t === 'change') return deliver(frame.sub!, [{ key: frame.key!, item: frame.item }], true);
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
        held.clear();
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

// A setting's save (plan 5.9): one call, `{feature}/set`, each time. The caller shows its own value first. Until its
// answer, anything that arrives for the key is held, not shown; at the last answer the newest held value is shown, or,
// after a failure with nothing held, the feature's topic is subscribed again to read the committed value back.
export async function save(feature: string, key: string, value: unknown): Promise<Outcome<void>> {
  const slot = `${feature}\n${key}`;
  saving.set(slot, (saving.get(slot) ?? 0) + 1);
  let outcome: Outcome<void> = { ok: false, why: 'failed' };
  try {
    outcome = await call<void>(feature, 'set', { key, value });
    return outcome;
  } finally {
    answered(feature, key, outcome);
  }
}

// One of the key's saves is answered. At the last, the newest held value is shown, or, after a failure with nothing
// held, the topic is subscribed again so its state brings back the committed value.
function answered(feature: string, key: string, outcome: Outcome<void>) {
  const slot = `${feature}\n${key}`;
  const left = saving.get(slot)! - 1;
  if (left > 0) return saving.set(slot, left);
  saving.delete(slot);
  if (held.has(slot)) {
    const item = held.get(slot);
    held.delete(slot);
    for (const s of subscriptions.values()) if (gets(s, feature, key)) s.apply.change(item);
  } else if (!outcome.ok && outcome.why === 'failed' && current) {
    for (const [id, s] of subscriptions) {
      if (!gets(s, feature, key)) continue;
      current.send(JSON.stringify({ t: 'unsub', id }));
      sendSub(current, id);
    }
  }
}

const gets = (s: Subscription, topic: string, key: string) => s.topic === topic && (s.key === undefined || s.key === key);

// A state's entries or a change's, the same way: each held while its key has a save in flight, the rest applied.
function deliver(sub: number, entries: Entry[], change: boolean) {
  const subscription = subscriptions.get(sub);
  if (!subscription) return;
  const shown = entries.filter(({ key, item }) => {
    const slot = `${subscription.topic}\n${key}`;
    if (saving.has(slot)) held.set(slot, item);
    return !saving.has(slot);
  }).map(({ item }) => item);
  if (!change) subscription.apply.state(shown);
  else if (shown.length) subscription.apply.change(shown[0]);
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
