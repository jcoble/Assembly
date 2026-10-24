import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { createInterface } from 'node:readline';
import { readFileSync, readdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { once } from 'node:events';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';

// Run manually from any worktree: node --experimental-strip-types tauri-svelte-preview/scripts/mcpCredential.integration.test.ts
// Requires signed-in local provider CLIs. No native app or saved machine is opened.
const root = fileURLToPath(new URL('../..', import.meta.url));
const cwd = mkdtempSync(join(tmpdir(), 'assembly-mcp-credential-'));
const claudePath = execFileSync('which', ['claude'], { encoding: 'utf8' }).trim();
const codexPath = execFileSync('which', ['codex'], { encoding: 'utf8' }).trim();
const token = randomBytes(32).toString('hex');
let calls: string[] = [];
const server = createServer(async (req, res) => {
  if (req.headers.authorization !== `Bearer ${token}`) { res.writeHead(401).end(); return; }
  if (req.method === 'GET') { res.writeHead(405).end(); return; }
  let text = ''; for await (const chunk of req) text += chunk;
  if (!text) { res.writeHead(202).end(); return; }
  const body = JSON.parse(text); calls.push(body.method);
  if (body.id == null) { res.writeHead(202).end(); return; }
  let result: unknown;
  if (body.method === 'initialize') result = { protocolVersion: body.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'assembly-test', version: '1' } };
  else if (body.method === 'tools/list') result = { tools: [{ name: 'proof', description: 'Return proof that this request authenticated.', inputSchema: { type: 'object', properties: {} } }] };
  else if (body.method === 'tools/call') result = { content: [{ type: 'text', text: 'authenticated-proof' }] };
  else result = {};
  res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ jsonrpc: '2.0', id: body.id, result }));
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const url = `http://127.0.0.1:${(server.address() as any).port}/mcp`;
const mcpServers = [{ type: 'http', name: 'assembly', url, headers: [{ name: 'Authorization', value: `Bearer ${token}` }] }];

function inspectTree(pid: number) {
  const processes = readdirSync('/proc').filter(name => /^\d+$/.test(name)).flatMap(name => {
    try {
      const status = readFileSync(`/proc/${name}/status`, 'utf8');
      return [{ pid: Number(name), parent: Number(status.match(/^PPid:\s+(\d+)/m)?.[1]), args: '' }];
    } catch { return []; }
  });
  const owned = new Set([pid]);
  for (let i = 0; i < 10; i++) for (const process of processes) if (owned.has(process.parent)) owned.add(process.pid);
  const descendants = processes.filter(process => owned.has(process.pid));
  assert.ok(descendants.length >= 2, 'provider CLI must be running');
  for (const process of descendants) assert.equal(readFileSync(`/proc/${process.pid}/cmdline`, 'utf8').includes(token), false, 'raw token must not be in any owned argv');
  return descendants.map(process => process.pid);
}

async function phase(provider: string, method: string, sessionId?: string) {
  calls = [];
  const args = provider === 'claude'
    ? ['--experimental-strip-types', `${root}/tauri-svelte-preview/scripts/claudeAcpLauncher.ts`]
    : [`${root}/tauri-svelte-preview/node_modules/@agentclientprotocol/codex-acp/dist/index.js`];
  const child = spawn('node', args, { cwd, detached: true, env: { ...process.env, CLAUDE_CODE_EXECUTABLE: claudePath, CODEX_PATH: codexPath }, stdio: ['pipe', 'pipe', 'pipe'] });
  const ownedPids = new Set<number>([child.pid!]);
  let stderr = ''; child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-4000); });
  const pending = new Map<number, { resolve: (value: any) => void; reject: (error: Error) => void }>();
  let nextId = 0;
  const reader = createInterface({ input: child.stdout });
  reader.on('line', line => {
    try {
      const message = JSON.parse(line);
      if (message.id != null && message.method) {
        const result = message.method === 'session/request_permission'
          ? { outcome: { outcome: 'selected', optionId: message.params.options.find((option: any) => option.kind === 'allow_once')?.optionId ?? message.params.options[0].optionId } }
          : {};
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: message.id, result }) + '\n');
      } else if (message.id != null) {
        const waiter = pending.get(message.id); pending.delete(message.id);
        if (message.error) waiter?.reject(new Error(JSON.stringify(message.error))); else waiter?.resolve(message.result);
      }
    } catch (error) { console.error('parse error', error); }
  });
  const request = async (method: string, params: any) => {
    const id = ++nextId;
    let timer: ReturnType<typeof setTimeout>;
    try {
      return await new Promise<any>((resolve, reject) => {
        timer = setTimeout(() => reject(new Error(`${method} timed out: ${stderr.replaceAll(token, '<redacted>')}`)), 90_000);
        pending.set(id, { resolve, reject }); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
      });
    } finally { clearTimeout(timer!); pending.delete(id); }
  };
  try {
    await request('initialize', { protocolVersion: 1, clientCapabilities: {}, clientInfo: { name: 'assembly-security-check', version: '1' } });
    const params: any = { cwd, mcpServers, _meta: { claudeCode: { options: { allowedTools: ['mcp__assembly'], strictMcpConfig: true } } } };
    if (sessionId) params.sessionId = sessionId;
    const result = await request(method, params);
    const id = result?.sessionId ?? sessionId;
    assert.ok(id, 'session id');
    const pids = inspectTree(child.pid!);
    for (const pid of pids) ownedPids.add(pid);
    await request('session/prompt', { sessionId: id, prompt: [{ type: 'text', text: 'Call the assembly proof tool exactly once. Do not use any other tool. Reply with its result.' }] });
    for (const pid of inspectTree(child.pid!)) ownedPids.add(pid);
    assert.ok(calls.includes('tools/call'), `authenticated tools/call required, received ${JSON.stringify(calls)}`);
    console.log(`${provider} ${method}: authenticated tools/call, token absent from argv (${pids.length} owned processes)`);
    return id;
  } finally {
    if (child.exitCode === null && child.signalCode === null) {
      try { for (const pid of inspectTree(child.pid!)) ownedPids.add(pid); } catch {}
    }
    reader.close(); child.stdin.end();
    // Provider SDKs can give children their own process groups; stop each recorded PID.
    for (const pid of [...ownedPids].reverse()) { try { process.kill(pid, 'SIGKILL'); } catch {} }
    try { process.kill(-child.pid!, 'SIGTERM'); } catch {}
    if (child.exitCode === null && child.signalCode === null) await once(child, 'exit');
    try { process.kill(-child.pid!, 'SIGKILL'); } catch {}
    for (const pid of ownedPids) {
      try {
        const status = readFileSync(`/proc/${pid}/status`, 'utf8');
        if (!/^State:\s+Z/m.test(status)) throw new Error(`Owned process ${pid} has not exited after termination`);
      } catch (error: any) { if (error.code !== 'ENOENT') throw error; }
    }
    console.log(`${provider} ${method}: owned process tree stopped`);
  }
}
try {
  for (const provider of process.argv.slice(2).length ? process.argv.slice(2) : ['claude', 'codex']) {
    const session = await phase(provider, 'session/new');
    await phase(provider, 'session/load', session);
    await phase(provider, 'session/resume', session);
  }
} finally { server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); rmSync(cwd, { recursive: true, force: true }); }
