import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const cli = new URL('./cli.ts', import.meta.url);
function fixture(t: any) {
  const home = mkdtempSync(join(tmpdir(), 'runbook-test-'));
  t.after(() => rmSync(home, { recursive: true, force: true }));
  const cwd = join(home, 'Assembly');
  mkdirSync(cwd);
  function run(args: string[], input = '') {
    return spawnSync(process.execPath, [cli.pathname, ...args], {
      cwd, input, encoding: 'utf8', env: { ...process.env, HOME: home },
    });
  }
  function hook(provider: string, event: any) {
    const r = run(['hook', provider], JSON.stringify({ cwd, session_id: 'parent', ...event }));
    assert.equal(r.status, 0, r.stderr);
    return r.stdout ? JSON.parse(r.stdout) : {};
  }
  return { home, cwd, run, hook };
}
test('both providers reload short rules and real state, not full instruction files', t => {
  const f = fixture(t);
  writeFileSync(join(f.cwd, 'AGENTS.md'), 'FULL_INSTRUCTION_SENTINEL');
  for (const provider of ['codex', 'claude']) {
    const started = f.hook(provider, { hook_event_name: 'SessionStart', source: 'startup' });
    assert.match(started.hookSpecificOutput.additionalContext, /Recovery rules/);
    const update = f.run(['update', provider, 'parent'], 'Task: https://app.notion.com/p/test\nNext: prove saved state.');
    assert.equal(update.status, 0, update.stderr);
    f.hook(provider, { hook_event_name: 'PreCompact', trigger: 'manual' });
    const context = f.hook(provider, { hook_event_name: 'SessionStart', source: 'compact' }).hookSpecificOutput.additionalContext;
    assert.match(context, /Next: prove saved state/);
    assert.match(context, /Automatic checkpoint/);
    assert.doesNotMatch(context, /FULL_INSTRUCTION_SENTINEL/);
    assert.ok(context.indexOf('Recovery rules') < context.indexOf('Next:'));
    assert.equal(context.match(/^# Recovery rules$/gm)?.length, 1, 'Managed recovery must not duplicate rules.');
    assert.ok(context.length <= 13000);
  }
});
test('updates preserve rules, are atomic and reject oversize without changing the file', t => {
  const f = fixture(t);
  f.hook('codex', { hook_event_name: 'SessionStart', source: 'startup' });
  const path = f.run(['read', 'codex', 'parent']).stdout;
  assert.match(path, /Never edit.*AGENTS/);
  assert.equal(f.run(['update', 'codex', 'parent'], 'First complete state').status, 0);
  const before = f.run(['read', 'codex', 'parent']).stdout;
  assert.notEqual(f.run(['update', 'codex', 'parent'], 'x'.repeat(12001)).status, 0);
  assert.equal(f.run(['read', 'codex', 'parent']).stdout, before);
  const stateFiles = f.run(['where', 'codex', 'parent']);
  const binding = JSON.parse(stateFiles.stdout);
  const inode = statSync(binding.path).ino;
  assert.equal(f.run(['update', 'codex', 'parent'], 'Second complete state').status, 0);
  assert.notEqual(statSync(binding.path).ino, inode);
  assert.match(readFileSync(binding.path, 'utf8'), /^# Recovery rules/);
  assert.equal(statSync(binding.path).mode & 0o777, 0o600);
});
test('subagent inherits parent runbook read-only and sessions remain isolated', t => {
  const f = fixture(t);
  f.hook('codex', { hook_event_name: 'SessionStart', source: 'startup' });
  f.run(['update', 'codex', 'parent'], 'Parent state, approved step 2.');
  const child = f.hook('codex', { hook_event_name: 'SubagentStart', agent_id: 'child', agent_type: 'worker' });
  assert.match(child.hookSpecificOutput.additionalContext, /Parent state, approved step 2/);
  assert.match(child.hookSpecificOutput.additionalContext, /Check with.*parent/);
  assert.notEqual(f.run(['update', 'codex', 'child'], 'bad overwrite').status, 0);
  const binding = JSON.parse(f.run(['where', 'codex', 'child']).stdout);
  assert.equal(binding.parent, 'codex:parent');
  assert.equal(binding.readOnly, true);
  const before = f.run(['read', 'codex', 'parent']).stdout;
  f.hook('codex', { hook_event_name: 'PreCompact', session_id: 'parent', agent_id: 'child', trigger: 'auto' });
  assert.equal(f.run(['read', 'codex', 'parent']).stdout, before);
  f.hook('codex', { hook_event_name: 'SessionStart', session_id: 'other', source: 'startup' });
  f.run(['update', 'codex', 'other'], 'Unrelated run');
  assert.equal(f.run(['read', 'codex', 'parent']).stdout, before);
});
test('checkpoint labels transcript excerpts as observations, handles malformed tails and read errors visibly', t => {
  const f = fixture(t);
  f.hook('claude', { hook_event_name: 'SessionStart', source: 'startup' });
  const transcript = join(f.home, 'transcript.jsonl');
  writeFileSync(transcript, [
    JSON.stringify({ type: 'user', message: { content: 'Keep proof honest' } }),
    'broken record',
    JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: 'Testing is still blocked' }] } }),
  ].join('\n'));
  f.hook('claude', { hook_event_name: 'PreCompact', transcript_path: transcript, trigger: 'auto' });
  const text = f.run(['read', 'claude', 'parent']).stdout;
  assert.match(text, /observations.*not approved decisions/i);
  assert.match(text, /Testing is still blocked/);
  const binding = JSON.parse(f.run(['where', 'claude', 'parent']).stdout);
  rmSync(binding.path);
  const failed = f.hook('claude', { hook_event_name: 'SessionStart', source: 'compact' });
  assert.match(failed.hookSpecificOutput.additionalContext, /RECOVERY BLOCKED/);
  assert.match(failed.hookSpecificOutput.additionalContext, /Recovery rules/);
});

