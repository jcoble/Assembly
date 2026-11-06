import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { blocked } from './merge-guard.ts';
// Command vectors below are inert guard inputs. Nothing executes them.
const command = (...args: string[]) => args.join(' ');
test('owner permission is explicit on the supported merge command', () => {
  assert.ok(blocked(command('gh', 'pr', 'merge', '131', '--merge'), 'cdx/task'));
  assert.equal(blocked(command('ALLOW_MERGE=1', 'gh', 'pr', 'merge', '131', '--merge'), 'cdx/task'), undefined);
  assert.ok(blocked(command('ALLOW_MERGE=1', 'gh', 'pr', 'merge', '131', '--merge', '&&', 'gh', 'pr', 'merge', '132', '--merge'), 'cdx/task'));
  assert.ok(blocked(command('ALLOW_MERGE=1', 'gh', 'api', '-X', 'PUT', 'repos/x/y/pulls/131/merge'), 'cdx/task'));
});
test('default branch writes are refused; ordinary branch commands and documentation remain allowed', () => {
  for (const target of ['HEAD:main', 'main', 'main:main']) assert.ok(blocked(command('git', 'push', 'origin', target), 'cdx/task'));
  assert.ok(blocked(command('git', 'switch', 'main', '&&', 'git', 'merge', 'cdx/task'), 'cdx/task'));
  assert.ok(blocked(command('git', 'merge', 'cdx/task'), 'main'));
  assert.equal(blocked(command('git', 'push', 'origin', 'cdx/task'), 'cdx/task'), undefined);
  assert.equal(blocked(command('echo', '"gh', 'pr', 'merge', 'is owner controlled"'), 'cdx/task'), undefined);
});

test('repository selection and command wrapper preserve permission and merge mode', () => {
  for (const prefix of [['gh', '-R', 'owner/repo'], ['command', 'gh'], ['gh', '--repo=owner/repo']]) {
    assert.ok(blocked(command(...prefix, 'pr', 'merge', '131', '--merge'), 'cdx/task'));
    assert.equal(blocked(command('ALLOW_MERGE=1', ...prefix, 'pr', 'merge', '131', '--merge'), 'cdx/task'), undefined);
    assert.ok(blocked(command('ALLOW_MERGE=1', ...prefix, 'pr', 'merge', '131', '--merge', '--auto'), 'cdx/task'));
  }
  assert.ok(blocked(command('git', '-C', '/owned/repo', 'merge', 'cdx/task'), 'main'));
  assert.equal(blocked(command('command', 'git', '-C', '/owned/repo', 'status'), 'cdx/task'), undefined);
});

const hook = fileURLToPath(new URL('./merge-guard.ts', import.meta.url));
const runHook = (cwd: string, input: string) => spawnSync(process.execPath, [hook], { cwd, input, encoding: 'utf8' });
const asked = (cmd: string) => JSON.stringify({ tool_input: { command: cmd } });

test('a shell outside any repository can still run commands, and merges stay refused there', () => {
  const folder = realpathSync(mkdtempSync(join(tmpdir(), 'merge-guard-')));
  try {
    assert.equal(runHook(folder, asked('ls')).status, 0);
    assert.equal(runHook(folder, asked('cd /some/worktree')).status, 0);
    const merge = runHook(folder, asked(command('gh', 'pr', 'merge', '131', '--merge')));
    assert.equal(merge.status, 2);
    assert.match(merge.stderr, /Blocked/);
    assert.equal(runHook(folder, asked(command('git', 'push', 'origin', 'HEAD:main'))).status, 2);
  } finally {
    rmSync(folder, { recursive: true });
  }
});

test('input the guard cannot read is still refused, inside or outside a repository', () => {
  const folder = realpathSync(mkdtempSync(join(tmpdir(), 'merge-guard-')));
  try {
    assert.equal(runHook(folder, 'not json').status, 2);
  } finally {
    rmSync(folder, { recursive: true });
  }
});
