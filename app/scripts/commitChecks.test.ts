import assert from 'node:assert/strict';
import test from 'node:test';
import { checkFiles, checkFixCommit } from './commitChecks.ts';

const empty = { fileSize: {}, providerBranches: {} };
const lines = (count: number) => 'const value = 1;\n'.repeat(count);
const file = (source: string, path = 'src/example.ts', addedLines: number[] = []) => ({ path, source, addedLines });

test('new source files have a 600-line limit', () => {
  assert.deepEqual(checkFiles([file(lines(600))], empty), []);
  assert.match(checkFiles([file(lines(601))], empty)[0], /src\/example.ts.*600/);
});

test('baselined source files cannot grow and shrinking asks for a lower baseline', () => {
  const baseline = { ...empty, fileSize: { 'src/example.ts': 800 } };
  assert.deepEqual(checkFiles([file(lines(800))], baseline), []);
  assert.match(checkFiles([file(lines(801))], baseline)[0], /800/);
  assert.match(checkFiles([file(lines(799))], baseline)[0], /lower.*799/);
});

test('generated files and build outputs are excluded from size checks', () => {
  for (const path of ['node_modules/a.ts', 'target/a.rs', 'build/a.svelte', 'generated/a.ts', 'src/a.generated.ts']) {
    assert.deepEqual(checkFiles([file(lines(601), path)], empty), []);
  }
  assert.deepEqual(checkFiles([file('// @generated\n' + lines(601))], empty), []);
});

test('provider comparisons are confined to the adapter folder', () => {
  const source = "if (provider === 'codex') run();";
  assert.match(checkFiles([file(source)], empty)[0], /provider.*0/);
  assert.deepEqual(checkFiles([file(source, 'tauri-svelte-preview/src-tauri/src/agent_conversation/providers/acp.rs')], empty), []);
});

test('existing provider branch counts can stay the same or shrink', () => {
  const baseline = { ...empty, providerBranches: { 'src/example.ts': 1 } };
  const branch = "if (provider !== 'claude') run();";
  assert.deepEqual(checkFiles([file(branch)], baseline), []);
  assert.deepEqual(checkFiles([file('run();')], baseline), []);
  assert.match(checkFiles([file(branch + '\n' + branch)], baseline)[0], /provider.*1/);
});

test('provider strings, switch cases and Rust variants are recognized', () => {
  for (const source of [
    'if (provider == "claude") {}',
    "if ('codex' === provider) {}",
    "case 'antigravity': run();",
    'AgentConversationProvider::Codex => run(),',
    'AgentConversationProvider::Claude | AgentConversationProvider::Antigravity => run(),',
    'if provider != AgentConversationProvider::Antigravity {}',
  ]) assert.equal(checkFiles([file(source)], empty).length, 1, source);
  assert.deepEqual(checkFiles([file("const provider = 'codex';")], empty), []);
});

test('provider checks ignore test files and Rust test modules', () => {
  const branch = "if (provider === 'codex') run();";
  assert.deepEqual(checkFiles([file(branch, 'src/a.test.ts'), file(branch, 'tests/a.ts')], empty), []);
  assert.deepEqual(checkFiles([file('#[cfg(test)]\nmod tests {\n' + branch + '\n}', 'src/a.rs')], empty), []);
});

test('production code after a Rust test-only item is still checked', () => {
  const later = "if provider == \"codex\" { run(); }";
  const helper = 'fn root() {\n  #[cfg(test)]\n  let path = test_root();\n}\n' + later;
  assert.equal(checkFiles([file(helper, 'src/a.rs')], empty).length, 1);
  const module = '#[cfg(test)]\nmod tests {\n  fn t() { let x = "{"; }\n}\n' + later;
  assert.equal(checkFiles([file(module, 'src/a.rs')], empty).length, 1);
});

test('fix subjects require a changed test or an explicit reason', () => {
  for (const subject of ['fix: repair', 'FIX(core): repair', 'Fix the crash']) {
    assert.match(checkFixCommit(subject, [file('const x = 1;')])[0], /test/);
    assert.deepEqual(checkFixCommit(subject, [file('test();', 'src/a.test.ts')]), []);
    assert.deepEqual(checkFixCommit(subject, [file('test();', 'tests/a.rs')]), []);
    assert.deepEqual(checkFixCommit(subject, [file('test();', 'mobile/test/chat_test.dart')]), []);
  }
  assert.deepEqual(checkFixCommit('feat: add feature', []), []);
  assert.deepEqual(checkFixCommit('fix: docs\n\nNo-test: docs', []), ['No-test: docs']);
  assert.match(checkFixCommit('fix: repair\n\nNo-test: ', [])[0], /test/);
});

