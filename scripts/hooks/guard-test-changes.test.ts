import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
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

test('input it cannot read is refused', () => {
  const result = spawnSync(process.execPath, [guard], { input: 'not json', encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /refusing/);
});
