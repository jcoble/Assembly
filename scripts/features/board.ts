// Board proof uses only host-authenticated owner calls and the real provider's configured MCP reporting tool.
import { randomUUID } from 'node:crypto';

type OwnerPromptAccepted = { sessionId: string; state: 'queued' };
type Frame = { t: string; id?: number; sub?: number; code?: string; message?: string; result?: any; machine?: string; engine?: string; items?: any[]; events?: any[]; more?: boolean };
type Need = { id: string; machine: string; version: number; sessionId: string; state: string; text?: string; answerText?: string };
class Client {
  socket: WebSocket;
  frames: Frame[] = [];
  changed: (() => void) | undefined;
  id = 10;
  constructor(api: string, token: string, path = '/socket') {
    this.socket = new WebSocket(api.replace(/^http/, 'ws') + path);
    this.socket.onmessage = event => { this.frames.push(JSON.parse(String(event.data))); this.changed?.(); };
    this.socket.onopen = () => this.send({ t: 'hello', protocol: 1, token });
    this.socket.onerror = () => { this.frames.push({ t: 'error', message: 'Socket failed' }); this.changed?.(); };
    this.socket.onclose = () => { this.frames.push({ t: 'closed' }); this.changed?.(); };
  }
  send(frame: object) { this.socket.send(JSON.stringify(frame)); }
  async take(match: (f: Frame) => boolean): Promise<Frame> {
    while (true) {
      const index = this.frames.findIndex(match);
      if (index >= 0) return this.frames.splice(index, 1)[0]!;
      if (this.frames.some(f => f.t === 'closed' || (f.t === 'error' && f.id === undefined)))
        throw new Error('Socket closed or failed');
      await new Promise<void>(resolve => { this.changed = resolve; });
      this.changed = undefined;
    }
  }
  async ready() { return await this.take(f => f.t === 'welcome'); }
  async response(feature: string, method: string, args: object) {
    const id = ++this.id;
    this.send({ t: 'call', id, feature, method, args });
    return await this.take(f => f.id === id && (f.t === 'result' || f.t === 'error'));
  }
  async call<T = any>(feature: string, method: string, args: object): Promise<T> {
    const frame = await this.response(feature, method, args);
    if (frame.t !== 'result') throw new Error(frame.message ?? frame.code);
    return frame.result as T;
  }
  close() { this.socket.onclose = null; this.socket.close(); }
}
const clients: Client[] = [];
const env = process.env;
const timer = setTimeout(() => { console.log('fail board: scenario deadline exceeded'); for (const c of clients) c.close(); process.exit(1); }, 180_000);
function requireValue(value: unknown, message: string): asserts value { if (!value) throw new Error(message); }
async function open(api: string, token: string, path?: string) {
  const client = new Client(api, token, path); clients.push(client); await client.ready(); return client;
}
// The rows the panel reads, as one whole cut of the scope. Sparse events only say to read again.
async function cut(client: Client, topic: string, machine: string, expectedSource: object | null) {
  const id = ++client.id;
  client.send({ t: 'sub', id, stream: 'engine', topic, machine, ...(expectedSource ? { expectedSource } : {}) });
  const items: any[] = [];
  while (true) {
    const frame = await client.take(f => f.id === id && (f.t === 'engine_snapshot' || f.t === 'error'));
    if (frame.t === 'error') throw new Error(frame.message ?? frame.code);
    items.push(...frame.items!);
    if (!frame.more) break;
  }
  client.send({ t: 'unsub', id });
  return items;
}
async function main() {
  const desktop = await open(env.CHECK4_API!, env.CHECK4_TOKEN!);
  const route = env.CHECK4_MACHINE === 'local' ? null : env.CHECK4_MACHINE!;
  const source = await desktop.call('sessions', 'resolve_source', { routeMachine: route });
  const machine = source.machine as string;
  const expected = route ? source : null;
  const created = await desktop.call('sessions', 'create', { agent: env.CHECK4_AGENT, repoUrl: env.CHECK4_REPO, machine: route, parentSessionId: null, title: null });
  const session = created.sessionId as string;
  const rows = await desktop.sub('rows', session);
  async function prompt(text: string) {
    const accepted = await desktop.call<OwnerPromptAccepted>('send', 'send', { session, clientId: randomUUID(), text });
    requireValue(accepted?.sessionId === session && accepted.state === 'queued', 'The owner prompt was not accepted');
  }
  // Waits on the socket's own frames: any event on the machine's needs reads the whole cut again.
  const needEvents = ++desktop.id;
  desktop.send({ t: 'sub', id: needEvents, stream: 'engine', topic: 'needs_me', machine, ...(expected ? { expectedSource: expected } : {}) });
  await desktop.take(f => f.id === needEvents && f.t === 'engine_snapshot' && !f.more);
  async function needMatching(match: (need: Need) => boolean): Promise<Need> {
    while (true) {
      const found = (await cut(desktop, 'needs_me', machine, expected) as Need[]).find(need => need.sessionId === session && match(need));
      if (found) return found;
      await desktop.take(f => f.id === needEvents && f.t === 'engine_events');
    }
  }
  const epoch = async () => {
    const row = (await cut(desktop, 'sessions', machine, expected)).find(item => item.id === session);
    requireValue(Number.isSafeInteger(row?.epoch), 'The session has no current owner epoch');
    return row.epoch as number;
  };
  const resolve = async (need: Need, answer: string) => desktop.call('engine', 'command', {
    commandId: randomUUID(), entityId: need.id, expectedVersion: need.version, epoch: await epoch(), machine, surface: 'board',
    command: { kind: 'resolve_need', state: 'answered', answer } });
  await prompt('Use the Assembly ask_owner MCP tool once, with the question "Which option?" and kind "question". ' +
    'When it returns, repeat the answer you received in one line and end the turn. Change no files and use no helpers.');
  const asked = await needMatching(need => need.state === 'open');
  requireValue(asked.text === 'Which option?', `The need holds ${asked.text}`);
  console.log('ok real provider asked through ask_owner and the need reached the engine stream');
  const receipt = await resolve(asked, 'Simple');
  requireValue(receipt.applied === true, `The answer was refused: ${receipt.error}`);
  const answered = await needMatching(need => need.id === asked.id && need.state !== 'open');
  requireValue(answered.state === 'answered' && answered.answerText === 'Simple', 'The need did not record its answer');
  const stale = await resolve(asked, 'Again');
  requireValue(stale.applied !== true, 'A second answer at the old version was applied');
  await desktop.take(f => f.sub === rows.id && f.t === 'rows' && JSON.stringify((f as any).row?.payload ?? '').includes('Simple'));
  console.log('ok one answer recorded and delivered to the asker; the old version was refused');
  await prompt('Use the Assembly ask_owner MCP tool once, with the question "Second question?" and kind "question".');
  const second = await needMatching(need => need.state === 'open' && need.id !== asked.id);
  await desktop.call('engine', 'command', {
    commandId: randomUUID(), entityId: session, expectedVersion: (await cut(desktop, 'sessions', machine, expected)).find(item => item.id === session).version,
    epoch: await epoch(), machine, surface: null, command: { kind: 'stop_session' } });
  const closed = await needMatching(need => need.id === second.id && need.state !== 'open');
  requireValue(closed.state === 'cancelled', `Ending the asker left the need ${closed.state}`);
  const late = await resolve(second, 'Too late').catch((error: Error) => ({ applied: false, error: error.message }));
  requireValue(late.applied !== true, 'A cancelled need took an answer');
  console.log('ok ending the asker cancelled its open need and a late answer was refused');
}
try { await main(); }
catch (error) { console.log('fail board: ' + (error instanceof Error ? error.message : String(error))); process.exitCode = 1; }
finally { clearTimeout(timer); for (const client of clients) client.close(); }
