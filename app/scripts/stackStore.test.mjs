/**
 * stackStore.test.mjs — the shell stack runner's store, run in plain node.
 *
 * `stackStore.svelte.ts` is a runes module, so node cannot import it as it
 * stands: `$state` is compiler syntax, not a function. The test therefore does
 * what vite does — strip the TypeScript types, run the Svelte compiler over the
 * result, and import the compiled JavaScript. The compiled file is written
 * inside `node_modules` so that `svelte/internal/client` still resolves, and it
 * is deleted again at the end. (Same trick as `contextStore.test.mjs`.)
 *
 * This works only because the store has no runtime imports of its own. Keep it
 * that way, or this test has to grow a bundler.
 *
 * What is covered:
 *  - the four states a stack can be in, derived from a saved run record and the
 *    list of processes that are listening on a port right now;
 *  - reading saved stacks back, including every broken thing a saved value can
 *    turn out to be;
 *  - the tag that says which stack a terminal session belongs to.
 */
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import { compileModule } from 'svelte/compiler';

const storePath = fileURLToPath(
  new URL('../src/lib/shell/stacks/stackStore.svelte.ts', import.meta.url)
);
const outputDir = fileURLToPath(new URL('../node_modules/.mcb-test-stacks/', import.meta.url));
const outputPath = `${outputDir}stackStore.compiled.mjs`;

mkdirSync(outputDir, { recursive: true });
const source = readFileSync(storePath, 'utf8');
const javascript = stripTypeScriptTypes(source, { mode: 'strip' });
const compiled = compileModule(javascript, {
  generate: 'client',
  filename: 'stackStore.svelte.js'
});
writeFileSync(outputPath, compiled.js.code);

// A save that does not reach SQLite is refused, so the store needs the smallest
// in-memory stand-in for the two injected setting functions.
const memory = new Map();
let rejectWrites = false;

let store;
try {
  store = await import(outputPath);
} finally {
  rmSync(outputDir, { recursive: true, force: true });
}

const {
  addStack,
  buildStackRows,
  configureStackPersistence,
  deriveStackState,
  describeStackState,
  isStackSession,
  ownedIdForStack,
  parseStackDefinitions,
  parseStackRuns,
  recordStackExit,
  recordStackStart,
  removeStack,
  resetStacks: resetStore,
  serializeStackDefinitions,
  serializeStackRuns,
  stackIdForOwnedId,
  stackProcessesFor,
  stacks,
  updateStack
} = store;

configureStackPersistence({
  read: async (key) => (memory.has(key) ? JSON.parse(memory.get(key)) : null),
  write: async (key, value) => {
    if (rejectWrites) throw new Error('SQLite write failed');
    memory.set(key, JSON.stringify(value));
  }
});

/** A clean store AND clean storage: adding reads storage first now. */
function resetStacks() {
  resetStore();
  memory.clear();
  rejectWrites = false;
}

let passed = 0;
let tests = Promise.resolve();
function test(name, run) {
  tests = tests.then(async () => {
    try {
      await run();
      passed += 1;
    } catch (error) {
      console.error(`FAILED: ${name}`);
      throw error;
    }
  });
}

const web = { id: 'stack-web', name: 'Web', script: 'pnpm dev', cwd: '/Users/me/app' };
const api = { id: 'stack-api', name: 'API', script: 'pnpm api', cwd: '/Users/me/other' };

const process5173 = { pid: 501, port: 5173, command: 'node', cwd: '/Users/me/app' };
const processNested = { pid: 502, port: 8080, command: 'node', cwd: '/Users/me/app/server' };
const processElsewhere = { pid: 900, port: 3000, command: 'node', cwd: '/Users/me/other' };

function runningRun(overrides = {}) {
  return {
    ownedId: 'owned-1',
    stackId: web.id,
    startedAt: 1000,
    exited: false,
    exitCode: null,
    signal: null,
    ...overrides
  };
}

// ── The four states ──────────────────────────────────────────────────────────

test('a stack with no run at all is not running', () => {
  const derived = deriveStackState(web, null, [process5173]);
  assert.equal(derived.state, 'stopped');
  assert.deepEqual(derived.ports, []);
});

test('a started stack with a listening port in its folder is running', () => {
  const derived = deriveStackState(web, runningRun(), [process5173, processElsewhere]);
  assert.equal(derived.state, 'running');
  assert.deepEqual(derived.ports, [5173]);
  assert.deepEqual(
    derived.processes.map((entry) => entry.pid),
    [501]
  );
});

test('a port opened in a folder underneath the stack folder still counts', () => {
  const derived = deriveStackState(web, runningRun(), [processNested]);
  assert.equal(derived.state, 'running');
  assert.deepEqual(derived.ports, [8080]);
});