test('installer is idempotent and preserves unrelated hooks', t => {
  const f = fixture(t);
  mkdirSync(join(f.home, '.claude'), { recursive: true });
  mkdirSync(join(f.home, '.codex'), { recursive: true });
  const existing = { hooks: { SessionStart: [{ hooks: [{ type: 'command', command: 'existing-session-hook' }] }], PostToolUse: [{ hooks: [{ type: 'command', command: 'existing-tool-hook' }] }] }, unrelated: true };
  for (const name of ['.claude/settings.json', '.codex/hooks.json']) writeFileSync(join(f.home, name), JSON.stringify(existing));
  for (let i = 0; i < 2; i++) {
    const r = spawnSync(process.execPath, [new URL('./install.ts', import.meta.url).pathname, '--without-codex-trust'], { cwd: f.cwd, encoding: 'utf8', env: { ...process.env, HOME: f.home } });
    assert.equal(r.status, 0, r.stderr);
  }
  for (const name of ['.claude/settings.json', '.codex/hooks.json']) {
    const config = JSON.parse(readFileSync(join(f.home, name), 'utf8'));
    assert.equal(config.unrelated, true);
    assert.equal(config.hooks.SessionStart.length, 2);
    assert.equal(config.hooks.SessionStart[0].hooks[0].command, 'existing-session-hook');
    assert.equal(config.hooks.PostToolUse[0].hooks[0].command, 'existing-tool-hook');
    assert.equal(config.hooks.PreCompact.length, 1);
  }
});
test('child recovery with shared session_id never checkpoints parent and missing bindings block explicitly', t => {
  const f = fixture(t);
  f.hook('claude', { hook_event_name: 'SessionStart', source: 'startup' });
  f.run(['update', 'claude', 'parent'], 'Parent approved state');
  f.hook('claude', { hook_event_name: 'SubagentStart', agent_id: 'child', agent_type: 'worker' });
  const before = f.run(['read', 'claude', 'parent']).stdout;
  f.hook('claude', { hook_event_name: 'PreCompact', agent_id: 'child', trigger: 'auto' });
  assert.equal(f.run(['read', 'claude', 'parent']).stdout, before);
  const context = f.hook('claude', { hook_event_name: 'SessionStart', source: 'compact', agent_id: 'child' }).hookSpecificOutput.additionalContext;
  assert.match(context, /read-only/);
  const codex = f.hook('codex', { hook_event_name: 'PreCompact', session_id: 'unknown', trigger: 'auto' });
  assert.equal(codex.continue, false);
  const claude = f.run(['hook', 'claude'], JSON.stringify({ cwd: f.cwd, session_id: 'unknown', hook_event_name: 'PreCompact', trigger: 'auto' }));
  assert.equal(claude.status, 2);
  assert.equal(JSON.parse(claude.stdout).decision, 'block');
});

