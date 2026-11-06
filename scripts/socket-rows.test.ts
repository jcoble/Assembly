import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { test } from 'node:test';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Socket } from 'node:net';
import { readRows, type Row } from './features/live-rows.ts';
import { logRows } from '../dotnet/scripts/row-log.ts';

type Frame = Record<string, any>;
type Peer = { path: string; socket: Socket; send: (frame: Frame) => void };
const token = 'test-only-secret';
const row = (seq: number, prevSeq: number, extra = {}): Row =>
  ({ seq, prevSeq, firstSeq: seq, kind: 'user', payload: { text: `row${seq}` }, ...extra });

// A real RFC6455 peer over Node's HTTP upgrade. Tests inspect actual requests and masked client frames.
// No production transport implementation or dependency is introduced.
async function fixture(handler: (peer: Peer, frame: Frame) => void) {
  const peers = new Set<Socket>();
  const frames: { path: string; frame: Frame }[] = [];
  const urls: string[] = [];
  const errors: unknown[] = [];
  let httpHits = 0;
  const server = createServer((_req, res) => { httpHits++; res.writeHead(404).end(); });
  server.on('upgrade', (req, socket: Socket, head) => {
    urls.push(req.url!); peers.add(socket);
    socket.on('close', () => peers.delete(socket));
    socket.on('error', () => {});
    const accept = createHash('sha1').update(req.headers['sec-websocket-key']! + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    const send = (frame: Frame) => {
      const data = Buffer.from(JSON.stringify(frame));
      const header = data.length < 126 ? Buffer.from([0x81, data.length]) : Buffer.from([0x81, 126, data.length >> 8, data.length & 255]);
      socket.write(Buffer.concat([header, data]));
    };
    let buffer = head;
    const receive = (data: Buffer) => {
      buffer = Buffer.concat([buffer, data]);
      for (;;) {
        if (buffer.length < 2) return;
        const opcode = buffer[0]! & 15;
        let length = buffer[1]! & 127;
        let offset = 2;
        if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4; }
        if (length === 127) { errors.push(new Error('unexpected oversized test frame')); socket.destroy(); return; }
        if (buffer.length < offset + 4 + length) return;
        const mask = buffer.subarray(offset, offset + 4); offset += 4;
        const payload = buffer.subarray(offset, offset + length);
        buffer = buffer.subarray(offset + length);
        for (let index = 0; index < payload.length; index++) payload[index] ^= mask[index % 4]!;
        if (opcode === 8) { socket.end(Buffer.from([0x88, 0])); return; }
        if (opcode !== 1) continue;
        try {
          const frame = JSON.parse(payload.toString());
          frames.push({ path: req.url!, frame });
          if (frame.t === 'hello') {
            assert.equal(frame.token, token); assert.equal(frame.protocol, 1);
            send({ t: 'welcome', protocol: 1, machine: 'workbox-test', format: 5, commit: 'a'.repeat(40) });
          } else handler({ path: req.url!, socket, send }, frame);
        } catch (error) { errors.push(error); socket.destroy(); }
      }
    };
    socket.on('data', receive);
    if (head.length) receive(Buffer.alloc(0));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const address = server.address() as { port: number };
  return {
    api: `http://127.0.0.1:${address.port}`, frames, peers, errors,
    async drained() { await Promise.all([...peers].map(socket => once(socket, 'close', { signal: AbortSignal.timeout(2000) }))); },
    async close() {
      const sockets = [...peers];
      await Promise.all(sockets.map(async socket => { const closed = once(socket, 'close'); socket.destroy(); await closed; }));
      server.close(); await once(server, 'close');
      assert.equal(httpHits, 0); assert.deepEqual(errors, []);
      assert.ok(urls.every(url => ['/socket', '/socket/bulk'].includes(url)));
      for (const { frame } of frames) if (frame.t !== 'hello') assert.ok(!JSON.stringify(frame).includes(token));
    }
  };
}
const result = (peer: Peer, frame: Frame, value: unknown) => peer.send({ t: 'result', id: frame.id, result: value });
const signal = () => AbortSignal.timeout(2000);

async function command(file: string, args: string[], env: NodeJS.ProcessEnv) {
  const child = spawn(file, args, { env: { ...process.env, ...env }, signal: signal(), stdio: ['ignore', 'pipe', 'pipe'] });
  let stdout = '', stderr = '';
  child.stdout.on('data', data => { stdout += data; });
  child.stderr.on('data', data => { stderr += data; });
  const [code] = await once(child, 'close');
  assert.ok(!stdout.includes(token)); assert.ok(!stderr.includes(token));
  return { code, stdout, stderr };
}

// The server sends live traffic before catch-up finishes, including an overlap in the bulk result.
test('snapshot cut, paged catch-up and concurrent live overlap preserve one ordered chain', async () => {
  let subscription: { peer: Peer; id: number };
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') {
      assert.equal(frame.topic, 'rows'); assert.equal(frame.key, 's'); assert.equal(frame.after, undefined);
      subscription = { peer, id: frame.id }; peer.send({ t: 'state', sub: frame.id, seq: 4, items: [] });
    } else if (frame.t === 'call') {
      assert.equal(peer.path, '/socket/bulk'); assert.equal(frame.feature, 'rows'); assert.equal(frame.method, 'after');
      assert.equal(frame.args.session, 's'); assert.equal(frame.args.limit, 500);
      if (frame.args.after === 0) result(peer, frame, { rows: [row(1, 0), row(3, 1)] });
      else {
        assert.equal(frame.args.after, 3);
        subscription.peer.send({ t: 'rows', sub: subscription.id, row: row(7, 4) });
        result(peer, frame, { rows: [row(4, 3), row(7, 4)] });
      }
    }
  });
  try {
    const received = await readRows({ api: host.api, token, session: 's', signal: signal() });
    assert.equal(received.cut, 4); assert.deepEqual(received.rows.map(r => r.seq), [1, 3, 4, 7]);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

for (const [name, rows] of [
  ['duplicate', [row(1, 0), row(1, 1)]], ['gap', [row(1, 0), row(3, 2)]],
  ['backwards', [row(2, 0), row(1, 2)]], ['incomplete', []]
] as const) test(`catch-up fails on ${name} instead of accepting silence`, async () => {
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, seq: 3, items: [] });
    if (frame.t === 'call') result(peer, frame, { rows });
  });
  try {
    await assert.rejects(readRows({ api: host.api, token, session: 's', signal: signal() }), /chain|stopped|backwards/);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

for (const mode of ['error', 'disconnect', 'abort'] as const) test(`snapshot ${mode} rejects and cleans up sockets`, async () => {
  const deadline = new AbortController();
  const host = await fixture((peer, frame) => {
    if (frame.t !== 'sub') return;
    if (mode === 'error') peer.send({ t: 'error', id: frame.id, code: 'unreachable', message: 'owner offline' });
    if (mode === 'disconnect') peer.socket.destroy();
    if (mode === 'abort') deadline.abort(new Error('requested cancellation'));
  });
  try {
    await assert.rejects(readRows({ api: host.api, token, session: 's', signal: deadline.signal }), /offline|closed|cancel|socket/i);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

test('row-log latest uses sessions/page, visual pages and chronological rows without HTTP', async () => {
  const items = [row(1, 0), row(4, 1)];
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, seq: 4, items: [] });
    if (frame.t !== 'call') return;
    if (frame.feature === 'sessions') {
      assert.equal(frame.method, 'page'); assert.deepEqual(frame.args, { limit: 1 });
      return result(peer, frame, { sessions: [{ sessionId: 's', repoUrl: '/safe/test' }] });
    }
    if (frame.method === 'after') return result(peer, frame, { rows: items });
    assert.equal(frame.method, 'page'); assert.equal(peer.path, '/socket/bulk');
    if (frame.args.limit === 500) return result(peer, frame, { rows: items, hasBefore: false });
    if (frame.args.before == null) result(peer, frame, { rows: [items[1]], hasBefore: true, before: 4 });
    else { assert.equal(frame.args.before, 4); result(peer, frame, { rows: [items[0]], hasBefore: false }); }
  });
  const lines: string[] = [];
  try {
    await logRows({ api: host.api, token, target: 'latest', followMs: 0, signal: signal(), log: line => lines.push(line) });
    assert.ok(lines.includes('\nAll checks passed.')); await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

test('keyed session lookup and requested follow deadline end an otherwise idle live stream', async () => {
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub' && frame.topic === 'sessions') {
      assert.equal(frame.key, 'older');
      peer.send({ t: 'state', sub: frame.id, items: [{ key: 'older', item: { sessionId: 'older', repoUrl: '/safe/test' } }] });
    } else if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, seq: 0, items: [] });
    else if (frame.t === 'call') result(peer, frame, { rows: [], hasBefore: false });
  });
  try {
    await logRows({ api: host.api, token, target: 'older', followMs: 10, signal: signal(), log: () => {} });
    await host.drained(); assert.equal(host.peers.size, 0);
    assert.ok(host.frames.some(({ frame }) => frame.t === 'unsub'));
  } finally { await host.close(); }
});

