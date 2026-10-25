import assert from 'node:assert/strict';
import { test } from 'node:test';
import { libModule } from '../../runes.ts';

type Sent = { url: string; body: unknown; keepalive: boolean; signal: AbortSignal };
type Outcome = { ok: true; result: unknown } | { ok: false; why: 'failed' | 'aborted' };
type Call = {
  call: (feature: string, method: string, body?: Record<string, unknown>, options?: { key?: string; signal?: AbortSignal }) => Promise<Outcome>;
  save: (feature: string, key: string, value: unknown) => void;
  onFailure: (handler: (message: string) => void) => void;
};

// A fresh call.ts, on a fake fetch. `answer` answers each request (now, or as a promise); without it a request waits
// until it's aborted.
type Answer = { status: number; json: unknown };
let copies = 0;
async function load(answer?: (sent: Sent) => Answer | Promise<Answer>) {
  const sent: Sent[] = [];
  const failures: string[] = [];
  const window = new EventTarget();
  const document = Object.assign(new EventTarget(), { visibilityState: 'visible' });
  Object.assign(globalThis, {
    window,
    document,
    fetch: async (url: string, init: RequestInit) => {
      const request = { url, body: JSON.parse(String(init.body)), keepalive: !!init.keepalive, signal: init.signal! };
      sent.push(request);
      if (!answer) return new Promise((_, reject) => init.signal!.addEventListener('abort', () => reject(init.signal!.reason)));
      const { status, json } = await answer(request);
      return new Response(JSON.stringify(json), { status });
    },
  });
  // The fragment makes each load a new copy of the module.
  const backend = `${libModule('host/backend.ts')}#${++copies}`;
  const module = (await import(libModule('call.ts', { './debug.svelte': libModule('debug.svelte.ts'), './host/backend': backend }))) as Call;
  module.onFailure((message) => failures.push(message));
  return { sent, failures, window, document, ...module };
}

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

// Pins: base#10
test('a newer call with the same key aborts the older one, quietly', async () => {
  const { call, sent, failures } = await load();
  const older = call('preferences', 'get', {}, { key: 'preferences/get' });
  void call('preferences', 'get', {}, { key: 'preferences/get' });
  assert.equal(sent.length, 2);
  assert.equal(sent[0]!.signal.aborted, true);
  assert.equal(sent[1]!.signal.aborted, false);
  assert.deepEqual(await older, { ok: false, why: 'aborted' });
  assert.deepEqual(failures, []);
});

// Pins: base#10
test('the owner closing aborts its call, quietly', async () => {
  const { call, sent, failures } = await load();
  const owner = new AbortController();
  const pending = call('preferences', 'get', {}, { signal: owner.signal });
  owner.abort();
  assert.deepEqual(await pending, { ok: false, why: 'aborted' });
  assert.equal(sent[0]!.signal.aborted, true);
  assert.deepEqual(failures, []);
});

test('a failed call says so and is told once, with the backend\'s error', async () => {
  const { call, failures } = await load(() => ({ status: 404, json: { error: 'preferences has no call nope' } }));
  assert.deepEqual(await call('preferences', 'nope'), { ok: false, why: 'failed' });
  assert.deepEqual(failures, ['preferences/nope failed: preferences has no call nope']);
});

test('a call answers its result', async () => {
  const { call, sent } = await load(() => ({ status: 200, json: { result: { theme: 'dark' } } }));
  assert.deepEqual(await call('preferences', 'get'), { ok: true, result: { theme: 'dark' } });
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

// Pins: base#7
test('a feature has one save on the way at a time, so an older one never lands after a newer one', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const answers: (() => void)[] = [];
  const { save, sent } = await load(() => new Promise((resolve) => answers.push(() => resolve({ status: 200, json: { result: null } }))));
  save('preferences', 'theme', 'dark');
  t.mock.timers.tick(300);
  await settle();
  save('preferences', 'theme', 'light');
  t.mock.timers.tick(300);
  await settle();
  assert.equal(sent.length, 1);
  answers[0]!();
  await settle();
  await settle();
  assert.deepEqual(sent.map((s) => s.body), [{ values: { theme: 'dark' } }, { values: { theme: 'light' } }]);
});

// Pins: base#7
test('a save still waiting when the window goes to the background goes then', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { save, sent, document } = await load(() => ({ status: 200, json: { result: null } }));
  save('preferences', 'theme', 'dark');
  document.visibilityState = 'hidden';
  document.dispatchEvent(new Event('visibilitychange'));
  assert.deepEqual(sent.map((s) => [s.body, s.keepalive]), [[{ values: { theme: 'dark' } }, true]]);
});