test('several ports come back sorted and without repeats', () => {
  const derived = deriveStackState(web, runningRun(), [
    processNested,
    process5173,
    { pid: 503, port: 5173, command: 'node', cwd: '/Users/me/app' }
  ]);
  assert.deepEqual(derived.ports, [5173, 8080]);
});

test('a started stack with no port yet is still starting', () => {
  const derived = deriveStackState(web, runningRun(), [processElsewhere]);
  assert.equal(derived.state, 'starting');
  assert.deepEqual(derived.ports, []);
});

test('a stack whose terminal ended with a non-zero code failed', () => {
  const derived = deriveStackState(web, runningRun({ exited: true, exitCode: 1 }), []);
  assert.equal(derived.state, 'failed');
  assert.equal(derived.exitCode, 1);
});

test('a stack killed by a signal failed too', () => {
  const derived = deriveStackState(
    web,
    runningRun({ exited: true, exitCode: null, signal: 'SIGKILL' }),
    []
  );
  assert.equal(derived.state, 'failed');
});

test('a stack whose terminal ended cleanly is simply not running', () => {
  const derived = deriveStackState(web, runningRun({ exited: true, exitCode: 0 }), [process5173]);
  assert.equal(derived.state, 'stopped');
  assert.deepEqual(derived.ports, []);
});

test('a stack that ended with nothing to report is not called a failure', () => {
  const derived = deriveStackState(web, runningRun({ exited: true, exitCode: null }), []);
  assert.equal(derived.state, 'stopped');
});

test('processes are matched to a stack by folder, never by pid', () => {
  const matched = stackProcessesFor(web, [process5173, processNested, processElsewhere]);
  assert.deepEqual(
    matched.map((entry) => entry.pid),
    [501, 502]
  );
  // A folder whose NAME merely starts the same way is a different folder.
  assert.deepEqual(
    stackProcessesFor({ ...web, cwd: '/Users/me/ap' }, [process5173]),
    []
  );
});

// ── Plain-English state sentences ────────────────────────────────────────────

test('each state reads as a sentence a person can act on', () => {
  assert.equal(describeStackState({ state: 'stopped', ports: [], exitCode: null, signal: null }), 'not running');
  assert.equal(
    describeStackState({ state: 'starting', ports: [], exitCode: null, signal: null }),
    'started, no port yet'
  );
  assert.equal(
    describeStackState({ state: 'running', ports: [5173], exitCode: null, signal: null }),
    'running on port 5173'
  );
  assert.equal(
    describeStackState({ state: 'running', ports: [5173, 8080], exitCode: null, signal: null }),
    'running on ports 5173 and 8080'
  );
  assert.equal(
    describeStackState({ state: 'failed', ports: [], exitCode: 1, signal: null }),
    'stopped with error code 1'
  );
  assert.equal(
    describeStackState({ state: 'failed', ports: [], exitCode: null, signal: 'SIGKILL' }),
    'stopped by SIGKILL'
  );
});

// ── Rows ─────────────────────────────────────────────────────────────────────

test('rows carry their own state, ports and sentence', () => {
  const rows = buildStackRows(
    [web, api],
    { 'owned-1': runningRun() },
    [process5173, processElsewhere]
  );
  assert.equal(rows.length, 2);
  assert.equal(rows[0].definition.id, web.id);
  assert.equal(rows[0].state, 'running');
  assert.equal(rows[0].ownedId, 'owned-1');
  assert.equal(rows[0].statusLabel, 'running on port 5173');
  // The other stack has no run record, so the port listening in ITS folder is
  // somebody else's process and the row must not claim it.
  assert.equal(rows[1].state, 'stopped');
  assert.equal(rows[1].ownedId, null);
  assert.deepEqual(rows[1].ports, []);
});

// ── Reading saved stacks back ────────────────────────────────────────────────

test('nothing saved reads back as no stacks', () => {
  assert.deepEqual(parseStackDefinitions(null), []);
  assert.deepEqual(parseStackDefinitions(undefined), []);
  assert.deepEqual(parseStackDefinitions(''), []);
});

test('a saved value that is not usable JSON reads back as no stacks', () => {
  assert.deepEqual(parseStackDefinitions('not json at all'), []);
  assert.deepEqual(parseStackDefinitions('{"definitions":[]}'), []);
  assert.deepEqual(parseStackDefinitions('42'), []);
});

test('a saved stack missing any of its four fields is dropped, the rest survive', () => {
  const raw = JSON.stringify([
    { id: 'a', name: 'Web', script: 'pnpm dev', cwd: '/p' },
    { id: '', name: 'No id', script: 'x', cwd: '/p' },
    { id: 'b', name: '', script: 'x', cwd: '/p' },
    { id: 'c', name: 'No script', script: '   ', cwd: '/p' },
    { id: 'd', name: 'No folder', script: 'x', cwd: null },
    'not even an object',
    null
  ]);
  const parsed = parseStackDefinitions(raw);
  assert.deepEqual(
    parsed.map((entry) => entry.id),
    ['a']
  );
});

