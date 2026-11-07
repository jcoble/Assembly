import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';

const guard = resolve(import.meta.dirname, 'guard-test-changes.ts');
const rust = 'fn keeps_path() {\n assert_eq!(path, "a");\n}\n';

// A throwaway repo on branch `work` with its own approvals folder, so no test reads the real one.
function inRepo(run: (write: (path: string, text: string) => string, send: (tool_input: unknown) => ReturnType<typeof spawnSync>, approve: (approval: object) => void) => void): void {
  const base = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-guard-fixture-')));
  const root = join(base, 'repo'), approvals = join(base, 'approvals');
  mkdirSync(root); mkdirSync(approvals);
  try {
    execFileSync('git', ['init', '--quiet', '--initial-branch=work'], { cwd: root });
    execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', '-c', 'core.hooksPath=/dev/null', 'commit', '--quiet', '--allow-empty', '-m', 'base'], { cwd: root });
    const write = (path: string, text: string) => { const full = join(root, path); mkdirSync(dirname(full), { recursive: true }); writeFileSync(full, text); return full; };
    const send = (tool_input: unknown) => spawnSync(process.execPath, [guard], { input: JSON.stringify({ tool_input }), encoding: 'utf8', env: { ...process.env, ASSEMBLY_TEST_APPROVALS_HOME: approvals } });
    run(write, send, approval => writeFileSync(join(approvals, 'one.json'), JSON.stringify({ reason: 'outdated', ...approval })));
  } finally { rmSync(base, { recursive: true, force: true }); }
}

test('changing an assertion is blocked, sends the agent to its controller, and names the test', () => inRepo((write, send) => {
  const result = send({ file_path: write('desktop/src/view_tests.rs', rust), old_string: '"a"', new_string: '"b"' });
  assert.equal(result.status, 2);
  assert.match(String(result.stderr), /BLOCKED: .*view_tests\.rs \(keeps_path\)/);
  assert.match(String(result.stderr), /If the code is the bug, fix the code, not the test\./);
  assert.match(String(result.stderr), /Explain to your controller .* why the test is wrong/);
  assert.match(String(result.stderr), /only a test change that changes product behaviour goes to Jesse/);
  assert.match(String(result.stderr), /Then make the edit again\./);
  assert.doesNotMatch(String(result.stderr), /TEST-CHANGE|sign/);
}));

test('removing or commenting out an assertion is blocked, in every language', () => inRepo((write, send) => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  assert.equal(send({ file_path, old_string: ' assert_eq!(path, "a");\n', new_string: '' }).status, 2);
  assert.equal(send({ file_path, old_string: 'assert_eq!', new_string: '// assert_eq!' }).status, 2);
  assert.equal(send({ file_path, content: 'fn keeps_path() {}\n' }).status, 2);
  for (const [path, text, old_string] of [
    ['dotnet/Assembly.Tests/IdentityTests.cs', 'public void Keeps() {\n Assert.Equal("a", actual);\n}\n', '"a"'],
    ['scripts/reader.test.ts', 'test("keeps", () => {\n assert.equal(value, "a");\n});\n', '"a"'],
    ['mobile/test/view_test.dart', "test('keeps', () {\n expect(state, 'a');\n});\n", "'a'"],
  ]) assert.equal(send({ file_path: write(path, text), old_string, new_string: '"changed"' }).status, 2, path);
}));

test('a pure addition, or an edit beside the assertions, is allowed', () => inRepo((write, send) => {
  const file_path = write('desktop/src/view_tests.rs', rust);
  assert.equal(send({ file_path, old_string: 'assert_eq!(path, "a");', new_string: 'assert_eq!(path, "a");\n assert!(ready);' }).status, 0);
  assert.equal(send({ file_path, old_string: 'fn keeps_path() {', new_string: 'fn keeps_path() {\n let ready = true;' }).status, 0);
  assert.equal(send({ file_path, content: rust + 'fn more() {\n assert!(ready);\n}\n' }).status, 0);
}));

test('an approval note for this branch, file and test allows the edit; one for another branch does not', () => inRepo((write, send, approve) => {
  const file_path = write('desktop/src/view_tests.rs', rust), edit = { file_path, old_string: '"a"', new_string: '"b"' };
  const tests = [{ file: 'desktop/src/view_tests.rs', name: 'keeps_path' }];
  approve({ branch: 'other', tests });
  assert.equal(send(edit).status, 2);
  approve({ branch: 'work', tests });
  assert.equal(send(edit).status, 0);
}));

test('production code is not guarded', () => inRepo((write, send) => {
  const file_path = write('desktop/src/view.rs', 'fn check() {\n debug_assert!(ready);\n}\n');
  assert.equal(send({ file_path, old_string: 'debug_assert!(ready);', new_string: '' }).status, 0);
}));

test('moving a test into another changed test file is not a removal', () => inRepo((write, send) => {
  const file_path = write('desktop/src/big_tests.rs', rust);
  write('desktop/src/split_tests.rs', rust);
  assert.equal(send({ file_path, old_string: rust, new_string: '' }).status, 0);
}));

test('input it cannot read is refused', () => {
  const result = spawnSync(process.execPath, [guard], { input: 'not json', encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /refusing/);
});
