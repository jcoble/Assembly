import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { outgoing, updates } from './pre-push.ts';
const sha = 'a'.repeat(40), zero = '0'.repeat(40);
test('outgoing updates include every reference and forbid direct default branch writes', () => {
  assert.equal(updates(`refs/heads/cdx/a ${sha} refs/heads/cdx/a ${zero}\nrefs/heads/cdx/b ${sha} refs/heads/cdx/b ${sha}`).length, 2);
  assert.throws(() => updates(`refs/heads/cdx/a ${sha} refs/heads/main ${zero}`), /default-branch/);
  assert.throws(() => updates(`refs/heads/cdx/a ${sha} refs/heads/master ${sha}`), /default-branch/);
  assert.throws(() => updates('malformed'), /Invalid/);
  assert.deepEqual(updates(''), []);
});

test('only commits no origin branch has are outgoing', t => {
  const dir = mkdtempSync(join(tmpdir(), 'pre-push-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const bare = join(dir, 'origin.git'), work = join(dir, 'work');
  execFileSync('git', ['init', '--quiet', '--bare', bare]);
  execFileSync('git', ['init', '--quiet', '--initial-branch=main', work]);
  const git = (args: string[]) => execFileSync('git', [
    '-c', 'user.name=Test', '-c', 'user.email=test@example.com', '-c', 'core.hooksPath=/dev/null', '-c', 'commit.gpgsign=false', ...args,
  ], { cwd: work, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  const commit = (subject: string) => {
    git(['commit', '--quiet', '--allow-empty', '-m', subject]);
    return git(['rev-parse', 'HEAD']);
  };
  git(['remote', 'add', 'origin', bare]);
  commit('on main');
  git(['push', '--quiet', 'origin', 'main']);
  git(['switch', '--quiet', '-c', 'other']);
  const theirs = commit('on another unmerged branch, with a message of its own');
  git(['push', '--quiet', 'origin', 'other']);
  git(['switch', '--quiet', '-c', 'mine']);
  const first = commit('mine');
  const update = (local: string, remote: string) => ({ local, remote, remoteRef: 'refs/heads/mine' });

  assert.deepEqual(outgoing(update(first, zero), git), [first], 'a new branch answers only for its own commit');
  assert.ok(!outgoing(update(first, zero), git).includes(theirs));

  git(['push', '--quiet', 'origin', 'mine']);
  const second = commit('mine again');
  assert.deepEqual(outgoing(update(second, first), git), [second], 'an update answers for what it adds');

  git(['switch', '--quiet', '-c', 'side', first]);
  const side = commit('from the side');
  git(['switch', '--quiet', 'mine']);
  git(['merge', '--quiet', '--no-ff', '-m', 'merge', 'side']);
  const merged = git(['rev-parse', 'HEAD']);
  assert.deepEqual(new Set(outgoing(update(merged, first), git)), new Set([second, side]), 'a merge commit itself is not checked');

  assert.deepEqual(outgoing(update(zero, first), git), [], 'a delete sends nothing');
  assert.deepEqual(outgoing({ ...update(first, zero), remoteRef: 'refs/tags/v1' }, git), [], 'a tag is not a branch');
});