for (const mode of ['duplicate', 'gap'] as const) test(`live ${mode} fails during catch-up and releases both channels`, async () => {
  let live: { peer: Peer; id: number };
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') {
      live = { peer, id: frame.id }; peer.send({ t: 'state', sub: frame.id, seq: 1, items: [] });
    } else if (frame.t === 'call') {
      live.peer.send({ t: 'rows', sub: live.id, row: row(2, 1) });
      live.peer.send({ t: 'rows', sub: live.id, row: mode === 'duplicate' ? row(2, 1) : row(4, 3) });
      result(peer, frame, { rows: [row(1, 0)] });
    }
  });
  try {
    await assert.rejects(readRows({ api: host.api, token, session: 's', signal: signal() }), /live.*(seq|prevSeq)/);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

test('rows scenario waits for the requested run end, takes a fresh cut and checks resume', async () => {
  let subscriptions = 0;
  let live: { peer: Peer; id: number };
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') {
      subscriptions++;
      live = { peer, id: frame.id };
      peer.send({ t: 'state', sub: frame.id, seq: subscriptions === 1 ? 1 : 4, items: [] });
    } else if (frame.t === 'call') {
      assert.equal(frame.feature, 'rows'); assert.equal(frame.method, 'after');
      if (subscriptions === 1) {
        result(peer, frame, { rows: [row(1, 0, { kind: 'turn', runId: 'run', payload: { state: 'started' } })] });
        live.peer.send({ t: 'rows', sub: live.id, row: row(2, 1, { kind: 'turn', runId: 'other', payload: { state: 'finished' } }) });
        live.peer.send({ t: 'rows', sub: live.id, row: row(4, 2, { kind: 'turn', runId: 'run', payload: { state: 'finished' } }) });
      } else {
        const items = [row(1, 0), row(2, 1), row(4, 2)];
        result(peer, frame, { rows: items.filter(row => row.seq > frame.args.after) });
      }
    }
  });
  try {
    const checked = await command('bash', ['scripts/features/rows.sh'], {
      CHECK4_API: host.api, CHECK4_TOKEN: token, CHECK4_SESSION: 's', CHECK4_RUN: 'run'
    });
    assert.equal(checked.code, 0, checked.stderr);
    assert.match(checked.stdout, /ok the socket from 1 starts at 2 with prevSeq 1/);
    assert.equal(subscriptions, 3);
    assert.ok(host.frames.some(({ frame }) => frame.method === 'after' && frame.args.after === 1));
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

test('disconnect while waiting for turn end fails rather than reporting completed snapshot success', async () => {
  let live: Socket;
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') { live = peer.socket; peer.send({ t: 'state', sub: frame.id, seq: 1, items: [] }); }
    else if (frame.t === 'call') {
      result(peer, frame, { rows: [row(1, 0, { kind: 'turn', runId: 'run', payload: { state: 'started' } })] });
      live.destroy();
    }
  });
  try {
    await assert.rejects(readRows({ api: host.api, token, session: 's', untilRun: 'run', signal: signal() }), /closed|connection failed/);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

test('row-log rejects duplicated visual items rather than declaring stitched history valid', async () => {
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub' && frame.topic === 'sessions')
      peer.send({ t: 'state', sub: frame.id, items: [{ key: 's', item: { sessionId: 's', repoUrl: '/safe/test' } }] });
    else if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, seq: 0, items: [] });
    else if (frame.t === 'call') result(peer, frame, { rows: [row(1, 0), row(1, 0)], hasBefore: false });
  });
  try {
    await assert.rejects(logRows({ api: host.api, token, target: 's', followMs: 0, signal: signal(), log: () => {} }), /more than one page/);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

for (const valid of [true, false]) test(`live-rows CLI ${valid ? 'retains JSON output' : 'fails broken chains without a success receipt'}`, async () => {
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, seq: 2, items: [] });
    else if (frame.t === 'call') result(peer, frame, { rows: valid ? [row(1, 0), row(2, 1)] : [row(2, 1)] });
  });
  try {
    const checked = await command(process.execPath, ['scripts/features/live-rows.ts', '0'], {
      CHECK4_API: host.api, CHECK4_TOKEN: token, CHECK4_SESSION: 's'
    });
    assert.equal(checked.code, valid ? 0 : 1, checked.stderr);
    if (valid) assert.deepEqual(JSON.parse(checked.stdout), [{ seq: 1, prevSeq: 0 }, { seq: 2, prevSeq: 1 }]);
    else { assert.equal(checked.stdout, ''); assert.match(checked.stderr, /chain/); }
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});

