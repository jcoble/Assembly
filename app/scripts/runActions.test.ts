/**
 * runActions.test.ts — the two pure modules behind the Run panel.
 *
 * Both are pure by design so they can run in plain node with nothing mocked:
 * `keybindingCapture.ts` turns a keydown into a written combination and matches
 * one against the saved actions, and `runOutputTail.ts` keeps the last few
 * hundred lines of a running command's output.
 *
 * Node has no `KeyboardEvent`, so the tests hand in plain objects with the four
 * modifier flags and a `key`. That is every field the module reads, and reading
 * anything else would be a bug this test is meant to catch.
 */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  formatKeybinding,
  isModifierOnly,
  matchKeybinding
} from '../src/lib/shell/panels/run/keybindingCapture.ts';
import {
  RUN_OUTPUT_TAIL_LINES,
  appendOutputTail
} from '../src/lib/shell/panels/run/runOutputTail.ts';

/** A keydown as the two functions read it. */
function keydown(
  key: string,
  modifiers: { meta?: boolean; ctrl?: boolean; alt?: boolean; shift?: boolean } = {}
): KeyboardEvent {
  return {
    key,
    metaKey: modifiers.meta === true,
    ctrlKey: modifiers.ctrl === true,
    altKey: modifiers.alt === true,
    shiftKey: modifiers.shift === true
  } as KeyboardEvent;
}

let passed = 0;
function test(name: string, run: () => void): void {
  try {
    run();
    passed += 1;
  } catch (error) {
    console.error(`FAILED: ${name}`);
    throw error;
  }
}

// ── formatKeybinding ─────────────────────────────────────────────────────────

test('a combination is written with its modifiers in one fixed order', () => {
  assert.equal(formatKeybinding(keydown('r', { meta: true, shift: true })), 'Cmd+Shift+R');
  assert.equal(
    formatKeybinding(keydown('R', { shift: true, alt: true, ctrl: true, meta: true })),
    'Cmd+Ctrl+Alt+Shift+R'
  );
  assert.equal(formatKeybinding(keydown('d', { ctrl: true })), 'Ctrl+D');
});

test('the same combination is written the same way whichever order it arrives in', () => {
  const first = formatKeybinding(keydown('k', { alt: true, meta: true }));
  const second = formatKeybinding(keydown('K', { meta: true, alt: true }));
  assert.equal(first, second);
  assert.equal(first, 'Cmd+Alt+K');
});

test('a key that is only a modifier being held is not a combination', () => {
  assert.equal(formatKeybinding(keydown('Shift', { shift: true })), null);
  assert.equal(formatKeybinding(keydown('Meta', { meta: true })), null);
  assert.equal(formatKeybinding(keydown('Control', { ctrl: true })), null);
  assert.equal(formatKeybinding(keydown('Alt', { alt: true })), null);
});

test('named keys keep their names and the space bar is spelled out', () => {
  assert.equal(formatKeybinding(keydown('Enter', { meta: true })), 'Cmd+Enter');
  assert.equal(formatKeybinding(keydown('ArrowUp', { alt: true })), 'Alt+ArrowUp');
  assert.equal(formatKeybinding(keydown(' ', { ctrl: true })), 'Ctrl+Space');
  assert.equal(formatKeybinding(keydown('F5')), 'F5');
});

// ── isModifierOnly ───────────────────────────────────────────────────────────

test('holding a modifier keeps the field waiting; a real key ends the wait', () => {
  assert.equal(isModifierOnly(keydown('Shift', { shift: true })), true);
  assert.equal(isModifierOnly(keydown('R', { shift: true })), false);
  assert.equal(isModifierOnly(keydown('r')), false);
});

// ── matchKeybinding ──────────────────────────────────────────────────────────

const web = {
  id: 'stack-web',
  name: 'Web',
  script: 'pnpm dev',
  cwd: '/Users/me/app',
  keybinding: 'Cmd+Shift+R'
};
const api = {
  id: 'stack-api',
  name: 'API',
  script: 'dotnet watch',
  cwd: '/Users/me/app/api',
  keybinding: 'Ctrl+D'
};
const unbound = { id: 'stack-db', name: 'Database', script: 'docker compose up', cwd: '/Users/me/app' };

test('a keydown finds the action that was saved with that combination', () => {
  const matched = matchKeybinding(keydown('r', { meta: true, shift: true }), [unbound, web, api]);
  assert.equal(matched?.id, 'stack-web');
});

test('a keydown nothing was saved for matches nothing', () => {
  assert.equal(matchKeybinding(keydown('j', { meta: true }), [web, api, unbound]), null);
  assert.equal(matchKeybinding(keydown('Shift', { shift: true }), [web, api]), null);
  assert.equal(matchKeybinding(keydown('r', { meta: true, shift: true }), []), null);
});

test('when two actions share a combination the first one saved wins', () => {
  const rival = { ...web, id: 'stack-rival', name: 'Rival' };
  assert.equal(matchKeybinding(keydown('r', { meta: true, shift: true }), [web, rival])?.id, 'stack-web');
  assert.equal(matchKeybinding(keydown('r', { meta: true, shift: true }), [rival, web])?.id, 'stack-rival');
});

test('a saved combination written another way still matches', () => {
  const loose = { ...web, keybinding: 'shift + cmd + r' };
  assert.equal(matchKeybinding(keydown('R', { meta: true, shift: true }), [loose])?.id, 'stack-web');
});

