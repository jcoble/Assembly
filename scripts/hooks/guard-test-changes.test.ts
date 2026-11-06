import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';

const guard = resolve(import.meta.dirname, 'guard-test-changes.ts');
const rust = 'fn keeps_path() {\n assert_eq!(path, "a");\n}\n';

function inRepo(run: (write: (path: string, text: string) => string) => void, folder = 'repo'): void {
  const base = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-guard-fixture-')));
  const root = join(base, folder);
  mkdirSync(root, { recursive: true });
  try {
    execFileSync('git', ['init', '--quiet'], { cwd: root });
    run((path, text) => {
      const full = join(root, path);
      mkdirSync(dirname(full), { recursive: true });
      writeFileSync(full, text);
      return full;
    });
  } finally { rmSync(base, { recursive: true, force: true }); }
}
const send = (tool_input: unknown) => spawnSync(process.execPath, [guard], { input: JSON.stringify({ tool_input }), encoding: 'utf8' });

test('changing an expected value is blocked and names the test', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  const result = send({ file_path, old_string: '"a"', new_string: '"b"' });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /BLOCKED/);
  assert.match(result.stderr, /keeps_path/);
}));

test('C#, TS and Dart assertions are protected too', () => inRepo(write => {
  for (const [path, text, old_string] of [
    ['dotnet/Assembly.Tests/IdentityTests.cs', 'public void Keeps() {\n Assert.Equal("a", actual);\n}\n', '"a"'],
    ['dotnet/Assembly.Tests/ChainTests.cs', 'public void Chains() {\n actual.Should().Be("a");\n}\n', '"a"'],
    ['scripts/reader.test.ts', 'test("keeps", () => {\n assert.equal(value, "a");\n});\n', '"a"'],
    ['mobile/test/view_test.dart', "test('keeps', () {\n expect(state, 'a');\n});\n", "'a'"],
  ]) assert.equal(send({ file_path: write(path, text), old_string, new_string: '"changed"' }).status, 2, path);
}));

test('adding an assertion or editing around one is allowed', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  const added = 'assert_eq!(path, "a");\n assert!(ready);';
  assert.equal(send({ file_path, old_string: 'assert_eq!(path, "a");', new_string: added }).status, 0);
  assert.equal(send({ file_path, old_string: 'fn keeps_path() {', new_string: 'fn keeps_path() {\n let ready = true;' }).status, 0);
}));

test('deleting an assertion is blocked', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  assert.equal(send({ file_path, old_string: ' assert_eq!(path, "a");\n', new_string: '' }).status, 2);
}));

test('commenting an assertion out is blocked', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  assert.equal(send({ file_path, old_string: 'assert_eq!', new_string: '// assert_eq!' }).status, 2);
}));

test('renaming a test with assertions is blocked', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  assert.equal(send({ file_path, old_string: 'keeps_path', new_string: 'renamed' }).status, 2);
}));

test('Write over a test file cannot drop assertions, but can keep them or start a new file', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  assert.equal(send({ file_path, content: 'fn keeps_path() {}\n' }).status, 2);
  assert.equal(send({ file_path, content: rust + 'fn more() {\n assert!(ready);\n}\n' }).status, 0);
  assert.equal(send({ file_path: file_path.replace('view_tests', 'new_tests'), content: rust }).status, 0);
}));

test('every edit of a MultiEdit counts, and replace_all reaches later matches', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust + 'fn second() {\n assert_eq!(path, "a");\n}\n');
  const edits = [{ old_string: 'keeps_path() {', new_string: 'keeps_path() { // note' }, { old_string: '"a"', new_string: '"b"' }];
  assert.equal(send({ file_path, edits }).status, 2);
  const result = send({ file_path, old_string: '"a"', new_string: '"b"', replace_all: true });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /second/);
}));

test('a replacement holding $ patterns is taken literally', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  const new_string = 'fn keeps_path() {\n let cost = "$&";';
  assert.equal(send({ file_path, old_string: 'fn keeps_path() {', new_string }).status, 0);
}));