test('row-log CLI retains latest, explicit env, port and follow arguments on isolated test data', async () => {
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, seq: 0, items: [] });
    else if (frame.t === 'call' && frame.feature === 'sessions') result(peer, frame, { sessions: [{ sessionId: 's', repoUrl: '/safe/test' }] });
    else if (frame.t === 'call') result(peer, frame, { rows: [], hasBefore: false });
  });
  const directory = await mkdtemp(join(tmpdir(), 'assembly-row-log-test-'));
  const envFile = join(directory, 'token-env');
  await writeFile(envFile, `ASSEMBLY_TOKEN=${token}\n`);
  try {
    const checked = await command(process.execPath, ['dotnet/scripts/row-log.ts', 'latest', '--env', envFile,
      '--port', new URL(host.api).port, '--follow', '0.01'], {});
    assert.equal(checked.code, 0, checked.stderr);
    assert.match(checked.stdout, /session s/); assert.match(checked.stdout, /All checks passed/);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); await rm(directory, { recursive: true }); }
});

test('a failed live stream cancels an in-flight bulk catch-up instead of waiting for the outer deadline', async () => {
  let live: Socket;
  const host = await fixture((peer, frame) => {
    if (frame.t === 'sub') { live = peer.socket; peer.send({ t: 'state', sub: frame.id, seq: 1, items: [] }); }
    else if (frame.t === 'call') live.destroy(); // Deliberately leave the bulk request unanswered.
  });
  try {
    await assert.rejects(readRows({ api: host.api, token, session: 's', signal: signal() }), /closed|connection failed/);
    await host.drained(); assert.equal(host.peers.size, 0);
  } finally { await host.close(); }
});
