/**
 * playwrightStore.test.ts — the shell Playwright card's store, run in plain node.
 *
 * `playwrightStore.svelte.ts` is a runes module, so node cannot import it as it
 * stands: `$state` is compiler syntax, not a function. The test does what vite
 * does — strip the TypeScript types, run the Svelte compiler over the result,
 * and import the compiled JavaScript. The compiled file is written inside
 * `node_modules` so `svelte/internal/client` still resolves, and it is deleted
 * again at the end. Same recipe as `contextStore.test.mjs`.
 *
 * This works only because the store has no runtime imports of its own (its one
 * import is type-only). Keep it that way, or this test has to grow a bundler.
 *
 * What is covered: turning the raw `ps`-shaped rows the desktop app returns into
 * the groups the card shows — one group per process group id, a plain-English
 * name for what each group actually is, and how long it has been running.
 */
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import { compileModule } from 'svelte/compiler';

const storePath = fileURLToPath(
  new URL('../src/lib/shell/processes/playwrightStore.svelte.ts', import.meta.url)
);
const outputDir = fileURLToPath(new URL('../node_modules/.mcb-test-playwright/', import.meta.url));
const outputPath = `${outputDir}playwrightStore.compiled.mjs`;

mkdirSync(outputDir, { recursive: true });
const source = readFileSync(storePath, 'utf8');
const javascript = stripTypeScriptTypes(source, { mode: 'strip' });
const compiled = compileModule(javascript, {
  generate: 'client',
  filename: 'playwrightStore.svelte.js'
});
writeFileSync(outputPath, compiled.js.code);

let store;
try {
  store = await import(outputPath);
} finally {
  rmSync(outputDir, { recursive: true, force: true });
}

const {
  applyPlaywrightGroups,
  beginPlaywrightLoad,
  beginStopAll,
  beginStopSession,
  buildPlaywrightGroups,
  describeAge,
  describeCleanupOutcome,
  describePlaywrightKind,
  explainPlaywrightKind,
  failPlaywrightLoad,
  finishStop,
  groupPlaywrightProcesses,
  markPerSessionStopSupported,
  markPerSessionStopUnsupported,
  markPlaywrightActivated,
  markPlaywrightUnavailable,
  parseProcessElapsed,
  playwrightKindOf,
  playwrightState,
  resetPlaywright,
  summarizePlaywrightGroups
} = store;

let passed = 0;
function test(name, run) {
  try {
    run();
    passed += 1;
  } catch (error) {
    console.error(`FAIL ${name}`);
    console.error(error);
    process.exit(1);
  }
}

/** One `ps`-shaped row, the exact shape `list_playwright_sessions` returns. */
function psRow(overrides) {
  return {
    pid: 0,
    pgid: 0,
    command: '/usr/local/bin/node',
    name: 'node',
    label: 'Playwright CLI server',
    elapsed: '00:10',
    args: '/usr/local/bin/node /repo/node_modules/playwright/cli.js run-cli-server',
    ...overrides
  };
}

/** A whole `ps` listing for two process groups: a test run and an agent's browser. */
function psFixture() {
  return [
    psRow({
      pid: 101,
      pgid: 100,
      elapsed: '02:15:30',
      name: 'playwright/cli.js',
      label: 'Playwright CLI server'
    }),
    psRow({
      pid: 140,
      pgid: 100,
      elapsed: '02:15:12',
      name: 'Google Chrome',
      label: 'Playwright browser',
      command: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
      args: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome --user-data-dir=/tmp/playwright_chromiumdev_profile-abc'
    }),
    psRow({
      pid: 120,
      pgid: 100,
      elapsed: '02:15:20',
      name: 'Chromium',
      label: 'Playwright browser',
      command: '/tmp/ms-playwright/chromium-1200/Chromium.app/Contents/MacOS/Chromium',
      args: '/tmp/ms-playwright/chromium-1200/Chromium.app/Contents/MacOS/Chromium --user-data-dir=/tmp/playwright_chromiumdev_profile-abc'
    }),
    psRow({
      pid: 300,
      pgid: 300,
      elapsed: '00:04:00',
      name: 'playwright-mcp',
      label: 'Playwright MCP',
      args: '/usr/local/bin/node /repo/node_modules/.bin/playwright-mcp'
    })
  ];
}

