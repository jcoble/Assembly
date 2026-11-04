import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fixture } from './test-fixture.ts';

test('update_creates_page_lazily_once', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' });
  assert.equal(Object.keys(f.db().pages).length, 0);
  assert.equal(f.run(['update', 'codex', 'parent'], 'First state').status, 0);
  assert.equal(f.run(['update', 'codex', 'parent'], 'Next state').status, 0);
  assert.equal(Object.keys(f.db().pages).length, 1);
  assert.ok(f.binding().notionPageId);
});
test('update_pushes_body_and_keeps_local_copy', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' });
  f.run(['update', 'codex', 'parent'], 'Summary: working\nNext: prove');
  assert.equal(f.db().pages[f.binding().notionPageId], readFileSync(f.binding().path, 'utf8'));
  assert.equal(f.binding().syncPending, false);
});
test('update_succeeds_offline_and_marks_pending', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' }); f.change(db => db.offline = true);
  const result = f.run(['update', 'codex', 'parent'], 'Offline state');
  assert.equal(result.status, 0, result.stderr); assert.equal(f.binding().syncPending, true);
  assert.match(readFileSync(f.binding().path, 'utf8'), /Offline state/);
});
test('next_hook_flushes_pending', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' }); f.change(db => db.offline = true);
  f.run(['update', 'codex', 'parent'], 'Queued state'); f.change(db => db.offline = false);
  f.hook({ hook_event_name: 'SessionStart', source: 'resume' });
  assert.equal(f.binding().syncPending, false); assert.match(f.db().pages[f.binding().notionPageId], /Queued state/);
});
test('recover_prefers_notion_and_shows_edits', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' }); f.run(['update', 'codex', 'parent'], 'Local state');
  f.change(db => db.pages[f.binding().notionPageId] = 'Jesse edited this');
  assert.match(f.hook({ hook_event_name: 'SessionStart', source: 'compact' }).stdout, /Jesse edited this/);
});
test('recover_falls_back_to_cache_with_visible_note', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' }); f.run(['update', 'codex', 'parent'], 'Cached state'); f.change(db => db.offline = true);
  const result = f.hook({ hook_event_name: 'SessionStart', source: 'compact' });
  assert.match(result.stdout, /Cached state/); assert.match(result.stdout, /cache|offline/i);
});
test('oversize_update_rejected_with_prune_message', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' });
  const result = f.run(['update', 'codex', 'parent'], 'x'.repeat(6501));
  assert.equal(result.status, 1); assert.match(result.stderr, /delete finished items/i); assert.equal(f.db().calls.length, 0);
});
test('no_page_created_for_session_without_update', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' }); f.hook({ hook_event_name: 'PreCompact', trigger: 'auto' });
  assert.equal(f.db().calls.length, 0);
});
test('readonly_bind_never_writes_notion', t => {
  const f = fixture(t); f.hook({ hook_event_name: 'SessionStart' });
  f.run(['bind', 'codex', 'child', '--path', f.binding().path, '--parent', 'codex:parent']);
  assert.equal(f.run(['update', 'codex', 'child'], 'Forbidden').status, 1); assert.equal(f.db().calls.length, 0);
});
test('task_key_from_branch_sets_relation', t => {
  const f = fixture(t);
  spawnSync('git', ['init', '-b', 'tsk-1666-daily-workflow'], { cwd: f.cwd, env: f.env });
  f.hook({ hook_event_name: 'SessionStart' }); f.run(['update', 'codex', 'parent'], 'State');
  const create = f.db().calls.find((c: any) => c.path === '/v1/pages');
  assert.deepEqual(create.body.properties.Task, { relation: [{ id: 'task-page' }] });
});
