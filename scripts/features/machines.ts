// Socket setup before check4 preflight, and a non-destructive scenario for each case.
import { createServer } from 'node:net';

const mode = process.argv[2];
if (mode === 'port') {
  const server = createServer();
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('no loopback port');
  console.log(address.port);
  await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
} else {
  type Item = { name: string | null; ssh: string | null; port: number | null; tokenFile: string | null; reachable: boolean; reason?: string | null };
  type Frame = { t: string; id?: number; sub?: number; code?: string; message?: string; result?: unknown; items?: { key: string; item: Item }[] };
  const env = process.env;
  const socket = new WebSocket(`${env.CHECK4_API!.replace(/^http/, 'ws')}/socket`);
  const pending = new Map<number, { resolve: (frame: Frame) => void; reject: (error: Error) => void }>();
  let next = 0;
  let welcome!: () => void;
  let rejectWelcome!: (error: Error) => void;
  const ready = new Promise<void>((resolve, reject) => { welcome = resolve; rejectWelcome = reject; });
  const timeout = setTimeout(() => fail(new Error('machines socket did not complete within 30 seconds')), 30_000);
  function fail(error: Error) {
    rejectWelcome(error);
    for (const request of pending.values()) request.reject(error);
    pending.clear();
    socket.close();
  }
  socket.onopen = () => socket.send(JSON.stringify({ t: 'hello', token: env.CHECK4_TOKEN, protocol: 1 }));
  socket.onerror = () => fail(new Error('machines socket failed'));
  socket.onclose = () => fail(new Error('machines socket closed'));
  socket.onmessage = event => {
    const frame = JSON.parse(String(event.data)) as Frame;
    if (frame.t === 'welcome') { welcome(); return; }
    const id = frame.id ?? frame.sub;
    const request = id === undefined ? undefined : pending.get(id);
    if (!request) {
      if (frame.t === 'error') fail(new Error(`${frame.code}: ${frame.message}`));
      return;
    }
    pending.delete(id!);
    if (frame.t === 'error') request.reject(new Error(`${frame.code}: ${frame.message}`));
    else request.resolve(frame);
  };
  async function request(frame: Record<string, unknown>): Promise<Frame> {
    const id = ++next;
    const result = new Promise<Frame>((resolve, reject) => pending.set(id, { resolve, reject }));
    socket.send(JSON.stringify({ ...frame, id }));
    return await result;
  }
  async function call(method: string, args: unknown) {
    return (await request({ t: 'call', feature: 'machines', method, args })).result;
  }
  try {
    await ready;
    if (mode === 'setup') {
      const profile = { name: 'workbox-test', ssh: 'workbox-test', port: Number(env.CHECK4_BOX_PORT), tokenFile: env.CHECK4_BOX_TOKEN_FILE };
      await call('set', profile);
      const connected = await call('connect', { machine: profile.name }) as Item;
      if (!connected.reachable) throw new Error(`saved Workbox Test profile is unreachable: ${connected.reason ?? 'no reason reported'}`);
      console.log('ok Workbox Test saved and connected through the socket before preflight');
    } else if (mode === 'scenario') {
      const snapshot = await request({ t: 'sub', topic: 'machines' });
      const remote = snapshot.items?.find(entry => entry.key === 'workbox-test')?.item;
      if (!remote || remote.ssh !== 'workbox-test' || !remote.port || !remote.tokenFile || !remote.reachable)
        throw new Error('saved Workbox Test metadata or reachability is missing');
      console.log('ok saved machine metadata is present');
      const machine = env.CHECK4_MACHINE === 'local' ? undefined : 'workbox-test';
      const agents = await call('agents', { machine }) as { agents: { id: string }[] };
      if (!agents.agents.some(agent => agent.id === env.CHECK4_AGENT)) throw new Error('case provider is missing from its machine');
      // Config follows the same named-machine routing used for this existing case session's send.
      await request({ t: 'call', feature: 'config', method: 'get', args: { session: env.CHECK4_SESSION } });
      console.log('ok existing session config routes to its machine and the case provider is available');
    } else throw new Error('expected setup, scenario or port');
  } finally {
    clearTimeout(timeout);
    socket.onclose = null;
    socket.close();
  }
}
