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
test('guard_allows_heredoc_documentation', t => {
  const f = setup(t);
  for (const delimiter of ["'EOF'", '"EOF"', 'EOF']) for (const op of ['<<', '<<-']) {
    assert.deepEqual(f.guard(`cat > notes.md ${op}${delimiter}\n\tFor documentation: git worktree add /tmp/outside\n${op === '<<-' ? '\t' : ''}EOF`), {});
  }
  assert.ok(f.guard("cat <<'EOF'\ngit worktree list\nEOF\ngit worktree add /tmp/outside").deny);
  assert.deepEqual(f.guard("cat bash <<'EOF'\ngit worktree add /tmp/outside\nEOF"), {});
  assert.deepEqual(f.guard("bash -c true\ncat <<'EOF'\ngit worktree add /tmp/outside\nEOF"), {});
});
test('guard_denies_heredoc_into_shell', t => {
  const f = setup(t);
  for (const head of ['bash', 'sh', 'cat | bash', 'cat | sh']) {
    assert.ok(f.guard(`${head} <<'EOF'\ngit worktree add /tmp/outside\nEOF`).deny, head);
  }
  assert.ok(f.guard("cat <<'EOF' | bash\ngit worktree add /tmp/outside\nEOF").deny);
});
test('guard_allows_env_options_with_read_only_worktree', t => {
  const f = setup(t);
  for (const options of ['--unset=UNUSED', '-u UNUSED', '--chdir=/tmp', '-C /tmp', '--unknown']) {
    assert.deepEqual(f.guard(`env ${options} git worktree list`), {}, options);
    assert.ok(f.guard(`env ${options} git worktree add /tmp/outside`).deny, options);
  }
  assert.deepEqual(f.guard(`env --unset=UNUSED git worktree add '${f.home}/dev/work/worktrees/Assembly/safe'`), {});
  assert.ok(f.guard("env -S 'git worktree add /tmp/outside'").deny);
});
test('guard_denies_function_wrapper', t => {
  const f = setup(t); assert.ok(f.guard('g(){ command git worktree add -b alias-probe "$HOME/outside-worktree"; }; g').deny);
});
test('guard_denies_subshell_and_substitution', t => {
  const f = setup(t);
  for (const command of ['(git worktree add /tmp/x)', 'echo $(git worktree add /tmp/x)', 'echo `git worktree add /tmp/x`']) assert.ok(f.guard(command).deny, command);
});
test('guard_denies_shell_c_and_eval', t => {
  const f = setup(t);
  for (const shell of ['bash', 'sh', 'zsh', 'dash']) for (const flag of ['-c', '-lc']) assert.ok(f.guard(`${shell} ${flag} 'git worktree add /tmp/x'`).deny);
  for (const command of ["eval 'git worktree add /tmp/x'", 'eval git worktree add /tmp/x', "bash -c 'eval git worktree add /tmp/x'"]) assert.ok(f.guard(command).deny, command);
});
test('guard_denies_keyword_prefix', t => {
  const f = setup(t);
  for (const prefix of ['{', '}', '!', 'if', 'then', 'elif', 'else', 'do', 'while', 'until', 'time', 'nohup', 'exec', 'builtin', 'xargs', 'env -i', 'env -C /tmp', 'env --unknown']) assert.ok(f.guard(`${prefix} git worktree add /tmp/x`).deny, prefix);
  assert.ok(f.guard('if true; then git worktree add /tmp/x; fi').deny);
  assert.ok(f.guard('xargs git worktree add').deny);
});
test('guard_denies_find_exec', t => {
  const f = setup(t); assert.ok(f.guard('find . -exec git worktree add /x \\;').deny);
});
test('guard_denies_inline_alias', t => {
  const f = setup(t);
  for (const command of ["git -c 'alias.w=worktree add /tmp/x' w", "git -calias.w='!git worktree add /tmp/x' w"]) assert.ok(f.guard(command).deny, command);
});
test('guard_allows_wrappers_inside_root', t => {
  const f = setup(t), root = join(f.home, 'dev/work/worktrees/Assembly'); mkdirSync(join(root, 'lane'), { recursive: true });
  for (const command of [`g(){ command git worktree add '${root}/x'; }; g`, `(git worktree add '${root}/x')`, `bash -c 'git worktree add "${root}/x"'`, `eval 'git worktree add "${root}/x"'`, `env -i git worktree add '${root}/x'`, `env -C '${root}/lane' git worktree add ../x`, `cd '${root}/lane' && git worktree add ../x`, `git -C '${root}/lane' worktree add ../x`, `cd '${root}' && git -C lane worktree add ../x`]) assert.deepEqual(f.guard(command), {}, command);
});
test('guard_allows_quoted_mentions', t => {
  const f = setup(t);
  for (const command of ['git commit -m "git worktree add /tmp/x"', 'grep -r "worktree add" .', 'cat notes | grep worktree', 'echo "git" "worktree" "add"']) assert.deepEqual(f.guard(command), {}, command);
});
test('guard_allows_braced_home_inside_root', t => {
  const f = setup(t); assert.deepEqual(f.guard('git worktree add ${HOME}/dev/work/worktrees/repo/x'), {});
});
test('worktreecreate_uses_root_and_prints_only_path', t => {
  const f = setup(t), path = f.create(); assert.equal(path, join(f.home, 'dev/work/worktrees/Assembly/tsk-1666-fixture')); assert.ok(existsSync(join(path, '.git')));
});
test('backup_commits_and_pushes_branch', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work.txt'), 'ordinary work\n');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'pushed', result.reason);
  assert.equal(f.git(path, 'rev-parse', 'HEAD'), f.git(path, 'rev-parse', 'refs/remotes/origin/tsk-1666-fixture'));
  assert.match(f.git(path, 'log', '-1', '--format=%B'), /Committed-by: agent-worktree$/);
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
test('sweep_removes_idle_backed_up_after_12h', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe'); f.run(['backup', path]);
  age(f, path);
  const result = f.run(['sweep']); assert.equal(result.status, 0, result.stderr); assert.ok(!existsSync(path), result.stdout);
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
  assert.match(f.run(['sweep']).stdout, /flagged.*remote-rejected/); assert.ok(existsSync(path));
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
  const launcher = join(f.home, '.local/bin/agent-worktree');
  const created = spawnSync(launcher, ['create', 'installed-test'], { cwd: f.repo, encoding: 'utf8', env: f.env });
  assert.equal(created.status, 0, created.stderr); assert.ok(existsSync(created.stdout.trim()));
  const codex = JSON.parse(readFileSync(join(f.home, '.codex/hooks.json'), 'utf8'));
  assert.equal(Object.keys(codex.hooks).length, 6); assert.equal(codex.hooks.PreToolUse[0].matcher, 'Bash'); assert.equal(codex.hooks.SessionEnd[0].hooks[0].timeout, 60);
  const claude = JSON.parse(readFileSync(join(f.home, '.claude/settings.json'), 'utf8')); assert.ok(claude.hooks.WorktreeCreate); assert.ok(claude.hooks.WorktreeRemove);
  const payload = { cwd: f.repo, session_id: 'one', hook_event_name: 'PreToolUse', tool_input: { command: 'git worktree add /tmp/outside' } };
  const guarded = spawnSync('/bin/sh', ['-c', codex.hooks.PreToolUse[0].hooks[0].command], { cwd: f.repo, input: JSON.stringify(payload), encoding: 'utf8', env: f.env });
  assert.equal(guarded.status, 0, guarded.stderr); assert.equal(JSON.parse(guarded.stdout).hookSpecificOutput.permissionDecision, 'deny');
});

