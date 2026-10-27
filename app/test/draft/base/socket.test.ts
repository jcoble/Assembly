import assert from 'node:assert/strict';
import { test } from 'node:test';
import { jsModule, libModule } from '../../runes.ts';

type Outcome = { ok: true; result: unknown } | { ok: false; why: 'failed' | 'aborted' };
type Socket = {
  save: (feature: string, key: string, value: unknown) => Promise<Outcome>;
  call: (feature: string, method: string, args?: Record<string, unknown>, options?: { key?: string; signal?: AbortSignal }) => Promise<Outcome>;
  onFailure: (handler: (message: string) => void) => void;
  subscribe: <T>(topic: string, key: string | undefined, apply: { state: (items: T[]) => void; change: (item: T) => void }, signal?: AbortSignal) => void;
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
  assert.deepEqual(await older, { ok: false, why: 'aborted' });
  socket.receive({ t: 'result', id: 2, result: { text: 'new' } });
  assert.deepEqual(await newer, { ok: true, result: { text: 'new' } });
  assert.deepEqual(failures, []);
});

// Pins: base#10
test("an older call's result that arrives after a newer call took its key never reaches its caller", async () => {
  const { call, failures } = await load();
  const older = call('notes', 'get', {}, { key: 'notes/get' });
  const socket = await welcome();
  const newer = call('notes', 'get', {}, { key: 'notes/get' });
  await settle();
  // The backend had already finished the older one: its result arrives after the newer call was sent.
  socket.receive({ t: 'result', id: 1, result: { text: 'stale' } });
  assert.deepEqual(await older, { ok: false, why: 'aborted' });
  socket.receive({ t: 'result', id: 2, result: { text: 'new' } });
  assert.deepEqual(await newer, { ok: true, result: { text: 'new' } });

  // And when the older result comes after the newer one's.
  const third = call('notes', 'get', {}, { key: 'notes/get' });
  await settle();
  const fourth = call('notes', 'get', {}, { key: 'notes/get' });
  await settle();
  socket.receive({ t: 'result', id: 4, result: { text: 'newest' } });
  socket.receive({ t: 'result', id: 3, result: { text: 'stale' } });
  assert.deepEqual(await fourth, { ok: true, result: { text: 'newest' } });
  assert.deepEqual(await third, { ok: false, why: 'aborted' });
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

test('a superseded call reports nothing when the socket can\'t open', async () => {
  const { call, failures } = await load();
  const older = call('notes', 'get', {}, { key: 'notes/get' });
  const newer = call('notes', 'get', {}, { key: 'notes/get' });
  await settle();
  sockets[0]!.closeNow(1006);
  await settle();
  sockets[1]!.closeNow(1006);
  assert.deepEqual(await older, { ok: false, why: 'aborted' });
  assert.deepEqual(await newer, { ok: false, why: 'failed' });
  assert.equal(failures.length, 1);
});

type Setting = { key: string; value: string };

// A setting as a state entry or a change frame carries it: its key, and the item.
const entry = (key: string, value: string) => ({ key, item: { key, value } });

test('subscribe applies state and change to a map, and its owner closing sends unsub', async () => {
  const { subscribe } = await load();
  const values = new Map<string, string>();
  const owner = new AbortController();
  subscribe<Setting>(
    'settings',
    undefined,
    {
      state: (items) => {
        for (const item of items) values.set(item.key, item.value);
      },
      change: (item) => values.set(item.key, item.value),
    },
    owner.signal,
  );
  const socket = await welcome();
  assert.deepEqual(socket.sent.at(-1), { t: 'sub', id: 1, topic: 'settings' });
  socket.receive({ t: 'state', sub: 1, items: [entry('theme', 'dark')] });
  socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry('theme', 'light') });
  socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry('font', 'mono') });
  assert.deepEqual([...values], [['theme', 'light'], ['font', 'mono']]);

  owner.abort();
  assert.deepEqual(socket.sent.at(-1), { t: 'unsub', id: 1 });
  socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry('theme', 'late') });
  assert.equal(values.get('theme'), 'light');
});

test('a reopened socket subscribes again', async () => {
  const { subscribe } = await load();
  const states: Setting[][] = [];
  subscribe<Setting>('settings', 'theme', { state: (items) => states.push(items), change: () => {} });
  const socket = await welcome();
  assert.deepEqual(socket.sent.at(-1), { t: 'sub', id: 1, topic: 'settings', key: 'theme' });
  socket.closeNow(1006);
  const reopened = sockets[1]!;
  reopened.open();
  reopened.receive({ t: 'welcome', protocol: 1, machine: 'test-machine' });
  await settle();
  assert.deepEqual(reopened.sent.at(-1), { t: 'sub', id: 1, topic: 'settings', key: 'theme' });
  reopened.receive({ t: 'state', sub: 1, items: [entry('theme', 'dark')] });
  assert.deepEqual(states, [[{ key: 'theme', value: 'dark' }]]);
});

