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

// The real stopped-then-started path on Linux (the probe reads /proc): a lane whose unit is installed and stopped,
// a fake systemctl whose start launches a host that takes a while to listen, and the real probe source asking it.
test('a stopped installed host is started, and probed once it answers', { skip: process.platform !== 'linux', timeout: 20_000 }, async () => {
  const { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, realpathSync } = await import('node:fs');
  const { tmpdir } = await import('node:os');
  const { join } = await import('node:path');
  const { probeSource } = await import('./dev-remote-probe.ts');
  const { startStoppedHost } = await import('./dev-remote-ready.ts');
  const { command } = await import('./dev-remote-process.ts');
  const home = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-stopped-')));
  const spare = createServer();
  const port = await listen(spare);
  await new Promise(resolve => spare.close(resolve));
  const lane = { name: 'stopped-test', number: 9, unit: 'assembly-dotnet-stopped-test', data: 'assembly-dotnet-stopped-test', port };
  const root = join(home, lane.data), dll = join(root, 'bin/r1/backend/Assembly.Remote.dll'), state = join(home, 'systemctl.json');
  for (const directory of [join(root, 'bin/r1/backend'), join(root, 'serve'), join(home, '.config/systemd/user'), join(home, 'bin')]) mkdirSync(directory, { recursive: true });
  writeFileSync(join(root, 'lane-number'), '9\n');
  writeFileSync(join(root, 'serve/env'), 'ASSEMBLY_TOKEN=test-token\n');
  writeFileSync(join(home, '.config/systemd/user', lane.unit + '.service'),
    `[Service]\nExecStart=%h/.dotnet/dotnet ${dll} serve --port ${port} --home ${root}/serve --agents ${root}/serve/agents.json --token-file ${root}/serve/env\n`);
  // The host: listens only after a delay, then answers hello with an authenticated welcome.
  writeFileSync(dll, `const http=require('node:http'),crypto=require('node:crypto');
setTimeout(()=>{const s=http.createServer();s.on('upgrade',(q,p)=>{
 p.write('HTTP/1.1 101 Switching Protocols\\r\\nUpgrade: websocket\\r\\nConnection: Upgrade\\r\\nSec-WebSocket-Accept: '+crypto.createHash('sha1').update(q.headers['sec-websocket-key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64')+'\\r\\n\\r\\n');
 p.on('error',()=>{});p.once('data',()=>{const b=Buffer.from(JSON.stringify({t:'welcome',machine:'workbox-test',protocol:1,format:5,commit:'${'c'.repeat(40)}',backendIdentity:'id-1',engine:'e'}));p.write(Buffer.concat([Buffer.from([0x81,126,b.length>>8,b.length&255]),b]));});
});s.listen(${port},'127.0.0.1');},1500);`);
  writeFileSync(state, JSON.stringify({ pid: '0', starts: 0 }));
  writeFileSync(join(home, 'bin/systemctl'), `#!/usr/bin/env node
const fs=require('node:fs'),{spawn}=require('node:child_process');
const a=process.argv.slice(2),s=JSON.parse(fs.readFileSync(${JSON.stringify(state)},'utf8'));
if(a[1]==='start'){const c=spawn(process.execPath,[${JSON.stringify(dll)},'serve'],{detached:true,stdio:'ignore'});c.unref();s.pid=String(c.pid);s.starts++;fs.writeFileSync(${JSON.stringify(state)},JSON.stringify(s));}
else if(a[1]==='show'){const p=a[a.indexOf('-p')+1];console.log(p==='MainPID'?s.pid:s.pid==='0'?'inactive':'active');}
`, { mode: 0o755 });
  const env = { HOME: home, PATH: join(home, 'bin') + ':' + process.env.PATH };
  const run = (tool: string, args: string[]) => command(tool, args, { env });
  const probe = async () => JSON.parse(await run(process.execPath, ['--input-type=module', '-e', probeSource, JSON.stringify(lane)]));
  try {
    assert.deepEqual(await probe(), { stopped: true });
    const answer = await startStoppedHost({ unit: lane.unit, run, answer: probe, signal: new AbortController().signal, retryMs: 100, limitMs: 15_000 });
    assert.equal(answer.backendIdentity, 'id-1');
    assert.equal(answer.dll, dll);
    assert.equal(JSON.parse(readFileSync(state, 'utf8')).starts, 1);
  } finally {
    const pid = Number(JSON.parse(readFileSync(state, 'utf8')).pid);
    if (pid) { try { process.kill(pid); } catch {} }
    rmSync(home, { recursive: true, force: true });
  }
});
