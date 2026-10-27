import assert from 'node:assert/strict';
import { test } from 'node:test';
import { jsModule, libModule } from '../../runes.ts';

type Outcome = { ok: true; result: unknown } | { ok: false; why: 'failed' | 'aborted' };
type Socket = {
  call: (feature: string, method: string, args?: Record<string, unknown>, options?: { key?: string; signal?: AbortSignal }) => Promise<Outcome>;
  onFailure: (handler: (message: string) => void) => void;
};

// A socket the test drives: it opens, receives and closes when told, and keeps what the page sent.
class FakeSocket extends EventTarget {
  sent: Record<string, unknown>[] = [];
  url: string;
  constructor(url: string) {
    super();
    this.url = url;
    sockets.push(this);
  }
  send(data: string) {
    this.sent.push(JSON.parse(data));
  }
  close() {
    this.closeNow(1000);
  }
  open() {
    this.dispatchEvent(new Event('open'));
  }
  receive(frame: unknown) {
    this.dispatchEvent(Object.assign(new Event('message'), { data: JSON.stringify(frame) }));
  }
  closeNow(code: number) {
    this.dispatchEvent(Object.assign(new Event('close'), { code }));
  }
}
let sockets: FakeSocket[] = [];

// A fresh socket.ts, on the fake socket.
let copies = 0;
async function load() {
  sockets = [];
  const failures: string[] = [];
  Object.assign(globalThis, { FakeSocket });
  const debug = jsModule(`export const TrackedWebSocket = globalThis.FakeSocket; // ${++copies}`);
  const backend = `${libModule('host/backend.ts')}#${copies}`;
  const module = (await import(libModule('socket.ts', { './debug.svelte': debug, './host/backend': backend }))) as Socket;
  module.onFailure((message) => failures.push(message));
  return { failures, ...module };
}

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

// Opens the first socket and answers its hello.
async function welcome() {
  await settle();
  const socket = sockets[0]!;
  socket.open();
  assert.equal(socket.sent[0]!.t, 'hello');
  socket.receive({ t: 'welcome', protocol: 1, machine: 'test-machine' });
  await settle();
  return socket;
}

// Pins: base#10
test('a newer call with the same key sends cancel for the older one, quietly', async () => {
  const { call, failures } = await load();
  const older = call('notes', 'get', {}, { key: 'notes/get' });
  const socket = await welcome();
  const newer = call('notes', 'get', {}, { key: 'notes/get' });
  await settle();
  assert.deepEqual(socket.sent.slice(1), [
    { t: 'call', id: 1, feature: 'notes', method: 'get', args: {} },
    { t: 'cancel', id: 1 },
    { t: 'call', id: 2, feature: 'notes', method: 'get', args: {} },
  ]);
  socket.receive({ t: 'error', id: 1, code: 'cancelled', message: 'notes/get was cancelled' });
  assert.deepEqual(await older, { ok: false, why: 'aborted' });
  socket.receive({ t: 'result', id: 2, result: { text: 'hi' } });
  assert.deepEqual(await newer, { ok: true, result: { text: 'hi' } });
  assert.deepEqual(failures, []);
});

// Pins: base#10
test('the owner closing sends cancel, quietly', async () => {
  const { call, failures } = await load();
  const owner = new AbortController();
  const pending = call('notes', 'get', {}, { signal: owner.signal });
  const socket = await welcome();
  owner.abort();
  assert.deepEqual(socket.sent.at(-1), { t: 'cancel', id: 1 });
  socket.receive({ t: 'error', id: 1, code: 'cancelled', message: 'notes/get was cancelled' });
  assert.deepEqual(await pending, { ok: false, why: 'aborted' });
  assert.deepEqual(failures, []);
});

test('a call cancelled after the backend finished it still comes back with its result', async () => {
  const { call, failures } = await load();
  const owner = new AbortController();
  const pending = call('notes', 'save', { text: 'kept' }, { signal: owner.signal });
  const socket = await welcome();
  owner.abort();
  assert.deepEqual(socket.sent.at(-1), { t: 'cancel', id: 1 });
  socket.receive({ t: 'result', id: 1, result: { text: 'kept' } });
  assert.deepEqual(await pending, { ok: true, result: { text: 'kept' } });
  assert.deepEqual(failures, []);
});

test('the socket closing fails its pending calls as aborted', async () => {
  const { call, failures } = await load();
  const pending = call('notes', 'get');
  const socket = await welcome();
  socket.closeNow(1006);
  assert.deepEqual(await pending, { ok: false, why: 'aborted' });
  assert.deepEqual(failures, []);
});

test('a socket that never opens is tried once more, then the failure is told', async () => {
  const { call, failures } = await load();
  const pending = call('notes', 'get');
  await settle();
  sockets[0]!.closeNow(1006);
  await settle();
  assert.equal(sockets.length, 2);
  sockets[1]!.closeNow(1006);
  assert.deepEqual(await pending, { ok: false, why: 'failed' });
  assert.equal(sockets.length, 2);
  assert.equal(failures.length, 1);
  assert.match(failures[0]!, /^notes\/get failed/);
});

test('an open socket that closes is reopened once, and the failure is told if that fails', async () => {
  const { call, failures } = await load();
  void call('notes', 'get');
  const socket = await welcome();
  socket.closeNow(1006);
  assert.equal(sockets.length, 2);
  sockets[1]!.closeNow(1006);
  await settle();
  assert.equal(sockets.length, 2);
  assert.equal(failures.length, 1);
  assert.match(failures[0]!, /couldn't reopen/);
});