test('assertions in production code are not guarded', () => inRepo(write => {
  const file_path = write('desktop/src/view.rs', 'fn check() {\n debug_assert!(ready);\n}\n');
  assert.equal(send({ file_path, old_string: 'debug_assert!(ready);', new_string: '' }).status, 0);
}));

test('only the path inside the repo decides what is a test', () => inRepo(write => {
  const file_path = write('desktop/src/view.rs', 'fn check() {\n debug_assert!(ready);\n}\n');
  assert.equal(send({ file_path, old_string: 'debug_assert!(ready);', new_string: '' }).status, 0);
}, 'latest/tests'));

test('a file outside any repo is not guarded', () => {
  const base = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-guard-fixture-')));
  try {
    const file_path = join(base, 'view_tests.rs');
    writeFileSync(file_path, rust);
    assert.equal(send({ file_path, old_string: '"a"', new_string: '"b"' }).status, 0);
  } finally { rmSync(base, { recursive: true, force: true }); }
});

test('line endings do not hide or invent a change', () => inRepo(write => {
  const crlf = 'test("keeps", () => {\r\n  expect(a)\r\n    .toBe(1);\r\n});\r\n';
  const file_path = write('scripts/reader.test.ts', crlf);
  assert.equal(send({ file_path, old_string: 'expect(a)\n    .toBe(1);', new_string: 'expect(a)\n    .toBe(2);' }).status, 2);
  assert.equal(send({ file_path, content: crlf.replaceAll('\r\n', '\n') }).status, 0);
}));

test('a hook command reached through a symlink still guards', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  const link = join(dirname(file_path), 'linked-guard.ts');
  symlinkSync(guard, link);
  const result = spawnSync(process.execPath, [link], { input: JSON.stringify({ tool_input: { file_path, old_string: '"a"', new_string: '"b"' } }), encoding: 'utf8' });
  assert.equal(result.status, 2);
}));

test('input it cannot read is refused', () => {
  const result = spawnSync(process.execPath, [guard], { input: 'not json', encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /refusing/);
});

test("moving a test into another changed test file is not a removal", () => inRepo(write => {
  const file_path = write("desktop/src/big_tests.rs", rust);
  write("desktop/src/split_tests.rs", rust);
  assert.equal(send({ file_path, old_string: rust, new_string: "" }).status, 0);
}));

test("a same-named test with a different expected value elsewhere does not excuse a removal", () => inRepo(write => {
  const file_path = write("desktop/src/big_tests.rs", rust);
  write("desktop/src/split_tests.rs", rust.replace("\"a\"", "\"b\""));
  assert.equal(send({ file_path, old_string: rust, new_string: "" }).status, 2);
}));

test("the same test in a file git has no change for does not excuse a removal", () => inRepo(write => {
  const file_path = write("desktop/src/big_tests.rs", rust);
  const other = write("desktop/src/split_tests.rs", rust);
  const root = dirname(dirname(dirname(other)));
  execFileSync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "add", "."], { cwd: root });
  execFileSync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "--quiet", "-m", "x"], { cwd: root });
  assert.equal(send({ file_path, old_string: rust, new_string: "" }).status, 2);
}));

