// Board proof uses only host-authenticated owner calls and the real provider's configured MCP reporting tool.
import { randomUUID } from 'node:crypto';

type Frame = { t: string; id?: number; sub?: number; code?: string; message?: string; result?: any; item?: any; items?: { key: string; item: any }[] };
type Detail = { task: { machineId: string; taskKey: string; creatorSessionId: string; state: string }; questions: any[]; feed: any[] };
type Page = { items: { task: Detail['task'] }[]; nextCursor: string | null };
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
  async ready() { await this.take(f => f.t === 'welcome'); }
  async sub(topic: string, key: string | null = null) {
    const id = ++this.id;
    this.send({ t: 'sub', id, topic, key });
    return { id, state: await this.take(f => f.sub === id && f.t === 'state') };
  }
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
async function pageContains(client: Client, machineId: string, taskKey: string, expected: string) {
  let cursor: string | null = null;
  do {
    const page: Page = await client.call('board', 'page', { project: null, state: null, cursor });
    const found = page.items.find(x => x.task.machineId === machineId && x.task.taskKey === taskKey);
    if (found) { requireValue(found.task.state === expected, 'Retained state differs'); return found.task; }
    cursor = page.nextCursor;
  } while (cursor !== null);
  throw new Error('Task absent from all SQL pages');
}
async function main() {
  const desktop = await open(env.CHECK4_API!, env.CHECK4_TOKEN!);
  const board = await desktop.sub('board');
  requireValue(board.state.items?.length === 0, 'Board topic must have empty state');
  const argv = process.argv.slice(2);
  if (argv[0] === '--verify-retained') {
    const option = (name: string) => argv[argv.indexOf(name) + 1];
    const machineId = option('--machine'), taskKey = option('--task'), state = option('--state');
    requireValue(argv.includes('--machine') && argv.includes('--task') && argv.includes('--state') && machineId && taskKey && state, 'Retained mode needs machine, task and state');
    await pageContains(desktop, machineId, taskKey, state);
    const detail = await desktop.response('board', 'task', { machineId, taskKey, beforeSeq: null, questionCursor: null });
    requireValue(detail.t === 'result' || detail.code === 'unreachable', 'Detail must succeed online or fail unreachable offline');
    console.log('ok retained SQL card and explicit source reachability');
    return;
  }
  const machineId = env.CHECK4_MACHINE === 'local' ? 'local' : env.CHECK4_MACHINE!;
  const created = await desktop.call('sessions', 'create', { agent: env.CHECK4_AGENT, repoUrl: env.CHECK4_REPO,
    machine: machineId === 'local' ? null : machineId, parentSessionId: null, title: null });
  const session = created.sessionId as string;
  const sessionSub = await desktop.sub('sessions', session);
  requireValue(sessionSub.state.items?.some(x => x.item.sessionId === session), 'Desktop must know recovery session');
  const source = await open(env.CHECK4_HOST_API!, env.CHECK4_HOST_TOKEN!);
  const sourceBoard = await source.sub('board');
  const key = 'board-proof-' + randomUUID();
  const base = { taskKey: key, title: 'Board proof with two independent questions and a long project name',
    state: 'running', summary: 'Awaiting two independent choices', taskUrl: null, prUrl: null, changedPaths: null, question: null, answerReceived: null };
  const report = (question: object | null, extra: object = {}) => JSON.stringify({ ...base, question, ...extra });
  const detail = () => source.call<Detail>('board', 'task', { machineId: 'local', taskKey: key, beforeSeq: null, questionCursor: null });
  async function reported(predicate: (d: Detail) => boolean) {
    while (true) {
      await source.take(f => f.t === 'change' && f.sub === sourceBoard.id && f.item?.taskKey === key);
      const current = await detail();
      if (predicate(current)) return current;
    }
  }
  async function idle(runId: string) {
    let running = false;
    while (true) {
      const frame = await desktop.take(f => f.t === 'change' && f.sub === sessionSub.id);
      if (frame.item?.activeRunId === runId) running = true;
      if (running && frame.item?.activeRunId === null) return;
    }
  }
  const firstTurn = await desktop.call('send', 'send', { session, clientId: randomUUID(), text:
    'Use the Assembly board_update MCP tool exactly twice in this order with these JSON arguments. Do not use helpers or change files. ' +
    report({ questionKey: 'choice', expectedGeneration: 0, kind: 'text', prompt: 'Which option?', recommendation: 'Simple' }) + '\n' +
    report({ questionKey: 'approval', expectedGeneration: 0, kind: 'approval', prompt: 'Approve the proposal?', recommendation: null }) +
    '\nThen end this turn. You will receive a Board answer later.' });
  await reported(d => d.questions.length === 2);
  await idle(firstTurn.runId);
  console.log('ok real provider reported two independent questions through MCP');
  const answer = { machineId, taskKey: key, questionKey: 'choice', generation: 1, answerKey: randomUUID(), answer: { kind: 'text', text: 'Simple' } };
  const accepted = await desktop.call('board', 'answer', answer);
  const duplicate = await desktop.call('board', 'answer', answer);
  requireValue(JSON.stringify(accepted) === JSON.stringify(duplicate), 'Duplicate acceptance changed');
  // The answer itself starts the next real provider turn; require that turn to report receipt and reask.
  // It receives the instructions here as a steering message only after the answer's turn has settled.
  const delivered = await detail();
  const answerTurn = delivered.questions.find(q => q.questionKey === 'choice').deliveryTurnId;
  requireValue(answerTurn, 'Answer delivery has no acknowledged turn');
  await idle(answerTurn);
  const receivedKey = randomUUID();
  const finalTurn = await desktop.call('send', 'send', { session, clientId: randomUUID(), text:
    'Inspect the immediately preceding Board answer in this chat. Only if it names question choice, generation 1 and answer Simple, ' +
    'use board_update first to reask choice using ' +
    report({ questionKey: 'choice', expectedGeneration: 1, kind: 'text', prompt: 'Confirm simple?', recommendation: 'Simple' }) +
    '. Then record an answer already received in this chat for approval using ' +
    report(null, { answerReceived: { questionKey: 'approval', generation: 1, answerKey: receivedKey, answer: { kind: 'approve', text: null } },
      summary: 'Confirmed Board answer receipt; approval recorded from chat' }) + '. End the turn.' });
  const reasked = await reported(d => d.questions.some(q => q.questionKey === 'choice' && q.generation === 2) &&
    d.questions.some(q => q.questionKey === 'approval' && q.deliveryState === 'recorded_in_chat'));
  requireValue(reasked.questions.find(q => q.questionKey === 'choice').answer === null, 'Reasked question must be unanswered');
  const approval = reasked.questions.find(q => q.questionKey === 'approval');
  requireValue(approval.deliveryClientId === null && approval.deliveryTurnId === null, 'Chat-recorded answer must not send');
  const stale = await desktop.response('board', 'answer', { ...answer, answerKey: randomUUID() });
  requireValue(stale.code === 'failed' && stale.message?.includes('stale_generation'), 'Old generation was not rejected');
  await idle(finalTurn.runId);
  console.log('ok real provider received answer; reask rejected stale answer; chat answer recorded without echo');
  // Read the owning session's actual chat through the same bulk path used by the session view.
  const bulk = await open(env.CHECK4_API!, env.CHECK4_TOKEN!, '/socket/bulk');
  const chat = await bulk.call('rows', 'page', { session, limit: 100 });
  requireValue(chat.rows?.some((row: any) => row.kind === 'user'), 'Recovery session chat is unavailable');
  const sourceSeq = (await detail()).feed[0].seq as number;
  while (true) {
    const frame = await desktop.take(f => f.t === 'change' && f.sub === board.id && f.item?.taskKey === key && f.item?.machineId === machineId);
    if (frame.item.seq >= sourceSeq) break;
  }
  await pageContains(desktop, machineId, key, 'running');
  const routed = await desktop.call<Detail>('board', 'task', { machineId, taskKey: key, beforeSeq: null, questionCursor: null });
  requireValue(routed.task.creatorSessionId === session && routed.task.machineId === machineId, 'Recovery link/source identity differs');
  console.log('ok desktop SQL mirror, source detail and actual recovery chat agree');
}
try { await main(); }
catch (error) { console.log('fail board: ' + (error instanceof Error ? error.message : String(error))); process.exitCode = 1; }
finally { clearTimeout(timer); for (const client of clients) client.close(); }
