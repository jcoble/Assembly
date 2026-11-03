import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { test } from 'node:test';

// RFC6455 server fixture; the production CLI is the real WebSocket client.
function encode(value: unknown) {
  const body = Buffer.from(JSON.stringify(value));
  const header = body.length < 126 ? Buffer.from([0x81, body.length]) : Buffer.from([0x81, 126, body.length >> 8, body.length & 255]);
  return Buffer.concat([header, body]);
}
type Frame = { t: string; id: number; feature?: string; method?: string; args?: any; token?: string; protocol?: number; topic?: string; key?: string; after?: number };
async function websocketServer(receive: (path: string, frame: Frame, send: (value: unknown) => void, peer: any) => void) {
  const peers = new Set<any>();
  const frames: { path: string; frame: Frame }[] = [];
  const requests: { path: string; authorization?: string }[] = [];
  let httpHits = 0;
  const server = createServer((_request, response) => { httpHits++; response.writeHead(404).end(); });
  server.on('upgrade', (request, peer, head) => {
    peers.add(peer);
    peer.on('close', () => peers.delete(peer));
    peer.on('end', () => peer.end());
    peer.on('error', () => {});
    requests.push({ path: request.url!, authorization: request.headers.authorization });
    const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    peer.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    let buffered = head;
    const consume = (data: Buffer) => {
      buffered = Buffer.concat([buffered, data]);
      while (buffered.length >= 2) {
        const opcode = buffered[0] & 15;
        let length = buffered[1] & 127, offset = 2;
        assert(buffered[1] & 128, 'client frames are masked');
        assert.notEqual(length, 127, 'test client does not send an oversized frame');
        if (length === 126) { if (buffered.length < 4) return; length = buffered.readUInt16BE(2); offset = 4; }
        if (buffered.length < offset + 4 + length) return;
        const mask = buffered.subarray(offset, offset + 4);
        const body = Buffer.from(buffered.subarray(offset + 4, offset + 4 + length));
        buffered = buffered.subarray(offset + 4 + length);
        for (let i = 0; i < body.length; i++) body[i] ^= mask[i % 4];
        if (opcode === 8) { peer.end(Buffer.from([0x88, 0])); return; }
        assert.equal(opcode, 1);
        const frame = JSON.parse(body.toString());
        frames.push({ path: request.url!, frame });
        receive(request.url!, frame, value => peer.write(encode(value)), peer);
      }
    };
    peer.on('data', consume);
    consume(Buffer.alloc(0));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address() as { port: number };
  return {
    base: `http://127.0.0.1:${port}`, frames, requests, peers,
    get httpHits() { return httpHits; },
    async drained() { await Promise.all([...peers].map(peer => once(peer, 'close'))); assert.equal(peers.size, 0); },
    async close() { for (const peer of peers) peer.destroy(); await new Promise<void>(resolve => server.close(() => resolve())); },
  };
}
const welcome = (machine: string) => ({ t: 'welcome', protocol: 1, machine, format: 5, commit: 'a'.repeat(40) });
const owner = 'case-owner', repo = '/safe/fixture/repository';
const hostToken = 'private-host-token', desktopToken = 'private-desktop-token';
const remoteId = 'assembly-remote-terminal://workbox-test/fixture';
type Scenario = { create?: 'reject' | 'disconnect' | 'wrong-format'; gap?: boolean; exit?: number; countLeak?: boolean };

async function scenario(options: Scenario = {}) {
  let sub = 0, localClosed = false, remoteClosed = false, remoteStarted = false;
  const ready = Buffer.from('terminal-ready\n');
  const output = Buffer.from('input:terminal-input\n40 120\n');
  const host = await websocketServer((path, frame, send) => {
    assert.equal(path, '/socket/terminal');
    if (frame.t === 'hello') { assert.equal(frame.token, hostToken); assert.equal(frame.protocol, 1); send(welcome('workbox-test')); return; }
    if (frame.t === 'sub') {
      assert.deepEqual({ topic: frame.topic, key: frame.key, after: frame.after }, { topic: 'terminal', key: 'local-terminal', after: 0 });
      sub = frame.id; send({ t: 'state', sub, items: [] }); return;
    }
    assert.equal(frame.feature, 'terminal');
    const result = (value: unknown) => send({ t: 'result', id: frame.id, result: value });
    switch (frame.method) {
      case 'start': assert.equal(frame.args.owner, owner); result({ id: 'local-terminal', owner }); break;
      case 'read':
        if (localClosed) send({ t: 'error', id: frame.id, code: 'failed', message: 'unknown terminal' });
        else result({ offset: 0, end: ready.length, data: ready.toString('base64') });
        break;
      case 'resize': assert.equal(frame.args.cols, 120); assert.equal(frame.args.rows, 40); result({}); break;
      case 'write':
        assert.equal(Buffer.from(frame.args.data, 'base64').toString(), 'terminal-input\n');
        result({});
        send({ t: 'change', sub, item: { prevSeq: ready.length + (options.gap ? 1 : 0), seq: ready.length + output.length, data: output.toString('base64') } });
        send({ t: 'change', sub, item: { prevSeq: ready.length + output.length, seq: ready.length + output.length + 1, exit: options.exit ?? 7 } });
        break;
      case 'list': result(localClosed && !options.countLeak ? [] : [{ id: 'local-terminal', exitCode: options.exit ?? 7 }]); break;
      case 'close': localClosed = true; result({}); break;
      default: assert.fail(`unexpected host terminal call ${frame.method}`);
    }
  });
  const desktop = await websocketServer((path, frame, send, peer) => {
    assert(['/socket', '/socket/terminal'].includes(path));
    if (frame.t === 'hello') {
      assert.equal(frame.token, desktopToken); assert.equal(frame.protocol, 1);
      send({ ...welcome('desktop'), ...(options.create === 'wrong-format' && path === '/socket' ? { format: 4 } : {}) }); return;
    }
    const result = (value: unknown) => send({ t: 'result', id: frame.id, result: value });
    if (path === '/socket') {
      assert.equal(frame.feature, 'sessions'); assert.equal(frame.method, 'create');
      assert.deepEqual(frame.args, { agent: 'test-provider', repoUrl: repo, machine: 'workbox-test' });
      if (options.create === 'disconnect') { peer.destroy(); return; }
      if (options.create === 'reject') { send({ t: 'error', id: frame.id, code: 'failed', message: 'remote create refused' }); return; }
      result({ sessionId: 'remote-owner' }); return;
    }
    assert.equal(frame.feature, 'terminal');
    switch (frame.method) {
      case 'list': result(remoteStarted && !remoteClosed ? [{ id: remoteId }] : []); break;
      case 'start':
        if (frame.args.owner.startsWith('terminal-unknown-')) {
          send({ t: 'error', id: frame.id, code: 'failed', message: 'unknown terminal owner' }); return;
        }
        assert.equal(frame.args.owner, 'remote-owner'); remoteStarted = true; result({ id: remoteId }); break;
      case 'close': assert.equal(frame.args.terminal, remoteId); remoteClosed = true; result({}); break;
      default: assert.fail(`unexpected desktop terminal call ${frame.method}`);
    }
  });
  const child = spawn(process.execPath, [new URL('./features/terminal.ts', import.meta.url).pathname], {
    env: { ...process.env, CHECK4_HOST_API: host.base, CHECK4_HOST_TOKEN: hostToken, CHECK4_API: desktop.base,
      CHECK4_TOKEN: desktopToken, CHECK4_AGENT: 'test-provider', CHECK4_SESSION: owner, CHECK4_REPO: repo, CHECK4_MACHINE: 'workbox-test' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let stdout = '', stderr = '';
  child.stdout.on('data', data => stdout += data);
  child.stderr.on('data', data => stderr += data);
  try {
    const [code] = await once(child, 'close');
    await Promise.all([host.drained(), desktop.drained()]);
    assert.equal(stderr, '');
    for (const token of [hostToken, desktopToken]) {
      assert.ok(!child.spawnargs.join(' ').includes(token));
      assert.ok(!stdout.includes(token));
      assert.ok(!JSON.stringify([...host.requests, ...desktop.requests]).includes(token));
      assert.ok(!JSON.stringify([...host.frames, ...desktop.frames].filter(({ frame }) => frame.t !== 'hello')).includes(token));
    }
    assert.equal(host.httpHits + desktop.httpHits, 0);
    assert([...host.requests, ...desktop.requests].every(request => request.authorization === undefined));
    return { code, stdout, hostFrames: host.frames, desktopFrames: desktop.frames };
  } finally { await Promise.all([host.close(), desktop.close()]); }
}

test('terminal CLI preserves lifecycle and creates the remote owner through desktop control socket', { timeout: 5000 }, async () => {
  const result = await scenario();
  assert.equal(result.code, 0, result.stdout);
  assert.equal(result.stdout.split('\n').filter(line => line.startsWith('ok ')).length, 6);
  assert.match(result.stdout, /ok remote shell routes through the desktop and list\/start reuse it/);
  assert.equal(result.desktopFrames.filter(({ path, frame }) => path === '/socket' && frame.method === 'create').length, 1);
  assert.equal(result.desktopFrames.filter(({ frame }) => frame.method === 'start' && frame.args.owner.startsWith('terminal-unknown-')).length, 2);
});

for (const create of ['reject', 'disconnect', 'wrong-format'] as const) test(`remote owner ${create} is a real failure with sockets closed`, { timeout: 5000 }, async () => {
  const result = await scenario({ create });
  assert.equal(result.code, 1, result.stdout);
  assert.match(result.stdout, /fail terminal lifecycle:/);
  assert.doesNotMatch(result.stdout, /ok remote shell routes/);
  if (create === 'reject') assert.match(result.stdout, /remote create refused/);
});

for (const [label, options, expected] of [
  ['byte sequence gap', { gap: true }, /output has no gaps or repeated bytes/],
  ['wrong exit', { exit: 8 }, /8 !== 7/],
  ['terminal leaked after close', { countLeak: true }, /fail terminal lifecycle/],
] as const) test(`existing ${label} assertion still fails`, { timeout: 5000 }, async () => {
  const result = await scenario(options);
  assert.equal(result.code, 1, result.stdout);
  assert.match(result.stdout, expected);
});