// Approvals: each test makes its own throwaway controller key and signers file.
const keygen = (args: string[]) => execFileSync('ssh-keygen', args, { stdio: ['ignore', 'ignore', 'ignore'] });
function withApprovals(run: (home: string, approve: (approval: object, key?: string, id?: string) => string, newKey: (name: string) => string) => void): void {
  const home = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-guard-approvals-')));
  const keys = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-guard-keys-')));
  const newKey = (name: string) => { const key = join(keys, name); keygen(['-q', '-t', 'ed25519', '-N', '', '-C', name, '-f', key]); return key; };
  try {
    const controller = newKey('controller');
    writeFileSync(join(home, 'allowed_signers'), 'controller ' + readFileSync(controller + '.pub', 'utf8'));
    run(home, (approval, key = controller, id = 'one') => {
      const file = join(home, id + '.json');
      writeFileSync(file, JSON.stringify({ reason: 'outdated', approvedBy: 'controller', at: '2026-11-06T00:00:00Z', ...approval }));
      if (key) keygen(['-Y', 'sign', '-f', key, '-n', 'assembly-test-change', file]);
      return file;
    }, newKey);
  } finally { rmSync(home, { recursive: true, force: true }); rmSync(keys, { recursive: true, force: true }); }
}
const sendWith = (home: string, tool_input: unknown) => spawnSync(process.execPath, [guard], { input: JSON.stringify({ tool_input }), encoding: 'utf8', env: { ...process.env, ASSEMBLY_TEST_APPROVALS_HOME: home } });
function committed(file_path: string): string {
  const git = (...args: string[]) => execFileSync('git', ['-C', dirname(file_path), '-c', 'user.name=t', '-c', 'user.email=t@t', ...args], { encoding: 'utf8' });
  git('add', '-A'); git('commit', '--quiet', '-m', 'x');
  return git('rev-parse', '--abbrev-ref', 'HEAD').trim();
}
const edit = (file_path: string) => ({ file_path, old_string: '"a"', new_string: '"b"' });

test('a signed approval for this branch, file and test allows the edit', () => inRepo(write => withApprovals((home, approve) => {
  const file_path = write('desktop/src/view_tests.rs', rust), branch = committed(file_path);
  approve({ branch, tests: [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path' }] });
  assert.equal(sendWith(home, edit(file_path)).status, 0);
})));

test('an approval that does not match, or is not signed by the controller, is refused', () => inRepo(write => {
  const file_path = write('desktop/src/view_tests.rs', rust), branch = committed(file_path);
  const test = { file: 'desktop/src/view_tests.rs', name: 'keeps_path' };
  const cases: [string, (approve: (approval: object, key?: string) => string, newKey: (name: string) => string) => void][] = [
    ['wrong branch', approve => approve({ branch: branch + '-other', tests: [test] })],
    ['wrong file', approve => approve({ branch, tests: [{ ...test, file: 'desktop/src/other_tests.rs' }] })],
    ['wrong name', approve => approve({ branch, tests: [{ ...test, name: 'other' }] })],
    ['tampered after signing', approve => { const file = approve({ branch, tests: [{ ...test, name: 'other' }] }); writeFileSync(file, readFileSync(file, 'utf8').replace('"other"', '"keeps_path"')); }],
    ['signed by another key', (approve, newKey) => approve({ branch, tests: [test] }, newKey('stranger'))],
    ['unsigned', approve => approve({ branch, tests: [test] }, '')],
  ];
  for (const [label, make] of cases) withApprovals((home, approve, newKey) => {
    make(approve, newKey);
    const result = sendWith(home, edit(file_path));
    assert.equal(result.status, 2, label);
    assert.match(result.stderr, /BLOCKED/, label);
  });
}));

test('an approval for one of two changed tests still refuses, naming only the other', () => inRepo(write => withApprovals((home, approve) => {
  const file_path = write('desktop/src/view_tests.rs', rust + 'fn second() {\n assert_eq!(path, "a");\n}\n'), branch = committed(file_path);
  approve({ branch, tests: [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path' }] });
  const result = sendWith(home, { ...edit(file_path), replace_all: true });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /\(second\)/);
  assert.doesNotMatch(result.stderr, /keeps_path/);
})));

test('the block sends the agent to its controller, never to approve itself', () => inRepo(write => withApprovals(home => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  committed(file_path);
  const { stderr } = sendWith(home, edit(file_path));
  assert.match(stderr, /Tests define expected behavior\. Do NOT change tests to match code\. If the code has a bug, fix the code\./);
  assert.match(stderr, /Do not change the test\. Write the proposed change as a patch in your reports folder\./);
  assert.match(stderr, /ask your controller \(the session that started you\), not Jesse/);
  assert.match(stderr, /signing an approval for this branch and these tests\. Never write or sign an approval yourself\./);
  assert.match(stderr, /make the edit again, and name the test in a TEST-CHANGE line when you commit/);
  assert.doesNotMatch(stderr, /Explain to Jesse/);
})));