function age(f: ReturnType<typeof setup>, path: string, hours = 13) {
  const at = new Date(Date.now() - hours * 3600000);
  const db = JSON.parse(readFileSync(f.registry, 'utf8')); db[path].updated = at.toISOString(); writeFileSync(f.registry, JSON.stringify(db));
  for (const file of f.git(path, 'ls-files', '--cached', '--others', '--exclude-standard').split('\n').filter(Boolean)) utimesSync(join(path, file), at, at);
  for (const name of ['HEAD', 'index', 'logs/HEAD']) {
    const file = f.git(path, 'rev-parse', '--path-format=absolute', '--git-path', name);
    if (existsSync(file)) utimesSync(file, at, at);
  }
}
function idle(t: any) {
  const f = setup(t), path = f.create();
  writeFileSync(join(path, 'work'), 'safe');
  assert.equal(JSON.parse(f.run(['backup', path]).stdout).status, 'pushed');
  age(f, path); return { f, path };
}
test('sweep_keeps_idle_under_12h', t => {
  const { f, path } = idle(t); age(f, path, 11);
  assert.match(f.run(['sweep']).stdout, /12|idle/i); assert.ok(existsSync(path));
});
test('sweep_keeps_idle_with_unpushed_commits', t => {
  const { f, path } = idle(t); writeFileSync(join(path, 'work'), 'local commit'); f.git(path, 'commit', '-am', 'local'); age(f, path);
  assert.match(f.run(['sweep']).stdout, /unpushed|remote/i); assert.ok(existsSync(path));
});
test('sweep_keeps_when_remote_lacks_head', t => {
  const { f, path } = idle(t); f.git(f.remote, 'update-ref', '-d', 'refs/heads/tsk-1666-fixture');
  assert.match(f.run(['sweep']).stdout, /remote/i); assert.ok(existsSync(path));
});
test('sweep_keeps_ignored_unique_file_and_names_it', t => {
  const { f, path } = idle(t); writeFileSync(join(f.repo, '.git/info/exclude'), 'unique-notes\n'); writeFileSync(join(path, 'unique-notes'), 'important');
  assert.match(f.run(['sweep']).stdout, /unique-notes/); assert.ok(existsSync(path));
  assert.match(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, /unique-notes/);
});
test('sweep_removes_with_only_regenerable_ignored_dirs', t => {
  const { f, path } = idle(t); writeFileSync(join(f.repo, '.git/info/exclude'), 'target/\nnode_modules/\n');
  for (const dir of ['target', 'node_modules']) { mkdirSync(join(path, dir)); writeFileSync(join(path, dir, 'generated'), 'cache'); }
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
});
test('sweep_keeps_with_live_process', t => {
  const { f, path } = idle(t); writeFileSync(f.processes, `p9999\nn${path}\n`);
  assert.match(f.run(['sweep']).stdout, /live/i); assert.ok(existsSync(path));
});
test('sweep_keeps_during_rebase', t => {
  const { f, path } = idle(t); mkdirSync(f.git(path, 'rev-parse', '--path-format=absolute', '--git-path', 'rebase-merge'));
  assert.match(f.run(['sweep']).stdout, /rebase/i); assert.ok(existsSync(path));
});
test('sweep_keeps_when_measurement_fails', t => {
  const { f, path } = idle(t); rmSync(f.git(path, 'rev-parse', '--path-format=absolute', '--git-path', 'index'));
  assert.match(f.run(['sweep']).stdout, /measure|ENOENT/i); assert.ok(existsSync(path));
});
test('sweep_never_removes_legacy_idle', t => {
  const { f, path } = idle(t); writeFileSync(f.registry, '{}');
  assert.match(f.run(['sweep', '--legacy']).stdout, /legacy/i); assert.ok(existsSync(path));
});
test('restore_after_idle_removal', t => {
  const { f, path } = idle(t); const head = f.git(path, 'rev-parse', 'HEAD');
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
  assert.match(result.stdout, /tsk-1666-fixture/);
  const restored = f.run(['restore', 'tsk-1666-fixture'], f.home); assert.equal(restored.status, 0, restored.stderr);
  assert.equal(restored.stdout.trim(), path); assert.equal(f.git(path, 'rev-parse', 'HEAD'), head); assert.equal(readFileSync(join(path, 'work'), 'utf8'), 'safe');
});
test('worktreeremove_backs_up_and_returns_nonzero_to_retain', t => {
  const f = setup(t), path = f.create(); writeFileSync(join(path, 'work'), 'safe');
  const result = spawnSync(process.execPath, [new URL('./cli.ts', import.meta.url).pathname, 'hook', 'claude'], { cwd: f.repo, input: JSON.stringify({ cwd: f.repo, worktree_path: path, session_id: 'one', hook_event_name: 'WorktreeRemove' }), encoding: 'utf8', env: { ...f.env, FAKE_PROCESSES: f.processes } });
  assert.equal(result.status, 1, result.stderr); assert.ok(existsSync(path)); assert.match(result.stderr, /retain/i);
  assert.equal(f.git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/tsk-1666-fixture').split(/\s/)[0], f.git(path, 'rev-parse', 'HEAD'));
});

test('sweep_backs_up_idle_dirty_files_before_removal', t => {
  const { f, path } = idle(t); writeFileSync(join(path, 'new-work'), 'recover this'); age(f, path);
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
  assert.equal(f.run(['restore', 'tsk-1666-fixture']).status, 0);
  assert.equal(readFileSync(join(path, 'new-work'), 'utf8'), 'recover this');
});
test('sweep_keeps_idle_stash', t => {
  const { f, path } = idle(t); writeFileSync(join(path, 'work'), 'stashed work'); f.git(path, 'stash', 'push'); age(f, path);
  assert.match(f.run(['sweep']).stdout, /stash/i); assert.ok(existsSync(path));
});
test('sweep_removes_when_only_other_branch_has_stash', t => {
  const { f, path } = idle(t); writeFileSync(join(f.repo, 'README'), 'other branch stash'); f.git(f.repo, 'stash', 'push');
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
  assert.match(f.git(f.repo, 'stash', 'list'), /WIP on main:/);
});
test('sweep_ignores_ds_store_and_build_output', t => {
  const { f, path } = idle(t);
  writeFileSync(join(f.repo, '.git/info/exclude'), '.DS_Store\n.build/\ngen/schemas/\n');
  for (const file of ['.DS_Store', 'nested/.DS_Store', '.build/debug/output', 'nested/.build/output', 'gen/schemas/schema.json']) {
    mkdirSync(join(path, file, '..'), { recursive: true }); writeFileSync(join(path, file), 'generated');
  }
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
});
test('sweep_keeps_settings_local_json', t => {
  const { f, path } = idle(t); writeFileSync(join(f.repo, '.git/info/exclude'), '.claude/\n');
  mkdirSync(join(path, '.claude')); writeFileSync(join(path, '.claude/settings.local.json'), 'important settings');
  assert.match(f.run(['sweep']).stdout, /settings.local.json/); assert.ok(existsSync(path));
});
test('sweep_keeps_other_protected_ignored_paths', t => {
  for (const file of ['.lane', 'owner.db', 'scratch/notes', 'gen/notes']) {
    const { f, path } = idle(t); writeFileSync(join(f.repo, '.git/info/exclude'), file + '\n');
    mkdirSync(join(path, file, '..'), { recursive: true }); writeFileSync(join(path, file), 'important');
    assert.ok(existsSync(path)); assert.match(f.run(['sweep']).stdout, /preservation/); assert.ok(existsSync(path));
  }
});
for (const activity of ['file', 'index', 'lease']) test('sweep_uses_newest_activity_' + activity, t => {
  const { f, path } = idle(t);
  if (activity === 'file') utimesSync(join(path, 'work'), new Date(), new Date());
  if (activity === 'index') utimesSync(f.git(path, 'rev-parse', '--path-format=absolute', '--git-path', 'index'), new Date(), new Date());
  if (activity === 'lease') {
    const db = JSON.parse(readFileSync(f.registry, 'utf8')); db[path].updated = new Date().toISOString(); writeFileSync(f.registry, JSON.stringify(db));
  }
  assert.ok(existsSync(path)); assert.match(f.run(['sweep']).stdout, /12/); assert.ok(existsSync(path));
});
test('sweep_removes_worktree_with_existing_runbook', t => {
  const { f, path } = idle(t), cli = new URL('./cli.ts', import.meta.url).pathname;
  const run = (args: string[], input = '') => spawnSync(process.execPath, [cli, ...args], { cwd: path, input, encoding: 'utf8', env: f.env });
  assert.equal(run(['init', 'codex', 'row']).status, 0);
  assert.equal(run(['update', 'codex', 'row'], 'Safe fixture runbook').status, 0);
  const result = f.run(['sweep']); assert.ok(!existsSync(path), result.stdout);
});

test('sweep_keeps_clean_excluded_files', t => {
  const { f, path } = idle(t); writeFileSync(join(path, 'fixture.sqlite'), 'safe fixture data');
  f.git(path, 'add', '.'); f.git(path, 'commit', '-m', 'fixture database'); f.git(path, 'push'); age(f, path);
  const result = f.run(['sweep']); assert.ok(existsSync(path), result.stdout); assert.match(result.stdout, /Sensitive filename/);
});
test('sweep_flags_failed_secret_scan', t => {
  const { f, path } = idle(t); writeFileSync(join(path, 'secret'), 'api_key = "sk-' + 'c'.repeat(50) + '"'); age(f, path);
  const result = f.run(['sweep']); assert.ok(existsSync(path), result.stdout);
  assert.match(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, /Secret scanner/);
});
test('sweep_keeps_idle_with_live_lease', t => {
  const { f, path } = idle(t); f.run(['lease', path, 'claude:other']); age(f, path);
  assert.match(f.run(['sweep']).stdout, /lease/); assert.ok(existsSync(path));
});
test('sweep_dry_run_preserves_registry_and_files', t => {
  const { f, path } = idle(t); writeFileSync(join(f.repo, '.git/info/exclude'), 'unique\n'); writeFileSync(join(path, 'unique'), 'important');
  const before = readFileSync(f.registry, 'utf8'); f.run(['sweep', '--dry-run']);
  assert.equal(readFileSync(f.registry, 'utf8'), before); assert.ok(existsSync(path));
});

function installCommitHook(f: ReturnType<typeof setup>, path: string) {
  const hooks = join(f.home, 'commit-hooks'); mkdirSync(hooks);
  writeFileSync(join(hooks, 'commit-msg'), readFileSync(new URL('../check-commit-msg.sh', import.meta.url)), { mode: 0o755 });
  f.git(path, 'config', 'core.hooksPath', hooks);
}
const taskLine = 'Task: TSK-1666 https://app.notion.com/p/40e394b0689d813ba726f42786e4d040';
test('backup_on_tsk_branch_reuses_task_line_from_history', t => {
  const f = setup(t), path = f.create(); installCommitHook(f, path);
  f.git(path, 'commit', '--allow-empty', '-m', 'chore: bind task\n\nArchitecture: unchanged\n' + taskLine + '\nCommitted-by: Fixture');
  f.git(path, 'commit', '--allow-empty', '-m', 'chore: latest task\n\nArchitecture: unchanged\n' + taskLine.replace('40e394b0689d813ba726f42786e4d040', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa') + '\nCommitted-by: Fixture');
  writeFileSync(join(path, 'work'), 'safe');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'pushed', result.reason);
  assert.match(f.git(path, 'log', '-1', '--format=%B'), /Task: TSK-1666 https:\/\/app.notion.com\/p\/a{32}/);
  assert.match(f.git(path, 'log', '-1', '--format=%B'), /Committed-by: agent-worktree$/);
});
test('backup_on_tsk_branch_without_task_line_keeps_and_flags', t => {
  const f = setup(t), path = f.create();
  // A different task's line must never be reused.
  f.git(path, 'commit', '--allow-empty', '-m', taskLine.replace('TSK-1666', 'TSK-999'));
  installCommitHook(f, path);
  const head = f.git(path, 'rev-parse', 'HEAD'); writeFileSync(join(path, 'work'), 'keep');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'failed');
  assert.match(result.reason, /add Task: TSK-1666/); assert.ok(existsSync(path));
  assert.equal(f.git(path, 'rev-parse', 'HEAD'), head);
  assert.equal(f.git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/tsk-1666-fixture'), '');
  assert.match(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, /add Task: TSK-1666/);
});
test('backup_on_plain_branch_has_no_task_line', t => {
  const f = setup(t), path = f.create('plain'); installCommitHook(f, path);
  f.git(path, 'commit', '--allow-empty', '-m', 'chore: unrelated task\n\nArchitecture: unchanged\n' + taskLine + '\nCommitted-by: Fixture');
  writeFileSync(join(path, 'work'), 'safe');
  const result = JSON.parse(f.run(['backup', path]).stdout); assert.equal(result.status, 'pushed', result.reason);
  assert.doesNotMatch(f.git(path, 'log', '-1', '--format=%B'), /^Task:/m);
});
test('install_registers_only_expected_events', t => {
  const f = setup(t);
  for (const [provider, file] of [['codex', '.codex/hooks.json'], ['claude', '.claude/settings.json']]) {
    mkdirSync(join(f.home, provider === 'codex' ? '.codex' : '.claude'), { recursive: true });
    writeFileSync(join(f.home, file), JSON.stringify({ hooks: { PreToolUse: [{ matcher: 'Read', hooks: [{ type: 'command', command: 'user-hook' }] }] } }));
  }
  const result = spawnSync(process.execPath, [new URL('./install.ts', import.meta.url).pathname, '--without-codex-trust'], { cwd: f.repo, env: f.env, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  for (const [provider, file, extra] of [['codex', '.codex/hooks.json', []], ['claude', '.claude/settings.json', ['WorktreeCreate', 'WorktreeRemove']]] as const) {
    const config = JSON.parse(readFileSync(join(f.home, file), 'utf8'));
    const events = ['SessionStart', 'PreCompact', 'SubagentStart', 'SubagentStop', 'PreToolUse', 'SessionEnd', ...extra];
    assert.deepEqual(Object.keys(config.hooks).sort(), events.sort(), provider);
    assert.equal(config.hooks.PreToolUse[0].hooks[0].command, 'user-hook');
    assert.equal(config.hooks.PreToolUse[0].matcher, 'Read');
    assert.equal(config.hooks.PreToolUse[1].matcher, 'Bash');
  }
});

test('backup_on_tsk_branch_ignores_task_line_in_main', t => {
  const f = setup(t);
  f.git(f.repo, 'commit', '--allow-empty', '-m', taskLine);
  f.git(f.repo, 'push');
  const path = f.create(); installCommitHook(f, path); writeFileSync(join(path, 'work'), 'keep');
  const result = JSON.parse(f.run(['backup', path]).stdout);
  assert.equal(result.status, 'failed'); assert.match(result.reason, /add Task: TSK-1666/);
  assert.ok(existsSync(path)); assert.match(JSON.parse(readFileSync(f.registry, 'utf8'))[path].flag, /add Task: TSK-1666/);
});

test('subagent_stop_releases_shared_lease_without_flagging_parent', t => {
  const f = setup(t), path = f.create();
  f.run(['lease', path, 'codex:parent']); f.run(['lease', path, 'codex:child']);
  const result = spawnSync(process.execPath, [new URL('./cli.ts', import.meta.url).pathname, 'hook', 'codex'], {
    cwd: f.home, input: JSON.stringify({ cwd: path, session_id: 'parent', agent_id: 'child', hook_event_name: 'SubagentStop' }),
    encoding: 'utf8', env: { ...f.env, FAKE_PROCESSES: f.processes },
  });
  assert.equal(result.status, 0, result.stderr);
  const row = JSON.parse(readFileSync(f.registry, 'utf8'))[path];
  assert.deepEqual(Object.keys(row.leases), ['codex:parent']); assert.equal(row.flag, undefined); assert.ok(existsSync(path));
});
test('subagent_stop_backs_up_exclusive_worktree', t => {
  const f = setup(t), path = f.create(); f.run(['lease', path, 'codex:child']); writeFileSync(join(path, 'work'), 'safe');
  const result = spawnSync(process.execPath, [new URL('./cli.ts', import.meta.url).pathname, 'hook', 'codex'], {
    cwd: f.home, input: JSON.stringify({ cwd: path, session_id: 'parent', agent_id: 'child', hook_event_name: 'SubagentStop' }),
    encoding: 'utf8', env: { ...f.env, FAKE_PROCESSES: f.processes },
  });
  assert.equal(result.status, 0, result.stderr); assert.ok(existsSync(path));
  const row = JSON.parse(readFileSync(f.registry, 'utf8'))[path];
  assert.deepEqual(row.leases, {}); assert.equal(row.flag, undefined);
  assert.equal(row.backedUpHead, f.git(path, 'rev-parse', 'HEAD'));
  assert.equal(f.git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/tsk-1666-fixture').split(/\s/)[0], row.backedUpHead);
});

test('install_removes_legacy_stop_and_post_hooks_and_preserves_user_hooks', t => {
  const f = setup(t);
  for (const [provider, file] of [['codex', '.codex/hooks.json'], ['claude', '.claude/settings.json']]) {
    mkdirSync(join(f.home, provider === 'codex' ? '.codex' : '.claude'), { recursive: true });
    const launcher = "'" + join(f.home, '.local/bin/agent-runbook') + "' hook " + provider;
    const post = "/bin/sh '" + join(f.home, '.local/share/agent-runbook/fast-hook.sh') + "' post '" + process.execPath + "' '" + join(f.home, '.local/share/agent-runbook/cli.ts') + "' " + provider;
    const hooks = Object.fromEntries([['Stop', launcher], ['PostToolUse', post]].map(([event, command]) => [event, [{ hooks: [{ type: 'command', command }, { type: 'command', command: 'user-' + event }] }]]));
    writeFileSync(join(f.home, file), JSON.stringify({ hooks }));
  }
  const result = spawnSync(process.execPath, [new URL('./install.ts', import.meta.url).pathname, '--without-codex-trust'], { cwd: f.repo, env: f.env, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  for (const file of ['.codex/hooks.json', '.claude/settings.json']) {
    const config = JSON.parse(readFileSync(join(f.home, file), 'utf8'));
    for (const event of ['Stop', 'PostToolUse']) assert.deepEqual(config.hooks[event], [{ hooks: [{ type: 'command', command: 'user-' + event }] }]);
  }
});
