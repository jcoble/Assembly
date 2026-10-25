import assert from 'node:assert/strict';
import { test } from 'node:test';
import { libModule } from '../../runes.ts';

type Sent = { url: string; body: unknown; keepalive: boolean; signal: AbortSignal };
type Call = {
  call: <T>(feature: string, method: string, body?: Record<string, unknown>, options?: { key?: string; signal?: AbortSignal }) => Promise<T | undefined>;
  save: (feature: string, key: string, value: unknown) => void;
  onFailure: (handler: (message: string) => void) => void;
};

// A fresh call.ts, on a fake fetch: `answer` answers each request, or leaves it waiting until it's aborted.
let copies = 0;
async function load(answer?: (sent: Sent) => { status: number; json: unknown }) {
  const sent: Sent[] = [];
  const failures: string[] = [];
  const window = new EventTarget();
  Object.assign(globalThis, {
    window,
    fetch: (url: string, init: RequestInit) => {
      const request = { url, body: JSON.parse(String(init.body)), keepalive: !!init.keepalive, signal: init.signal! };
      sent.push(request);
      if (!answer) return new Promise((_, reject) => init.signal!.addEventListener('abort', () => reject(init.signal!.reason)));
      const { status, json } = answer(request);
      return Promise.resolve(new Response(JSON.stringify(json), { status }));
    },
  });
  // The fragment makes each load a new copy of the module.
  const backend = `${libModule('host/backend.ts')}#${++copies}`;
  const module = (await import(libModule('call.ts', { './debug.svelte': libModule('debug.svelte.ts'), './host/backend': backend }))) as Call;
  module.onFailure((message) => failures.push(message));
  return { sent, failures, window, ...module };
}

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

// Pins: (no base line yet: a newer load aborts the stale one)
test('a newer call with the same key aborts the older one, quietly', async () => {
  const { call, sent, failures } = await load();
  const older = call('preferences', 'get', {}, { key: 'preferences/get' });
  void call('preferences', 'get', {}, { key: 'preferences/get' });
  assert.equal(sent.length, 2);
  assert.equal(sent[0]!.signal.aborted, true);
  assert.equal(sent[1]!.signal.aborted, false);
  assert.equal(await older, undefined);
  assert.deepEqual(failures, []);
});

// Pins: (no base line yet: a newer load aborts the stale one)
test('the owner closing aborts its call, quietly', async () => {
  const { call, sent, failures } = await load();
  const owner = new AbortController();
  const pending = call('preferences', 'get', {}, { signal: owner.signal });
  owner.abort();
  assert.equal(await pending, undefined);
  assert.equal(sent[0]!.signal.aborted, true);
  assert.deepEqual(failures, []);
});

test('a failed call answers undefined and is told once, with the backend\'s error', async () => {
  const { call, failures } = await load(() => ({ status: 404, json: { error: 'preferences has no call nope' } }));
  assert.equal(await call('preferences', 'nope'), undefined);
  assert.deepEqual(failures, ['preferences/nope failed: preferences has no call nope']);
});

test('a call answers its result', async () => {
  const { call, sent } = await load(() => ({ status: 200, json: { result: { theme: 'dark' } } }));
  assert.deepEqual(await call('preferences', 'get'), { theme: 'dark' });
  assert.match(sent[0]!.url, /\/preferences\/get$/);
});

// Pins: base#7
test('saves within 300 ms of each other go as one call with the newest values', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { save, sent } = await load(() => ({ status: 200, json: { result: null } }));
  save('preferences', 'theme', 'dark');
  t.mock.timers.tick(200);
  save('preferences', 'theme', 'light');
  save('preferences', 'sessionsWidth', 280);
  t.mock.timers.tick(299);
  await settle();
  assert.equal(sent.length, 0);
  t.mock.timers.tick(1);
  await settle();
  assert.deepEqual(sent.map(({ url, body, keepalive }) => ({ url: url.replace(/^.*\/preferences/, '/preferences'), body, keepalive })), [
    { url: '/preferences/set', body: { values: { theme: 'light', sessionsWidth: 280 } }, keepalive: true },
  ]);
});

// Pins: base#7
test('a save still waiting when the window closes goes as it closes', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { save, sent, window } = await load(() => ({ status: 200, json: { result: null } }));
  save('preferences', 'theme', 'dark');
  t.mock.timers.tick(100);
  window.dispatchEvent(new Event('pagehide'));
  assert.equal(sent.length, 1);
  assert.deepEqual(sent[0]!.body, { values: { theme: 'dark' } });
  assert.equal(sent[0]!.keepalive, true);
  t.mock.timers.tick(300);
  await settle();
  assert.equal(sent.length, 1);
});
