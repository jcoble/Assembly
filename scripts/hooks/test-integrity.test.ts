import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { violations, isTest } from './test-integrity.ts';

const cs = (expected: string) => `public void PreservesIdentity() {\n Assert.Equal(\n  ${expected},\n  actual);\n}`;
test('changed multiline assertion requires its named reason even with production edits', () => {
  const changes = [{ path: 'dotnet/Assembly.Tests/IdentityTests.cs', before: cs('"source"'), after: cs('"wrong"') }, { path: 'dotnet/Assembly.Engine/X.cs', before: 'old', after: 'new' }];
  assert.equal(violations(changes, 'feat: source change').length, 1);
  assert.equal(violations(changes, 'TEST-CHANGE: OtherTest: reason').length, 1);
  assert.deepEqual(violations(changes, 'TEST-CHANGE: PreservesIdentity: reviewed representation; same source identity'), []);
});
test('Rust, TS chained assertions and Dart are protected', () => {
  for (const [path, before, after, name] of [
    ['desktop/src/view_tests.rs', 'fn keeps_path() {\n assert_eq!(path, "a");\n}', 'fn keeps_path() {}', 'keeps_path'],
    ['scripts/reader.test.ts', 'test("keeps source", () => {\n expect(value)\n .toEqual("source");\n});', 'test("keeps source", () => {});', 'keeps source'],
    ['mobile/test/view_test.dart', "test('keeps state', () {\n expect(state, 'ready');\n});", "test('keeps state', () {});", 'keeps state']
  ]) assert.ok(violations([{ path, before, after }], 'TEST-CHANGE: general').some(error => error.includes(name)));
});
test('new coverage records intent without an exemption for added assertions', () => {
  const change = { path: 'desktop/src/view_tests.rs', before: 'fn works() {\n assert!(ready);\n}', after: 'fn works() {\n assert!(ready);\n assert_eq!(id, "real");\n}' };
  assert.deepEqual(violations([change], 'TEST-CHANGE: regression for existing behavior'), []);
  assert.match(violations([change], 'test: coverage')[0], /Test-only/);
});
test('expected whitespace remains significant', () => {
  assert.ok(violations([{ path: 'dotnet/Assembly.Tests/X.cs', before: cs('"a  b"'), after: cs('"a b"') }], 'TEST-CHANGE: general').some(error => error.includes('PreservesIdentity')));
  for (const path of ['desktop/src/view/tests.rs', 'dotnet/Assembly.Tests/Pinned/X.cs', 'scripts/hooks/guard.test.ts', 'mobile/test/a_test.dart']) assert.ok(isTest(path));
});
test('actual staged changes with spaces and test deletion cannot evade the hook', () => {
  const root = mkdtempSync(join(tmpdir(), 'assembly-hook-fixture-'));
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const guard = resolve(import.meta.dirname, 'test-integrity.ts');
  try {
    git('init', '--quiet'); mkdirSync(join(root, 'dotnet/Assembly.Tests'), { recursive: true });
    const path = 'dotnet/Assembly.Tests/Identity tests.cs';
    writeFileSync(join(root, path), cs('"source"')); git('add', path);
    mkdirSync(join(root, 'scripts/hooks'), { recursive: true });
    writeFileSync(join(root, 'scripts/hooks/test-integrity.ts'), '// Policy active in this fixture.');
    git('add', 'scripts/hooks/test-integrity.ts');
    git('-c', 'user.name=Hook Fixture', '-c', 'user.email=hook-fixture@example.invalid', 'commit', '--quiet', '-m', 'test: seed owned fixture\n\nCommitted-by: Hook Fixture');
    writeFileSync(join(root, path), cs('"wrong"')); git('add', path);
    const message = join(root, 'message'); writeFileSync(message, 'TEST-CHANGE: general');
    const result = spawnSync(process.execPath, [guard, message], { cwd: root, encoding: 'utf8' });
    assert.equal(result.status, 1); assert.match(result.stderr, /PreservesIdentity/);
    writeFileSync(message, 'TEST-CHANGE: PreservesIdentity: intentional fixture correction');
    assert.equal(spawnSync(process.execPath, [guard, message], { cwd: root }).status, 0);
    git('rm', '--force', '--quiet', path); writeFileSync(message, 'TEST-CHANGE: general');
    assert.equal(spawnSync(process.execPath, [guard, message], { cwd: root }).status, 1);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('renaming a test into a production path does not remove assertion protection', () => {
  assert.ok(violations([{ path: 'desktop/src/helper.rs', previousPath: 'desktop/src/helper_tests.rs', before: 'fn checks() {\n assert!(ready);\n}', after: 'fn checks() {}' }], 'TEST-CHANGE: general').some(error => error.includes('checks')));
});

test('commenting out an assertion remains a named assertion change alongside production edits', () => {
  const before = 'fn checks() {\n assert_eq!(value, "/* literal */");\n}';
  const after = 'fn checks() {\n /*\n assert_eq!(value, "/* literal */");\n */\n}';
  const changes = [{ path: 'desktop/src/a_tests.rs', before, after }, { path: 'desktop/src/a.rs', before: 'old', after: 'new' }];
  assert.match(violations(changes, 'feat: repair')[0], /checks/);
  assert.deepEqual(violations(changes, 'TEST-CHANGE: checks: explicit reviewed replacement'), []);
  assert.deepEqual(violations([{ path: 'desktop/src/a_tests.rs', before, after: before }], 'TEST-CHANGE: new coverage'), []);
});