// ── appendOutputTail ─────────────────────────────────────────────────────────

test('a multi-line chunk becomes one tail line per line of output', () => {
  assert.deepEqual(appendOutputTail([], 'first\nsecond\nthird'), ['first', 'second', 'third']);
});

test('a chunk with no trailing newline leaves the last line open for the next chunk', () => {
  const first = appendOutputTail([], 'ready on ');
  assert.deepEqual(first, ['ready on ']);
  assert.deepEqual(appendOutputTail(first, 'port 5173\n'), ['ready on port 5173', '']);
});

test('the tail never grows past its line limit', () => {
  assert.equal(RUN_OUTPUT_TAIL_LINES, 200);
  let tail: string[] = [];
  for (let index = 0; index < RUN_OUTPUT_TAIL_LINES + 50; index += 1) {
    tail = appendOutputTail(tail, `line ${index}\n`);
  }
  assert.equal(tail.length, RUN_OUTPUT_TAIL_LINES);
  assert.equal(tail[0], `line ${RUN_OUTPUT_TAIL_LINES + 50 - RUN_OUTPUT_TAIL_LINES + 1}`);
  assert.equal(tail.at(-2), `line ${RUN_OUTPUT_TAIL_LINES + 49}`);
});

test('one oversized chunk is cut down to the limit too', () => {
  const chunk = Array.from({ length: 500 }, (_, index) => `line ${index}`).join('\n');
  const tail = appendOutputTail(['older'], chunk);
  assert.equal(tail.length, RUN_OUTPUT_TAIL_LINES);
  assert.equal(tail.at(-1), 'line 499');
});

test('the tail is left readable: no escape sequences, no stray carriage returns', () => {
  assert.deepEqual(appendOutputTail([], '\u001b[32mgreen\u001b[0m\r\ndone\n'), [
    'green',
    'done',
    ''
  ]);
});

test('the input tail is never changed in place', () => {
  const original = ['one'];
  const next = appendOutputTail(original, ' and two\n');
  assert.deepEqual(original, ['one']);
  assert.notEqual(next, original);
});

// ── The panel's wiring ───────────────────────────────────────────────────────
//
// These read the components' source. A Svelte component cannot be mounted in
// plain node without a bundler, and the four facts below are the ones that go
// quietly wrong in a refactor: the panel must build on the kit, must hand a
// preview URL to the browser rather than opening it itself, must let go of its
// output subscription when the tab is closed, and must never start anything on
// its own.

const source = (path: string): string =>
  readFileSync(new URL(`../src/${path}`, import.meta.url), 'utf8');

const panel = source('lib/shell/panels/run/RunPanel.svelte');
const dialog = source('lib/shell/panels/run/AddActionDialog.svelte');
const actionRow = source('lib/shell/panels/run/RunActionRow.svelte');
const processRow = source('lib/shell/panels/run/RunningProcessRow.svelte');

test('the panel is built on the kit primitives rather than its own controls', () => {
  for (const primitive of ['PanelHeader', 'EmptyState', 'ScrollArea', 'Button', 'IconButton']) {
    assert.match(panel, new RegExp(`\\b${primitive}\\b`), `the panel uses ${primitive}`);
  }
  assert.match(actionRow, /\bListRow\b/);
  assert.match(actionRow, /\bHoverActionButton\b/);
  assert.match(actionRow, /\bChip\b/);
  for (const [name, component] of [
    ['panel', panel],
    ['action row', actionRow],
    ['running row', processRow]
  ] as const) {
    assert.doesNotMatch(component, /<button/, `the ${name} has no hand-rolled button`);
  }
});

test('a preview page is opened through the navigation module, never directly', () => {
  assert.match(panel, /openUrlInBrowser\(\{ url \}\)/);
  assert.match(panel, /definition\.openPreviewOnRun === true && url/);
  assert.doesNotMatch(panel, /window\.open|location\.href/);
});

test('the output subscription is dropped when the tab is not on screen', () => {
  assert.match(panel, /if \(!visible \|\| sessions\.length === 0\) return;/);
  assert.match(panel, /watch\?\.stop\(\)/);
  assert.doesNotMatch(panel, /setInterval|setTimeout/);
});

test('the panel never starts anything by itself', () => {
  // Every start is inside a handler the reader triggered: a click, or a
  // shortcut they saved. Nothing calls `run` or `startStack` at mount.
  assert.match(panel, /onRun=\{\(\) => void run\(row\.definition\)\}/, 'the row runs it');
  assert.match(panel, /await stopStack\(row\.definition\.id\);\s*await run\(/, 'restart runs it');
  assert.match(panel, /if \(!matched\) return;[\s\S]{0,80}void run\(matched\)/, 'a shortcut runs it');
  assert.doesNotMatch(panel, /onMount/);
});

test('the dialog offers every field the action needs, and says what is not wired yet', () => {
  for (const field of [
    'run-action-name',
    'run-action-keybinding',
    'run-action-command',
    'run-action-preview',
    'run-action-folder',
    'run-action-env',
    'run-action-on-worktree',
    'run-action-open-preview'
  ]) {
    assert.match(dialog, new RegExp(field), `the dialog has ${field}`);
  }
  assert.match(dialog, /takes effect once worktree\s+creation lands/);
});

console.log(`runActions: ${passed} passed`);
