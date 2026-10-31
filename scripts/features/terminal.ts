// Terminal lifecycle on the case's host; remote routing through the desktop. Tokens stay in hello/headers.
import assert from 'node:assert/strict';

const env = process.env;
type Frame = { t: string; id?: number; sub?: number; result?: any; code?: string; message?: string;
  item?: { seq: number; prevSeq: number; data?: string; exit?: number } };

async function connect(api: string, token: string) {
  const socket = new WebSocket(`${api.replace(/^http/, 'ws')}/socket/terminal`);
  const frames: Frame[] = [];
  let changed: (() => void) | undefined;
  let failure: Error | undefined;
  socket.onmessage = event => {
    frames.push(JSON.parse(String(event.data)));
    changed?.();
  };
  const closed = () => { failure = new Error('the terminal socket closed'); changed?.(); };
  socket.onerror = closed;
  socket.onclose = closed;
  const wait = async (predicate: (frame: Frame) => boolean) => {
    for (;;) {
      const at = frames.findIndex(predicate);
      if (at >= 0) return frames.splice(at, 1)[0]!;
      if (failure) throw failure;
      await new Promise<void>(resolve => { changed = resolve; });
    }
  };
  await new Promise<void>((resolve, reject) => {
    socket.onopen = () => resolve();
    socket.onerror = () => reject(new Error('the terminal socket failed to open'));
  });
  socket.onerror = closed;
  socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
  await wait(frame => frame.t === 'welcome');
  let id = 0;
  const request = async (method: string, args = {}) => {
    const key = ++id;
    socket.send(JSON.stringify({ t: 'call', id: key, feature: 'terminal', method, args }));
    return await wait(frame => frame.id === key && (frame.t === 'result' || frame.t === 'error'));
  };
  return {
    request,
    async call(method: string, args = {}) {
      const frame = await request(method, args);
      assert.equal(frame.t, 'result', `${method}: ${frame.code ?? ''} ${frame.message ?? ''}`);
      return frame.result;
    },
    async subscribe(terminal: string) {
      const sub = ++id;
      socket.send(JSON.stringify({ t: 'sub', id: sub, topic: 'terminal', key: terminal, after: 0 }));
      const state = await wait(frame => frame.sub === sub || frame.id === sub);
      assert.equal(state.t, 'state');
      return async () => (await wait(frame => frame.t === 'change' && frame.sub === sub)).item!;
    },
    close() { socket.onclose = null; socket.close(); }
  };
}

const connections: Awaited<ReturnType<typeof connect>>[] = [];
let terminal: string | undefined;
let host: Awaited<ReturnType<typeof connect>> | undefined;
const watchdog = setTimeout(() => {
  console.log('fail terminal lifecycle: no completion within 20 seconds');
  for (const connection of connections) connection.close();
  process.exit(1);
}, 20_000);
try {
  host = await connect(env.CHECK4_HOST_API!, env.CHECK4_HOST_TOKEN!);
  connections.push(host);
  const started = await host.call('start', {
    owner: env.CHECK4_SESSION, cwd: env.CHECK4_REPO, shell: '/bin/sh', cols: 80, rows: 24,
    command: 'stty -echo; printf "terminal-ready\\n"; read line; printf "input:%s\\n" "$line"; stty size; exit 7'
  });
  terminal = started.id;
  assert.equal(started.owner, env.CHECK4_SESSION);
  console.log(`ok terminal starts on the ${env.CHECK4_MACHINE} owning backend`);
  const next = await host.subscribe(terminal!);
  let bytes = Buffer.alloc(0);
  let offset = 0;
  let exit: number | undefined;
  const read = async () => {
    const row = await next();
    if (row.seq <= offset) return;
    assert.equal(row.prevSeq, offset, 'output has no gaps or repeated bytes');
    if (row.data != null) {
      const chunk = Buffer.from(row.data, 'base64');
      assert.equal(row.seq, offset + chunk.length);
      bytes = Buffer.concat([bytes, chunk]);
      offset = row.seq;
    } else {
      assert.equal(row.seq, offset + 1);
      exit = row.exit;
    }
  };
  const kept = await host.call('read', { terminal, offset: 0 });
  bytes = Buffer.from(kept.data, 'base64');
  offset = kept.offset + bytes.length;
  while (!bytes.toString().includes('terminal-ready')) await read();
  const replay = await host.call('read', { terminal, offset: 0 });
  assert.equal(replay.offset, 0);
  assert.equal(replay.end, offset);
  assert.deepEqual(Buffer.from(replay.data, 'base64'), bytes);
  console.log('ok live output and replay contain the same bytes');
  await host.call('resize', { terminal, cols: 120, rows: 40 });
  await host.call('write', { terminal, data: Buffer.from('terminal-input\n').toString('base64') });
  while (exit === undefined) await read();
  assert.match(bytes.toString(), /input:terminal-input/);
  assert.match(bytes.toString(), /40 120/);
  assert.equal(exit, 7);
  const ended = (await host.call('list')).find((item: any) => item.id === terminal);
  assert.equal(ended.exitCode, 7);
  console.log('ok input reaches the program, resize reaches the PTY, and exit follows output');
  await host.call('close', { terminal });
  assert(!(await host.call('list')).some((item: any) => item.id === terminal));
  assert.equal((await host.request('read', { terminal, offset: 0 })).t, 'error');
  terminal = undefined;
  console.log('ok close removes the terminal and its replay');

  const desktop = await connect(env.CHECK4_API!, env.CHECK4_TOKEN!);
  connections.push(desktop);
  // Create a known remote owner without starting a turn or checking out a folder.
  const response = await fetch(`${env.CHECK4_API}/sessions`, {
    method: 'POST', headers: { Authorization: `Bearer ${env.CHECK4_TOKEN}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ agent: env.CHECK4_AGENT, repoUrl: env.CHECK4_REPO, machine: 'workbox-test' })
  });
  assert(response.ok, `create remote owner: HTTP ${response.status}`);
  const remote = await response.json() as { sessionId: string };
  const before = (await desktop.call('list')).map((item: any) => item.id).sort();
  for (const cwd of [undefined, env.CHECK4_REPO]) {
    const answer = await desktop.request('start', { owner: `terminal-unknown-${crypto.randomUUID()}`, cwd });
    assert.equal(answer.t, 'error');
    assert.equal(answer.code, 'failed');
    assert.match(answer.message!, /unknown.*owner/i);
    assert.deepEqual((await desktop.call('list')).map((item: any) => item.id).sort(), before);
  }
  console.log('ok unknown owners are refused with and without a folder');
  const routed = await desktop.call('start', { owner: remote.sessionId, shell: '/bin/sh' });
  assert.match(routed.id, /^assembly-remote-terminal:\/\/workbox-test\//);
  assert.equal((await desktop.call('start', { owner: remote.sessionId })).id, routed.id);
  assert((await desktop.call('list', { owner: remote.sessionId })).some((item: any) => item.id === routed.id));
  await desktop.call('close', { terminal: routed.id });
  console.log('ok remote shell routes through the desktop and list/start reuse it');
} catch (error) {
  console.log(`fail terminal lifecycle: ${(error as Error).message}`);
  process.exitCode = 1;
} finally {
  if (terminal && host) {
    try { await host.call('close', { terminal }); }
    catch { process.exitCode = 1; }
  }
  for (const connection of connections) connection.close();
  clearTimeout(watchdog);
}
