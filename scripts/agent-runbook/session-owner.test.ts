import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdirSync, writeFileSync, readFileSync, symlinkSync, existsSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { fixture } from './test-fixture.ts';

function setup(t: any, provider = 'claude') {
  const f = fixture(t), executable = join(f.bin, provider), driver = join(f.home, 'driver.cjs');
  symlinkSync(process.execPath, executable);
  writeFileSync(driver, `const {spawnSync} = require('node:child_process');
process.env.OWNER_TEST_PARENT = String(process.pid);
const result = spawnSync(${JSON.stringify(process.execPath)}, [${JSON.stringify(new URL('./cli.ts', import.meta.url).pathname)}, 'hook', ${JSON.stringify(provider)}], {input: process.argv[2], encoding: 'utf8'});
process.stdout.write(JSON.stringify({...result, pid: process.pid, startTime: process.env.OWNER_TEST_START || require('node:child_process').execFileSync('ps', ['-p', String(process.pid), '-o', 'lstart='], {encoding:'utf8'}).trim()}));`);
  const owner = join(f.home, '.local/state/agent-runbook/owners', provider, createHash('sha256').update('parent').digest('hex'));
  const seed = (pid: number, startTime: string) => { mkdirSync(join(owner, '..'), { recursive: true }); writeFileSync(owner, JSON.stringify({ pid, startTime })); };
  const hook = (event = {}, extra = {}) => {
    const result = spawnSync(executable, [driver, JSON.stringify({ cwd: f.cwd, session_id: 'parent', hook_event_name: 'SessionStart', ...event })], { cwd: f.cwd, env: { ...f.env, ...extra }, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const child = JSON.parse(result.stdout); assert.equal(child.status, 0, child.stderr); assert.equal(child.stderr, '');
    return { ...child, context: JSON.parse(child.stdout).hookSpecificOutput?.additionalContext || '' };
  };
  return { ...f, owner, seed, hook };
}
async function live(t: any, inspect = true) {
  const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
  await once(child, 'spawn');
  t.after(async () => { if (child.exitCode === null && child.signalCode === null) { const exited = once(child, 'exit'); child.kill(); await exited; } });
  return { child, pid: child.pid!, startTime: inspect ? execFileSync('ps', ['-p', String(child.pid), '-o', 'lstart='], { encoding: 'utf8' }).trim() : 'Wed Nov  4 07:00:00 2026' };
}
test('session_owner_warns_when_other_copy_alive', async t => {
  const other = await live(t);
  for (const provider of ['claude', 'codex']) {
    const f = setup(t, provider); f.seed(other.pid, other.startTime);
    const result = f.hook();
    assert.ok(result.context.endsWith(`Another running copy of this session already exists (pid ${other.pid}). Do not change anything or ask Jesse anything until you have messaged it and agreed who continues.`));
    assert.deepEqual(JSON.parse(readFileSync(f.owner, 'utf8')), { pid: result.pid, startTime: result.startTime });
  }
});
test('session_owner_silent_when_previous_copy_dead', async t => {
  const f = setup(t), other = await live(t), exited = once(other.child, 'exit'); other.child.kill(); await exited;
  f.seed(other.pid, other.startTime); assert.doesNotMatch(f.hook().context, /Another running copy/);
});
test('session_owner_silent_when_pid_reused', async t => {
  const f = setup(t), other = await live(t); f.seed(other.pid, 'different start time');
  assert.doesNotMatch(f.hook().context, /Another running copy/);
});
test('session_owner_silent_for_subagent', async t => {
  const f = setup(t), other = await live(t); f.seed(other.pid, other.startTime);
  const before = readFileSync(f.owner, 'utf8');
  for (const event of [{ agent_id: 'child' }, { hook_event_name: 'SubagentStart', agent_id: 'child' }]) {
    assert.doesNotMatch(f.hook(event).context, /Another running copy/); assert.equal(readFileSync(f.owner, 'utf8'), before);
  }
});
test('session_owner_failure_does_not_break_hook', t => {
  const f = setup(t); mkdirSync(join(f.owner, '../..'), { recursive: true }); writeFileSync(join(f.owner, '..'), 'blocked');
  // Simulate unavailable process inspection without depending on host ps access.
  writeFileSync(join(f.bin, 'ps'), '#!/bin/sh\necho unavailable >&2\nexit 1\n', { mode: 0o755 });
  const result = f.run(['hook', 'claude'], JSON.stringify({ cwd: f.cwd, session_id: 'parent', hook_event_name: 'SessionStart' }));
  assert.equal(result.status, 0); assert.equal(result.stderr, '');
  assert.match(JSON.parse(result.stdout).hookSpecificOutput.additionalContext, /Recovery rules/);
});
test('session_owner_end_only_removes_own_record', async t => {
  const f = setup(t), other = await live(t); f.seed(other.pid, other.startTime);
  f.hook({ hook_event_name: 'SessionEnd' }); assert.ok(existsSync(f.owner));
  const script = join(f.home, 'end.cjs');
  writeFileSync(script, `const {spawnSync} = require('node:child_process'); const args = [${JSON.stringify(new URL('./cli.ts', import.meta.url).pathname)}, 'hook', 'claude']; for (const hook_event_name of ['SessionStart', 'SessionEnd']) { const r = spawnSync(${JSON.stringify(process.execPath)}, args, {input:JSON.stringify({cwd:process.cwd(),session_id:'parent',hook_event_name}),encoding:'utf8'}); if(r.status) process.exit(r.status); }`);
  assert.equal(spawnSync(join(f.bin, 'claude'), [script], { cwd: f.cwd, env: f.env }).status, 0);
  assert.ok(!existsSync(f.owner));
});
test('session_owner_process_table_fixture_covers_warning_and_write_failure', async t => {
  const other = await live(t, false);
  for (const provider of ['claude', 'codex']) {
    const f = setup(t, provider);
    // This supplements, rather than replaces, the native ps tests above.
    writeFileSync(join(f.bin, 'ps'), `#!/bin/sh
if [ "$2" = "$OWNER_TEST_PARENT" ]; then
  printf '1 %s %s\n' "$OWNER_TEST_START" "$OWNER_TEST_EXECUTABLE"
elif [ "$2" = "$OWNER_TEST_PREVIOUS" ] && kill -0 "$2" 2>/dev/null; then
  printf '1 %s node\n' "$OWNER_TEST_START"
else exit 1; fi
`, { mode: 0o755 });
    const env = { OWNER_TEST_START: other.startTime, OWNER_TEST_PREVIOUS: String(other.pid), OWNER_TEST_EXECUTABLE: join(f.bin, provider) };
    f.seed(other.pid, other.startTime);
    const result = f.hook({}, env); assert.match(result.context, /Another running copy/);
    assert.deepEqual(JSON.parse(readFileSync(f.owner, 'utf8')), { pid: result.pid, startTime: other.startTime });
    f.seed(other.pid, 'reused pid'); assert.doesNotMatch(f.hook({}, env).context, /Another running copy/);
    f.seed(other.pid, other.startTime); const before = readFileSync(f.owner, 'utf8');
    assert.doesNotMatch(f.hook({ agent_id: 'child' }, env).context, /Another running copy/); assert.equal(readFileSync(f.owner, 'utf8'), before);
    f.hook({ hook_event_name: 'SessionEnd' }, env); assert.equal(readFileSync(f.owner, 'utf8'), before);
    // An owner directory in place of the record forces atomicWrite to fail.
    rmSync(f.owner); mkdirSync(f.owner);
    assert.match(f.hook({}, env).context, /Recovery rules/);
  }
});
