import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
test('staged Python additions refuse while removal and TypeScript tooling remain allowed', () => {
  const root = mkdtempSync(join(tmpdir(), 'assembly-policy-fixture-'));
  const script = resolve(import.meta.dirname, 'staged-policy.ts');
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, stdio: 'pipe' });
  const check = () => spawnSync(process.execPath, [script], { cwd: root, encoding: 'utf8' });
  try {
    git('init', '--quiet');
    writeFileSync(join(root, 'old.py'), '# existing'); git('add', 'old.py');
    git('-c', 'user.name=Hook Fixture', '-c', 'user.email=hook-fixture@example.invalid', 'commit', '--quiet', '-m', 'seed fixture');
    git('rm', '--quiet', 'old.py'); assert.equal(check().status, 0);
    writeFileSync(join(root, 'new.py'), '# new'); git('add', 'new.py');
    assert.equal(check().status, 1); assert.match(check().stderr, /new.py/);
    git('rm', '--force', '--quiet', 'new.py');
    writeFileSync(join(root, 'new.ts'), '// new'); git('add', 'new.ts'); assert.equal(check().status, 0);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
