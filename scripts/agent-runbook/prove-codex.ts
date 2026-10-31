import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync, copyFileSync } from 'node:fs';
import { tmpdir, homedir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { withCodex } from './codex-rpc.ts';

const home = mkdtempSync(join(tmpdir(), 'runbook-codex-proof-'));
const codexHome = join(home, '.codex');
mkdirSync(codexHome);
const launcher = join(homedir(), '.local/bin/agent-runbook');
const binary = process.env.RUNBOOK_CODEX || join(homedir(), '.local/bin/codex');
const hook = { type: 'command', command: "'" + launcher + "' hook codex", timeout: 8, additionalContextLimit: 4000 };
writeFileSync(join(codexHome, 'hooks.json'), JSON.stringify({ hooks: { SessionStart: [{ hooks: [hook] }], PreCompact: [{ hooks: [hook] }] } }));
writeFileSync(join(codexHome, 'config.toml'), '[features]\nhooks = true\n');
const auth = join(homedir(), '.codex/auth.json');
if (existsSync(auth)) copyFileSync(auth, join(codexHome, 'auth.json'));
const env = { ...process.env, HOME: home, CODEX_HOME: codexHome };
const provider = 'codex';
const events: any[] = [];
let onComplete: (message: any) => void = () => {};
async function complete(action: () => Promise<any>) {
  let done: (message: any) => void;
  let fail: (error: Error) => void;
  const promise = new Promise<any>((resolve, reject) => { done = resolve; fail = reject; });
  onComplete = message => done(message);
  const timer = setTimeout(() => fail(Error('No provider completion event within 30 seconds.')), 30000);
  try { await action(); const result = await promise; assert.equal(result.params.turn.status, 'completed', JSON.stringify(result.params.turn.error)); return result.params.turn; }
  finally { clearTimeout(timer); onComplete = () => {}; }
}
try {
  await withCodex(binary, async request => {
    const entries = await request('hooks/list', { cwds: [home] });
    await request('config/batchWrite', { edits: entries.data[0].hooks.map((h: any) => ({ keyPath: 'hooks.state.' + JSON.stringify(h.key), value: { trusted_hash: h.currentHash, enabled: true }, mergeStrategy: 'upsert' })) });
    const started = await request('thread/start', { cwd: home, ephemeral: true, model: 'gpt-6.1-sol', approvalPolicy: 'never', sandbox: 'read-only' });
    const id = started.thread.id;
    const turn = (text: string) => request('turn/start', { threadId: id, input: [{ type: 'text', text, text_elements: [] }] });
    await complete(() => turn('Reply READY only. Do not use tools.'));
    const sentinel = 'resume-' + randomUUID().slice(0, 8);
    const saved = spawnSync(launcher, ['update', 'codex', id], { env, input: 'Task: isolated provider proof.\nNext: ' + sentinel, encoding: 'utf8' });
    assert.equal(saved.status, 0, saved.stderr);
    await complete(() => request('thread/compact/start', { threadId: id }));
    const resumed = await complete(() => turn('Reply only with the Next value from your injected live runbook. Do not use tools.'));
    const text = resumed.items.filter((x: any) => x.type === 'agentMessage').map((x: any) => x.text).join('');
    assert.ok(text.includes(sentinel), 'Post-compaction model did not receive the saved runbook sentinel.');
    const contextEvents = events.filter(x => x.run?.eventName === 'sessionStart' && x.method === 'hook/completed');
    assert.equal(contextEvents.length, 2, 'Expected startup and compact recovery dispatch.');
    assert.ok(contextEvents[1].run.entries.some((x: any) => x.kind === 'context' && x.text.includes(sentinel) && x.text.includes('# Recovery rules')));
    const read = spawnSync(launcher, ['read', 'codex', id], { env, encoding: 'utf8' });
    assert.match(read.stdout, /Checkpoint:/);
    console.log(JSON.stringify({ provider, host: process.platform, thread: id, result: 'PASS', savedNext: sentinel, modelReply: text.trim(), recoveryRulesPresent: true, checkpointSaved: true, postCompactModelReadSavedState: true, hooks: events.filter(x => x.method === 'hook/completed').map(x => ({ event: x.run.eventName, status: x.run.status, durationMs: x.run.durationMs, contextCharacters: x.run.entries.filter((e: any) => e.kind === 'context').reduce((n: number,e: any) => n + e.text.length,0) })) }, null, 2));
  }, env, message => {
    if ((message.method || '').startsWith('hook/')) events.push({ method: message.method, run: message.params?.run });
    if (message.method === 'turn/completed') onComplete(message);
  });
} finally { rmSync(home, { recursive: true, force: true }); }
