import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
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

const fixtureVersion = 'desktop/Cargo.toml';
const manifest = (value: string) => `[package]\nname = "x"\nversion = "${value}"\n`;
test('a commit changing a hard contract without raising the version number is refused', () => {
  const root = mkdtempSync(join(tmpdir(), 'assembly-version-fixture-'));
  const script = resolve(import.meta.dirname, 'staged-policy.ts');
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, stdio: 'pipe', encoding: 'utf8' }).trim();
  const named = ['-c', 'user.name=Hook Fixture', '-c', 'user.email=hook-fixture@example.invalid'];
  const commit = (message: string, ...flags: string[]) => git(...named, 'commit', '--quiet', ...flags, '-m', message);
  const check = (...args: string[]) => spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8' });
  const put = (file: string, text: string) => { mkdirSync(dirname(join(root, file)), { recursive: true }); writeFileSync(join(root, file), text); git('add', file); };
  const hard = ['dotnet/Assembly.Infrastructure/Socket/SocketFrames.cs', 'dotnet/Assembly.Application/Features/Chat/Rows/RowWire.cs',
    'dotnet/Assembly.Engine/Persistence/EngineSchema.cs', 'dotnet/Assembly.Application/Common/Acp/AcpProtocol.cs', 'scripts/package-adapters.ts'];
  try {
    git('init', '--quiet');
    put(fixtureVersion, manifest('0.2.0'));
    for (const file of hard) put(file, 'first');
    put('scripts/hooks/staged-policy.ts', 'hardContract('); commit('seed fixture');

    for (const file of ['desktop/src/main.rs', 'dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs', 'dotnet/Assembly.Engine/EngineCommands.cs', 'dotnet/Assembly.Application/Features/Chat/Sessions/SendFeature.cs']) put(file, 'changed');
    assert.equal(check().status, 0, 'the desktop, and the judgment part of the list, need no number');
    commit('no hard contract');

    for (const file of hard) {
      put(file, 'changed');
      const refused = check();
      assert.equal(refused.status, 1, file);
      assert.match(refused.stderr, /raise the version number/);
      assert.ok(refused.stderr.includes(file));
      put(fixtureVersion, manifest('0.2.1'));
      assert.equal(check().status, 0, 'raising it passes');
      put(fixtureVersion, manifest('0.2.0'));
      git('checkout', '--quiet', 'HEAD', '--', file);
    }

    put(hard[0], 'changed'); put(fixtureVersion, manifest('0.10.0')); commit('raised');
    put(hard[0], 'again'); put(fixtureVersion, manifest('0.9.9'));
    assert.equal(check().status, 1, 'a lower number is not a raise, by number and not by text');
    put(fixtureVersion, manifest('0.10.0'));
    assert.equal(check().status, 1, 'the same number is not a raise');
    commit('slipped past', '--no-verify');
    assert.equal(check('--commit', git('rev-parse', 'HEAD')).status, 1, 'CI refuses it too');
    assert.equal(check('--commit', git('rev-parse', 'HEAD^')).status, 0);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('a merge carries its parents\' numbers, and commits from before the rule still pass', () => {
  const root = mkdtempSync(join(tmpdir(), 'assembly-version-merge-'));
  const script = resolve(import.meta.dirname, 'staged-policy.ts');
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, stdio: 'pipe', encoding: 'utf8' }).trim();
  const named = ['-c', 'user.name=Hook Fixture', '-c', 'user.email=hook-fixture@example.invalid'];
  const hard = 'dotnet/Assembly.Infrastructure/Socket/SocketFrames.cs';
  const put = (file: string, text: string) => { mkdirSync(dirname(join(root, file)), { recursive: true }); writeFileSync(join(root, file), text); git('add', file); };
  const check = (...args: string[]) => spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8' });
  try {
    git('init', '--quiet', '-b', 'main');
    put(fixtureVersion, manifest('0.2.0')); put(hard, 'first'); put('scripts/hooks/staged-policy.ts', '// no rule yet'); git(...named, 'commit', '--quiet', '-m', 'seed');
    put(hard, 'old change'); git(...named, 'commit', '--quiet', '-m', 'before the rule');
    assert.equal(check('--commit', git('rev-parse', 'HEAD')).status, 0, 'its parent has no rule yet');
    put('scripts/hooks/staged-policy.ts', 'hardContract('); git(...named, 'commit', '--quiet', '-m', 'the rule');
    git('checkout', '--quiet', '-b', 'other');
    put(hard, 'theirs'); put(fixtureVersion, manifest('0.2.1')); git(...named, 'commit', '--quiet', '-m', 'their bump');
    git('checkout', '--quiet', 'main');
    put('note.txt', 'unrelated'); git(...named, 'commit', '--quiet', '-m', 'ours');
    git(...named, 'merge', '--quiet', '--no-commit', '--no-ff', 'other');
    assert.equal(check().status, 0, 'a merge in progress');
    git(...named, 'commit', '--quiet', '-m', 'merged');
    assert.equal(check('--commit', git('rev-parse', 'HEAD')).status, 0, 'a merge commit in CI');
  } finally { rmSync(root, { recursive: true, force: true }); }
});
