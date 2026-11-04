import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { spawn, spawnSync } from 'node:child_process';
import { watch } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Socket } from 'node:net';
import { runCase } from './check4-case.ts';
import { connectSocket } from './features/socket.ts';

const commit = 'a'.repeat(40), token = 'private-fixture-token';
type Peer = { path: string; send(frame: any, split?: boolean): void; drop(): void };
// A real RFC6455 endpoint using Node's HTTP upgrade, including masked client frames and close handshakes.
async function server(handler: (peer: Peer, frame: any) => void, machine = 'fixture-host', build: string | null | undefined = commit) {
  const peers = new Set<Socket>();
  const frames: { path: string; frame: any }[] = [];
  let httpHits = 0;
  const http = createServer((_req, res) => { httpHits++; res.writeHead(404).end(); });
  http.on('upgrade', (request, socket) => {
    peers.add(socket);
    socket.on('close', () => peers.delete(socket));
    socket.on('error', () => {});
    const accept = createHash('sha1').update(`${request.headers['sec-websocket-key']}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    const peer: Peer = {
      path: request.url!,
      send(frame, split = false) {
        const body = Buffer.from(JSON.stringify(frame));
        const header = body.length < 126 ? Buffer.from([0x81, body.length]) : Buffer.from([0x81, 126, body.length >> 8, body.length & 255]);
        const packet = Buffer.concat([header, body]);
        if (split) { socket.write(packet.subarray(0, 3)); socket.write(packet.subarray(3)); }
        else socket.write(packet);
      },
      drop() { socket.destroy(); }
    };
    let input = Buffer.alloc(0);
    socket.on('data', bytes => {
      input = Buffer.concat([input, bytes]);
      while (input.length >= 2) {
        const opcode = input[0]! & 15, masked = (input[1]! & 128) !== 0;
        let length = input[1]! & 127, head = 2;
        if (length === 126) { if (input.length < 4) return; length = input.readUInt16BE(2); head = 4; }
        if (length === 127) { socket.destroy(); return; }
        if (input.length < head + (masked ? 4 : 0) + length) return;
        const mask = input.subarray(head, head + 4); if (masked) head += 4;
        const body = Buffer.from(input.subarray(head, head + length));
        input = input.subarray(head + length);
        if (masked) for (let i = 0; i < body.length; i++) body[i] ^= mask[i % 4]!;
        if (opcode === 8) { socket.end(Buffer.from([0x88, 0])); return; }
        if (opcode !== 1) continue;
        const frame = JSON.parse(body.toString());
        frames.push({ path: request.url!, frame });
        if (frame.t === 'hello') {
          assert.equal(frame.token, token);
          peer.send({ t: 'welcome', protocol: 1, machine, format: 5, ...(build === undefined ? {} : { commit: build }) }, true);
        } else handler(peer, frame);
      }
    });
  });
  await new Promise<void>(resolve => http.listen(0, '127.0.0.1', resolve));
  const address = http.address() as { port: number };
  return {
    api: `http://127.0.0.1:${address.port}`, frames, async idle() { await Promise.all([...peers].map(peer => once(peer, 'close'))); }, get httpHits() { return httpHits; }, get openPeers() { return peers.size; },
    async close() { for (const peer of peers) peer.destroy(); await new Promise<void>((resolve, reject) => http.close(error => error ? reject(error) : resolve())); }
  };
}

async function fixture(t: any, mode = 'pass', machine = 'workbox-test') {
  const out = await mkdtemp(join(tmpdir(), 'check4-case-'));
  t.after(() => rm(out, { recursive: true, force: true }));
  let rowsPeer: Peer, rowSub = 0, ownerSub = 0, ownerPeer: Peer;
  const row = (seq: number, kind: string, payload: any) => ({ sessionId: 'session-1', runId: 'run-1', seq, prevSeq: seq - 1, kind, payload });
  const host = await server((peer, frame) => {
    if (frame.t === 'call' && frame.feature === 'check4') peer.send({ t: 'error', id: frame.id, code: mode === 'feature-error' ? 'failed' : 'not_found' });
    if (frame.t === 'sub' && frame.topic === 'sessions') {
      ownerPeer = peer; ownerSub = frame.id;
      peer.send({ t: 'state', sub: frame.id, items: [{ key: 'session-1', item: { sessionId: 'session-1', acpSessionId: null, activeRunId: 'run-1' } }] });
    }
  });
  t.after(() => host.close());
  const local = await server((peer, frame) => {
    if (frame.t === 'call') {
      if (frame.feature === 'check4') { peer.send({ t: 'error', id: frame.id, code: 'not_found' }); return; }
      if (frame.feature === 'projects') { peer.send({ t: 'result', id: frame.id, result: { project: { id: 'fixture-project' } } }); return; }
      if (frame.method === 'create') { peer.send({ t: 'result', id: frame.id, result: { sessionId: 'session-1' } }); return; }
      if (frame.method === 'after') {
        const seq = frame.args.after + 1;
        peer.send({ t: 'result', id: frame.id, result: { rows: [{ ...row(seq, 'user', {}), runId: 'older' }] } });
        return;
      }
      if (frame.method === 'send') {
        if (mode === 'error') { peer.send({ t: 'error', id: frame.id, code: 'unreachable', message: 'Workbox Test disconnected' }); return; }
        if (mode === 'disconnect') { peer.drop(); return; }
        const offset = mode === 'catchup' || mode === 'unrelated' ? 2 : 0;
        if (mode === 'unrelated') {
          rowsPeer.send({ t: 'rows', sub: rowSub, row: { ...row(1, 'user', { clientId: 'another-client' }), runId: 'unrelated-run' } });
          rowsPeer.send({ t: 'rows', sub: rowSub, row: { ...row(2, 'turn', { state: 'finished' }), runId: 'unrelated-run' } });
        }
        const accepted = { sessionId: 'session-1', messageId: `prompt/session-1/${frame.args.clientId}`, clientId: frame.args.clientId,
          state: 'queued', messageVersion: 1, seq: 1, epoch: 0, runId: null };
        if (mode === 'invalid-ack') accepted.clientId = 'wrong-client';
        if (mode === 'legacy-ack') Object.assign(accepted, { runId: 'run-1' });
        const ack = () => peer.send({ t: 'result', id: frame.id, result: accepted });
        if (mode === 'ack-first') ack();
        rowsPeer.send({ t: 'rows', sub: rowSub, row: { ...row(1 + offset, 'user', { clientId: mode === 'uncorrelated' ? 'another-client' : frame.args.clientId }), ...(mode === 'missing-run' ? { runId: null } : {}) } });
        rowsPeer.send({ t: 'rows', sub: rowSub, row: row(2 + offset, 'turn', { state: 'started' }) });
        rowsPeer.send({ t: 'rows', sub: rowSub, row: row(3 + offset, 'message', { blocks: [{ value: 'fixture-host\nfour-way-ok' }] }) }, true);
        rowsPeer.send({ t: 'rows', sub: rowSub, row: row(mode === 'gap' ? 5 : 4 + offset, 'turn', { state: mode === 'failed-turn' ? 'failed' : 'finished' }) });
        // Pushed rows precede the send result; owner identity follows the completed turn.
        if (mode !== 'ack-first') ack();
        ownerPeer.send({ t: 'change', sub: ownerSub, key: 'session-1', item: { sessionId: 'session-1', acpSessionId: 'owner-provider-session', activeRunId: null } });
        if (mode === 'uncorrelated') rowsPeer.drop();
      }
    } else if (frame.t === 'sub' && frame.topic === 'rows') {
      rowsPeer = peer; rowSub = frame.id; peer.send({ t: 'state', sub: frame.id, seq: mode === 'catchup' ? 2 : 0, items: [] });
    } else if (frame.t === 'sub' && frame.topic === 'sessions') {
      if (machine === 'local') { ownerPeer = peer; ownerSub = frame.id; }
      // A deliberately wrong cache: a remote case must never use this for its transcript identity.
      peer.send({ t: 'state', sub: frame.id, items: [{ key: 'session-1', item: { sessionId: 'session-1', acpSessionId: 'wrong-cache', activeRunId: null } }] });
    }
  });
  t.after(() => local.close());
  return { local, host, out, env: {
    ...process.env, CHECK4_API: local.api, CHECK4_TOKEN: token, CHECK4_HOST_API: machine === 'local' ? local.api : host.api, CHECK4_HOST_TOKEN: token,
    CHECK4_HOST_NAME: 'fixture-host', CHECK4_AGENT: 'codex', CHECK4_MACHINE: machine, CHECK4_REPO: '/safe/fixture',
    CHECK4_COMMIT: commit, CHECK4_HOST_COMMIT: commit, CHECK4_CASE: 'codex-workbox-test', CHECK4_OUT: out, CHECK4_FEATURES: '["socket"]'
  } };
}

test('remote case uses exact calls, subscribes before send and preserves split/pushed rows and owner identity', async t => {
  const f = await fixture(t);
  const result = await runCase(f.env);
  assert.equal(result.reason, '');
  assert.equal(result.state, 'finished');
  assert.equal(result.acpSessionId, 'owner-provider-session');
  assert.match(result.reply, /four-way-ok/);
  assert.equal(result.features.length, 1);
  assert.equal(result.features[0]!.name, 'socket');
  assert.equal(result.features[0]!.pass, true);
  const sent = f.local.frames.map(entry => entry.frame);
  const create = sent.find(frame => frame.method === 'create');
  assert.deepEqual(sent.find(frame => frame.feature === 'projects').args, { machine: 'workbox-test', path: '/safe/fixture' });
  const { clientId, ...asked } = create.args;
  assert.match(clientId, /^[a-f0-9-]{36}$/);
  assert.deepEqual(asked, { agent: 'codex', projectId: 'fixture-project', machine: 'workbox-test' });
  const sending = sent.find(frame => frame.method === 'send');
  assert.match(sending.args.clientId, /^[a-f0-9-]{36}$/);
  assert(sent.findIndex(frame => frame.topic === 'rows') < sent.indexOf(sending));
  assert.equal(f.local.frames.filter(entry => entry.frame.t === 'hello').length, 2);
  assert.equal(f.host.frames.filter(entry => entry.frame.t === 'hello').length, 2); // owner identity plus the socket feature
  for (const entry of [...f.local.frames, ...f.host.frames]) {
    assert(!entry.path.includes(token));
    if (entry.frame.t !== 'hello') assert(!JSON.stringify(entry.frame).includes(token));
  }
  assert.equal(f.local.httpHits + f.host.httpHits, 0);
  await Promise.all([f.local.idle(), f.host.idle()]);
  assert.equal(f.local.openPeers + f.host.openPeers, 0);
  assert.equal(JSON.parse(await readFile(join(f.out, 'codex-workbox-test.rows.json'), 'utf8')).length, 4);
});

for (const [mode, reason] of [['error', /unreachable/], ['disconnect', /closed|connection failed/], ['gap', /gap/], ['failed-turn', /ended failed/]] as const) {
  test(`${mode} fails with its reason, preserves rows and closes every connection`, async t => {
    const f = await fixture(t, mode);
    const result = await runCase(f.env);
    assert.match(result.reason, reason);
    await Promise.all([f.local.idle(), f.host.idle()]);
  assert.equal(f.local.openPeers + f.host.openPeers, 0);
    const kept = JSON.parse(await readFile(join(f.out, 'codex-workbox-test.rows.json'), 'utf8'));
    assert.equal(kept.length, mode === 'gap' ? 3 : mode === 'failed-turn' ? 4 : 0);
    assert(!JSON.stringify(result).includes(token));
  });
}

test('welcome rejects an unknown build and the wrong owning machine', async t => {
  for (const [machine, build, expected] of [['fixture-host', null, /unknown or stale/], ['other-host', commit, /wrong machine/]] as const) {
    const host = await server(() => {}, machine, build);
    t.after(() => host.close());
    await assert.rejects(connectSocket(host.api, token, { signal: new AbortController().signal, machine: 'fixture-host', commit }), expected);
    await host.idle();
    assert.equal(host.openPeers, 0);
  }
});

test('requests correlate out-of-order replies and disposal rejects pending subscriptions', async t => {
  let first: number;
  const host = await server((peer, frame) => {
    if (frame.t === 'sub') peer.send({ t: 'state', sub: frame.id, items: [] });
    else if (!first) first = frame.id;
    else { peer.send({ t: 'result', id: frame.id, result: 'second' }); peer.send({ t: 'result', id: first, result: 'first' }); }
  });
  t.after(() => host.close());
  const socket = await connectSocket(host.api, token, { signal: new AbortController().signal, commit });
  assert.deepEqual(await Promise.all([socket.call('fixture', 'first'), socket.call('fixture', 'second')]), ['first', 'second']);
  const sub = await socket.subscribe('sessions', 'fixture');
  const pending = sub.next();
  sub.close();
  await assert.rejects(pending, /disposed/);
  await socket.close();
  await host.idle();
  assert.equal(host.openPeers, 0);
});

for (const [agent, machine] of [['claude', 'local'], ['codex', 'local'], ['claude', 'workbox-test']]) {
  test(`${agent} on ${machine} keeps the same complete case contract`, async t => {
    const f = await fixture(t, 'pass', machine);
    const result = await runCase({ ...f.env, CHECK4_AGENT: agent });
    assert.equal(result.reason, '');
    assert.equal(result.state, 'finished');
    assert.equal(result.acpSessionId, 'owner-provider-session');
    assert.equal(result.features.length, 1);
    assert.equal(result.features[0]!.pass, true);
    const create = f.local.frames.find(entry => entry.frame.method === 'create')!.frame;
    assert.equal(create.args.agent, agent);
    assert.equal(create.args.machine, machine === 'local' ? undefined : 'workbox-test');
    await Promise.all([f.local.idle(), f.host.idle()]);
    assert.equal(f.local.openPeers + f.host.openPeers, 0);
  });
}

test('cancellation stops the exact feature process group, including its owned grandchild', async t => {
  const f = await fixture(t);
  const features = join(f.out, 'scripts', 'features');
  await mkdir(features, { recursive: true });
  const childFile = join(f.out, 'grandchild.pid');
  const wrapperFile = join(f.out, 'wrapper.pid');
  await writeFile(join(features, 'owned-child.sh'), `#!/usr/bin/env bash
trap 'wait; exit 0' TERM
printf '%s' "$$" > "$CHECK4_WRAPPER_FILE"
node -e 'require("node:fs").writeFileSync(process.env.CHECK4_CHILD_FILE + ".new", String(process.pid)); require("node:fs").renameSync(process.env.CHECK4_CHILD_FILE + ".new", process.env.CHECK4_CHILD_FILE); require("node:net").createServer().listen(0, "127.0.0.1")' &
wait
`, { mode: 0o755 });
  const ready = new Promise<void>(resolve => {
    const changes = watch(f.out, (_event, name) => { if (String(name) === 'grandchild.pid') { changes.close(); resolve(); } });
    t.after(() => changes.close());
  });
  const worker = spawn(process.execPath, [fileURLToPath(new URL('./check4-case.ts', import.meta.url))], {
    cwd: f.out, env: { ...f.env, CHECK4_FEATURES: '["owned-child"]', CHECK4_CHILD_FILE: childFile, CHECK4_WRAPPER_FILE: wrapperFile }, stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '', errors = '';
  worker.stdout.on('data', data => { output += String(data); });
  worker.stderr.on('data', data => { errors += String(data); });
  const exited = once(worker, 'close');
  await ready;
  const wrapper = Number(await readFile(wrapperFile, 'utf8'));
  const grandchild = Number(await readFile(childFile, 'utf8'));
  worker.kill('SIGTERM');
  await exited;
  assert.equal(errors, '');
  assert.match(JSON.parse(output).reason, /interrupted/);
  assert.equal(JSON.parse(output).features.length, 1);
  assert.throws(() => process.kill(wrapper, 0), { code: 'ESRCH' });
  assert.throws(() => process.kill(grandchild, 0), { code: 'ESRCH' });
  await Promise.all([f.local.idle(), f.host.idle()]);
  assert.equal(f.local.openPeers + f.host.openPeers, 0);
  t.diagnostic(`owned wrapper PGID/PID ${wrapper}, grandchild PID ${grandchild}: both exited`);
});

test('orchestrator preserves failed attempts and still records all four blocked cases and every declared feature', async t => {
  const root = await mkdtemp(join(tmpdir(), 'check4-archive-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  await mkdir(join(root, 'scripts', 'features'), { recursive: true });
  await mkdir(join(root, 'bin'));
  await writeFile(join(root, 'scripts', 'check4.sh'), await readFile(new URL('./check4.sh', import.meta.url)));
  await writeFile(join(root, 'scripts', 'lane-env.sh'), `DATA="$CHECK4_TEST_ROOT/data"
BOX_DATA=data
TOKEN_FILE="$CHECK4_TEST_ROOT/token.env"
BOX_TOKEN_FILE=box.env
DESKTOP_PORT=1
BOX_PORT=1
`);
  await writeFile(join(root, 'token.env'), 'ASSEMBLY_TOKEN=fixture-private-token');
  for (const feature of ['rows', 'socket']) await writeFile(join(root, 'scripts', 'features', `${feature}.sh`), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
  await writeFile(join(root, 'bin', 'ssh'), `#!/bin/sh
case "$*" in
  *'echo $HOME'*) printf '%s\n' "$CHECK4_TEST_ROOT" ;;
  *hostname*) printf 'fixture-host\n' ;;
esac
`, { mode: 0o755 });
  await writeFile(join(root, 'source'), 'clean');
  const git = (...args: string[]) => {
    const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  git('init', '-q');
  git('add', '.');
  git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'fixture');
  const sha = git('rev-parse', 'HEAD');
  await writeFile(join(root, 'source'), 'intentionally dirty: preflight must fail');
  const parent = join(root, 'evidence', sha);
  await mkdir(join(parent, 'four-way'), { recursive: true });
  const original = '{"pass":false,"reason":"retained original failure"}';
  await writeFile(join(parent, 'four-way', 'summary.json'), original);
  await writeFile(join(parent, 'four-way', 'failure.log'), 'original diagnostic');
  for (let attempt = 0; attempt < 2; attempt++) {
    const worker = spawn('bash', ['scripts/check4.sh'], { cwd: root, env: { ...process.env, CHECK4_TEST_ROOT: root, PATH: `${root}/bin:${process.env.PATH}` }, stdio: ['ignore', 'pipe', 'pipe'] });
    let errors = '';
    worker.stderr.on('data', data => { errors += String(data); });
    worker.stdout.resume();
    const [code] = await once(worker, 'close');
    assert.equal(code, 1, errors);
    const summary = JSON.parse(await readFile(join(parent, 'four-way', 'summary.json'), 'utf8'));
    assert.equal(summary.partial, false);
    assert.equal(summary.pass, false);
    assert.deepEqual(summary.features, ['rows', 'socket']);
    assert.equal(summary.cases.length, 4);
    assert.deepEqual(summary.cases.map((entry: any) => `${entry.agent}/${entry.machine}`), ['claude/local', 'claude/workbox-test', 'codex/local', 'codex/workbox-test']);
    for (const entry of summary.cases) assert.match(entry.reason, /uncommitted changes/);
  }
  const archived = (await readdir(parent)).filter(name => name.startsWith('four-way.attempt-'));
  assert.equal(archived.length, 2);
  const originals = await Promise.all(archived.map(async directory => ({ directory, summary: await readFile(join(parent, directory, 'summary.json'), 'utf8') })));
  const kept = originals.find(entry => entry.summary === original)!;
  assert(kept, 'the original failed summary survived both attempts');
  assert.equal(await readFile(join(parent, kept.directory, 'failure.log'), 'utf8'), 'original diagnostic');
});

test('snapshot catch-up pages only on bulk through the cut before live rows continue', async t => {
  const f = await fixture(t, 'catchup');
  const result = await runCase(f.env);
  assert.equal(result.reason, '');
  const reads = f.local.frames.filter(entry => entry.frame.method === 'after');
  assert.deepEqual(reads.map(entry => [entry.path, entry.frame.args.after]), [['/socket/bulk', 0], ['/socket/bulk', 1]]);
  assert.match(result.reply, /four-way-ok/);
  assert.equal(JSON.parse(await readFile(join(f.out, 'codex-workbox-test.rows.json'), 'utf8')).length, 4);
  await Promise.all([f.local.idle(), f.host.idle()]);
  assert.equal(f.local.openPeers + f.host.openPeers, 0);
});

test('a failed feature remains in the receipt and fails the complete case', async t => {
  const f = await fixture(t, 'feature-error');
  const result = await runCase(f.env);
  assert.match(result.reason, /socket scenario failed/);
  assert.equal(result.features.length, 1);
  assert.equal(result.features[0]!.pass, false);
  assert.equal(result.features[0]!.exitCode, 1);
  assert.match(await readFile(join(f.out, 'codex-workbox-test.socket.log'), 'utf8'), /failed/);
  await Promise.all([f.local.idle(), f.host.idle()]);
  assert.equal(f.local.openPeers + f.host.openPeers, 0);
});

for (const mode of ['ack-first', 'invalid-ack', 'legacy-ack']) {
  test(`${mode}: validates the immutable queued prompt acknowledgment and closes owned peers`, async t => {
    const f = await fixture(t, mode);
    const result = await runCase({ ...f.env, CHECK4_FEATURES: '[]' });
    if (mode === 'ack-first') {
      assert.equal(result.reason, '');
      assert.equal(result.run, 'run-1');
      assert.equal(result.state, 'finished');
    } else {
      assert.match(result.reason, /invalid committed owner prompt acknowledgment/);
      assert.equal(result.run, '');
    }
    await Promise.all([f.local.idle(), f.host.idle()]);
    assert.equal(f.local.openPeers + f.host.openPeers, 0);
  });
}

for (const mode of ['unrelated', 'uncorrelated', 'missing-run']) {
  test(`${mode}: run identity comes only from the submitted client user row`, async t => {
    const f = await fixture(t, mode);
    const result = await runCase({ ...f.env, CHECK4_FEATURES: '[]' });
    if (mode === 'unrelated') {
      assert.equal(result.reason, '');
      assert.equal(result.run, 'run-1');
      assert.equal(result.state, 'finished');
      const kept = JSON.parse(await readFile(join(f.out, 'codex-workbox-test.rows.json'), 'utf8'));
      assert(kept.every((row: any) => row.runId === 'run-1'));
    } else {
      assert.match(result.reason, mode === 'missing-run' ? /no run identity/ : /closed|connection failed/);
      assert.equal(result.run, '');
      assert.equal(result.features.length, 0);
    }
    await Promise.all([f.local.idle(), f.host.idle()]);
    assert.equal(f.local.openPeers + f.host.openPeers, 0);
  });
}