test('Rust fixes accept added test attributes and changed code inside test modules', () => {
  for (const attribute of ['#[test]', '#[tokio::test]']) {
    assert.deepEqual(checkFixCommit('fix: repair', [file(attribute + '\nfn works() {}', 'src/a.rs', [1, 2])]), []);
  }
  const source = 'fn production() {}\n#[cfg(test)]\nmod tests {\n  fn works() {\n    assert!(true);\n  }\n}\nfn other() {}';
  assert.deepEqual(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [5])]), []);
  assert.match(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [1])])[0], /test/);
  assert.match(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [8])])[0], /test/);
});

test('existing JavaScript suites and any test-gated Rust module count as tests', () => {
  for (const path of ['scripts/parseUnifiedDiff.test.mjs', 'scripts/a.test.js', 'scripts/a.test.mts']) {
    assert.deepEqual(checkFixCommit('fix: repair', [file('test();', path)]), []);
  }
  const source = 'fn production() {}\n#[cfg(test)]\nmod contract_tests {\n  fn works() {\n    assert!(true);\n  }\n}';
  assert.deepEqual(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [5])]), []);
});

test('a test-only helper does not turn the next production module into test code', () => {
  const source = '#[cfg(test)]\nfn helper() {}\nmod production {\n  fn run() {\n    work();\n  }\n}';
  assert.match(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [5])])[0], /test/);
});

test('an attribute and module on one line still count as a test module', () => {
  const source = '#[cfg(test)] mod tests {\n  fn works() {\n    assert!(true);\n  }\n}';
  assert.deepEqual(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [3])]), []);
});

test('platform-gated test modules count as tests and are not production code', () => {
  const source = 'fn production() {}\n#[cfg(all(test, target_os = "macos"))]\nmod tests {\n  fn works() {\n    assert!(true);\n  }\n}';
  assert.deepEqual(checkFixCommit('fix: repair', [file(source, 'src/a.rs', [5])]), []);
});


test('removed module locations cannot be recreated', () => {
  const root = 'tauri-svelte-preview/';
  for (const [oldPath, newPath] of [
    ['src/lib/shell/components/conversation/TimelineItem.svelte', 'src/lib/shell/conversation/components/TimelineItem.svelte'],
    ['src-tauri/src/agent_conversation/remote_history.rs', 'src-tauri/src/agent_conversation/remote/history.rs'],
  ]) {
    assert.deepEqual(checkFiles([file('', root + oldPath)], empty), [
      `${root + oldPath}: this location was removed by the module refactor (TSK-1512); use ${root + newPath.replace(/[^/]+$/, '')}.`,
    ]);
    assert.deepEqual(checkFiles([file('', root + newPath)], empty), []);
  }
});

test('feature internals are only imported inside their feature', () => {
  const root = 'tauri-svelte-preview/src/lib/shell/';
  const internal = '$lib/shell/terminal/ghosttyFactory';
  for (const source of [`import x from '${internal}';`, `import '${internal}.ts';`, `import('${internal}');`, "import x from './terminal/../terminal/ghosttyFactory.ts';"]) {
    assert.deepEqual(checkFiles([file(source, root + 'outside.svelte')], empty), [
      `${root}outside.svelte: imports ${root}terminal/ghosttyFactory, which is internal to ${root}terminal/.`,
    ]);
  }
  for (const [source, path] of [
    [`import x from '${internal}';`, 'terminal/inside.ts'],
    ["import x from './ghosttyFactory.ts';", 'terminal/inside.svelte'],
    [`import x from '${internal}';`, 'outside.test.ts'],
  ]) assert.deepEqual(checkFiles([file(source, root + path)], empty), []);
  for (const [subject, consumer] of [
    [internal, root + 'settings/SettingsDialog.svelte'],
    ['$lib/shell/browser/components/browserPanelBounds', 'tauri-svelte-preview/src/routes/+page.svelte'],
  ]) {
    assert.deepEqual(checkFiles([file(`import x from '${subject}';`, consumer)], empty), []);
    assert.equal(checkFiles([file(`import x from '${subject}';`, consumer + '.other.ts')], empty).length, 1);
  }
  for (const path of ['browser/components/AnnotationCanvas.svelte', 'browser/components/browserPanelBounds.ts', 'browser/browserBounds.ts', 'conversation/components/TimelineItem.svelte', 'conversation/components/ToolItem.svelte']) {
    const owner = root + path.split('/')[0] + '/';
    const resolved = root + path.replace(/\.(ts|svelte)$/, '');
    assert.deepEqual(checkFiles([file(`import x from '$lib/shell/${path}';`, root + 'outside.ts')], empty), [
      `${root}outside.ts: imports ${resolved}, which is internal to ${owner}.`,
    ]);
  }
});

test('package and public imports are not boundary errors', () => {
  const source = "import x from 'svelte';\nimport Terminal from '$lib/shell/terminal/WorkspaceTerminal.svelte';\nimport { send } from '$lib/shell/conversation/conversationService.ts';";
  assert.deepEqual(checkFiles([file(source, 'tauri-svelte-preview/src/routes/+page.svelte')], empty), []);
});
