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
  normalizePreviewUrl,
  orderShortcutsForRoot,
  parseRunDefinitions,
  readEnvLines,
  releaseSupersededTerminals,
  serializeRunDefinitions
} from '../src/lib/features/run/model.ts';
import {
  RUN_OUTPUT_TAIL_CHARS,
  RUN_OUTPUT_TAIL_LINES,
  appendOutputTail
} from '../src/lib/features/run/runOutputTail.ts';
import type { RunDefinition, RunRecord, TerminalInfo } from '../src/lib/features/run/types.ts';

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

// ── Regression tests for Codex review fixes ──────────────────────────────────

test('releaseSupersededTerminals closes prior exited terminals and prunes cache without touching other actions', async () => {
  const runs: Record<string, RunRecord> = {
    'term-exited': {
      ownedId: 'term-exited',
      stackId: 'stack-build',
      startedAt: 1000,
      exited: true,
      exitCode: 0,
      signal: null
    },
    'term-other': {
      ownedId: 'term-other',
      stackId: 'stack-api',
      startedAt: 2000,
      exited: false,
      exitCode: null,
      signal: null
    }
  };
  const terminals: Record<string, TerminalInfo> = {
    'term-exited': {
      id: 'term-exited',
      cwd: '/repo',
      shell: '/bin/bash',
      command: 'pnpm build',
      cols: 96,
      rows: 28,
      pid: 101,
      startedAt: '2026-11-25T00:00:00Z',
      exitCode: 0
    },
    'term-other': {
      id: 'term-other',
      cwd: '/repo',
      shell: '/bin/bash',
      command: 'pnpm dev',
      cols: 96,
      rows: 28,
      pid: 202,
      startedAt: '2026-11-25T00:00:01Z',
      exitCode: null
    }
  };

  const closed: string[] = [];
  const result = await releaseSupersededTerminals(runs, terminals, 'stack-build', async (id) => {
    closed.push(id);
  });

  assert.deepEqual(closed, ['term-exited']);
  assert.deepEqual(result.closedIds, ['term-exited']);
  assert.deepEqual(Object.keys(result.remainingRuns), ['term-other']);
  assert.deepEqual(Object.keys(result.remainingTerminals), ['term-other']);
});

test('releaseSupersededTerminals tolerates already-closed backend errors and still cleans remaining state', async () => {
  const runs: Record<string, RunRecord> = {
    'term-gone': {
      ownedId: 'term-gone',
      stackId: 'stack-build',
      startedAt: 1000,
      exited: true,
      exitCode: 1,
      signal: null
    }
  };
  const terminals: Record<string, TerminalInfo> = {
    'term-gone': {
      id: 'term-gone',
      cwd: '/repo',
      shell: '/bin/bash',
      cols: 96,
      rows: 28,
      pid: 303,
      startedAt: '2026-11-25T00:00:00Z',
      exitCode: 1
    }
  };

  const result = await releaseSupersededTerminals(runs, terminals, 'stack-build', async () => {
    throw new Error('terminal_missing');
  });

  assert.deepEqual(result.closedIds, ['term-gone']);
  assert.deepEqual(result.remainingRuns, {});
  assert.deepEqual(result.remainingTerminals, {});
});

test('orderShortcutsForRoot prioritizes actions inside the active project root', () => {
  const other: RunDefinition = {
    id: 'stack-other',
    name: 'Other',
    script: 'echo other',
    cwd: '/work/other-app',
    keybinding: 'Ctrl+Shift+R'
  };
  const current: RunDefinition = {
    id: 'stack-current',
    name: 'Current',
    script: 'echo current',
    cwd: '/work/app/web',
    keybinding: 'Ctrl+Shift+R'
  };
  const ordered = orderShortcutsForRoot([other, current], '/work/app');
  assert.equal(ordered[0]?.id, 'stack-current');
  assert.equal(ordered[1]?.id, 'stack-other');
  assert.equal(matchKeybinding(keydown('r', { ctrl: true, shift: true }), ordered)?.id, 'stack-current');
});

test('normalizePreviewUrl allows only valid http(s) URLs', () => {
  assert.equal(normalizePreviewUrl('  http://localhost:5173/app  '), 'http://localhost:5173/app');
  assert.equal(normalizePreviewUrl('https://example.com'), 'https://example.com/');
  assert.equal(normalizePreviewUrl('javascript:alert(1)'), null);
  assert.equal(normalizePreviewUrl('file:///etc/passwd'), null);
  assert.equal(normalizePreviewUrl('not a url'), null);
});