test('remote bindings use bounded read-only SSH and expose loss of access', t => {
  const f = fixture(t);
  const bin = join(f.home, 'bin'); mkdirSync(bin);
  const output = join(f.home, 'remote-output');
  writeFileSync(output, 'Task: remote parent\nNext: remote parent state');
  writeFileSync(join(bin, 'ssh'), '#!/bin/sh\ncat "$REMOTE_OUTPUT"\n', { mode: 0o755 });
  function run(args: string[], input = '') {
    return spawnSync(process.execPath, [cli.pathname, ...args], { cwd: f.cwd, input, encoding: 'utf8', env: { ...process.env, HOME: f.home, PATH: bin + ':' + process.env.PATH, REMOTE_OUTPUT: output } });
  }
  const bind = run(['bind', 'codex', 'remote-child', '--path', "/home/owner/it's a runbook.md", '--ssh', 'workbox', '--parent', 'claude:parent']);
  assert.equal(bind.status, 0, bind.stderr);
  const success = run(['hook', 'codex'], JSON.stringify({ cwd: f.cwd, session_id: 'remote-child', hook_event_name: 'SessionStart', source: 'compact' }));
  const context = JSON.parse(success.stdout).hookSpecificOutput.additionalContext;
  assert.ok(context.startsWith('# Recovery rules\n'));
  assert.match(context, /Next: remote parent state/);
  assert.equal(readFileSync(output, 'utf8'), 'Task: remote parent\nNext: remote parent state');
  assert.notEqual(run(['update', 'codex', 'remote-child'], 'overwrite').status, 0);
  writeFileSync(join(bin, 'ssh'), '#!/bin/sh\nexit 255\n', { mode: 0o755 });
  const recovered = run(['hook', 'codex'], JSON.stringify({ cwd: f.cwd, session_id: 'remote-child', hook_event_name: 'SessionStart', source: 'compact' }));
  assert.equal(recovered.status, 0);
  assert.match(JSON.parse(recovered.stdout).hookSpecificOutput.additionalContext, /RECOVERY BLOCKED/);
});


test('plain bound runbooks always recover critical rules without changing the parent file', t => {
  const f = fixture(t);
  const path = join(f.home, 'plain-runbook.md');
  const state = 'Task: bound parent proof\nNext: keep the approved step';
  writeFileSync(path, state);
  for (const provider of ['codex', 'claude']) {
    const bound = f.run(['bind', provider, 'external-child', '--path', path, '--parent', provider + ':parent']);
    assert.equal(bound.status, 0, bound.stderr);
    const context = f.hook(provider, { hook_event_name: 'SessionStart', source: 'compact', session_id: 'external-child' }).hookSpecificOutput.additionalContext;
    assert.ok(context.startsWith('# Recovery rules\n'), 'Bound recovery must start with the condensed critical rules.');
    assert.match(context, /Never edit AGENTS/);
    assert.match(context, /Next: keep the approved step/);
    assert.match(context, /read-only/);
    assert.equal(readFileSync(path, 'utf8'), state, 'Recovery must not modify the parent file.');
  }
});
