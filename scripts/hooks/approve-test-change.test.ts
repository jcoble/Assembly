import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { testHash } from './test-integrity.ts';

const script = resolve(import.meta.dirname, 'approve-test-change.ts');

// A throwaway repo on branch claude/x holding one test, and a patch that changes its expected value.
function proposal(base: string): { root: string; patch: string } {
  const root = join(base, 'repo'), patch = join(base, 'change.patch'), file = join(root, 'desktop/src/view_tests.rs');
  const git = (...args: string[]) => execFileSync('git', ['-C', root, '-c', 'user.name=t', '-c', 'user.email=t@t', ...args], { encoding: 'utf8' });
  execFileSync('git', ['init', '--quiet', '-b', 'claude/x', root]);
  mkdirSync(join(root, 'desktop/src'), { recursive: true });
  writeFileSync(file, 'fn keeps_path() {\n    assert_eq!(path, "a");\n}\n'); git('add', '.'); git('commit', '--quiet', '-m', 'seed');
  writeFileSync(file, 'fn keeps_path() {\n    assert_eq!(path, "b");\n}\n'); writeFileSync(patch, git('diff')); git('checkout', '--quiet', '--', '.');
  return { root, patch };
}

test('writes a signed approval the guard can verify', () => {
  const base = mkdtempSync(join(tmpdir(), 'assembly-approve-'));
  try {
    const key = join(base, 'controller'), out = join(base, 'approvals'), signers = join(base, 'allowed_signers'), { root, patch } = proposal(base);
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    writeFileSync(signers, 'controller ' + readFileSync(key + '.pub', 'utf8'));
    const file = execFileSync(process.execPath, [script, '--key', key, '--repo', root, '--branch', 'claude/x', '--patch', patch,
      '--test', 'desktop/src/view_tests.rs::keeps_path', '--reason', 'spec changed', '--out', out], { encoding: 'utf8' }).trim();
    const approval = JSON.parse(readFileSync(file, 'utf8'));
    assert.equal(approval.branch, 'claude/x');
    assert.deepEqual(approval.tests, [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path', sha256: testHash('fn keeps_path() {\n    assert_eq!(path, "b");\n}\n', 'keeps_path') }]);
    assert.equal(approval.reason, 'spec changed');
    assert.match(approval.approvedBy, /^controller@/);
    assert.ok(!Number.isNaN(Date.parse(approval.at)));
    assert.equal(Date.parse(approval.expires) - Date.parse(approval.at), 24 * 3600_000);
    assert.equal(execFileSync('git', ['-C', root, 'status', '--porcelain'], { encoding: 'utf8' }), '');
    execFileSync('ssh-keygen', ['-Y', 'verify', '-f', signers, '-I', 'controller', '-n', 'assembly-test-change', '-s', file + '.sig'], { input: readFileSync(file), stdio: ['pipe', 'ignore', 'ignore'] });
  } finally { rmSync(base, { recursive: true, force: true }); }
});

const refuses = (label: string, branch: string, spec: string) => {
  const base = mkdtempSync(join(tmpdir(), 'assembly-approve-'));
  try {
    const key = join(base, 'controller'), out = join(base, 'approvals'), { root, patch } = proposal(base);
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    const result = spawnSync(process.execPath, [script, '--key', key, '--repo', root, '--branch', branch, '--patch', patch, '--test', spec, '--reason', 'r', '--out', out], { encoding: 'utf8' });
    assert.notEqual(result.status, 0, label);
    assert.ok(!existsSync(out), label);
  } finally { rmSync(base, { recursive: true, force: true }); }
};

test('refuses an approval for a detached HEAD', () => refuses('HEAD', 'HEAD', 'desktop/src/view_tests.rs::keeps_path'));

test('refuses a test file that is absolute or holds . or .. parts', () => {
  for (const file of ['/abs/view_tests.rs', './desktop/view_tests.rs', 'desktop/../view_tests.rs', 'desktop/./view_tests.rs', '..']) refuses(file, 'claude/x', file + '::keeps_path');
});

// A throwaway repo on branch work and a controller key. propose turns the lane's text into a patch against the current tip;
// sign runs the script on a patch.
const seed = 'fn keeps_path() {\n    assert_eq!(path, "a");\n}\nfn keeps_name() {\n    assert_eq!(name, "x");\n}\n';
type Sign = (patch: string, tests: string[]) => ReturnType<typeof spawnSync>;
function withBranch(run: (propose: (text: string) => string, sign: Sign, out: string, root: string) => void): void {
  const base = mkdtempSync(join(tmpdir(), 'assembly-approve-')), root = join(base, 'repo'), out = join(base, 'approvals'), file = join(root, 'desktop/src/view_tests.rs');
  const git = (...args: string[]) => execFileSync('git', ['-C', root, '-c', 'user.name=t', '-c', 'user.email=t@t', ...args], { encoding: 'utf8' });
  let patches = 0;
  try {
    const key = join(base, 'controller');
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    execFileSync('git', ['init', '--quiet', '-b', 'work', root]);
    mkdirSync(join(root, 'desktop/src'), { recursive: true });
    writeFileSync(file, seed); git('add', '.'); git('commit', '--quiet', '-m', 'seed');
    const propose = (text: string) => {
      const tip = git('show', 'HEAD:desktop/src/view_tests.rs'), patch = join(base, 'change-' + ++patches + '.patch');
      writeFileSync(file, text); writeFileSync(patch, git('diff')); writeFileSync(file, tip);
      return patch;
    };
    run(propose, (patch, tests) => spawnSync(process.execPath, [script, '--key', key, '--repo', root, '--branch', 'work', '--patch', patch,
      ...tests.flatMap(name => ['--test', name]), '--reason', 'r', '--out', out], { encoding: 'utf8' }), out, root);
  } finally { rmSync(base, { recursive: true, force: true }); }
}

test('refuses a patch that does not apply, a named test missing from the result, or a changed test it was not told about', () => withBranch((propose, sign, out, root) => {
  const file = 'desktop/src/view_tests.rs', stale = propose(seed.replace('"a"', '"b"'));
  writeFileSync(join(root, file), seed.replace('"a"', '"z"'));
  execFileSync('git', ['-C', root, '-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '--quiet', '-am', 'moves the tip']);
  const tip = seed.replace('"a"', '"z"');
  assert.notEqual(sign(stale, [file + '::keeps_path']).status, 0, 'patch does not apply');
  assert.notEqual(sign(propose(tip.replace('"x"', '"y"')), [file + '::missing']).status, 0, 'named test missing');
  assert.notEqual(sign(propose(tip.replace('"z"', '"b"').replace('"x"', '"y"')), [file + '::keeps_name']).status, 0, 'unnamed changed test');
  assert.ok(!existsSync(out));
  assert.equal(execFileSync('git', ['-C', root, 'status', '--porcelain'], { encoding: 'utf8' }), '');
  const result = sign(propose(tip.replace('"x"', '"y"')), [file + '::keeps_name']);
  assert.equal(result.status, 0, String(result.stderr));
}));

test('records a rename as the old name removed plus the new name with its approved text', () => withBranch((propose, sign, out) => {
  const proposed = seed.replace('fn keeps_path', 'fn keeps_route').replace('"a"', '"b"');
  const result = sign(propose(proposed), ['desktop/src/view_tests.rs::keeps_route']);
  assert.equal(result.status, 0, String(result.stderr));
  const approval = JSON.parse(readFileSync(String(result.stdout).trim(), 'utf8'));
  assert.deepEqual(approval.tests, [
    { file: 'desktop/src/view_tests.rs', name: 'keeps_route', sha256: testHash(proposed, 'keeps_route') },
    { file: 'desktop/src/view_tests.rs', name: 'keeps_path', removed: true },
  ]);
  assert.ok(existsSync(join(out, approval.id + '.json.sig')));
}));

test('refuses a patch that leaves Rust unformatted', () => {
  const base = mkdtempSync(join(tmpdir(), 'assembly-approve-'));
  try {
    const root = join(base, 'repo'), key = join(base, 'controller'), file = join(root, 'desktop/src/view_tests.rs');
    const formatted = 'fn keeps_path() {\n    assert_eq!(path, "a");\n}\n';
    const git = (...args: string[]) => execFileSync('git', ['-C', root, '-c', 'user.name=t', '-c', 'user.email=t@t', ...args], { encoding: 'utf8' });
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    execFileSync('git', ['init', '--quiet', '-b', 'work', root]);
    mkdirSync(join(root, 'desktop/src'), { recursive: true });
    writeFileSync(file, formatted); git('add', '.'); git('commit', '--quiet', '-m', 'seed');
    const sign = (text: string) => {
      writeFileSync(file, text); const patch = join(base, 'p' + Math.random() + '.patch'); writeFileSync(patch, git('diff')); writeFileSync(file, formatted);
      return spawnSync(process.execPath, [script, '--key', key, '--repo', root, '--branch', 'work', '--patch', patch,
        '--test', 'desktop/src/view_tests.rs::keeps_path', '--reason', 'r', '--out', join(base, 'out')], { encoding: 'utf8' });
    };
    const unformatted = sign('fn keeps_path() {\n    assert_eq!(\n        path,\n        "b");\n}\n');
    assert.notEqual(unformatted.status, 0);
    assert.match(unformatted.stderr, /not formatted/);
    assert.ok(!existsSync(join(base, 'out')));
    assert.equal(sign(formatted.replace('"a"', '"b"')).status, 0);
  } finally { rmSync(base, { recursive: true, force: true }); }
});
