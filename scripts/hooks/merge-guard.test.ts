import test from 'node:test';
import assert from 'node:assert/strict';
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
