import assert from 'node:assert/strict';
import {
  cleanupPasteReplyDraft,
  cleanupPasteText,
  createPasteCleanupHistoryItem,
  formatPasteCleanupStats,
  pasteCleanupModes,
  summarizePasteCleanupHistoryText
} from '../src/lib/pasteCleanup.ts';

const messy = '  First line  \r\n\r\n\r\n\tSecond   line  \n\n\n';

assert.deepEqual(pasteCleanupModes, ['plain', 'compact', 'prompt', 'reply']);
assert.equal(cleanupPasteText(messy, 'plain'), 'First line\n\nSecond   line');
assert.equal(cleanupPasteText(messy, 'compact'), 'First line Second line');
assert.equal(
  cleanupPasteText('```txt\n  Here is the answer.  \n\n\nThanks.  \n```', 'prompt'),
  'Here is the answer.\n\nThanks.'
);
assert.equal(
  cleanupPasteText(
    '> Here is the answer.  \r\n> \r\nCopy code\n```ts\nconst x = 1;\n```\n\n\nThanks.  ',
    'reply'
  ),
  'Here is the answer.\n\n```ts\nconst x = 1;\n```\n\nThanks.'
);
assert.equal(cleanupPasteText('A\u00a0B\r\nC', 'plain'), 'A B\nC');
assert.equal(cleanupPasteReplyDraft('  I can do that.\r\n\r\n\r\n\tNext step.  '), 'I can do that.\n\nNext step.');
assert.equal(formatPasteCleanupStats('', ''), '0 chars');
assert.equal(formatPasteCleanupStats('  abc  ', 'abc'), '3 chars - trimmed 4');
assert.equal(
  summarizePasteCleanupHistoryText('  First line\r\n\r\nSecond line with more detail  ', 24),
  'First line Second lin...'
);

const historyItem = createPasteCleanupHistoryItem('cleaned', '  A cleaned reply.\r\n\r\n ', 'reply', 123);
assert.equal(historyItem.kind, 'cleaned');
assert.equal(historyItem.mode, 'reply');
assert.equal(historyItem.text, 'A cleaned reply.');
assert.equal(historyItem.summary, 'A cleaned reply.');
assert.equal(historyItem.charCount, 16);
assert.ok(historyItem.id.startsWith('cleaned-123-'));