// ── How long a process has been running ──────────────────────────────────────

test('an elapsed time from ps is read in every shape ps writes it', () => {
  assert.equal(parseProcessElapsed('45'), 45);
  assert.equal(parseProcessElapsed('12:34'), 12 * 60 + 34);
  assert.equal(parseProcessElapsed('01:02:03'), 3600 + 2 * 60 + 3);
  assert.equal(parseProcessElapsed('2-03:04:05'), 2 * 86400 + 3 * 3600 + 4 * 60 + 5);
  assert.equal(parseProcessElapsed('  00:59  '), 59);
});

test('an elapsed time we cannot read is null, not a wrong number', () => {
  assert.equal(parseProcessElapsed(''), null);
  assert.equal(parseProcessElapsed('ages'), null);
  assert.equal(parseProcessElapsed(undefined), null);
});

test('an age reads as a plain phrase with the right singular', () => {
  assert.equal(describeAge(0), 'less than a minute');
  assert.equal(describeAge(59), 'less than a minute');
  assert.equal(describeAge(60), '1 minute');
  assert.equal(describeAge(59 * 60), '59 minutes');
  assert.equal(describeAge(3600), '1 hour');
  assert.equal(describeAge(5 * 3600), '5 hours');
  assert.equal(describeAge(86400), '1 day');
  assert.equal(describeAge(3 * 86400 + 7200), '3 days');
  assert.equal(describeAge(null), 'unknown age');
});

// ── What kind of thing each process is ───────────────────────────────────────

test('each label the desktop app returns maps to one of the four kinds', () => {
  assert.equal(playwrightKindOf({ label: 'Playwright browser' }), 'browser');
  assert.equal(playwrightKindOf({ label: 'Playwright CLI daemon' }), 'daemon');
  assert.equal(playwrightKindOf({ label: 'Playwright CLI server' }), 'server');
  assert.equal(playwrightKindOf({ label: 'Playwright MCP' }), 'mcp');
  assert.equal(playwrightKindOf({ label: 'Playwright session' }), 'other');
  assert.equal(playwrightKindOf({}), 'other');
});

test('kind names are readable by someone who has never used Playwright', () => {
  assert.equal(describePlaywrightKind('browser'), "Playwright's own browser");
  assert.equal(describePlaywrightKind('daemon'), "Playwright's test-runner daemon");
  assert.equal(describePlaywrightKind('server'), "Playwright's command-line server");
  assert.equal(describePlaywrightKind('mcp'), "Playwright's browser-control server for agents");
  assert.equal(describePlaywrightKind('other'), 'A Playwright helper process');
  for (const kind of ['browser', 'daemon', 'server', 'mcp', 'other']) {
    assert.ok(explainPlaywrightKind(kind).length > 20, `${kind} needs a real sentence`);
  }
});

// ── Grouping ─────────────────────────────────────────────────────────────────

test('processes are grouped by process group, one group per session', () => {
  const groups = groupPlaywrightProcesses(psFixture());
  assert.equal(groups.length, 2);
  assert.deepEqual(
    groups.map((group) => group.pgid),
    [100, 300]
  );
  assert.deepEqual(groups[0].pids, [101, 120, 140]);
  assert.equal(groups[0].processCount, 3);
  assert.deepEqual(groups[1].pids, [300]);
});

test('a group is named after the thing that started it, not its browsers', () => {
  const groups = groupPlaywrightProcesses(psFixture());
  assert.equal(groups[0].kind, 'server');
  assert.equal(groups[0].kindLabel, "Playwright's command-line server");
  assert.equal(groups[1].kind, 'mcp');
});

test('a group of browsers alone is still named honestly', () => {
  const groups = groupPlaywrightProcesses([
    psRow({ pid: 51, pgid: 50, label: 'Playwright browser', elapsed: '00:30' }),
    psRow({ pid: 52, pgid: 50, label: 'Playwright browser', elapsed: '00:20' })
  ]);
  assert.equal(groups.length, 1);
  assert.equal(groups[0].kind, 'browser');
  assert.equal(groups[0].kindLabel, "Playwright's own browser");
});