// A window: a fresh socket.ts subscribed to settings with no key, as preferences are, and the values it shows.
async function window() {
  const page = await load();
  const values = new Map<string, string>();
  const shown: string[] = [];
  const show = (item: Setting) => {
    values.set(item.key, item.value);
    shown.push(`${item.key}=${item.value}`);
  };
  page.subscribe<Setting>('settings', undefined, { state: (items) => items.forEach(show), change: show });
  const socket = await welcome();
  socket.receive({ t: 'state', sub: 1, items: [] });
  return { ...page, socket, values, shown };
}

// The backend commits a save: the change to every window, and the saver's result after it.
function commit(windows: { socket: FakeSocket }[], saver: { socket: FakeSocket }, id: number, key: string, value: string) {
  for (const w of windows) w.socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry(key, value) });
  saver.socket.receive({ t: 'result', id, result: null });
}

test('each save() sends exactly one call', async () => {
  const a = await window();
  void a.save('settings', 'theme', 'dark');
  await settle();
  assert.deepEqual(a.socket.sent.slice(2), [{ t: 'call', id: 2, feature: 'settings', method: 'set', args: { key: 'theme', value: 'dark' } }]);
});

test('two windows saving one key: commits A1, B1, A2 leave both on A2, and A1, A2, B1 leave both on B1', async () => {
  for (const [order, last] of [[['A1', 'B1', 'A2'], 'A2'], [['A1', 'A2', 'B1'], 'B1']] as const) {
    const a = await window();
    const b = await window();
    const ids: Record<string, number> = {};
    for (const value of ['A1', 'A2']) void a.save('settings', 'theme', value);
    void b.save('settings', 'theme', 'B1');
    await settle();
    ids.A1 = 2;
    ids.A2 = 3;
    ids.B1 = 2;
    for (const value of order) commit([a, b], value.startsWith('A') ? a : b, ids[value]!, 'theme', value);
    await settle();
    assert.equal(a.values.get('theme'), last, `A after ${order}`);
    assert.equal(b.values.get('theme'), last, `B after ${order}`);
  }
});

test("a failed save, then a successful one, then another window's change: the window ends on the other window's value", async () => {
  const a = await window();
  const failed = a.save('settings', 'theme', 'bad');
  await settle();
  a.socket.receive({ t: 'error', id: 2, code: 'failed', message: 'refused' });
  assert.deepEqual(await failed, { ok: false, why: 'failed' });
  // Read back: subscribed again, and the committed value comes back as its state.
  assert.deepEqual(a.socket.sent.slice(-2), [{ t: 'unsub', id: 1 }, { t: 'sub', id: 1, topic: 'settings' }]);
  a.socket.receive({ t: 'state', sub: 1, items: [entry('theme', 'dark')] });
  assert.equal(a.values.get('theme'), 'dark');

  void a.save('settings', 'theme', 'light');
  await settle();
  commit([a], a, 3, 'theme', 'light');
  a.socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry('theme', 'other') });
  await settle();
  assert.equal(a.values.get('theme'), 'other');
});

test("A1, B1, A2 with A2's answer right after its change: the window never shows B1 after A2", async () => {
  const a = await window();
  void a.save('settings', 'theme', 'A1');
  void a.save('settings', 'theme', 'A2');
  await settle();
  commit([a], a, 2, 'theme', 'A1');
  a.socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry('theme', 'B1') });
  commit([a], a, 3, 'theme', 'A2');
  await settle();
  assert.equal(a.values.get('theme'), 'A2');
  assert.ok(!a.shown.slice(a.shown.indexOf('theme=A2')).includes('theme=B1'), a.shown.join(', '));
});

test("a read-back's state doesn't overwrite a key with a save still in flight", async () => {
  const a = await window();
  void a.save('settings', 'font', 'mono');
  const failed = a.save('settings', 'theme', 'bad');
  await settle();
  // font commits first; theme fails, and its read-back's state has both.
  a.socket.receive({ t: 'change', sub: 1, op: 'upsert', ...entry('font', 'mono') });
  a.socket.receive({ t: 'error', id: 3, code: 'failed', message: 'refused' });
  await failed;
  a.socket.receive({ t: 'state', sub: 1, items: [entry('theme', 'dark'), entry('font', 'serif')] });
  assert.equal(a.values.get('theme'), 'dark');
  assert.equal(a.values.get('font'), undefined);
  // font's answer: its newest held value, the state's, is applied. The state was read after font's commit.
  a.socket.receive({ t: 'result', id: 2, result: null });
  await settle();
  assert.equal(a.values.get('font'), 'serif');
});
