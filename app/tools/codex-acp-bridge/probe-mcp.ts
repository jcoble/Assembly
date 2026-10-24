// Task 6 can rerun this against signed adapters; prepare dummy.py/config.json/calls.jsonl/port in --root.
// Disposable ACP/MCP and app-server contract probe. No provider calls run locally when --ssh is set.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { readFileSync } from 'node:fs';

type Json = Record<string, any>; // JSON-RPC payloads vary between ACP and app-server.
const args = process.argv.slice(2);
const option = (name: string) => args[args.indexOf(name) + 1];
const remote = args.includes('--ssh') ? option('--ssh') : undefined;
const root = option('--root');
const shellQuote = (s: string) => "'" + s.replaceAll("'", "'\\''") + "'";
function read(name: string): string {
  return remote ? execFileSync('ssh', [remote, 'cat ' + shellQuote(root + '/' + name)], { encoding: 'utf8' }) : readFileSync(root + '/' + name, 'utf8');
}
class Peer {
  child;
  pending = new Map<number, { resolve: (value: Json) => void; reject: (error: Error) => void }>();
  next = 0;
  text = '';
  events: Json[] = [];
  constructor(command: string, argv: string[] = []) {
    const invocation = 'export PATH="$HOME/.local/bin:$PATH"; exec ' + [command, ...argv].map(shellQuote).join(' ');
    this.child = remote ? spawn('ssh', ['-T', remote, invocation]) : spawn(command, argv);
    this.child.stderr.resume(); // Provider stderr may contain private context; never log it.
    const lines = createInterface({ input: this.child.stdout });
    lines.on('line', (line) => {
      const m: Json = JSON.parse(line);
      if (m.method) {
        this.events.push(m);
        if (m.method === 'session/update' && m.params.update.content?.text) this.text += m.params.update.content.text;
        if (m.id !== undefined) {
          const selected = m.params?.options?.find((o: Json) => o.kind === 'allow_once') ?? m.params?.options?.[0];
          this.send({ jsonrpc: '2.0', id: m.id, result: selected ? { outcome: { outcome: 'selected', optionId: selected.optionId } } : {} });
        }
      } else {
        const p = this.pending.get(m.id);
        this.pending.delete(m.id);
        if (m.error) p?.reject(new Error(JSON.stringify(m.error))); else p?.resolve(m.result);
      }
    });
    this.child.on('close', () => { for (const p of this.pending.values()) p.reject(new Error('Transport closed')); this.pending.clear(); });
  }
  send(message: Json) { this.child.stdin.write(JSON.stringify(message) + '\n'); }
  async request(method: string, params: Json): Promise<Json> {
    const id = ++this.next;
    const result = new Promise<Json>((resolve, reject) => this.pending.set(id, { resolve, reject }));
    this.send({ jsonrpc: '2.0', id, method, params });
    return result;
  }
  async stop() {
    if (this.child.exitCode !== null) return;
    const exit = new Promise<void>((resolve) => this.child.once('close', () => resolve()));
    this.child.stdin.end();
    await exit;
  }
}
const config: Json = JSON.parse(read('config.json'));
const peers: Peer[] = [];
let turnTimer: ReturnType<typeof setInterval> | undefined;
const watchdog = setTimeout(() => { for (const p of peers) p.child.kill('SIGTERM'); }, 240_000);
try {
  if (args.includes('--adapter')) {
    const adapter = option('--adapter');
    const initial = new Peer(adapter); peers.push(initial);
    const initialized = await initial.request('initialize', { protocolVersion: 1, clientCapabilities: {}, clientInfo: { name: 'tsk1416-probe', version: '1' } });
    console.log(JSON.stringify({ adapter, initialize: initialized }));
    await initial.stop();
    for (const transport of ['stdio', 'http']) {
      if (transport === 'http' && !initialized.agentCapabilities.mcpCapabilities?.http) {
        console.log(JSON.stringify({ transport, verdict: 'Not advertised' })); continue;
      }
      const server = transport === 'stdio' ? { name: 'probe', command: 'python3', args: [root + '/dummy.py', 'stdio'], env: [] } : { type: 'http', name: 'probe', url: 'http://127.0.0.1:' + read('port').trim() + '/mcp', headers: [{ name: 'Authorization', value: 'Bearer ' + config.token }] };
      let sessionId = '';
      for (const phase of ['new', 'load']) {
        const p = new Peer(adapter); peers.push(p);
        await p.request('initialize', { protocolVersion: 1, clientCapabilities: {} });
        const session = await p.request('session/' + phase, { cwd: root, mcpServers: [server], ...(sessionId ? { sessionId } : {}) });
        sessionId ||= session.sessionId;
        p.text = '';
        const before = read('calls.jsonl').trim().split('\n').filter(Boolean).length;
        const result = await p.request('session/prompt', { sessionId, prompt: [{ type: 'text', text: 'Call the probe_nonce MCP tool now. Return only the nonce it gives you. Do not run shell commands or reuse a previous nonce.' }] });
        const calls = read('calls.jsonl').trim().split('\n').filter(Boolean).slice(before).map((line) => JSON.parse(line));
        const nonce = config[transport + 'Nonce'];
        const pass = calls.some((c: Json) => c.transport === transport && c.nonce === nonce) && p.text.includes(nonce);
        console.log(JSON.stringify({ adapter, transport, phase, sessionId, nonce, result, text: p.text, toolCalls: calls.length, verdict: pass ? 'PASS' : 'FAIL' }));
        await p.stop();
        assert.ok(pass, 'Real tool call and nonce required');
      }
    }
  }
  if (args.includes('--codex')) {
    const p = new Peer(option('--codex'), ['app-server']); peers.push(p);
    console.log(JSON.stringify({ appServerInitialize: await p.request('initialize', { clientInfo: { name: 'tsk1416-probe', version: '1' }, capabilities: { experimentalApi: true } }) }));
    p.send({ method: 'initialized', params: {} });
    const opened = await p.request('thread/start', { cwd: root, approvalPolicy: 'never', sandbox: 'danger-full-access' });
    const threadId = opened.thread.id;
    const started = await p.request('turn/start', { threadId, input: [{ type: 'text', text: 'Use the shell tool to run sleep 10, then answer ORIGINAL.' }] });
    const request = { threadId, expectedTurnId: started.turn.id, input: [{ type: 'text', text: 'Change your final answer to exactly STEERED_1416.' }] };
    try {
      const result = await p.request('turn/steer', request);
      console.log(JSON.stringify({ method: 'turn/steer', request, result, verdict: 'accepted' }));
      await new Promise<void>((resolve) => { turnTimer = setInterval(() => { if (p.events.some((e) => e.method === 'turn/completed' && e.params.turn.id === started.turn.id)) { clearInterval(turnTimer); resolve(); } }, 100); });
      const completed = p.events.filter((e) => e.method === 'item/completed' && e.params.item.type === 'agentMessage');
      console.log(JSON.stringify({ verdict: completed.some((e) => e.params.item.phase === 'final_answer' && e.params.item.text.trim() === 'STEERED_1416') ? 'steering verified on' : 'accepted; response change unverified', completed }));
    } catch (error) { console.log(JSON.stringify({ method: 'turn/steer', request, error: String(error), verdict: 'steering verified off' })); }
    await p.stop();
  }
} finally {
  clearTimeout(watchdog);
  clearInterval(turnTimer);
  for (const p of peers) await p.stop();
}
