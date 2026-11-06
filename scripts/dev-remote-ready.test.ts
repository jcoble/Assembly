import assert from 'node:assert/strict';
import { createServer, type Server } from 'node:net';
import { test } from 'node:test';
import { HostDown, HostUnseen, waitForAnswer } from './dev-remote-ready.ts';

const running = async () => ({ active: true, pid: '42' });
// The real kind of answer: a socket that takes the connection or refuses it.
const knock = (port: number) => () => new Promise<string>((resolve, reject) => {
  import('node:net').then(({ connect }) => {
    const socket = connect(port, '127.0.0.1');
    socket.once('connect', () => { socket.destroy(); resolve('welcome'); });
    socket.once('error', reject);
  });
});
const listen = (server: Server, port = 0) => new Promise<number>(resolve => server.listen(port, '127.0.0.1', () => resolve((server.address() as { port: number }).port)));

test('a host that starts answering later is ready, with no journal involved', async () => {
  const held = createServer();
  const port = await listen(held);
  await new Promise(resolve => held.close(resolve));
  const later = createServer();
  let asks = 0;
  const ask = knock(port);
  const answer = await waitForAnswer({ pid: '42', state: running, signal: new AbortController().signal, retryMs: 5, limitMs: 10_000,
    answer: async () => { if (++asks === 3) await listen(later, port); return ask(); } });
  await new Promise(resolve => later.close(resolve));
  assert.equal(answer, 'welcome');
  assert.ok(asks >= 3);
});

test('a host that has exited is reported as down at once, not after the wait', async () => {
  const started = Date.now();
  await assert.rejects(waitForAnswer({ pid: '42', state: async () => ({ active: false, pid: '0' }), signal: new AbortController().signal, retryMs: 5, limitMs: 60_000,
    answer: () => Promise.reject(Error('host socket unreachable')) }), (error: Error) => {
    assert.ok(error instanceof HostDown);
    assert.match(error.message, /failed to start/);
    return true;
  });
  assert.ok(Date.now() - started < 30_000);
});

test('a restarted host is a different process, so it is down too', async () => {
  await assert.rejects(waitForAnswer({ pid: '42', state: async () => ({ active: true, pid: '43' }), signal: new AbortController().signal, retryMs: 5, limitMs: 60_000,
    answer: () => Promise.reject(Error('host socket unreachable')) }), HostDown);
});

test('a running host the check cannot reach is not called down', async () => {
  await assert.rejects(waitForAnswer({ pid: '42', state: running, signal: new AbortController().signal, retryMs: 5, limitMs: 30,
    answer: () => Promise.reject(Error('host welcome timed out')) }), (error: Error) => {
    assert.ok(error instanceof HostUnseen);
    assert.doesNotMatch(error.message, /failed to start/);
    assert.match(error.message, /still running/);
    assert.match(error.message, /host welcome timed out/);
    return true;
  });
});

test('not being able to read the service is also not called down', async () => {
  await assert.rejects(waitForAnswer({ pid: '42', state: () => Promise.reject(Error('systemctl failed')), signal: new AbortController().signal, retryMs: 5, limitMs: 30,
    answer: () => Promise.reject(Error('host socket unreachable')) }), HostUnseen);
});

test('closing the owner stops the wait', async () => {
  const owner = new AbortController();
  const wait = waitForAnswer({ pid: '42', state: running, signal: owner.signal, retryMs: 60_000, limitMs: 120_000, answer: () => Promise.reject(Error('no')) });
  owner.abort();
  await assert.rejects(wait, /cancelled/);
});
