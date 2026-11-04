import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, test } from 'node:test';
import { backendIdentity } from './dev-remote-identity.ts';
import { waitForWelcome } from './wait-welcome.ts';

const original = process.cwd();
const root = mkdtempSync(join(tmpdir(), 'assembly-welcome-'));
const command = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
command('init', '-q');
command('config', 'user.name', 'Readiness Test');
command('config', 'user.email', 'readiness@example.invalid');
for (const path of ['dotnet/Assembly.Remote', 'desktop', 'app', 'shell', 'scripts']) mkdirSync(join(root, path), { recursive: true });
for (const path of ['dotnet/Assembly.Remote/Assembly.Remote.csproj', 'desktop/Cargo.toml', 'app/old.ts', 'shell/old.rs']) writeFileSync(join(root, path), 'original\n');
function commit(message: string) { command('add', '-A'); command('commit', '-qm', message); return command('rev-parse', 'HEAD'); }
const historical = commit('historical app and shell');
command('rm', '-qr', 'app', 'shell');
const target = commit('retire app and shell');
writeFileSync(join(root, 'scripts/tool.ts'), 'tooling only\n');
const equivalent = commit('tooling only');
writeFileSync(join(root, 'desktop/Cargo.toml'), 'changed native\n');
const staleNative = commit('native code changed');
writeFileSync(join(root, 'desktop/Cargo.toml'), 'original\n');
writeFileSync(join(root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj'), 'changed backend\n');
const staleBackend = commit('backend code changed');
const identities = new Map(await Promise.all([historical, target, equivalent, staleNative, staleBackend].map(async commit => [commit, await backendIdentity(root, commit)] as const)));
process.chdir(root);
after(() => { process.chdir(original); rmSync(root, { recursive: true, force: true }); });

// A real RFC6455 loopback peer: handshake, masked client frames, close handshake and split server frames.
// Keeping the fixture here avoids introducing a production socket library or dependency for a readiness test.
function serverFrame(value: unknown): Buffer {
  const body = Buffer.from(JSON.stringify(value));
  const header = body.length < 126 ? Buffer.from([0x81, body.length]) : Buffer.from([0x81, 126, body.length >> 8, body.length & 255]);
  return Buffer.concat([header, body]);
}
async function fixture(reply: (peer: any, hello: any, attempt: number) => void, answerClose = true) {
  let attempts = 0, httpHits = 0;
  const frames: any[] = [], requests: { url: string; authorization?: string }[] = [];
  const peers = new Set<any>();
  const server = createServer((_request, response) => { httpHits++; response.writeHead(404).end(); });
  server.on('upgrade', (request, peer, head) => {
    peers.add(peer);
    peer.on('close', () => peers.delete(peer));
    peer.on('end', () => peer.end());
    peer.on('error', () => {});
    requests.push({ url: request.url!, authorization: request.headers.authorization });
    const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    peer.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    let buffered = head;
    const consume = (data: Buffer) => {
      buffered = Buffer.concat([buffered, data]);
      while (buffered.length >= 2) {
        const opcode = buffered[0] & 15, masked = (buffered[1] & 128) !== 0;
        let length = buffered[1] & 127, offset = 2;
        if (length === 126) { if (buffered.length < 4) return; length = buffered.readUInt16BE(2); offset = 4; }
        if (length === 127) throw new Error('unexpected oversized test client frame');
        const maskBytes = masked ? 4 : 0;
        if (buffered.length < offset + maskBytes + length) return;
        const mask = buffered.subarray(offset, offset + maskBytes);
        const body = Buffer.from(buffered.subarray(offset + maskBytes, offset + maskBytes + length));
        buffered = buffered.subarray(offset + maskBytes + length);
        if (masked) for (let i = 0; i < body.length; i++) body[i] ^= mask[i % 4];
        if (opcode === 8) { if (answerClose) peer.end(Buffer.from([0x88, 0])); return; }
        if (opcode === 1) { const hello = JSON.parse(body.toString()); frames.push(hello); reply(peer, hello, ++attempts); }
      }
    };
    peer.on('data', consume);
    consume(Buffer.alloc(0));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address() as { port: number };
  return {
    base: `http://127.0.0.1:${address.port}`, frames, requests, peers,
    get httpHits() { return httpHits; },
    async restart() { server.listen(address.port, '127.0.0.1'); await once(server, 'listening'); },
    async drained() { await Promise.all([...peers].map(peer => once(peer, 'close'))); assert.equal(peers.size, 0); },
    async close() { for (const peer of peers) peer.destroy(); await new Promise<void>(resolve => server.close(() => resolve())); },
  };
}
const welcome = (commit: string | null = target) => ({ t: 'welcome', protocol: 1, machine: 'workbox-test', format: 5, commit, backendIdentity: identities.get(commit ?? '') });
const token = 'private-owner-token-for-hello';
const options = (base: string, seconds = 0.6) => ({ name: 'Workbox Test', base, commit: target, seconds });

for (const name of ['wait-version.sh', 'wait-welcome.ts', 'dev-remote-identity.ts', 'dev-remote-process.ts'])
  writeFileSync(join(root, 'scripts', name), readFileSync(join(original, 'scripts', name)));

async function cli(base: string, seconds = '1', sendToken = true) {
  const child = spawn('bash', [join(root, 'scripts/wait-version.sh'), 'the host', base, target, seconds], { stdio: ['pipe', 'pipe', 'pipe'] });
  let stdout = '', stderr = '';
  child.stdout.on('data', data => stdout += data);
  child.stderr.on('data', data => stderr += data);
  if (sendToken) child.stdin.end(`${token}\n`);
  const [code] = await once(child, 'close');
  assert.equal(stderr, '');
  assert.ok(!child.spawnargs.join(' ').includes(token));
  assert.ok(!stdout.includes(token));
  return { code, stdout };
}

for (const actual of [target, equivalent]) test(`accepts authenticated ${actual === target ? 'exact' : 'same retained code'} commit`, async () => {
  const server = await fixture((peer, hello) => {
    assert.deepEqual(hello, { t: 'hello', token, protocol: 1 });
    const frame = serverFrame(welcome(actual));
    peer.write(frame.subarray(0, 7));
    setImmediate(() => peer.write(frame.subarray(7)));
  });
  try {
    assert.equal(await waitForWelcome(options(server.base), token), actual);
    await server.drained();
    assert.deepEqual(server.requests, [{ url: '/socket', authorization: undefined }]);
    assert.equal(server.httpHits, 0);
    assert.equal(server.frames.length, 1);
  } finally { await server.close(); }
});

test('reconnects when a starting host drops its first connection', async () => {
  const server = await fixture((peer, _hello, attempt) => attempt === 1 ? peer.destroy() : peer.write(serverFrame(welcome())));
  try {
    assert.equal(await waitForWelcome(options(server.base), token), target);
    await server.drained();
    assert.equal(server.frames.length, 2);
  } finally { await server.close(); }
});

test('a host that starts listening after readiness begins is retried', async () => {
  const server = await fixture(peer => peer.write(serverFrame(welcome())));
  await server.close();
  // Delayed startup is the behavior under test; completion still waits for socket state.
  const startup = setTimeout(() => { void server.restart(); }, 150);
  try {
    assert.equal(await waitForWelcome(options(server.base), token), target);
    await server.drained();
  } finally { clearTimeout(startup); await server.close(); }
});

test('Rust-only and retired frontend revisions use the same backend', async () => {
  for (const value of [staleNative, historical]) {
    const server = await fixture(peer => peer.write(serverFrame(welcome(value))));
    try { assert.equal(await waitForWelcome(options(server.base), token), value); await server.drained(); }
    finally { await server.close(); }
  }
});

test('a mismatched backend fails on the first welcome and names both commits', async () => {
  const server = await fixture(peer => peer.write(serverFrame(welcome(staleBackend))));
  try {
    await assert.rejects(waitForWelcome(options(server.base, 10), token), new RegExp(`backend mismatch: remote ${staleBackend}; desktop ${target}`));
    await server.drained();
    assert.equal(server.frames.length, 1);
  } finally { await server.close(); }
});

const invalid: [string, unknown, RegExp][] = [
  ['protocol', { ...welcome(), protocol: 2 }, /incompatible welcome protocol/],
  ['format', { ...welcome(), format: 4 }, /incompatible welcome format/],
  ['machine', { ...welcome(), machine: '' }, /invalid welcome machine/],
  ['frame kind', { ...welcome(), t: 'result' }, /invalid welcome machine or frame/],
  ['null commit', welcome(null), /commit is null \(unknown build\)/],
  ['missing producer commit', { t: 'welcome', protocol: 1, machine: 'workbox-test', format: 5, engine: 'workbox-test' }, /commit is missing or invalid/],
  ['malformed commit', welcome('not-a-commit'), /commit is missing or invalid/],
  ['unknown identity', welcome('0'.repeat(40)), /backend identity is missing or invalid/],
];
for (const [label, frame, expected] of invalid) test(`rejects ${label} without leaking token or waiting for the deadline`, async () => {
  const server = await fixture(peer => peer.write(serverFrame(frame)));
  try {
    await assert.rejects(waitForWelcome(options(server.base), token), error => {
      assert.match(String(error), expected);
      assert.ok(!String(error).includes(token));
      return true;
    });
    await server.drained();
    assert.equal(server.httpHits, 0);
  } finally { await server.close(); }
});

test('a silent upgraded peer is bounded and closed', async () => {
  const server = await fixture(() => {});
  try {
    await assert.rejects(waitForWelcome(options(server.base, 0.1), token), /deadline.*no answer/);
    await server.drained();
  } finally { await server.close(); }
});

test('CLI wrapper reads token from stdin and prints the actual accepted commit once', async () => {
  const server = await fixture(peer => peer.write(serverFrame(welcome(equivalent))));
  try {
    assert.deepEqual(await cli(server.base), { code: 0, stdout: `${equivalent}\n` });
    assert.ok(!JSON.stringify(server.requests).includes(token));
    await server.drained();
  } finally { await server.close(); }
});

test('CLI deadline owns stdin even when its writer never sends the token', async () => {
  const result = await cli('http://127.0.0.1:1', '0.1', false);
  assert.equal(result.code, 1);
  assert.match(result.stdout, /^the host on port 1: deadline after 0.1 s; last: no answer\n$/);
});

test('CLI exits and releases the peer even if it ignores the close handshake', async () => {
  const server = await fixture(peer => peer.write(serverFrame(welcome())), false);
  try {
    assert.deepEqual(await cli(server.base), { code: 0, stdout: `${target}\n` });
    await server.drained();
  } finally { await server.close(); }
});

test('unreachable startup is bounded and names its port', async () => {
  const server = await fixture(() => {});
  await server.close();
  await assert.rejects(waitForWelcome(options(server.base, 0.15), token), /Workbox Test on port \d+: deadline.*socket unreachable/);
});

test('authentication refusal is bounded without printing peer reasons or secrets', async () => {
  const server = await fixture(peer => {
    const body = Buffer.from([0x11, 0x31]); // WebSocket 4401, no arbitrary reason echoed by the client.
    peer.end(Buffer.concat([Buffer.from([0x88, body.length]), body]));
  });
  try {
    await assert.rejects(waitForWelcome(options(server.base, 0.15), token), error => {
      assert.match(String(error), /socket closed \(4401\)/);
      assert.ok(!String(error).includes(token));
      return true;
    });
    await server.drained();
  } finally { await server.close(); }
});

test('malformed JSON fails with a fixed diagnostic and closes the socket', async () => {
  const server = await fixture(peer => peer.write(Buffer.from([0x81, 1, 0x7b])));
  try {
    await assert.rejects(waitForWelcome(options(server.base), token), /invalid welcome JSON/);
    await server.drained();
  } finally { await server.close(); }
});
