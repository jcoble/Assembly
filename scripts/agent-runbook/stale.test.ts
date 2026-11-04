import assert from 'node:assert/strict';
import { test } from 'node:test';
import { writeFileSync, appendFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fixture } from './test-fixture.ts';

function transcript(f: ReturnType<typeof fixture>, count: number, codex = false) {
  const path = join(f.home, 'transcript.jsonl');
  writeFileSync(path, Array.from({ length: count }, (_, i) => JSON.stringify(codex
    ? { type: 'response_item', payload: { type: 'function_call', call_id: 'call-' + i } }
    : { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'call-' + i }] } })).join('\n') + '\n');
  return path;
}
test('stop_blocks_once_when_stale', t => {
  const f = fixture(t), path = transcript(f, 10);
  f.hook({ hook_event_name: 'SessionStart', transcript_path: path });
  const event = { hook_event_name: 'Stop', transcript_path: path, stop_hook_active: false };
  assert.equal(JSON.parse(f.hook(event).stdout).decision, 'block');
  assert.equal(JSON.parse(f.hook(event).stdout).decision, undefined);
});
test('stop_allows_when_recently_updated', t => {
  const f = fixture(t), path = transcript(f, 10);
  f.hook({ hook_event_name: 'SessionStart', transcript_path: path }); f.run(['update', 'codex', 'parent'], 'Fresh');
  assert.equal(JSON.parse(f.hook({ hook_event_name: 'Stop', transcript_path: path }).stdout).decision, undefined);
});
test('stop_hook_active_never_blocks', t => {
  const f = fixture(t), path = transcript(f, 30); f.hook({ hook_event_name: 'SessionStart' });
  assert.equal(JSON.parse(f.hook({ hook_event_name: 'Stop', transcript_path: path, stop_hook_active: true }).stdout).decision, undefined);
});
test('subagent_threshold_is_25', t => {
  const f = fixture(t), path = transcript(f, 24); f.hook({ hook_event_name: 'SessionStart' });
  f.hook({ hook_event_name: 'SubagentStart', agent_id: 'child' });
  const event = { hook_event_name: 'SubagentStop', agent_id: 'child', agent_transcript_path: path };
  assert.equal(JSON.parse(f.hook(event).stdout).decision, undefined);
  appendFileSync(path, JSON.stringify({ type: 'response_item', payload: { type: 'custom_tool_call', call_id: 'last' } }) + '\n');
  assert.equal(JSON.parse(f.hook(event).stdout).decision, 'block');
  assert.equal(f.run(['update', 'codex', 'child'], 'Own child state').status, 0);
});
function usage(path: string) {
  appendFileSync(path, JSON.stringify({ type: 'event_msg', payload: { type: 'token_count', info: { model_context_window: 100000, last_token_usage: { total_tokens: 70000 } } } }) + '\n');
}
test('nudge_fires_once_at_70_percent', t => {
  const f = fixture(t), path = transcript(f, 10, true); usage(path); f.hook({ hook_event_name: 'SessionStart' });
  const event = { hook_event_name: 'PostToolUse', transcript_path: path };
  assert.match(f.hook(event).stdout, /update.*now/i); assert.equal(f.hook(event).stdout.trim(), '{}');
});
test('nudge_rearms_after_update', t => {
  const f = fixture(t), path = transcript(f, 10, true); usage(path); f.hook({ hook_event_name: 'SessionStart' });
  const event = { hook_event_name: 'PostToolUse', transcript_path: path }; f.hook(event);
  f.run(['update', 'codex', 'parent'], 'Current');
  appendFileSync(path, Array.from({ length: 10 }, (_, i) => JSON.stringify({ type: 'response_item', payload: { type: 'function_call', call_id: 'new-' + i } })).join('\n') + '\n'); usage(path);
  assert.match(f.hook(event).stdout, /update.*now/i);
});
test('codex_rollout_tool_calls_counted', async t => {
  const f = fixture(t), path = transcript(f, 10, true);
  const { toolCallsSince } = await import('./stale.ts');
  assert.equal(toolCallsSince(path, 0), 10);
});
test('precompact_auto_never_blocks', t => {
  const f = fixture(t);
  for (const provider of ['codex', 'claude']) {
    const result = f.hook({ hook_event_name: 'PreCompact', trigger: 'auto', session_id: 'missing' }, provider);
    assert.equal(result.status, 0); assert.deepEqual(JSON.parse(result.stdout), {});
  }
});
test('precompact_manual_missing_binding_blocks', t => {
  const f = fixture(t);
  const result = f.hook({ hook_event_name: 'PreCompact', trigger: 'manual', session_id: 'missing' });
  assert.equal(JSON.parse(result.stdout).continue, false);
});
test('compact_recovery_tells_agent_to_refresh', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' });
  assert.match(f.hook({ hook_event_name: 'SessionStart', source: 'compact' }).stdout, /refresh.*runbook.*first/i);
});
test('unknown_context_window_is_not_guessed', async t => {
  const f = fixture(t), path = transcript(f, 10);
  appendFileSync(path, JSON.stringify({ type: 'assistant', message: { usage: { input_tokens: 180000 } } }) + '\n');
  const { contextPercent } = await import('./stale.ts');
  assert.equal(contextPercent(path, 'claude'), undefined);
});
test('fast_post_prefilter_skips_node_without_transcript_growth', t => {
  const f = fixture(t), path = transcript(f, 10, true); usage(path);
  const script = new URL('./fast-hook.sh', import.meta.url).pathname;
  const cli = new URL('./cli.ts', import.meta.url).pathname;
  const input = JSON.stringify({ cwd: f.cwd, session_id: 'parent', hook_event_name: 'PostToolUse', transcript_path: path });
  f.hook({ hook_event_name: 'SessionStart' });
  const first = spawnSync('/bin/sh', [script, 'post', process.execPath, cli, 'codex'], { input, env: f.env, encoding: 'utf8' });
  assert.match(first.stdout, /update.*now/i);
  const second = spawnSync('/bin/sh', [script, 'post', '/nonexistent-node', cli, 'codex'], { input, env: f.env, encoding: 'utf8' });
  assert.equal(second.status, 0); assert.equal(second.stdout.trim(), '{}');
});
