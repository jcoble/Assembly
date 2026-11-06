import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync, readFileSync, existsSync, utimesSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fixture } from './test-fixture.ts';

export function setup(t: any) {
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
export function age(f: ReturnType<typeof setup>, path: string, hours = 13) {
  const at = new Date(Date.now() - hours * 3600000);
  const db = JSON.parse(readFileSync(f.registry, 'utf8')); db[path].updated = at.toISOString(); writeFileSync(f.registry, JSON.stringify(db));
  for (const file of f.git(path, 'ls-files', '--cached', '--others', '--exclude-standard').split('\n').filter(Boolean)) utimesSync(join(path, file), at, at);
  for (const name of ['HEAD', 'index', 'logs/HEAD']) {
    const file = f.git(path, 'rev-parse', '--path-format=absolute', '--git-path', name);
    if (existsSync(file)) utimesSync(file, at, at);
  }
}
export function idle(t: any) {
  const f = setup(t), path = f.create();
  writeFileSync(join(path, 'work'), 'safe');
  assert.equal(JSON.parse(f.run(['backup', path]).stdout).status, 'pushed');
  age(f, path); return { f, path };
}
export function hookIn(f: any, event: any) {
  const result = spawnSync(process.execPath, [new URL('./cli.ts', import.meta.url).pathname, 'hook', 'claude'], {
    cwd: f.repo, input: JSON.stringify({ cwd: f.repo, session_id: 'parent', ...event }), encoding: 'utf8', env: { ...f.env, FAKE_PROCESSES: f.processes },
  });
  assert.equal(result.status, 0, result.stderr); return result.stdout.trim();
}
export const remoteTip = (f: any, branch: string) => f.git(f.repo, 'ls-remote', '--heads', 'origin', 'refs/heads/' + branch).split(/\s/)[0];
