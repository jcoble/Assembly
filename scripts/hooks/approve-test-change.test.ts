import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';

const script = resolve(import.meta.dirname, 'approve-test-change.ts');

test('writes a signed approval the guard can verify', () => {
  const base = mkdtempSync(join(tmpdir(), 'assembly-approve-'));
  try {
    const key = join(base, 'controller'), out = join(base, 'approvals'), signers = join(base, 'allowed_signers');
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    writeFileSync(signers, 'controller ' + readFileSync(key + '.pub', 'utf8'));
    const file = execFileSync(process.execPath, [script, '--key', key, '--branch', 'claude/x', '--test', 'desktop/src/view_tests.rs::keeps_path',
      '--test', 'scripts/a.test.ts::reads a::b', '--reason', 'spec changed', '--out', out], { encoding: 'utf8' }).trim();
    const approval = JSON.parse(readFileSync(file, 'utf8'));
    assert.equal(approval.branch, 'claude/x');
    assert.deepEqual(approval.tests, [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path' }, { file: 'scripts/a.test.ts', name: 'reads a::b' }]);
    assert.equal(approval.reason, 'spec changed');
    assert.match(approval.approvedBy, /^controller@/);
    assert.ok(!Number.isNaN(Date.parse(approval.at)));
    execFileSync('ssh-keygen', ['-Y', 'verify', '-f', signers, '-I', 'controller', '-n', 'assembly-test-change', '-s', file + '.sig'], { input: readFileSync(file), stdio: ['pipe', 'ignore', 'ignore'] });
  } finally { rmSync(base, { recursive: true, force: true }); }
});

const refuses = (label: string, branch: string, spec: string) => {
  const base = mkdtempSync(join(tmpdir(), 'assembly-approve-'));
  try {
    const key = join(base, 'controller'), out = join(base, 'approvals');
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    const result = spawnSync(process.execPath, [script, '--key', key, '--branch', branch, '--test', spec, '--reason', 'r', '--out', out], { encoding: 'utf8' });
    assert.notEqual(result.status, 0, label);
    assert.ok(!existsSync(out), label);
  } finally { rmSync(base, { recursive: true, force: true }); }
};

test('refuses an approval for a detached HEAD', () => refuses('HEAD', 'HEAD', 'desktop/src/view_tests.rs::keeps_path'));

test('refuses a test file that is absolute or holds . or .. parts', () => {
  for (const file of ['/abs/view_tests.rs', './desktop/view_tests.rs', 'desktop/../view_tests.rs', 'desktop/./view_tests.rs', '..']) refuses(file, 'claude/x', file + '::keeps_path');
});
