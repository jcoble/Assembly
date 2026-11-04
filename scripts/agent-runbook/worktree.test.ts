import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdirSync, writeFileSync, readFileSync, existsSync, utimesSync, rmSync, symlinkSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fixture } from './test-fixture.ts';

function setup(t: any) {
  const f = fixture(t), repo = join(f.home, 'dev/work/Assembly'), remote = join(f.home, 'origin.git');
  mkdirSync(repo, { recursive: true });
  const env = { ...f.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1', GIT_AUTHOR_NAME: 'Fixture', GIT_AUTHOR_EMAIL: 'fixture@example.test', GIT_COMMITTER_NAME: 'Fixture', GIT_COMMITTER_EMAIL: 'fixture@example.test' };
  const git = (cwd: string, ...args: string[]) => {
    const r = spawnSync('git', ['-C', cwd, ...args], { env, encoding: 'utf8' });
    assert.equal(r.status, 0, r.stderr); return r.stdout.trim();
  };
  git(repo, 'init', '-b', 'main'); writeFileSync(join(repo, 'README'), 'Safe fixture\n'); git(repo, 'add', '.'); git(repo, 'commit', '-m', 'initial');
  git(repo, 'init', '--bare', remote); git(repo, 'remote', 'add', 'origin', remote); git(repo, 'push', '-u', 'origin', 'main'); git(repo, 'symbolic-ref', 'refs/remotes/origin/HEAD', 'refs/remotes/origin/main');
  // Process discovery is an OS boundary; its fake uses lsof's real field format.
  const processes = join(f.home, 'processes'); writeFileSync(processes, '');
  writeFileSync(join(f.bin, 'lsof'), '#!/bin/sh\ncat "$FAKE_PROCESSES"\n', { mode: 0o755 });
  function run(args: string[], cwd = repo, extra = {}) {
    return spawnSync(process.execPath, [new URL('./agent-worktree.ts', import.meta.url).pathname, ...args], { cwd, encoding: 'utf8', env: { ...env, FAKE_PROCESSES: processes, ...extra } });
  }
  const create = (name = 'tsk-1666-fixture') => {
    const result = run(['create', name]); assert.equal(result.status, 0, result.stderr); return result.stdout.trim();
  };
  const registry = join(f.home, '.local/state/agent-runbook/worktrees.json');
  const guard = (command: string) => {
    const result = run(['guard', command]); assert.equal(result.status, 0, result.stderr); return JSON.parse(result.stdout);
  };
  return { ...f, env, repo, remote, git, run, create, registry, guard, processes };
}
test('deny_add_outside_root', t => {
  const f = setup(t);
  for (const command of ['git worktree add /tmp/out', 'git worktree add ../out', `git -C '${f.repo}' worktree add .claude/worktrees/one`, 'git worktree add ~/.warp/worktrees/one', 'git worktree move old ../out']) assert.ok(f.guard(command).deny, command);
});
test('allow_add_under_root', t => {
  const f = setup(t); assert.deepEqual(f.guard(`git worktree add '${f.home}/dev/work/worktrees/Assembly/one' -b one`), {});
});
test('allow_unrelated_commands_containing_worktree', t => {
  const f = setup(t); for (const command of ['git worktree list', "rg 'git worktree add /tmp/x'", 'echo worktree']) assert.deepEqual(f.guard(command), {});
});
test('worktreecreate_uses_root_and_prints_only_path', t => {
  const f = setup(t), path = f.create(); assert.equal(path, join(f.home, 'dev/work/worktrees/Assembly/tsk-1666-fixture')); assert.ok(existsSync(join(path, '.git')));
});
test('backup_commits_and_pushes_branch', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work.txt'), 'ordinary work\n');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'pushed', result.reason);
  assert.equal(f.git(path, 'rev-parse', 'HEAD'), f.git(path, 'rev-parse', 'refs/remotes/origin/tsk-1666-fixture'));
  assert.match(f.git(path, 'log', '-1', '--format=%B'), /Committed-by: Codex SOL 6.1$/);
});
test('backup_refuses_default_branch', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'dirty'), 'keep');
  f.git(path, 'symbolic-ref', 'refs/remotes/origin/HEAD', 'refs/remotes/origin/tsk-1666-fixture');
  const result = JSON.parse(f.run(['backup', path]).stdout);
  assert.equal(result.status, 'skipped'); assert.match(result.reason, /Default branch/); assert.ok(existsSync(join(path, 'dirty')));
});
test('backup_detached_head_makes_backup_branch', t => {
  const f = setup(t), path = f.create(); f.git(path, 'checkout', '--detach'); writeFileSync(join(path, 'work'), 'safe');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'pushed', result.reason); assert.match(f.git(path, 'branch', '--show-current'), /^backup\//);
});
test('backup_excludes_secret_and_large_and_keeps_worktree', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, '.env'), 'secret'); writeFileSync(join(path, 'large'), Buffer.alloc(20 * 1024 * 1024 + 1));
  const before = f.git(path, 'rev-parse', 'HEAD'), result = JSON.parse(f.run(['backup', path]).stdout);
  assert.equal(result.status, 'skipped'); assert.equal(result.kept.length, 2); assert.equal(f.git(path, 'rev-parse', 'HEAD'), before); assert.ok(existsSync(path));
});
test('failed_commit_hook_keeps_worktree_no_noverify', t => {
  const f = setup(t), path = f.create(); const hooks = join(f.home, 'reject-hooks'); mkdirSync(hooks);
  writeFileSync(join(hooks, 'pre-commit'), '#!/bin/sh\necho rejected >&2\nexit 1\n', { mode: 0o755 }); f.git(path, 'config', 'core.hooksPath', hooks); writeFileSync(join(path, 'work'), 'safe');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'failed'); assert.match(result.reason, /rejected/); assert.ok(existsSync(path));
});
test('no_remote_keeps_worktree', t => {
  const f = setup(t), path = f.create(); f.git(path, 'remote', 'remove', 'origin'); writeFileSync(join(path, 'work'), 'safe');
  assert.equal(JSON.parse(f.run(['backup', path]).stdout).status, 'skipped'); assert.ok(existsSync(path));
});
test('sweep_skips_live_cwd', t => {
  const f = setup(t), path = f.create(); f.run(['backup', path]); writeFileSync(f.processes, `p9999\nfcwd\nn${path}\n`);
  const result = f.run(['sweep']); assert.equal(result.status, 0, result.stderr); assert.ok(existsSync(path)); assert.match(result.stdout, /live/i);
});
test('sweep_skips_legacy_by_default', t => {
  const f = setup(t), path = join(f.home, 'dev/work/worktrees/Assembly/legacy'); mkdirSync(join(f.home, 'dev/work/worktrees/Assembly'), { recursive: true });
  f.git(f.repo, 'worktree', 'add', '-b', 'legacy', path);
  assert.equal(f.run(['sweep']).status, 0); assert.ok(existsSync(path));
});
test('sweep_removes_merged_leased_clean', t => {
  const f = setup(t), path = f.create(); f.run(['backup', path]);
  const result = f.run(['sweep']); assert.equal(result.status, 0, result.stderr); assert.ok(!existsSync(path), result.stdout);
});
test('sweep_reports_idle_unmerged_without_removing', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe'); f.run(['backup', path]);
  const db = JSON.parse(readFileSync(f.registry, 'utf8')); db[path].updated = new Date(Date.now() - 73 * 3600000).toISOString(); writeFileSync(f.registry, JSON.stringify(db));
  const result = f.run(['sweep']); assert.ok(existsSync(path)); assert.match(result.stdout, /idle.*unmerged/i);
});
test('done_backs_up_and_removes_without_a_pr', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe');
  const result = f.run(['done', path]); assert.equal(result.status, 0, result.stderr); assert.ok(!existsSync(path), result.stdout);
});
test('done_refuses_while_another_session_is_inside', t => {
  const f = setup(t), path = f.create(); writeFileSync(f.processes, `p9999\nfcwd\nn${path}/child\n`);
  assert.notEqual(f.run(['done', path]).status, 0); assert.ok(existsSync(path));
});
test('backup_blocks_secret_in_ordinary_file', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'ordinary.txt'), 'api_key = "sk-' + 'a'.repeat(50) + '"\n');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'skipped'); assert.match(result.reason, /secret/i);
});
test('scanner_failure_keeps_worktree', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe');
  const hooks = join(f.home, 'scan-failure'); mkdirSync(hooks);
  // A staged diff exceeding the scanner's bounded input is a real scanner failure.
  writeFileSync(join(path, 'big-diff'), 'ordinary line\n'.repeat(900000));
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'failed'); assert.match(result.reason, /scan/i); assert.ok(existsSync(path));
});
test('worktreecreate_failure_is_clear_and_stays_inside_root', t => {
  const f = setup(t); const result = f.run(['create', '../../escape']); assert.notEqual(result.status, 0); assert.match(result.stderr, /name|root/i); assert.ok(!existsSync(join(f.home, 'escape')));
});
test('two_sessions_one_worktree_not_removed', t => {
  const f = setup(t), path = f.create(); f.run(['lease', path, 'codex:a']); f.run(['lease', path, 'claude:b']); writeFileSync(join(path, 'work'), 'safe');
  const before = f.git(path, 'rev-parse', 'HEAD'); const result = f.run(['done', path, '--owner', 'codex:a']);
  assert.notEqual(result.status, 0); assert.ok(existsSync(path)); assert.equal(f.git(path, 'rev-parse', 'HEAD'), before);
});
test('sweep_lock_and_hourly_stamp', t => {
  const f = setup(t), path = f.create(); f.run(['backup', path]);
  const lock = join(f.home, '.local/state/agent-runbook/sweep.lock'); mkdirSync(lock);
  assert.match(f.run(['sweep', '--hourly']).stdout, /locked/i); assert.ok(existsSync(path));
  // Age does not break the lock of another process.
  utimesSync(lock, 0, 0); assert.ok(existsSync(path));
  rmSync(lock, { recursive: true });
  assert.equal(f.run(['sweep', '--hourly']).status, 0);
  assert.match(f.run(['sweep', '--hourly']).stdout, /this hour/);
});
test('restore_recreates_at_root', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe'); assert.equal(f.run(['done', path]).status, 0);
  const restored = f.run(['restore', 'tsk-1666-fixture', f.repo]); assert.equal(restored.status, 0, restored.stderr); assert.equal(restored.stdout.trim(), path); assert.equal(readFileSync(join(path, 'work'), 'utf8'), 'safe');
});
test('status_flags_strays', t => {
  const f = setup(t); const path = join(f.home, 'stray'); f.git(f.repo, 'worktree', 'add', '-b', 'stray', path);
  const result = f.run(['status']); assert.equal(result.status, 0, result.stderr); assert.equal(JSON.parse(result.stdout).find((r: any) => r.path === path).stray, true);
});
test('guard_denies_symlink_escape_and_tracks_cd_and_git_C', t => {
  const f = setup(t), root = join(f.home, 'dev/work/worktrees/Assembly'); mkdirSync(root, { recursive: true });
  symlinkSync(f.home, join(root, 'escape'));
  for (const command of [`git worktree add '${root}/escape/outside'`, `cd '${root}'; git -C ../../../.. worktree add outside`, 'git worktree add "$UNKNOWN/path"']) assert.ok(f.guard(command).deny, command);
});
test('failed_push_retains_dirty_work_and_remote_head', t => {
  const f = setup(t), path = f.create();
  writeFileSync(join(f.remote, 'hooks/pre-receive'), '#!/bin/sh\necho remote-rejected >&2\nexit 1\n', { mode: 0o755 });
  writeFileSync(join(path, 'work'), 'safe');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'failed'); assert.match(result.reason, /remote-rejected/); assert.ok(existsSync(path));
  assert.match(f.run(['sweep']).stdout, /Backup HEAD/); assert.ok(existsSync(path));
});
test('process_inventory_failure_prevents_commit_and_removal', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe');
  const before = f.git(path, 'rev-parse', 'HEAD');
  writeFileSync(join(f.bin, 'lsof'), '#!/bin/sh\nexit 1\n', { mode: 0o755 });
  assert.equal(JSON.parse(f.run(['backup', path]).stdout).status, 'skipped');
  assert.equal(f.git(path, 'rev-parse', 'HEAD'), before); assert.notEqual(f.run(['done', path]).status, 0); assert.ok(existsSync(path));
});
test('clean_worktree_with_secret_in_unpushed_history_is_not_pushed', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'ordinary'), 'api_key = "sk-' + 'b'.repeat(50) + '"');
  f.git(path, 'add', '.'); f.git(path, 'commit', '-m', 'unsafe fixture commit');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'skipped'); assert.match(result.reason, /history/);
  assert.equal(f.git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/tsk-1666-fixture'), '');
});
test('opaque_binary_diff_is_a_scanner_failure', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'opaque.bin'), Buffer.from([0, 1, 2, 3, 255]));
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'failed'); assert.match(result.reason, /scan/i);
  assert.equal(f.git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/tsk-1666-fixture'), '');
});
test('session_end_backs_up_and_keeps_resumable_worktree', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe');
  const cli = new URL('./cli.ts', import.meta.url).pathname;
  const run = (event: any) => spawnSync(process.execPath, [cli, 'hook', 'codex'], { cwd: path, input: JSON.stringify({ cwd: path, session_id: 'one', ...event }), encoding: 'utf8', env: { ...f.env, FAKE_PROCESSES: f.processes } });
  assert.equal(run({ hook_event_name: 'SessionStart' }).status, 0);
  const result = run({ hook_event_name: 'SessionEnd', reason: 'other' }); assert.equal(result.status, 0, result.stderr);
  assert.ok(existsSync(path)); assert.match(f.git(path, 'log', '-1', '--format=%s'), /back up/);
});
test('worktreecreate_hook_prints_path_and_failure_is_nonzero', t => {
  const f = setup(t), cli = new URL('./cli.ts', import.meta.url).pathname;
  const run = (name: string) => spawnSync(process.execPath, [cli, 'hook', 'claude'], { cwd: f.repo, input: JSON.stringify({ cwd: f.repo, session_id: 'one', hook_event_name: 'WorktreeCreate', name }), encoding: 'utf8', env: f.env });
  const created = run('hook-test'); assert.equal(created.status, 0, created.stderr); assert.equal(created.stdout.trim(), join(f.home, 'dev/work/worktrees/Assembly/hook-test'));
  const failed = run('../../escape'); assert.equal(failed.status, 1); assert.equal(failed.stdout, '');
});
test('installed_launchers_and_hook_payloads_run_from_temp_HOME', t => {
  const f = setup(t);
  const install = spawnSync(process.execPath, [new URL('./install.ts', import.meta.url).pathname, '--without-codex-trust'], { cwd: f.repo, encoding: 'utf8', env: f.env });
  assert.equal(install.status, 0, install.stderr);
  const projects = JSON.parse(readFileSync(join(f.home, '.config/agent-runbook/projects.json'), 'utf8'));
  assert.equal(projects.Assembly, '406394b0689d8162a0a4c21e11a6d23a');
  const launcher = join(f.home, '.local/bin/agent-worktree');
  const created = spawnSync(launcher, ['create', 'installed-test'], { cwd: f.repo, encoding: 'utf8', env: f.env });
  assert.equal(created.status, 0, created.stderr); assert.ok(existsSync(created.stdout.trim()));
  const codex = JSON.parse(readFileSync(join(f.home, '.codex/hooks.json'), 'utf8'));
  assert.equal(Object.keys(codex.hooks).length, 8); assert.equal(codex.hooks.PreToolUse[0].matcher, 'Bash'); assert.equal(codex.hooks.SessionEnd[0].hooks[0].timeout, 60);
  const claude = JSON.parse(readFileSync(join(f.home, '.claude/settings.json'), 'utf8')); assert.ok(claude.hooks.WorktreeCreate); assert.ok(claude.hooks.WorktreeRemove);
  const payload = { cwd: f.repo, session_id: 'one', hook_event_name: 'PreToolUse', tool_input: { command: 'git worktree add /tmp/outside' } };
  const guarded = spawnSync('/bin/sh', ['-c', codex.hooks.PreToolUse[0].hooks[0].command], { cwd: f.repo, input: JSON.stringify(payload), encoding: 'utf8', env: f.env });
  assert.equal(guarded.status, 0, guarded.stderr); assert.equal(JSON.parse(guarded.stdout).hookSpecificOutput.permissionDecision, 'deny');
});
