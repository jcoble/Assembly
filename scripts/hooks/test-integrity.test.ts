import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { violations, isTest, testHash } from './test-integrity.ts';

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

test('a test hash ignores wrapping but not code, and covers only its own test', () => {
  const one = 'fn a() {\n assert_eq!(path, "b");\n}\nfn other() {\n assert!(x);\n}\n';
  const wrapped = 'fn a() {\n    assert_eq!(\n        path,\n        "b",\n    );\n}\nfn other() {\n assert!(y);\n}\n';
  assert.equal(testHash(one, 'a'), testHash(wrapped, 'a'));
  assert.notEqual(testHash(one, 'a'), testHash(one.replace('"b"', '"c"'), 'a'));
  assert.notEqual(testHash(one, 'a'), testHash(one.replace('"b"', '"b "'), 'a'));
  assert.notEqual(testHash(one, 'other'), testHash(wrapped, 'other'));
  assert.equal(testHash(one, 'missing'), undefined);
  const csharp = 'public void Keeps()\n{\n  Assert.Equal("a", x);\n}\n[Fact]\npublic void Next()\n{\n}\n';
  assert.equal(testHash(csharp, 'Keeps'), testHash(csharp.replace('Next()\n{\n}', 'Next()\n{\n Assert.True(z);\n}'), 'Keeps'));
  assert.notEqual(testHash(csharp, 'Keeps'), testHash(csharp.replace('"a"', '"b"'), 'Keeps'));
  const ts = "test('keeps', () => {\n  expect(a).toBe(1);\n});\ntest('next', () => {});\n";
  assert.notEqual(testHash(ts, 'keeps'), testHash(ts.replace('toBe(1)', 'toBe(2)'), 'keeps'));
});

const approvedRust = 'fn keeps_path() {\n assert_eq!(path, "b");\n}\n', seedRust = 'fn keeps_path() {\n assert_eq!(path, "a");\n}\n';
test('with approvals, a changed assertion needs a matching approval cited by id', () => {
  const path = 'desktop/src/view_tests.rs', expires = '2999-01-01T00:00:00Z';
  const approval = { id: 'ap-1', branch: 'work', expires, tests: [{ file: path, name: 'keeps_path', sha256: testHash(approvedRust, 'keeps_path') }] };
  const change = { path, before: seedRust, after: approvedRust };
  assert.deepEqual(violations([change], 'TEST-CHANGE: keeps_path: approved in ap-1', [approval]), []);
  assert.match(violations([change], 'TEST-CHANGE: keeps_path: approved', [approval]).join(), /approval/);
  assert.match(violations([{ ...change, after: seedRust.replace('"a"', '"c"') }], 'TEST-CHANGE: keeps_path: approved in ap-1', [approval]).join(), /approval/);
  assert.match(violations([change], 'TEST-CHANGE: keeps_path: approved in ap-1', []).join(), /approval/);
  const renamed = approvedRust.replace('keeps_path', 'keeps_name');
  const rename = { id: 'ap-2', branch: 'work', expires, tests: [{ file: path, name: 'keeps_path', removed: true }, { file: path, name: 'keeps_name', sha256: testHash(renamed, 'keeps_name') }] };
  assert.deepEqual(violations([{ path, before: seedRust, after: renamed }], 'TEST-CHANGE: keeps_path: renamed, ap-2', [rename]), []);
});

// A repository whose test-integrity.ts has the approval check, with a throwaway controller key.
function approvingRepo(run: (root: string, git: (...args: string[]) => string, hook: (args: string[], env?: object) => { status: number | null; stderr: string; stdout: string }, sign: (approval: object) => void, home: string) => void): void {
  const base = mkdtempSync(join(tmpdir(), 'assembly-hook-approvals-')), root = join(base, 'repo'), home = join(base, 'approvals');
  const git = (...args: string[]) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', '-c', 'core.hooksPath=/dev/null', ...args], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const script = resolve(import.meta.dirname, 'test-integrity.ts');
  const hook = (args: string[], env = {}) => spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: '', ASSEMBLY_TEST_APPROVALS_HOME: home, ...env } });
  try {
    mkdirSync(join(root, 'desktop/src'), { recursive: true }); mkdirSync(join(root, 'scripts/hooks'), { recursive: true }); mkdirSync(home);
    const key = join(base, 'controller');
    execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key]);
    writeFileSync(join(home, 'allowed_signers'), 'controller ' + readFileSync(key + '.pub', 'utf8'));
    git('init', '--quiet', '-b', 'work');
    writeFileSync(join(root, 'desktop/src/view_tests.rs'), seedRust);
    writeFileSync(join(root, 'scripts/hooks/test-integrity.ts'), readFileSync(script, 'utf8'));
    git('add', '.'); git('commit', '--quiet', '-m', 'seed');
    const sign = (approval: object & { id?: string }) => {
      const file = join(home, approval.id + '.json');
      writeFileSync(file, JSON.stringify(approval));
      execFileSync('ssh-keygen', ['-Y', 'sign', '-f', key, '-n', 'assembly-test-change', file], { stdio: 'ignore' });
    };
    run(root, git, hook, sign, home);
  } finally { rmSync(base, { recursive: true, force: true }); }
}
const stage = (root: string, git: (...args: string[]) => string, text: string) => { writeFileSync(join(root, 'desktop/src/view_tests.rs'), text); git('add', '.'); };
const message = (root: string, text: string) => { writeFileSync(join(root, 'msg'), text); return join(root, 'msg'); };

test('a commit holds an approved change only once, and a Bash edit cannot get around it', () => approvingRepo((root, git, hook, sign, home) => {
  const expires = new Date(Date.now() + 3600_000).toISOString(), cite = 'TEST-CHANGE: keeps_path: approved in ap-1';
  sign({ id: 'ap-1', branch: 'work', expires, tests: [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path', sha256: testHash(approvedRust, 'keeps_path') }] });
  stage(root, git, seedRust.replace('"a"', '"c"'));
  assert.equal(hook([message(root, cite)]).status, 1);
  stage(root, git, approvedRust);
  assert.equal(hook([message(root, 'TEST-CHANGE: keeps_path: approved')]).status, 1);
  assert.equal(hook([message(root, cite)]).status, 0);
  git('commit', '--quiet', '-F', join(root, 'msg'));
  assert.equal(hook(['--used']).status, 0);
  assert.ok(existsSync(join(home, 'used', 'ap-1.json')) && existsSync(join(home, 'used', 'ap-1.json.sig')) && !existsSync(join(home, 'ap-1.json')));
  assert.equal(hook(['--commit', 'HEAD']).status, 0);
  stage(root, git, seedRust);
  assert.equal(hook([message(root, cite)]).status, 1);
}));

test('CI, which cannot see approvals, keeps the TEST-CHANGE rule', () => approvingRepo((root, git, hook) => {
  stage(root, git, approvedRust);
  git('commit', '--quiet', '-m', 'TEST-CHANGE: keeps_path: reviewed');
  assert.equal(hook(['--commit', 'HEAD']).status, 1);
  assert.equal(hook(['--commit', 'HEAD'], { GITHUB_ACTIONS: 'true' }).status, 0);
}));
