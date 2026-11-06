import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, mkdirSync, rmSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { changedAssertions, violations, isTest, testHash } from './test-integrity.ts';

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

test('a test hash is the exact text apart from line endings and trailing spaces, and covers only its own test', () => {
  const one = 'fn a() {\n assert_eq!(path, "b");\n}\nfn other() {\n assert!(x);\n}\n';
  const wrapped = 'fn a() {\n    assert_eq!(\n        path,\n        "b",\n    );\n}\nfn other() {\n assert!(y);\n}\n';
  assert.notEqual(testHash(one, 'a'), testHash(wrapped, 'a'));
  assert.equal(testHash(one, 'a'), testHash(one.replace('"b");', '"b");  ').replaceAll('\n', '\r\n'), 'a'));
  for (const [x, y] of [['new Foo()', 'newFoo()'], ['f(1,)', 'f(1)'], ['i++ + j', 'i + ++j']]) {
    assert.notEqual(testHash(`fn a() {\n    let v = ${x};\n    assert!(v);\n}\n`, 'a'), testHash(`fn a() {\n    let v = ${y};\n    assert!(v);\n}\n`, 'a'), x);
  }
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

test('a test hash keeps trailing spaces inside a literal', () => {
  const ts = "test('keeps', () => {\n  expect(a).toBe(`new\ntext`);\n});\n";
  assert.notEqual(testHash(ts, 'keeps'), testHash(ts.replace('new\n', 'new  \n'), 'keeps'));
  assert.equal(testHash(ts, 'keeps'), testHash(ts.replace('{\n', '{  \n'), 'keeps'));
});

test('an approval for a test renamed into a renamed file is matched by the old path', () => {
  const expires = '2999-01-01T00:00:00Z', before = "test('old', () => {\n  expect(a).toBe(1);\n});\n", after = before.replace("'old'", "'new'");
  const approval = { id: 'ap-3', branch: 'work', expires, tests: [{ file: 'scripts/new.test.ts', name: 'new', sha256: testHash(after, 'new') }, { file: 'scripts/old.test.ts', name: 'old', removed: true }] };
  const change = { path: 'scripts/new.test.ts', previousPath: 'scripts/old.test.ts', before, after };
  assert.deepEqual(violations([change], 'TEST-CHANGE: old: renamed, ap-3', [approval]), []);
  assert.match(violations([{ ...change, after: after.replace('toBe(1)', 'toBe(2)') }], 'TEST-CHANGE: old: renamed, ap-3', [approval]).join(), /approval/);
});

test('an approved move between files needs its replacement, with the approved text, in the same commit', () => {
  const expires = '2999-01-01T00:00:00Z', source = "test('moved', () => {\n  expect(a).toBe(1);\n});\n";
  const approval = { id: 'ap-4', branch: 'work', expires, tests: [{ file: 'scripts/a.test.ts', name: 'moved', removed: true }, { file: 'scripts/b.test.ts', name: 'moved', sha256: testHash(source, 'moved') }] };
  const removal = { path: 'scripts/a.test.ts', previousPath: 'scripts/a.test.ts', before: source, after: '' }, message = 'TEST-CHANGE: moved: moved to b, ap-4';
  const added = (after: string) => ({ path: 'scripts/b.test.ts', previousPath: 'scripts/b.test.ts', before: '', after });
  assert.match(violations([removal], message, [approval]).join(), /approval/);
  assert.match(violations([removal, added(source.replace('toBe(1)', 'toBe(2)'))], message, [approval]).join(), /approval/);
  assert.deepEqual(violations([removal, added(source)], message, [approval]), []);
});

// A repository on branch work whose seed commit holds the approval check and the controller's key in
// test-approvals/allowed_signers; BASE points at the seed, as CI's pull request base would.
function withRepo(run: (repo: {
  root: string; git: (...args: string[]) => string; check: (args: string[], env?: object) => ReturnType<typeof spawnSync>;
  approve: (approval: object & { id: string }, key?: string) => void; stranger: string; stage: (text: string) => void;
}) => void): void {
  const base = mkdtempSync(join(tmpdir(), 'assembly-committed-approvals-')), root = join(base, 'repo');
  const git = (...args: string[]) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', '-c', 'core.hooksPath=/dev/null', ...args], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const script = resolve(import.meta.dirname, 'test-integrity.ts');
  try {
    mkdirSync(join(root, 'desktop/src'), { recursive: true }); mkdirSync(join(root, 'scripts/hooks'), { recursive: true }); mkdirSync(join(root, 'test-approvals'));
    const key = (name: string) => { const path = join(base, name); execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', path]); return path; };
    const controller = key('controller'), stranger = key('stranger');
    git('init', '--quiet', '-b', 'work');
    writeFileSync(join(root, 'test-approvals/allowed_signers'), 'controller ' + readFileSync(controller + '.pub', 'utf8'));
    writeFileSync(join(root, 'desktop/src/view_tests.rs'), seedRust);
    writeFileSync(join(root, 'scripts/hooks/test-integrity.ts'), readFileSync(script, 'utf8'));
    git('add', '.'); git('commit', '--quiet', '-m', 'seed');
    const seed = git('rev-parse', 'HEAD').trim();
    run({
      root, git, stranger,
      check: (args, env = {}) => spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8', env: { ...process.env, BASE: seed, ...env } }),
      approve: (approval, signer = controller) => {
        const file = join(root, 'test-approvals', approval.id + '.json');
        writeFileSync(file, JSON.stringify(approval));
        execFileSync('ssh-keygen', ['-Y', 'sign', '-f', signer, '-n', 'assembly-test-change', file], { stdio: 'ignore' });
      },
      stage: text => { writeFileSync(join(root, 'desktop/src/view_tests.rs'), text); git('add', '.'); },
    });
  } finally { rmSync(base, { recursive: true, force: true }); }
}
// The merge check runs git merge-tree --write-tree (git 2.38+); an older git fails it closed, so its merge tests can't run.
const [gitMajor, gitMinor] = (execFileSync('git', ['--version'], { encoding: 'utf8' }).match(/(\d+)\.(\d+)/) ?? ['', '0', '0']).slice(1).map(Number);
const noMergeTree = gitMajor > 2 || (gitMajor === 2 && gitMinor >= 38) ? false : 'git ' + gitMajor + '.' + gitMinor + ' is older than 2.38, which has the merge-tree --write-tree the merge check needs';
const approvedEntry = () => [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path', sha256: testHash(approvedRust, 'keeps_path') }];
const inAnHour = () => new Date(Date.now() + 3600_000).toISOString();
const commitWith = (root: string, git: (...args: string[]) => string, text: string) => { writeFileSync(join(root, 'msg'), text); git('commit', '--quiet', '-F', join(root, 'msg')); };

test('a commit holds an approved change only once, and a Bash edit cannot get around it', () => withRepo(({ root, git, check, approve, stage }) => {
  const cite = 'TEST-CHANGE: keeps_path: approved in ap-1', msg = join(root, 'msg');
  approve({ id: 'ap-1', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  stage(seedRust.replace('"a"', '"c"'));
  writeFileSync(msg, cite);
  assert.equal(check([msg]).status, 1);
  stage(approvedRust);
  writeFileSync(msg, 'TEST-CHANGE: keeps_path: approved');
  assert.equal(check([msg]).status, 1);
  writeFileSync(msg, cite);
  assert.equal(check([msg]).status, 0);
  git('commit', '--quiet', '-F', msg);
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 0);
  stage(seedRust);
  assert.equal(check([msg]).status, 1);
}));

test('CI enforces approvals, and the environment cannot switch the check off', () => withRepo(({ root, git, check, approve, stage, stranger }) => {
  const lane = join(root, '..', 'lane');
  mkdirSync(lane);
  writeFileSync(join(lane, 'allowed_signers'), 'controller ' + readFileSync(stranger + '.pub', 'utf8'));
  approve({ id: 'ap-lane', branch: 'work', expires: inAnHour(), tests: approvedEntry() }, stranger);
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-lane');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 1);
  assert.equal(check(['--commit', 'HEAD', 'work'], { GITHUB_ACTIONS: 'true', ASSEMBLY_TEST_APPROVALS_HOME: lane }).status, 1);
}));

test('an assertion outside every test is credited to its own block, which an approval for a test does not cover', () => {
  const ts = "describe('suite', () => {\n  afterEach(() => {\n    expect(count).toBe(1);\n  });\n  test('a', () => {\n    expect(a).toBe(1);\n  });\n});\n";
  const after = ts.replace('toBe(1);\n  });\n  test', 'toBe(99);\n  });\n  test');
  assert.deepEqual(changedAssertions(ts, after), ["describe('suite', () => {"]);
  const approval = { id: 'ap-1', branch: 'work', expires: '2999-01-01T00:00:00Z', tests: [{ file: 'scripts/a.test.ts', name: 'a', sha256: testHash(after, 'a') }] };
  assert.match(violations([{ path: 'scripts/a.test.ts', before: ts, after }], "TEST-CHANGE: describe('suite', () => {: ap-1", [approval]).join(), /approval/);
  const rust = 'fn a() {\n    assert!(x);\n}\nconst _: () = assert!(ready);\n';
  assert.deepEqual(changedAssertions(rust, rust.replace('ready', 'done')), ['const _: () = assert!(ready);']);
});

test('a Rust lifetime or a C# verbatim string does not stretch a test past its end', () => {
  const rust = 'fn keeps(x: &\'static str) {\n    assert_eq!(x, "a");\n}\nconst Z: &str = "z";\n';
  assert.equal(testHash(rust, 'keeps'), testHash(rust.replace('"z"', '"y"'), 'keeps'));
  assert.deepEqual(changedAssertions(rust, rust.replace('"a"', '"b"')), ['keeps']);
  const csharp = 'public void Keeps()\n{\n    var p = @"C:\\dir\\";\n    Assert.Equal("a", p);\n}\nconst string Z = "z";\n';
  assert.equal(testHash(csharp, 'Keeps'), testHash(csharp.replace('"z"', '"y"'), 'Keeps'));
  assert.deepEqual(changedAssertions(csharp, csharp.replace('"a"', '"b"')), ['Keeps']);
});

test('the commit hook refuses an expired approval, which CI no longer judges by date', () => withRepo(({ root, git, check, approve, stage }) => {
  approve({ id: 'ap-old', branch: 'work', expires: new Date(Date.now() - 1000).toISOString(), tests: approvedEntry() });
  stage(approvedRust);
  writeFileSync(join(root, 'msg'), 'TEST-CHANGE: keeps_path: approved in ap-old');
  assert.equal(check([join(root, 'msg')]).status, 1);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-old');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 0);
}));

test('in --commit mode an approval counts once: a later change citing it, or removing it, is refused', () => withRepo(({ root, git, check, approve, stage }) => {
  approve({ id: 'ap-1', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-1');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 0);
  stage(approvedRust.replace('"b"', '"c"'));
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-1');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 1);
  git('rm', '--quiet', 'test-approvals/ap-1.json', 'test-approvals/ap-1.json.sig');
  commitWith(root, git, 'chore: drop the approval');
  const removal = check(['--commit', 'HEAD', 'work']);
  assert.equal(removal.status, 1);
  assert.match(String(removal.stderr), /only ever added/);
}));

test('a branch that brings its own allowed_signers and signs its own approval is refused', () => withRepo(({ root, git, check, approve, stage, stranger }) => {
  writeFileSync(join(root, 'test-approvals/allowed_signers'), 'controller ' + readFileSync(stranger + '.pub', 'utf8'));
  approve({ id: 'ap-self', branch: 'work', expires: inAnHour(), tests: approvedEntry() }, stranger);
  stage(approvedRust);
  writeFileSync(join(root, 'msg'), 'TEST-CHANGE: keeps_path: approved in ap-self');
  assert.equal(check([join(root, 'msg')]).status, 1);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-self');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 1);
}));

test('the base holding the controller\'s key turns the check on, whatever the branch does to its own checker', () => withRepo(({ root, git, check, stage }) => {
  git('rm', '--quiet', 'scripts/hooks/test-integrity.ts');
  commitWith(root, git, 'chore: drop the checker');
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: no approval');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 1);
}));

test('in --commit mode a merge may not remove an approval, and is not judged as a test change', { skip: noMergeTree }, () => withRepo(({ root, git, check, approve, stage }) => {
  approve({ id: 'ap-1', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-1');
  const side = (name: string) => { git('checkout', '--quiet', '-B', 'side', 'HEAD~1'); writeFileSync(join(root, name), name); git('add', name); commitWith(root, git, name); git('checkout', '--quiet', 'work'); };
  side('one');
  git('merge', '--quiet', '--no-ff', '-m', 'Merge one', 'side');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 0);
  side('two');
  git('merge', '--quiet', '--no-ff', '--no-commit', 'side');
  git('rm', '--quiet', 'test-approvals/ap-1.json', 'test-approvals/ap-1.json.sig');
  git('commit', '--quiet', '--no-edit');
  const removal = check(['--commit', 'HEAD', 'work']);
  assert.equal(removal.status, 1);
  assert.match(String(removal.stderr), /only ever added/);
}));

test('the checker run through a symlink still checks', () => withRepo(({ root, stage }) => {
  const link = join(root, '..', 'linked-test-integrity.ts');
  symlinkSync(resolve(import.meta.dirname, 'test-integrity.ts'), link);
  stage(approvedRust);
  writeFileSync(join(root, 'msg'), 'Subject');
  assert.equal(spawnSync(process.execPath, [link, join(root, 'msg')], { cwd: root }).status, 1);
}));

test('the key is read at the base tip, so a branch forked before the key landed is still checked', () => withRepo(({ root, git, check, stage }) => {
  git('checkout', '--quiet', '--orphan', 'old');
  git('rm', '--quiet', '--cached', 'test-approvals/allowed_signers');
  rmSync(join(root, 'test-approvals/allowed_signers'));
  commitWith(root, git, 'old: no key yet');
  git('checkout', '--quiet', '-b', 'keyed');
  git('checkout', '--quiet', 'work', '--', 'test-approvals/allowed_signers');
  commitWith(root, git, 'the key lands');
  git('checkout', '--quiet', '-B', 'work', 'old');
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: no approval');
  assert.equal(check(['--commit', 'HEAD', 'work'], { BASE: git('rev-parse', 'keyed').trim() }).status, 1);
}));

test('a merge may not drop an approval either parent holds, whichever parent comes first', { skip: noMergeTree }, () => withRepo(({ root, git, check, approve, stage }) => {
  approve({ id: 'ap-1', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-1');
  git('checkout', '--quiet', '-b', 'side', 'HEAD~1'); writeFileSync(join(root, 'other'), 'x'); git('add', 'other'); commitWith(root, git, 'side');
  git('merge', '--quiet', '--no-ff', '--no-commit', 'work');
  git('rm', '--quiet', '--force', 'test-approvals/ap-1.json', 'test-approvals/ap-1.json.sig');
  stage(seedRust);
  git('commit', '--quiet', '--no-edit');
  const merge = check(['--commit', 'HEAD', 'work']);
  assert.equal(merge.status, 1);
  assert.match(String(merge.stderr), /only ever added/);
}));

test('a merge\'s own test edit is judged, while a test brought in unchanged from a parent is not', { skip: noMergeTree }, () => withRepo(({ root, git, check, stage }) => {
  git('checkout', '--quiet', '-b', 'side'); stage(seedRust.replace('"a"', '"z"')); commitWith(root, git, 'side changes the test');
  git('checkout', '--quiet', 'work'); writeFileSync(join(root, 'other'), 'x'); git('add', 'other'); commitWith(root, git, 'work');
  git('merge', '--quiet', '--no-ff', '-m', 'Merge side', 'side');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 0);
  git('checkout', '--quiet', '-b', 'side2', 'HEAD~1'); writeFileSync(join(root, 'more'), 'x'); git('add', 'more'); commitWith(root, git, 'side2');
  git('checkout', '--quiet', 'work');
  git('merge', '--quiet', '--no-ff', '--no-commit', 'side2');
  stage(seedRust.replace('"a"', '"q"'));
  git('commit', '--quiet', '--no-edit');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 1);
}));

test('an approval the base already holds is used, even on a branch forked before it landed', () => withRepo(({ root, git, check, approve, stage }) => {
  git('checkout', '--quiet', '-b', 'base');
  approve({ id: 'ap-old', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  stage(approvedRust);
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-old');
  const base = git('rev-parse', 'HEAD').trim();
  git('checkout', '--quiet', 'work');
  git('checkout', '--quiet', base, '--', 'test-approvals', 'desktop/src/view_tests.rs');
  commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-old');
  assert.equal(check(['--commit', 'HEAD', 'work'], { BASE: base }).status, 1);
}));

test('a clean merge where both sides edited the same test file passes', { skip: noMergeTree }, () => withRepo(({ root, git, check, approve }) => {
  const beta = (n: number) => '\n// the second test\n\nfn beta() {\n assert_eq!(count, ' + n + ');\n}\n';
  const put = (text: string) => { writeFileSync(join(root, 'desktop/src/view_tests.rs'), text); git('add', 'desktop/src/view_tests.rs', 'test-approvals'); };
  put(seedRust + beta(4)); commitWith(root, git, 'test: add beta');
  git('checkout', '--quiet', '-b', 'side'); put(seedRust + beta(5)); commitWith(root, git, 'TEST-CHANGE: beta: main moved on');
  git('checkout', '--quiet', 'work');
  approve({ id: 'ap-1', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  put(approvedRust + beta(4)); commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-1');
  git('merge', '--quiet', '--no-ff', '-m', 'Merge side', 'side');
  assert.equal(readFileSync(join(root, 'desktop/src/view_tests.rs'), 'utf8'), approvedRust + beta(5));
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 0);
}));

test('a merge that deletes a test file the branch added is judged', { skip: noMergeTree }, () => withRepo(({ root, git, check }) => {
  writeFileSync(join(root, 'desktop/src/added_tests.rs'), 'fn added() {\n assert!(ready);\n}\n'); git('add', '.'); commitWith(root, git, 'test: add added');
  git('checkout', '--quiet', '-b', 'side', 'HEAD~1'); writeFileSync(join(root, 'other'), 'x'); git('add', 'other'); commitWith(root, git, 'side');
  git('checkout', '--quiet', 'work');
  git('merge', '--quiet', '--no-ff', '--no-commit', 'side');
  git('rm', '--quiet', 'desktop/src/added_tests.rs');
  git('commit', '--quiet', '--no-edit');
  assert.equal(check(['--commit', 'HEAD', 'work']).status, 1);
}));

test('a conflicted merge resolved to the base\'s text passes', { skip: noMergeTree }, () => withRepo(({ root, git, check, approve }) => {
  const put = (text: string) => { writeFileSync(join(root, 'desktop/src/view_tests.rs'), text); git('add', 'desktop/src/view_tests.rs', 'test-approvals'); };
  const based = seedRust.replace('"a"', '"c"');
  git('checkout', '--quiet', '-b', 'base'); put(based); commitWith(root, git, 'TEST-CHANGE: keeps_path: the base moved on');
  const base = git('rev-parse', 'HEAD').trim();
  git('checkout', '--quiet', 'work');
  approve({ id: 'ap-1', branch: 'work', expires: inAnHour(), tests: approvedEntry() });
  put(approvedRust); commitWith(root, git, 'TEST-CHANGE: keeps_path: approved in ap-1');
  assert.throws(() => git('merge', '--quiet', '--no-ff', 'base'));
  put(based);
  git('commit', '--quiet', '--no-edit');
  assert.equal(check(['--commit', 'HEAD', 'work'], { BASE: base }).status, 0);
}));
