import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { commandOf, foldOutput } from '../src/lib/chat/tools/tool.ts';

test('short output is shown whole', () => {
  assert.deepEqual(foldOutput('a\nb\nc', 200), { hidden: 0, text: 'a\nb\nc' });
});

test('long output keeps only the tail lines and counts the folded rest', () => {
  const lines = Array.from({ length: 5000 }, (_, i) => `line ${i + 1}`);
  const fold = foldOutput(lines.join('\n'), 200);
  assert.equal(fold.hidden, 4800);
  const shown = fold.text.split('\n');
  assert.equal(shown.length, 200);
  assert.equal(shown[0], 'line 4801');
  assert.equal(shown.at(-1), 'line 5000');
});

test('a trailing newline does not count as a line', () => {
  assert.deepEqual(foldOutput('a\nb\n', 1), { hidden: 1, text: 'b' });
});

test("an opened Bash call shows its command from the detail's input", () => {
  const { details } = JSON.parse(readFileSync(new URL('fixtures/rows-tools.json', import.meta.url), 'utf8'));
  assert.equal(commandOf('Bash', details.tool1.input), 'ls -la');
  assert.equal(commandOf('Read', details.tool2.input), null);
  assert.equal(commandOf('Bash', null), null);
  assert.equal(commandOf('Bash', '{"command":'), null);
});
