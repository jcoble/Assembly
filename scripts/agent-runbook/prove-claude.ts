import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir, homedir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const sourceHome = homedir();
const home = mkdtempSync(join(tmpdir(), 'runbook-claude-proof-'));
const config = join(home, '.claude');
mkdirSync(config);
const launcher = join(sourceHome, '.local/bin/agent-runbook');
const binary = process.env.RUNBOOK_CLAUDE || join(sourceHome, '.local/bin/claude');
const settings = join(config, 'settings.json');
const trace = join(home, 'hook-events.jsonl');
const wrapper = join(home, 'trace-hook.ts');
writeFileSync(join(home, 'package.json'), '{"type":"module"}');
writeFileSync(wrapper, `import {readFileSync, appendFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
const input=readFileSync(0,'utf8');const event=JSON.parse(input);
const result=spawnSync(${JSON.stringify(launcher)},['hook','claude'],{input,encoding:'utf8',env:{...process.env,HOME:${JSON.stringify(home)}}});
let context='';try{context=JSON.parse(result.stdout).hookSpecificOutput?.additionalContext||'';}catch{}
appendFileSync(${JSON.stringify(trace)},JSON.stringify({event:event.hook_event_name,source:event.source,status:result.status,contextCharacters:context.length,hasRules:context.includes('# Recovery rules'),context})+'\\n');
process.stdout.write(result.stdout||'');process.stderr.write(result.stderr||'');process.exitCode=result.status||0;
`);
const command = "'" + process.execPath + "' '" + wrapper + "'";
writeFileSync(settings, JSON.stringify({ hooks: { SessionStart: [{ hooks: [{ type: 'command', command, timeout: 8 }] }], PreCompact: [{ hooks: [{ type: 'command', command, timeout: 8 }] }] } }));
// Preserve the native login profile; forcing the default path selects a different macOS Keychain entry.
const env = { ...process.env, HOME: sourceHome };
const runbookEnv = { ...env, HOME: home };
const id = randomUUID();
function run(args: string[]) {
  const result = spawnSync(binary, ['--print', '--output-format', 'json', '--setting-sources', '', '--settings', settings, '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}', '--tools', '', '--model', 'claude-sonnet-5-5', ...args], { cwd: home, env, encoding: 'utf8', timeout: 45000 });
  if (result.status !== 0) throw Error('Claude command failed: ' + String(result.stderr || result.stdout).slice(-1600));
  let json: any;
  try { json = JSON.parse(result.stdout); } catch { throw Error('Claude did not return JSON: ' + result.stdout.slice(-1600)); }
  const providerResult = [json].flat().findLast(row => row?.type === 'result');
  assert.ok(providerResult && typeof providerResult.result === 'string', 'Claude did not return a structured result.');
  if (providerResult.is_error) throw Error('Claude provider error: ' + providerResult.result);
  return providerResult;
}
try {
  run(['--session-id', id, 'Reply READY only.']);
  const sentinel = 'resume-' + randomUUID().slice(0, 8);
  const saved = spawnSync(launcher, ['update', 'claude', id], { env: runbookEnv, input: 'Task: isolated provider proof.\nNext: ' + sentinel, encoding: 'utf8' });
  assert.equal(saved.status, 0, saved.stderr);
  const compact = run(['--resume', id, '/compact']);
  const resumed = run(['--resume', id, 'Reply only with the Next value from your injected live runbook.']);
  assert.ok(resumed.result.includes(sentinel), 'Post-compaction model did not receive the saved runbook marker.');
  const read = spawnSync(launcher, ['read', 'claude', id], { env: runbookEnv, encoding: 'utf8' });
  assert.match(read.stdout, /Checkpoint:/);
  const events = readFileSync(trace, 'utf8').trim().split('\n').map(line => JSON.parse(line));
  assert.ok(events.some(e => e.event === 'PreCompact' && e.status === 0));
  assert.ok(events.some(e => e.event === 'SessionStart' && e.source === 'compact' && e.hasRules && e.context.includes(sentinel)), 'Real compact recovery did not inject rules and saved state.');
  console.log(JSON.stringify({ hooks: events.map(({context, ...event}) => event), provider: 'claude', host: process.platform, session: id, result: 'PASS', savedNext: sentinel, modelReply: resumed.result.trim(), recoveryRulesPresent: true, model: Object.keys(resumed.modelUsage || {}), checkpointSaved: true, postCompactModelReadSavedState: true, compactResult: String(compact.result).slice(0,200), runbookCharacters: read.stdout.length }, null, 2));
} finally { rmSync(home, { recursive: true, force: true }); }