test('a group is as old as its oldest process', () => {
  const groups = groupPlaywrightProcesses(psFixture());
  assert.equal(groups[0].ageSeconds, 2 * 3600 + 15 * 60 + 30);
  assert.equal(groups[0].ageLabel, '2 hours');
  assert.equal(groups[1].ageLabel, '4 minutes');
});

test('every process in a group carries its own readable line', () => {
  const [group] = groupPlaywrightProcesses(psFixture());
  const browser = group.processes.find((process) => process.pid === 140);
  assert.equal(browser.kind, 'browser');
  assert.equal(browser.kindLabel, "Playwright's own browser");
  assert.equal(browser.name, 'Google Chrome');
  assert.equal(browser.ageLabel, '2 hours');
});

test('sessions from the desktop app become the same groups', () => {
  const processes = psFixture();
  const groups = buildPlaywrightGroups([
    {
      pgid: 100,
      label: 'Playwright CLI server',
      pids: [101, 120, 140],
      processes: processes.filter((process) => process.pgid === 100)
    },
    {
      pgid: 300,
      label: 'Playwright MCP',
      pids: [300],
      processes: processes.filter((process) => process.pgid === 300)
    }
  ]);
  assert.deepEqual(
    groups.map((group) => group.pgid),
    [100, 300]
  );
  assert.equal(groups[0].kind, 'server');
  assert.equal(groups[0].processCount, 3);
});

test('a session with no process detail still shows its pids and its kind', () => {
  const groups = buildPlaywrightGroups([
    { pgid: 900, label: 'Playwright CLI daemon', pids: [901, 902], processes: [] }
  ]);
  assert.equal(groups.length, 1);
  assert.equal(groups[0].kind, 'daemon');
  assert.deepEqual(groups[0].pids, [901, 902]);
  assert.equal(groups[0].processCount, 2);
  assert.equal(groups[0].ageLabel, 'unknown age');
});

test('a summary counts sessions and processes in plain words', () => {
  const groups = groupPlaywrightProcesses(psFixture());
  assert.equal(summarizePlaywrightGroups(groups), '2 sessions, 4 processes, oldest 2 hours');
  assert.equal(summarizePlaywrightGroups([groups[1]]), '1 session, 1 process, oldest 4 minutes');
  assert.equal(summarizePlaywrightGroups([]), '');
});

// ── What happened after a stop ───────────────────────────────────────────────

test('a clean stop says what it stopped', () => {
  assert.equal(
    describeCleanupOutcome({
      sessions: [],
      terminatedPgids: [100],
      terminatedPids: [101, 120, 140],
      failedPgids: []
    }),
    'Stopped 1 session (3 processes).'
  );
  assert.equal(
    describeCleanupOutcome({
      sessions: [],
      terminatedPgids: [100, 300],
      terminatedPids: [101, 300],
      failedPgids: []
    }),
    'Stopped 2 sessions (2 processes).'
  );
});

test('a stop that found nothing says so instead of claiming success', () => {
  assert.equal(
    describeCleanupOutcome({
      sessions: [],
      terminatedPgids: [],
      terminatedPids: [],
      failedPgids: []
    }),
    'Nothing was running, so nothing was stopped.'
  );
});

test('a stop that could not finish names how many processes are left', () => {
  const message = describeCleanupOutcome({
    sessions: [],
    terminatedPgids: [100],
    terminatedPids: [101],
    failedPgids: [{ pgid: 100, pid: 120, message: 'kill -TERM 120 failed: not permitted' }]
  });
  assert.ok(message.startsWith('Stopped 1 session (1 process).'), message);
  assert.ok(message.includes('1 process could not be stopped'), message);
  assert.ok(message.includes('not permitted'), message);
});

// ── Store state ──────────────────────────────────────────────────────────────

