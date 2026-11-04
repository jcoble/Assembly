import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { backendIdentity, workingBackendIdentity } from './dev-remote-identity.ts';

test('build identity ignores Rust-only revisions and detects uncommitted backend inputs', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backend-identity-'));
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  const commit = () => { git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture'); return git('rev-parse', 'HEAD'); };
  try {
    git('init', '-q');
    mkdirSync(join(root, 'dotnet/Assembly.Remote'), { recursive: true });
    mkdirSync(join(root, 'desktop'));
    const input = join(root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj');
    writeFileSync(input, 'backend');
    const first = commit();
    writeFileSync(join(root, 'desktop/native.rs'), 'rust only');
    const second = commit();
    const identity = await backendIdentity(root, first);
    assert.equal(await backendIdentity(root, second), identity);
    assert.equal(await workingBackendIdentity(root, second), identity);
    writeFileSync(input, 'uncommitted backend');
    assert.notEqual(await workingBackendIdentity(root, second), identity);
    const third = commit();
    assert.equal(await workingBackendIdentity(root, third), await backendIdentity(root, third));
    assert.notEqual(await backendIdentity(root, third), identity);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