test('saved stacks are trimmed, de-duplicated by id, and stripped of extra fields', () => {
  const raw = JSON.stringify([
    { id: ' a ', name: ' Web ', script: ' pnpm dev ', cwd: ' /p ', colour: 'purple' },
    { id: 'a', name: 'Second one with the same id', script: 'x', cwd: '/p' }
  ]);
  const parsed = parseStackDefinitions(raw);
  assert.equal(parsed.length, 1);
  assert.deepEqual(parsed[0], { id: 'a', name: 'Web', script: 'pnpm dev', cwd: '/p' });
});

test('saved stacks survive a round trip', () => {
  const parsed = parseStackDefinitions(serializeStackDefinitions([web, api]));
  assert.deepEqual(parsed, [web, api]);
});

// ── The four fields the Run panel added ──────────────────────────────────────
//
// A shortcut, a page to open, and the two toggles are all optional, because a
// configuration saved before they existed has to keep loading exactly as it is.

test('a stack saved with a shortcut, a preview page and both toggles survives a round trip', () => {
  const full = {
    id: 'stack-full',
    name: 'Web',
    script: 'pnpm dev',
    cwd: '/Users/me/app',
    keybinding: 'Cmd+Shift+R',
    previewUrl: 'http://localhost:5173',
    runOnWorktreeCreation: true,
    openPreviewOnRun: true
  };
  assert.deepEqual(parseStackDefinitions(serializeStackDefinitions([full])), [full]);
});

test('a stack saved before those fields existed still loads, with none of them set', () => {
  const raw = JSON.stringify([{ id: 'a', name: 'Web', script: 'pnpm dev', cwd: '/p' }]);
  const parsed = parseStackDefinitions(raw);
  assert.deepEqual(parsed, [{ id: 'a', name: 'Web', script: 'pnpm dev', cwd: '/p' }]);
  assert.equal(parsed[0].keybinding, undefined);
  assert.equal(parsed[0].previewUrl, undefined);
  assert.equal(parsed[0].runOnWorktreeCreation, undefined);
  assert.equal(parsed[0].openPreviewOnRun, undefined);
});

test('a toggle left off is not written down at all, so an old build reads the same bytes', () => {
  const written = serializeStackDefinitions([
    {
      id: 'a',
      name: 'Web',
      script: 'pnpm dev',
      cwd: '/p',
      runOnWorktreeCreation: false,
      openPreviewOnRun: false,
      keybinding: '',
      previewUrl: ''
    }
  ]);
  assert.equal(written, JSON.stringify([{ id: 'a', name: 'Web', script: 'pnpm dev', cwd: '/p' }]));
});

test('an unusable shortcut or toggle drops that field and keeps the configuration', () => {
  const raw = JSON.stringify([
    {
      id: 'a',
      name: 'Web',
      script: 'pnpm dev',
      cwd: '/p',
      keybinding: 17,
      previewUrl: { href: 'nope' },
      runOnWorktreeCreation: 'yes',
      openPreviewOnRun: 1
    }
  ]);
  const parsed = parseStackDefinitions(raw);
  assert.equal(parsed.length, 1);
  assert.deepEqual(parsed[0], { id: 'a', name: 'Web', script: 'pnpm dev', cwd: '/p' });
});

test('adding to a store that was never read keeps what was already saved', async () => {
  resetStacks();
  memory.set(
    'run-configurations.definitions',
    JSON.stringify([{ id: 'kept', name: 'Dev server', script: 'pnpm dev', cwd: '/p' }])
  );
  // Nothing hydrated: the Run tab was opened before a session was picked.
  const added = await addStack({ name: 'Database', script: 'docker compose up db', cwd: '/p' });
  assert.ok(added);
  const onDisk = JSON.parse(memory.get('run-configurations.definitions'));
  assert.deepEqual(
    onDisk.map((definition) => definition.name),
    ['Dev server', 'Database'],
    'the save must not write over what was already there'
  );
});

test('a save that does not reach storage is refused, not claimed', async () => {
  resetStacks();
  rejectWrites = true;
  const added = await addStack({ name: 'Web', script: 'pnpm dev', cwd: '/p' });
  assert.equal(added, null);
  assert.equal(stacks.definitions.length, 0, 'nothing is shown that was not saved');
});

