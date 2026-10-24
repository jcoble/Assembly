import assert from 'node:assert/strict';
import {
  normalizeConversationFileHref,
  parseSafeMarkdown,
  sanitizeConversationHref,
  sanitizeConversationMarkdown,
  splitConversationFileReference
} from '../src/lib/shell/conversation/conversationMessageSafety.ts';

const source = '# Answer\n\nHere is **safe** [documentation](https://example.test/docs).\n\n```ts\nconst value = 1;';
const blocks = parseSafeMarkdown(source);
assert.equal(blocks[0].kind, 'heading');
assert.equal(blocks[1].kind, 'paragraph');
assert.equal(blocks[2].kind, 'code');
assert.equal(blocks[2].complete, false, 'partial fences remain code, not executable HTML');
assert.equal(sanitizeConversationHref('javascript:alert(1)'), null);
assert.equal(sanitizeConversationHref('https://example.test'), 'https://example.test');
assert.equal(sanitizeConversationHref('assembly://session/owned-1'), 'assembly://session/owned-1');
assert.equal(parseSafeMarkdown('Visit https://example.test/docs')[0].parts[1].kind, 'link');
assert.match(sanitizeConversationMarkdown('<script>alert(1)</script>'), /&lt;script&gt;/);
assert.doesNotMatch(sanitizeConversationMarkdown('<script>alert(1)</script>'), /<script>/);

const table = parseSafeMarkdown('| Name | State |\n| --- | --- |\n| A | done |');
assert.equal(table[0].kind, 'table');
assert.equal(table[0].rows.length, 1);
const tasks = parseSafeMarkdown('- [x] shipped\n- [ ] pending');
assert.equal(tasks[0].kind, 'list');
assert.equal(tasks[0].items[0].checked, true);
// The box says whether a task is done. The `[x]` the tokenizer read it from was
// also being kept, and landed under the row as a paragraph of its own.
assert.deepEqual(tasks[0].items[0].blocks, []);
assert.equal(tasks[0].items[0].parts.map((part) => part.value).join(''), 'shipped');
const fileLink = parseSafeMarkdown('[source](src/main.ts)');
assert.equal(fileLink[0].parts[0].kind, 'file-link');
assert.equal(normalizeConversationFileHref('file:///Users/me/main.ts'), '/Users/me/main.ts');
assert.equal(normalizeConversationFileHref('file://host/share/main.ts'), 'host/share/main.ts');
assert.equal(normalizeConversationFileHref('file:/Users/me/main.ts'), '/Users/me/main.ts');
assert.equal(parseSafeMarkdown('[source](file:///Users/me/main.ts)')[0].parts[0].path, '/Users/me/main.ts');
const prImage = parseSafeMarkdown('Before ![before screenshot](https://github.com/user-attachments/assets/example)');
assert.deepEqual(prImage[0].parts[1], {
  kind: 'image',
  href: 'https://github.com/user-attachments/assets/example',
  alt: 'before screenshot'
});
assert.notEqual(parseSafeMarkdown('![bad](javascript:alert(1))')[0].parts[0].kind, 'image');
assert.deepEqual(splitConversationFileReference('src/main.ts#L42'), { path: 'src/main.ts', line: 42 });
assert.deepEqual(splitConversationFileReference('src/main.ts#42'), { path: 'src/main.ts', line: 42 });
assert.deepEqual(splitConversationFileReference('src/main.ts:42'), { path: 'src/main.ts', line: 42 });
assert.deepEqual(splitConversationFileReference('src/main.ts'), { path: 'src/main.ts' });


// What the hand-written scanner could not read. Each of these arrived as raw
// punctuation in the transcript, and each is ordinary in what an agent writes.
const flat = (parts) => parts.map(function text(part) {
  return part.kind === 'text' || part.kind === 'code' ? part.value : flat(part.parts);
}).join('');

// A sub-list used to flatten into its parent, so every level looked like one.
const nested = parseSafeMarkdown('- outer\n  - inner one\n  - inner two\n- second');
assert.equal(nested[0].kind, 'list');
assert.equal(nested[0].items.length, 2, 'two rows at the top level, not four');
assert.equal(nested[0].items[0].blocks[0].kind, 'list');
assert.equal(nested[0].items[0].blocks[0].items.length, 2);

// Emphasis holding code broke into asterisks and a stray backtick.
const nestedInline = parseSafeMarkdown('**bold with `code` in it**');
assert.equal(nestedInline[0].parts[0].kind, 'strong');
assert.deepEqual(
  nestedInline[0].parts[0].parts.map((part) => part.kind),
  ['text', 'code', 'text']
);

// Emphasis that wrapped across a line was left as punctuation.
const wrapped = parseSafeMarkdown('a **mark that\nspans the wrap** b');
assert.ok(
  wrapped[0].parts.some((part) => part.kind === 'strong'),
  'emphasis across a wrapped line is still emphasis'
);

// A backslash escape printed the backslash.
const escaped = parseSafeMarkdown('\\*not emphasis\\*');
assert.equal(flat(escaped[0].parts), '*not emphasis*');

// Neither existed at all.
assert.equal(parseSafeMarkdown('~~gone~~')[0].parts[0].kind, 'strike');
assert.equal(parseSafeMarkdown('a\n\n---\n\nb')[1].kind, 'rule');

// Markup an agent typed is writing, never markup.
const typed = parseSafeMarkdown('before <script>alert(1)</script> after');
assert.equal(typed[0].kind, 'paragraph');
assert.match(flat(typed[0].parts), /<script>/, 'it reaches the reader as characters');
assert.ok(
  typed[0].parts.every((part) => part.kind === 'text' || part.kind === 'code'),
  'no part carries markup'
);

// A closed fence is complete; the one still being written is not.
assert.equal(parseSafeMarkdown('```ts\nconst a = 1;\n```')[0].complete, true);
assert.equal(parseSafeMarkdown('```ts\nconst a = 1;')[0].complete, false);

// A quote holds blocks, so a list or a fence inside one survives.
const quoted = parseSafeMarkdown('> a note\n>\n> - one\n> - two');
assert.equal(quoted[0].kind, 'quote');
assert.deepEqual(quoted[0].blocks.map((block) => block.kind), ['paragraph', 'list']);

console.log('conversationMessageSafety.test.ts passed');
