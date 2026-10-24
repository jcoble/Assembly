import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmod, copyFile, mkdir, mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { scanSource } from './scanSchedulers.ts';

test('rejects prohibited async and scheduler patterns', () => {
  const source = `
setTimeout(work, 1);
task.then(done).catch(failed).finally(cleanup);
const first = new Promise<void>(() => {});
const fastest = Promise.race(tasks);
const deferred = Promise.withResolvers<void>();
void (async () => work());
function load(): Promise<string> { return first; }
`;
  const categories = scanSource('src/fixture.ts', source).map((hit) => hit.category);

  assert.deepEqual(categories, [
    'scheduler',
    'promise-chain',
    'promise-chain',
    'promise-chain',
    'promise-wrapper',
    'promise-wrapper',
    'promise-wrapper',
    'anonymous-async-iife',
    'non-async-promise-implementation'
  ]);
});

test('rejects CSS :has selectors and Tailwind has variants', () => {
  const source = `
.region:HAS (.selected) { color: red; }
<div class="has-[:checked]:bg-blue-500"></div>
<div class="group-has-[a]:block"></div>
<div class="peer-has-[input]:hidden"></div>
<div class="has-checked:bg-red-500"></div>
<div class="group-has-checked/card:block"></div>
<div class="peer-has-checked:hidden"></div>
`;
  const hits = scanSource('src/fixture.svelte', source);

  assert.equal(hits.filter((hit) => hit.category === 'css-has-selector').length, 1);
  assert.equal(hits.filter((hit) => hit.category === 'tailwind-has-variant').length, 6);
});

test('rejects animations that never end', () => {
  const source = `
.a { animation: spin 1s linear infinite; }
.b { animation-iteration-count: INFINITE; }
<span style:animation="pulse 2s infinite"></span>
<span style="animation: fade 1s ease-in infinite alternate"></span>
<div class="animate-spin"></div>
<div class="motion-safe:animate-pulse"></div>
<div class="animate-ping"></div>
<div class="animate-bounce"></div>
<span style:animation={"spin 1s infinite"}></span>
<span style:animation-iteration-count={'infinite'}></span>
<span style:animation|important="spin 1s infinite"></span>
<span style:animation-iteration-count|important="infinite"></span>
<span style:animation="spin {duration}s infinite"></span>
`;
  const hits = scanSource('src/fixture.svelte', source);
  assert.deepEqual(hits.map((hit) => [hit.line, hit.category]), [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14].map((line) => [line, 'endless-animation']));
  assert.equal(scanSource('src/fixture.css', '.a { animation: spin 1s infinite; }')[0]?.category, 'endless-animation');
  assert.equal(scanSource('src/fixture.css', '.a { animation: "spin" 1s infinite; }')[0]?.category, 'endless-animation');
  const wrapped = '.a {\n  animation:\n    spin 1s infinite;\n}\n.b {\n  animation-iteration-count:\n    infinite;\n}';
  assert.deepEqual(scanSource('src/fixture.css', wrapped).map((hit) => [hit.line, hit.category]), [[2, 'endless-animation'], [6, 'endless-animation']]);
});

test('allows animations that end', () => {
  const source = `
.pop { animation: pop 200ms ease-out; }
.twice { animation-iteration-count: 2; }
.named { animation: infinite-scroll 200ms var(--infinite-easing); }
/* this used to be animation: spin 1s infinite */
<span style:animation="pop 200ms ease-out" title="Infinite scroll">Infinite scroll</span>
<div class="animate-in fade-in"></div>
`;
  assert.deepEqual(scanSource('src/fixture.svelte', source), []);
});

test('allows async implementations, Promise interfaces and ordinary CSS', () => {
  const source = `
interface Loader { load(): Promise<string>; }
async function load(): Promise<string> { return await read(); }
const transform = async (): Promise<string> => await read();
.region .selected { color: green; }
<!-- removed has-[…] class -->
`;
  assert.deepEqual(scanSource('src/fixture.ts', source), []);
});

test('CSS files receive only CSS checks', () => {
  const ordinaryCss = '.timer::before { content: "setTimeout(() => task.then(done))"; }';
  assert.deepEqual(scanSource('src/fixture.css', ordinaryCss), []);
  assert.equal(scanSource('src/fixture.css', '.region:has(.selected) {}')[0]?.category, 'css-has-selector');
});

test('the executable hook scans the staged source instead of the working copy', async () => {
  const repo = await mkdtemp(join(tmpdir(), 'assembly-lifecycle-hook-'));
  const sourceRoot = join(repo, 'tauri-svelte-preview');
  const hook = join(repo, '.githooks', 'pre-commit');
  const probe = join(sourceRoot, 'src', 'probe.ts');
  const commitBody = 'Task: TSK-1453 https://app.notion.com/p/3f6394b0689d818aa5abf6690d7f3f72\n\nArchitecture: unchanged\n\nCommitted-by: Codex';
  const git = (...args: string[]) => spawnSync('git', args, { cwd: repo, encoding: 'utf8' });
  try {
    await mkdir(join(sourceRoot, 'scripts'), { recursive: true });
    await mkdir(join(sourceRoot, 'src'), { recursive: true });
    await mkdir(join(repo, '.githooks'), { recursive: true });
    await copyFile(new URL('scanSchedulers.ts', import.meta.url), join(sourceRoot, 'scripts', 'scanSchedulers.ts'));
    await copyFile(new URL('commitChecks.ts', import.meta.url), join(sourceRoot, 'scripts', 'commitChecks.ts'));
    await copyFile(new URL('commitChecks.baseline.json', import.meta.url), join(sourceRoot, 'scripts', 'commitChecks.baseline.json'));
    await copyFile(new URL('../../.githooks/pre-commit', import.meta.url), hook);
    await chmod(hook, 0o755);
    await writeFile(join(sourceRoot, 'package.json'), '{"type":"module"}\n');
    await symlink(join(new URL('..', import.meta.url).pathname, 'node_modules'), join(sourceRoot, 'node_modules'));
    assert.equal(git('init', '-q').status, 0);
    assert.equal(git('config', 'user.name', 'Codex').status, 0);
    assert.equal(git('config', 'user.email', 'codex@example.invalid').status, 0);
    assert.equal(git('config', 'core.hooksPath', '.githooks').status, 0);

    assert.equal(git('add', 'tauri-svelte-preview/scripts/commitChecks.baseline.json').status, 0);
    await writeFile(probe, 'setTimeout(work, 1);\n');
    assert.equal(git('add', 'tauri-svelte-preview/src/probe.ts').status, 0);
    await writeFile(probe, 'export const clean = true;\n');
    const rejected = git('commit', '-m', 'Reject bad staged source', '-m', commitBody);
    assert.notEqual(rejected.status, 0);
    assert.match(`${rejected.stdout}${rejected.stderr}`, /src\/probe\.ts:1: scheduler:setTimeout/);

    assert.equal(git('add', 'tauri-svelte-preview/src/probe.ts').status, 0);
    await writeFile(probe, 'setTimeout(work, 1);\n');
    assert.equal(git('commit', '-m', 'Accept clean staged source', '-m', commitBody).status, 0);

    assert.equal(git('restore', 'tauri-svelte-preview/src/probe.ts').status, 0);
    assert.equal(git('rm', 'tauri-svelte-preview/src/probe.ts').status, 0);
    assert.equal(git('commit', '-m', 'Accept staged deletion', '-m', commitBody).status, 0);
  } finally {
    await rm(repo, { recursive: true, force: true });
  }
});
