import assert from 'node:assert/strict';
import { test } from 'node:test';
import { writeFileSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { setup, age, hookIn, remoteTip } from './worktree-fixture.ts';

test('subagent_stop_backs_up_harness_worktree_and_releases_parent_lease', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child1' });
  writeFileSync(join(path, 'work'), 'safe');
  hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child1' });
  const row = JSON.parse(readFileSync(f.registry, 'utf8'))[path];
  assert.deepEqual(row.leases, {}); assert.equal(row.backedUpHead, f.git(path, 'rev-parse', 'HEAD'));
  assert.equal(remoteTip(f, 'agent-child1'), row.backedUpHead);
});
test('unchanged_agent_worktree_is_removed_without_pushing_a_branch', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child2' });
  hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child2' });
  assert.equal(remoteTip(f, 'agent-child2'), '');
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
});
test('agent_worktree_with_unpushed_commit_is_pushed_before_removal', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child3' });
  writeFileSync(join(path, 'work'), 'committed'); f.git(path, 'add', '.'); f.git(path, 'commit', '-m', 'work');
  const head = f.git(path, 'rev-parse', 'HEAD');
  hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child3' });
  assert.equal(remoteTip(f, 'agent-child3'), head); assert.ok(existsSync(path));
});
test('session_end_releases_only_its_own_leases_outside_cwd', t => {
  const f = setup(t), mine = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'mine' });
  const theirs = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'theirs', session_id: 'other' });
  writeFileSync(join(mine, 'work'), 'safe'); writeFileSync(join(theirs, 'work'), 'theirs');
  hookIn(f, { hook_event_name: 'SessionEnd' });
  const rows = JSON.parse(readFileSync(f.registry, 'utf8'));
  assert.deepEqual(rows[mine].leases, {}); assert.equal(remoteTip(f, 'mine'), f.git(mine, 'rev-parse', 'HEAD'));
  assert.ok(rows[theirs].leases['claude:other']); assert.equal(remoteTip(f, 'theirs'), '');
});
test('backup_refuses_sensitive_file_anywhere_in_unpushed_history', t => {
  const f = setup(t), path = f.create();
  writeFileSync(join(path, '.env'), 'x=1'); f.git(path, 'add', '.'); f.git(path, 'commit', '-m', 'add');
  f.git(path, 'rm', '.env'); f.git(path, 'commit', '-m', 'remove');
  const result = JSON.parse(f.run(['backup', path]).stdout);
  assert.equal(result.status, 'skipped'); assert.match(result.reason, /history/); assert.equal(remoteTip(f, 'tsk-1666-fixture'), '');
});
test('backup_refuses_during_unresolved_merge_without_flagging', t => {
  const f = setup(t), path = f.create();
  writeFileSync(join(path, 'README'), 'branch\n'); f.git(path, 'commit', '-am', 'branch side');
  writeFileSync(join(f.repo, 'README'), 'main\n'); f.git(f.repo, 'commit', '-am', 'main side');
  assert.notEqual(spawnSync('git', ['-C', path, 'merge', 'main'], { env: f.env }).status, 0);
  const head = f.git(path, 'rev-parse', 'HEAD');
  const result = JSON.parse(f.run(['backup', path]).stdout);
  assert.equal(result.status, 'skipped'); assert.match(result.reason, /Git operation/);
  assert.equal(f.git(path, 'rev-parse', 'HEAD'), head); assert.match(f.git(path, 'status', '--porcelain'), /^UU README/m);
  assert.equal(remoteTip(f, 'tsk-1666-fixture'), '');
  assert.equal(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, undefined);
});
test('session_end_backs_up_when_its_own_process_is_in_the_worktree', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'own-cwd' });
  writeFileSync(join(path, 'work'), 'safe'); writeFileSync(f.processes, `p${process.pid}\nn${path}\n`);
  hookIn(f, { hook_event_name: 'SessionEnd', cwd: path });
  assert.equal(remoteTip(f, 'own-cwd'), f.git(path, 'rev-parse', 'HEAD'));
  assert.equal(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, undefined);
  assert.match(f.run(['sweep']).stdout, /Live process/); assert.ok(existsSync(path));
});
test('other_live_process_blocks_backup_without_a_lasting_flag', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'other-cwd' });
  writeFileSync(join(path, 'work'), 'safe'); writeFileSync(f.processes, `p1\nn${path}\n`);
  hookIn(f, { hook_event_name: 'SessionEnd', cwd: path });
  assert.equal(remoteTip(f, 'other-cwd'), ''); assert.equal(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, undefined);
});
test('agent_worktree_from_unpushed_local_main_is_removed_without_pushing', t => {
  const f = setup(t); writeFileSync(join(f.repo, 'local'), 'only here'); f.git(f.repo, 'add', '.'); f.git(f.repo, 'commit', '-m', 'local only');
  const path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child4' });
  hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child4' });
  assert.equal(remoteTip(f, 'agent-child4'), '');
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
});
test('idle_sweep_pushes_work_that_session_end_could_not_back_up', t => {
  for (const work of ['dirty', 'committed']) {
    const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child5' });
    writeFileSync(join(path, 'work'), 'safe');
    if (work === 'committed') { f.git(path, 'add', '.'); f.git(path, 'commit', '-m', 'work'); }
    writeFileSync(f.processes, `p1\nn${path}\n`);
    hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child5' }); assert.equal(remoteTip(f, 'agent-child5'), '');
    writeFileSync(f.processes, ''); age(f, path);
    const result = f.run(['sweep']); assert.ok(!existsSync(path), work + result.stdout);
    assert.ok(remoteTip(f, 'agent-child5'), work);
    assert.match(f.git(f.repo, 'ls-tree', '-r', '--name-only', remoteTip(f, 'agent-child5')), /work/);
  }
});
// Finder can write .DS_Store into a folder at the moment Git removes it.
function recreateAfterRemove(f: any, file: string) {
  const real = spawnSync('which', ['git'], { encoding: 'utf8' }).stdout.trim();
  writeFileSync(join(f.bin, 'git'), `#!/bin/sh\n${real} "$@"\nrc=$?\ncase "$*" in *"worktree remove"*) for last; do :; done; mkdir -p "$last"; touch "$last/${file}";; esac\nexit $rc\n`, { mode: 0o755 });
}
test('removal_ignores_ds_store_written_as_the_folder_goes', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child6' });
  hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child6' }); recreateAfterRemove(f, '.DS_Store');
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
  assert.equal(JSON.parse(readFileSync(f.registry, 'utf8'))[path], undefined);
});
test('removal_reports_other_residue_and_forgets_the_removed_worktree', t => {
  const f = setup(t), path = hookIn(f, { hook_event_name: 'WorktreeCreate', name: 'agent-child7' });
  hookIn(f, { hook_event_name: 'SubagentStop', agent_id: 'child7' }); recreateAfterRemove(f, 'notes');
  assert.match(f.run(['sweep']).stdout, /residual/); assert.ok(existsSync(join(path, 'notes')));
  assert.equal(JSON.parse(readFileSync(f.registry, 'utf8'))[path], undefined);
});
