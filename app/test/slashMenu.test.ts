import assert from 'node:assert/strict';
import { test } from 'node:test';
import { slashMatches, slashPick, slashQuery, type SlashCommand } from '../src/lib/composer/slashMenu.ts';

const commands: SlashCommand[] = [
  { name: 'compact', description: 'Compact the conversation' },
  { name: 'context', description: 'Show context usage' },
  { name: 'autocompact', description: 'Toggle auto-compact' },
  { name: '__remote-workflow', description: 'Internal' },
  { name: 'review', description: 'Review a change', input: { hint: '<branch or PR>' } },
  { name: '$imagegen', description: 'Generate images' },
];

test('the menu belongs to a leading command word only', () => {
  assert.equal(slashQuery('/'), '');
  assert.equal(slashQuery('/con'), 'con');
  assert.equal(slashQuery('/context now'), null);
  assert.equal(slashQuery(' /context'), null);
  assert.equal(slashQuery('hi /context'), null);
});

test('start-of-name matches come first, then anywhere; "__" names are hidden', () => {
  assert.deepEqual(slashMatches(commands, 'comp').map((c) => c.name), ['compact', 'autocompact']);
  assert.deepEqual(slashMatches(commands, '').map((c) => c.name), ['compact', 'context', 'autocompact', 'review', '$imagegen']);
  assert.deepEqual(slashMatches(commands, 'REM').map((c) => c.name), []);
});

test('a pick inserts the name and selects its input hint', () => {
  assert.deepEqual(slashPick(commands[1]!), { draft: '/context ', select: [9, 9] });
  assert.deepEqual(slashPick(commands[4]!), { draft: '/review <branch or PR>', select: [8, 22] });
});

test('Codex skills go by their "$" names: typed with "$" or found after "/", picked with no slash', () => {
  assert.equal(slashQuery('$ima'), '$ima');
  assert.deepEqual(slashMatches(commands, '$ima').map((c) => c.name), ['$imagegen']);
  assert.deepEqual(slashMatches(commands, 'image').map((c) => c.name), ['$imagegen']);
  assert.deepEqual(slashPick(commands[5]!), { draft: '$imagegen ', select: [10, 10] });
});