test('the card starts inert: nothing loaded, nothing claimed', () => {
  resetPlaywright();
  assert.equal(playwrightState.activated, false);
  assert.deepEqual(playwrightState.groups, []);
  assert.equal(playwrightState.loading, false);
  assert.equal(playwrightState.error, null);
  assert.equal(playwrightState.unavailableReason, null);
  assert.equal(playwrightState.loadedAt, null);
  assert.equal(playwrightState.perSessionStopSupported, 'unknown');
});

test('a slow first load cannot overwrite a fast second one', () => {
  resetPlaywright();
  const first = beginPlaywrightLoad();
  const second = beginPlaywrightLoad();
  assert.notEqual(first, second);
  assert.equal(applyPlaywrightGroups(second, groupPlaywrightProcesses(psFixture())), true);
  assert.equal(applyPlaywrightGroups(first, []), false);
  assert.equal(playwrightState.groups.length, 2);
  assert.equal(playwrightState.loading, false);
  assert.ok(playwrightState.loadedAt !== null);
});

test('a reset invalidates the read that was already in flight', () => {
  resetPlaywright();
  // The card is unmounted mid-read (its teardown resets the store), then
  // mounted again and starts a new read. If the reset rewound the ticket, the
  // pre-reset read would be handed the same number as the new one, compare
  // equal, and overwrite the newer rows with what it found before the reset.
  const inFlight = beginPlaywrightLoad();
  resetPlaywright();
  const reopened = beginPlaywrightLoad();
  assert.notEqual(inFlight, reopened);
  assert.equal(applyPlaywrightGroups(inFlight, groupPlaywrightProcesses(psFixture())), false);
  assert.deepEqual(playwrightState.groups, [], 'the stale read landed nowhere');
  assert.equal(applyPlaywrightGroups(reopened, groupPlaywrightProcesses(psFixture())), true);
  assert.equal(playwrightState.groups.length, 2);
});

test('a failed load keeps its message and drops the spinner', () => {
  resetPlaywright();
  const ticket = beginPlaywrightLoad();
  assert.equal(failPlaywrightLoad(ticket, 'Could not read processes: no'), true);
  assert.equal(playwrightState.loading, false);
  assert.equal(playwrightState.error, 'Could not read processes: no');
  assert.equal(failPlaywrightLoad(ticket - 1, 'stale'), false);
});

test('outside the desktop app the card says so and shows no rows', () => {
  resetPlaywright();
  applyPlaywrightGroups(beginPlaywrightLoad(), groupPlaywrightProcesses(psFixture()));
  const ticket = beginPlaywrightLoad();
  assert.equal(markPlaywrightUnavailable(ticket, 'This runs in the desktop app only.'), true);
  assert.deepEqual(playwrightState.groups, []);
  assert.equal(playwrightState.unavailableReason, 'This runs in the desktop app only.');
  assert.equal(playwrightState.error, null);
});

test('one stop at a time, and the card remembers which one', () => {
  resetPlaywright();
  markPlaywrightActivated();
  assert.equal(playwrightState.activated, true);
  beginStopSession(100);
  assert.equal(playwrightState.stoppingPgid, 100);
  assert.equal(playwrightState.busy, true);
  finishStop('Stopped 1 session (3 processes).');
  assert.equal(playwrightState.stoppingPgid, null);
  assert.equal(playwrightState.busy, false);
  assert.equal(playwrightState.lastResult, 'Stopped 1 session (3 processes).');

  beginStopAll();
  assert.equal(playwrightState.stoppingAll, true);
  assert.equal(playwrightState.busy, true);
  assert.equal(playwrightState.lastResult, null);
  finishStop('Nothing was running, so nothing was stopped.');
  assert.equal(playwrightState.stoppingAll, false);
});

test('a desktop app without the one-session command says so once and stays honest', () => {
  resetPlaywright();
  assert.equal(playwrightState.perSessionStopSupported, 'unknown');
  markPerSessionStopUnsupported();
  assert.equal(playwrightState.perSessionStopSupported, 'no');
  markPerSessionStopSupported();
  assert.equal(playwrightState.perSessionStopSupported, 'yes');
});

console.log(`playwrightStore.test.ts: ${passed} passed`);
