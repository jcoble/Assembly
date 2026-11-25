/**
 * Focused pure-logic edge-case tests for the Run panel (`runOutputTail`, `keybindingCapture`,
 * env-var quoting, path boundary checks, and tolerant persistence parsing).
 *
 * Run via: `node --test web/scripts/run.test.ts`
 */

import assert from 'node:assert/strict';
import test from 'node:test';

import {
  formatKeybinding,
  isModifierOnly,
  matchKeybinding,
  normalizeKeybinding
} from '../src/lib/features/run/keybindingCapture.ts';
import {
  commandWithEnv,
  isInsideRoot,
  parseRunDefinitions,
  readEnvLines,
  serializeRunDefinitions
} from '../src/lib/features/run/model.ts';
import {
  RUN_OUTPUT_TAIL_CHARS,
  RUN_OUTPUT_TAIL_LINES,
  appendOutputTail
} from '../src/lib/features/run/runOutputTail.ts';
import type { RunDefinition } from '../src/lib/features/run/types.ts';

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

// ── runOutputTail (ANSI stripping, mid-line chunks, line + byte caps) ─────────

test('appendOutputTail strips OSC, CSI, and 2-byte escapes plus carriage returns across split chunks', () => {
  const first = appendOutputTail([], '\u001b]0;dev-server\u0007\u001b[32mready on ');
  assert.deepEqual(first, ['ready on ']);
  const second = appendOutputTail(first, 'port 5173\u001b[0m\r\nnext\u001bMline\n');
  assert.deepEqual(second, ['ready on port 5173', 'nextline', '']);
  assert.deepEqual(first, ['ready on '], 'input tail must remain immutable');
});

test('appendOutputTail caps large output by both line count and character ceiling', () => {
  const manyLines = Array.from({ length: RUN_OUTPUT_TAIL_LINES + 120 }, (_, i) => `line-${i}`).join(
    '\n'
  );
  const lineCapped = appendOutputTail([], manyLines);
  assert.equal(lineCapped.length, RUN_OUTPUT_TAIL_LINES);
  assert.equal(lineCapped.at(-1), `line-${RUN_OUTPUT_TAIL_LINES + 119}`);

  const hugeSingleLine = 'x'.repeat(RUN_OUTPUT_TAIL_CHARS + 4096);
  const charCapped = appendOutputTail([], hugeSingleLine);
  assert.equal(charCapped.join('\n').length, RUN_OUTPUT_TAIL_CHARS);
});

// ── keybindingCapture (modifier order, aliases, precedence) ───────────────────

test('formatKeybinding and normalizeKeybinding canonicalize modifier order, aliases, and Space', () => {
  assert.equal(
    formatKeybinding(keydown('r', { shift: true, alt: true, ctrl: true, meta: true })),
    'Cmd+Ctrl+Alt+Shift+R'
  );
  assert.equal(formatKeybinding(keydown(' ', { ctrl: true })), 'Ctrl+Space');
  assert.equal(formatKeybinding(keydown('Shift', { shift: true })), null);
  assert.equal(isModifierOnly(keydown('Meta', { meta: true })), true);
  assert.equal(normalizeKeybinding('shift + command + opt + r'), 'Cmd+Alt+Shift+R');
  assert.equal(normalizeKeybinding('Cmd+Shift'), null);
});

test('matchKeybinding resolves loose spellings and breaks ties by first definition', () => {
  const first: RunDefinition = {
    id: 'stack-1',
    name: 'First',
    script: 'pnpm dev',
    cwd: '/tmp/app',
    keybinding: 'shift + cmd + r'
  };
  const second: RunDefinition = {
    id: 'stack-2',
    name: 'Second',
    script: 'pnpm test',
    cwd: '/tmp/app',
    keybinding: 'Cmd+Shift+R'
  };
  assert.equal(matchKeybinding(keydown('r', { meta: true, shift: true }), [first, second])?.id, 'stack-1');
  assert.equal(matchKeybinding(keydown('r', { meta: true, shift: true }), [second, first])?.id, 'stack-2');
  assert.equal(matchKeybinding(keydown('k', { meta: true }), [first, second]), null);
});

// ── env quoting, tolerant persistence parsing, and root boundary matching ─────

test('readEnvLines and commandWithEnv handle comments, export, duplicates, unreadable lines, and POSIX quoting', () => {
  const parsed = readEnvLines(
    [
      '# comment',
      '',
      'export NODE_ENV="development"',
      "GREETING='hello world'",
      "QUOTE=it's fine",
      'NODE_ENV=production',
      '123INVALID=bad',
      'MISSING_EQUALS'
    ].join('\n')
  );
  assert.deepEqual(parsed.variables, [
    { key: 'NODE_ENV', value: 'production' },
    { key: 'GREETING', value: 'hello world' },
    { key: 'QUOTE', value: "it's fine" }
  ]);
  assert.deepEqual(parsed.unreadable, ['123INVALID=bad', 'MISSING_EQUALS']);

  assert.equal(
    commandWithEnv('pnpm dev', parsed.variables),
    `NODE_ENV='production' GREETING='hello world' QUOTE='it'\\''s fine' pnpm dev`
  );
});

test('parseRunDefinitions tolerates corrupt rows and preserves omission of unset optional fields', () => {
  const parsed = parseRunDefinitions([
    null,
    { id: 'bad-missing-cwd', name: 'Broken', script: 'echo 1' },
    { id: 'stack-1', name: 'Web', script: 'pnpm dev', cwd: '/repo', openPreviewOnRun: 'yes' },
    { id: 'stack-1', name: 'Duplicate', script: 'echo dup', cwd: '/repo' }
  ]);
  assert.deepEqual(parsed, [{ id: 'stack-1', name: 'Web', script: 'pnpm dev', cwd: '/repo' }]);
  assert.deepEqual(serializeRunDefinitions(parsed), [
    { id: 'stack-1', name: 'Web', script: 'pnpm dev', cwd: '/repo' }
  ]);
});

test('isInsideRoot enforces path-separator boundaries', () => {
  assert.equal(isInsideRoot('/a/app', '/a/app'), true);
  assert.equal(isInsideRoot('/a/app/web', '/a/app/'), true);
  assert.equal(isInsideRoot('/a/app-two', '/a/app'), false);
});
