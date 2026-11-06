#!/usr/bin/env node
// For the controller only: signs an approval of the exact test text a lane's patch produces on its branch.
// node scripts/hooks/approve-test-change.ts --key <private key> --repo <dir> --branch <b> --patch <file>
//   --test <file>::<name after the change> [--test ...] --reason "<text>" --out <folder>
// Applies the patch to the branch tip in a temporary index (never a checkout), hashes each named test's resulting text
// (test-integrity.ts testHash: the exact text), records each test the patch removes or renames away, and writes
// <out>/<id>.json, signed into <id>.json.sig with ssh-keygen. It refuses a Rust file the patch leaves unformatted (rustfmt),
// since the lane's commit must hold exactly the approved text. It expires 24 hours after signing.
// Hand the lane the id; it commits both files under test-approvals/ with the change. Never run this for your own test changes.
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { hostname, tmpdir } from 'node:os';
import { isAbsolute, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import type { ApprovedTest } from './test-approvals.ts';
import { changedAssertions, isTest, testHash, testText } from './test-integrity.ts';

const { values } = parseArgs({ options: {
  key: { type: 'string' }, repo: { type: 'string' }, branch: { type: 'string' }, patch: { type: 'string' },
  test: { type: 'string', multiple: true }, reason: { type: 'string' }, out: { type: 'string' },
} });
const { key, repo, branch, patch, test, reason, out } = values;
if (!key || !repo || !branch || !patch || !test?.length || !reason || !out) {
  throw new Error('usage: --key <private key> --repo <dir> --branch <b> --patch <file> --test <file>::<name> [--test ...] --reason "<text>" --out <folder>');
}
if (branch === 'HEAD') throw new Error('--branch must name a branch; a detached HEAD gets no approvals');
const named = test.map(spec => {
  const at = spec.indexOf('::');
  if (at <= 0 || at + 2 >= spec.length) throw new Error('--test must be <file>::<name>: ' + spec);
  const file = spec.slice(0, at);
  if (isAbsolute(file) || file.split('/').some(part => part === '.' || part === '..')) throw new Error('--test file must be repo-relative, without . or .. parts: ' + file);
  return { file, name: spec.slice(at + 2) };
});

const temp = mkdtempSync(join(tmpdir(), 'assembly-approve-'));
let tests: ApprovedTest[];
try {
  const env = { ...process.env, GIT_INDEX_FILE: join(temp, 'index') };
  const git = (args: string[]) => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8', env, stdio: ['ignore', 'pipe', 'pipe'] });
  const show = (spec: string) => { try { return git(['show', spec]); } catch { return ''; } };
  const tip = git(['rev-parse', '--verify', 'refs/heads/' + branch + '^{commit}']).trim();
  git(['read-tree', tip]);
  try { git(['apply', '--cached', resolve(patch)]); }
  catch (error) { throw new Error('the patch does not apply to ' + branch + ' at ' + tip + ': ' + String((error as { stderr?: string }).stderr ?? error)); }
  tests = named.map(({ file, name }) => {
    const sha256 = testHash(show(':' + file), name);
    if (!sha256) throw new Error(name + ' is not in ' + file + ' after the patch');
    return { file, name, sha256 };
  });
  const patched = git(['diff-index', '--cached', '--name-only', '-z', '--diff-filter=AM', tip]).split('\0').filter(Boolean);
  for (const file of patched.filter(path => path.endsWith('.rs'))) {
    const after = show(':' + file);
    if (execFileSync('rustfmt', ['--edition', '2024', '--emit', 'stdout'], { input: after, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }) !== after) {
      throw new Error(file + ' is not formatted after the patch; run cargo fmt, then write the patch again');
    }
  }
  for (const file of git(['diff-index', '--cached', '--name-only', '-z', tip]).split('\0').filter(path => path && isTest(path))) {
    const after = show(':' + file);
    for (const name of changedAssertions(show(tip + ':' + file), after)) {
      if (named.some(test => test.file === file && test.name === name)) continue;
      if (testText(after, name) !== undefined) throw new Error('the patch also changes ' + name + ' in ' + file + '; name it with --test');
      tests.push({ file, name, removed: true });
    }
  }
} finally { rmSync(temp, { recursive: true, force: true }); }

const id = randomUUID(), at = new Date();
mkdirSync(out, { recursive: true });
const file = join(out, id + '.json');
const approval = { id, branch, tests, reason, approvedBy: 'controller@' + hostname(), at: at.toISOString(), expires: new Date(at.getTime() + 24 * 3600_000).toISOString() };
writeFileSync(file + '.tmp', JSON.stringify(approval, null, 2) + '\n');
renameSync(file + '.tmp', file);
execFileSync('ssh-keygen', ['-Y', 'sign', '-f', key, '-n', 'assembly-test-change', file], { stdio: ['ignore', 'ignore', 'inherit'] });
process.stdout.write(file + '\n');