test('adding and changing a configuration keeps the four fields', async () => {
  resetStacks();
  const added = await addStack({
    name: 'Web',
    script: 'pnpm dev',
    cwd: '/Users/me/app',
    keybinding: ' Cmd+Shift+R ',
    previewUrl: ' http://localhost:5173 ',
    runOnWorktreeCreation: true,
    openPreviewOnRun: true
  });
  assert.equal(added.keybinding, 'Cmd+Shift+R');
  assert.equal(added.previewUrl, 'http://localhost:5173');
  assert.equal(added.runOnWorktreeCreation, true);
  assert.equal(added.openPreviewOnRun, true);

  const changed = await updateStack(added.id, {
    name: 'Web',
    script: 'pnpm dev',
    cwd: '/Users/me/app',
    openPreviewOnRun: false
  });
  assert.equal(changed.keybinding, undefined);
  assert.equal(changed.previewUrl, undefined);
  assert.equal(changed.runOnWorktreeCreation, undefined);
  assert.equal(changed.openPreviewOnRun, undefined);
});

test('the saved which-session-is-which-stack list is read just as carefully', () => {
  assert.deepEqual(parseStackRuns(null), []);
  assert.deepEqual(parseStackRuns('nonsense'), []);
  assert.deepEqual(parseStackRuns('{}'), []);
  const raw = JSON.stringify([
    { ownedId: 'o1', stackId: 's1', startedAt: 5, exited: false, exitCode: null, signal: null },
    { ownedId: '', stackId: 's2', startedAt: 5 },
    { ownedId: 'o3', stackId: '', startedAt: 5 },
    { ownedId: 'o4', stackId: 's4', startedAt: 'yesterday', exited: 'yes', exitCode: 'one' },
    17
  ]);
  const parsed = parseStackRuns(raw);
  assert.deepEqual(
    parsed.map((entry) => entry.ownedId),
    ['o1', 'o4']
  );
  const repaired = parsed[1];
  assert.equal(repaired.startedAt, 0);
  assert.equal(repaired.exited, false);
  assert.equal(repaired.exitCode, null);
  assert.equal(repaired.signal, null);
});

test('run records survive a round trip', () => {
  const record = runningRun({ exited: true, exitCode: 2, signal: null });
  assert.deepEqual(parseStackRuns(serializeStackRuns([record])), [record]);
});

// ── Mutations ────────────────────────────────────────────────────────────────

test('adding a stack keeps it, and refuses one with a blank field', async () => {
  resetStacks();
  const added = await addStack({ name: ' Web ', script: ' pnpm dev ', cwd: '/Users/me/app' });
  assert.ok(added);
  assert.equal(added.name, 'Web');
  assert.equal(added.script, 'pnpm dev');
  assert.equal(stacks.definitions.length, 1);

  assert.equal(await addStack({ name: '', script: 'pnpm dev', cwd: '/p' }), null);
  assert.equal(await addStack({ name: 'x', script: '  ', cwd: '/p' }), null);
  assert.equal(await addStack({ name: 'x', script: 'y', cwd: '' }), null);
  assert.equal(stacks.definitions.length, 1);
  assert.ok(stacks.notice, 'a refused add says why');
});

test('a session is tagged with the stack it was started for, and untagged when the stack goes', async () => {
  resetStacks();
  const added = await addStack({ name: 'Web', script: 'pnpm dev', cwd: '/Users/me/app' });
  await recordStackStart(added.id, 'owned-9');

  assert.equal(stackIdForOwnedId('owned-9'), added.id);
  assert.equal(isStackSession('owned-9'), true);
  assert.equal(isStackSession('owned-other'), false);
  assert.equal(ownedIdForStack(added.id), 'owned-9');

  await removeStack(added.id);
  assert.equal(stacks.definitions.length, 0);
  assert.equal(stackIdForOwnedId('owned-9'), null);
});

test('starting a stack again replaces the session it points at', async () => {
  resetStacks();
  const added = await addStack({ name: 'Web', script: 'pnpm dev', cwd: '/Users/me/app' });
  await recordStackStart(added.id, 'owned-1');
  await recordStackStart(added.id, 'owned-2');
  assert.equal(ownedIdForStack(added.id), 'owned-2');
  assert.equal(stackIdForOwnedId('owned-1'), null);
});

test('a terminal that ends is written down against its stack, and other terminals are ignored', async () => {
  resetStacks();
  const added = await addStack({ name: 'Web', script: 'pnpm dev', cwd: '/Users/me/app' });
  await recordStackStart(added.id, 'owned-1');

  assert.equal(await recordStackExit('someone-elses-session', { exitCode: 1, signal: null }), false);
  assert.equal(await recordStackExit('owned-1', { exitCode: 1, signal: null }), true);

  const rows = buildStackRows(stacks.definitions, stacks.runs, []);
  assert.equal(rows[0].state, 'failed');
  assert.equal(rows[0].statusLabel, 'stopped with error code 1');
});

await tests;
resetStacks();
console.log(`stackStore: ${passed} passed`);
