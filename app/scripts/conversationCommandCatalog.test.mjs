import assert from 'node:assert/strict';
import {
  filterConversationCommandCatalog,
  isExcludedConversationCommand,
  mergeConversationCommandCatalog,
  slashName
} from '../src/lib/shell/conversation/conversationCommandCatalog.ts';

const catalog = mergeConversationCommandCatalog([
  { id: 'review', label: 'Review changes' },
  { id: '/model', label: 'Model picker' },
  { id: 'theme', label: 'Theme' },
  { id: 'status', label: 'Status' }
], [{ id: 'review', label: 'Duplicate skill' }, { id: 'mcp', label: 'MCP' }]);

assert.deepEqual(catalog.filter((entry) => entry.source === 'provider').map((entry) => entry.name), ['review', 'model', 'theme', 'status']);
assert.deepEqual(catalog.filter((entry) => entry.source === 'skill').map((entry) => entry.name), ['mcp']);
assert.ok(catalog.some((entry) => entry.source === 'assembly' && entry.name === 'terminal'), 'external sessions retain /terminal');
assert.ok(catalog.some((entry) => entry.source === 'assembly' && entry.name === 'conversation'), 'external sessions retain /conversation');
assert.equal(isExcludedConversationCommand('/permissions'), true);
assert.equal(isExcludedConversationCommand('/pet'), true);
assert.equal(isExcludedConversationCommand('/review'), false);
assert.equal(slashName(catalog.find((entry) => entry.name === 'review')), '/review');
assert.deepEqual(filterConversationCommandCatalog(catalog, 'worktree').map((entry) => entry.name), ['new-worktree']);

console.log('conversationCommandCatalog.test.mjs passed');
