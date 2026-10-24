/**
 * Pins the rule the editor uses to decide whether a Markdown file opens as
 * rendered text or as raw source, and the Preview's file logic.
 *
 * Run: node --experimental-strip-types scripts/markdownPreview.test.ts
 */
import assert from 'node:assert/strict';
import { realpathSync } from 'node:fs';
import { createRequire } from 'node:module';

import {
  isMarkdownFile,
  markdownImageTarget,
  markdownPreviewDefault,
  frontmatterRows,
  splitFrontmatter,
  taskCheckboxOffsets,
  toggleTaskAt,
  toggleTaskCheckbox,
  withoutFetchingAttributes
} from '../src/lib/shell/files/components/editor/markdownPreview.ts';

// --- which files are Markdown ---------------------------------------------

assert.equal(isMarkdownFile('README.md'), true);
assert.equal(isMarkdownFile('notes.MD'), true);
assert.equal(isMarkdownFile('CHANGELOG.markdown'), true);
assert.equal(isMarkdownFile('plan.mdx'), true);
assert.equal(isMarkdownFile('main.rs'), false);
assert.equal(isMarkdownFile('mdfile'), false, 'the extension has to be an extension');
assert.equal(isMarkdownFile(''), false);
assert.equal(isMarkdownFile(null), false);
assert.equal(isMarkdownFile(undefined), false);
assert.equal(isMarkdownFile('  README.md  '), true, 'surrounding space is ignored');

// --- what a newly opened file shows first ----------------------------------

assert.equal(
  markdownPreviewDefault('README.md', 'jump'),
  'rendered',
  'a Markdown file reached from a diff or a file jump opens rendered'
);
assert.equal(
  markdownPreviewDefault('README.md', 'strip'),
  'raw',
  'picking the file in the open-files strip keeps the editor on source'
);
assert.equal(
  markdownPreviewDefault('main.rs', 'jump'),
  'raw',
  'anything that is not Markdown has no rendered form here'
);
assert.equal(markdownPreviewDefault('main.rs', 'strip'), 'raw');

assert.equal(
  markdownPreviewDefault('README.md', 'jump'),
  markdownPreviewDefault('README.md', 'jump'),
  'the rule is pure: same input, same answer'
);

// --- front matter is shown apart from the document ----------------------------

{
  const file = '---\ntitle: Notes\ntags: [a, b]\n---\n\n# Notes\n\nBody.\n';
  const { frontmatter, body } = splitFrontmatter(file);
  assert.equal(frontmatter, '---\ntitle: Notes\ntags: [a, b]\n---\n\n', 'the block and the blank lines after it');
  assert.equal(body, '# Notes\n\nBody.\n');
  assert.equal(frontmatter + body, file, 'splitting never loses a byte');
}
assert.deepEqual(
  splitFrontmatter('---\r\ntitle: x\r\n...\r\nBody\r\n'),
  { frontmatter: '---\r\ntitle: x\r\n...\r\n', body: 'Body\r\n' },
  'CRLF files and a "..." closing line are front matter too'
);
assert.deepEqual(
  splitFrontmatter('# Title\n\n---\ntitle: x\n---\n'),
  { frontmatter: '', body: '# Title\n\n---\ntitle: x\n---\n' },
  'only a block at the very start of the file counts'
);
assert.deepEqual(
  splitFrontmatter('---\ntitle: never closed\n\nBody\n'),
  { frontmatter: '', body: '---\ntitle: never closed\n\nBody\n' },
  'an opening line with no closing line is ordinary Markdown'
);
assert.deepEqual(splitFrontmatter(''), { frontmatter: '', body: '' });

// --- front matter shows as a small table -------------------------------------

assert.deepEqual(
  frontmatterRows('---\ntitle: Notes\ntags: [a, b]\ndate: 2026-10-07\n---\n\n'),
  [['title', 'Notes'], ['tags', '[a, b]'], ['date', '2026-10-07']],
  'one row per top-level key, the value as written'
);
assert.deepEqual(
  frontmatterRows('---\r\nname: x\r\nauthors:\r\n  - Ann\r\n  - Bo\r\n# a comment\r\nurl: "https://a.b/c: d"\r\n...\r\n'),
  [['name', 'x'], ['authors', '- Ann\n- Bo'], ['url', '"https://a.b/c: d"']],
  'nested lines belong to the key above them; comments are skipped; the first colon splits'
);
assert.deepEqual(frontmatterRows(''), [], 'no front matter, no rows');

// --- task checkboxes map to one character in the file -------------------------

// The offsets come from the same tokens the Preview renders, so a line that
// only looks like a task (indented code, a fence, inline code) never gets one.
const lexer = createRequire(realpathSync(new URL('../node_modules/@humanspeak/svelte-markdown/package.json', import.meta.url)))('marked') as typeof import('marked');
const lex = (source: string) => new lexer.Lexer({ gfm: true }).lex(source);
const marksAt = (source: string) => {
  const offsets = taskCheckboxOffsets(source, lex(source));
  return offsets.map((offset) => source.slice(offset - 1, offset + 2));
};
const lineOf = (source: string, offset: number) =>
  source.slice(source.lastIndexOf('\n', offset) + 1).split(/\r?\n/)[0];

{
  const file = '    - [ ] code\n- [ ]\treal\n- [ ] task\n';
  const offsets = taskCheckboxOffsets(file, lex(file));
  assert.deepEqual(offsets.map((offset) => lineOf(file, offset)), ['- [ ] task'],
    'indented code is code, and a tab after "]" is not a task to the renderer either');
}
{
  const file = [
    '- [ ] one',
    '- [x] two',
    '  - [X] nested',
    '',
    '1. [ ] ordered',
    '2) [x] ordered paren',
    '',
    '> - [ ] quoted',
    '>   - [x] quoted nested',
    '',
    '```md',
    '- [ ] inside a fence',
    '```',
    '',
    '- outer',
    '',
    '  ```',
    '  - [ ] fenced inside an item',
    '  ```',
    '- [ ] after the item fence',
    '',
    'Inline `- [ ] code` and `[x]` are text.',
    '',
    '- [y] not a box',
    '- [ ] last'
  ].join('\n');
  const offsets = taskCheckboxOffsets(file, lex(file));
  assert.deepEqual(offsets.map((offset) => lineOf(file, offset)), [
    '- [ ] one', '- [x] two', '  - [X] nested', '1. [ ] ordered', '2) [x] ordered paren',
    '> - [ ] quoted', '>   - [x] quoted nested', '- [ ] after the item fence', '- [ ] last'
  ], 'every rendered task in document order; fences, inline code and non-boxes are skipped');
  assert.deepEqual(marksAt(file), ['[ ]', '[x]', '[X]', '[ ]', '[x]', '[ ]', '[x]', '[ ]', '[ ]']);
}
{
  const crlf = '- [ ] a\r\n- [x] b\r\n\r\n> - [ ] c\r\n';
  assert.deepEqual(taskCheckboxOffsets(crlf, lex(crlf)), [3, 12, 25], 'CRLF line endings keep exact offsets');
}
{
  const file = 'Text with <kbd>Cmd</kbd>.\n\n- [ ] <kbd>S</kbd> inline HTML\n\n<div>\n- [ ] inside an HTML block\n</div>\n\n- [x] after\n';
  assert.deepEqual(taskCheckboxOffsets(file, lex(file)).map((offset) => lineOf(file, offset)),
    ['- [ ] <kbd>S</kbd> inline HTML', '- [x] after'], 'inline HTML is stepped past; an HTML block hides its lines');
}
{
  const file = '- [ ] same\n- [ ] same\n';
  assert.deepEqual(taskCheckboxOffsets(file, lex(file)), [3, 14], 'identical lines each get their own offset');
}
{
  const file = '- [ ] a\n- [ ] b\n';
  assert.deepEqual(taskCheckboxOffsets('- [ ] a\n', lex(file)), [3],
    'tokens from other text stop at the first task they cannot place');
}

{
  const file = '# Plan\n\n- [ ] write\n- [x] test\n\nEnd.\n';
  const [first, second] = taskCheckboxOffsets(file, lex(file));
  const checkedFirst = toggleTaskCheckbox(file, first);
  const changed = [...file].filter((char, index) => checkedFirst[index] !== char);
  assert.equal(checkedFirst.length, file.length, 'the file keeps its length');
  assert.deepEqual(changed, [' '], 'exactly one character changes');
  assert.equal(checkedFirst, '# Plan\n\n- [x] write\n- [x] test\n\nEnd.\n');
  assert.equal(toggleTaskCheckbox(file, second), '# Plan\n\n- [ ] write\n- [ ] test\n\nEnd.\n', 'x clears');
  assert.equal(toggleTaskCheckbox(toggleTaskCheckbox(file, first), first), file, 'two clicks give back the file byte for byte');
  assert.equal(toggleTaskCheckbox('- [X] a', 3), '- [ ] a', 'a capital X clears too');
  assert.throws(() => toggleTaskCheckbox(file, 0), /checkbox/, 'an offset that is not inside [ ] is refused');
}

// --- a click on a rendered box ----------------------------------------------

{
  const file = '- [ ] one\n- [x] two\n';
  const offsets = [3, 13];
  assert.equal(toggleTaskAt(file, offsets, 0, 2, true), '- [x] one\n- [x] two\n', 'ticking the first box writes its x');
  assert.equal(toggleTaskAt(file, offsets, 1, 2, false), '- [ ] one\n- [ ] two\n', 'clearing the second box writes its space');
  assert.equal(toggleTaskAt(file, offsets, 0, 3, true), null, 'more boxes on screen than tasks placed: refused');
  assert.equal(toggleTaskAt(file, offsets, 2, 2, true), null, 'a box past the last one: refused');
  assert.equal(toggleTaskAt(file, offsets, 1, 2, true), null, 'the file already holds the state the box now shows: refused');
}

// --- where an image in a Markdown file comes from ------------------------------

const root = '/Users/me/project';
const doc = '/Users/me/project/docs/guide.md';
assert.deepEqual(markdownImageTarget(doc, root, 'img/shot.png'), { kind: 'file', path: '/Users/me/project/docs/img/shot.png' });
assert.deepEqual(markdownImageTarget(doc, root, './img/shot.png'), { kind: 'file', path: '/Users/me/project/docs/img/shot.png' });
assert.deepEqual(markdownImageTarget(doc, root, '../assets/logo.svg'), { kind: 'file', path: '/Users/me/project/assets/logo.svg' });
assert.deepEqual(markdownImageTarget(doc, `${root}/`, '../assets/logo.svg'), { kind: 'file', path: '/Users/me/project/assets/logo.svg' }, 'a trailing slash on the root is fine');
assert.deepEqual(markdownImageTarget(doc, root, 'my%20shot.png?raw=1#top'), { kind: 'file', path: '/Users/me/project/docs/my shot.png' }, 'escapes decode; query and hash drop');
assert.equal(markdownImageTarget(doc, root, '/tmp/abs.png'), null, 'an absolute path is never read');
assert.equal(markdownImageTarget(doc, root, '/Users/me/project/docs/img/shot.png'), null, 'not even one inside the project');
assert.equal(markdownImageTarget(doc, root, '../../secret.png'), null, '".." cannot climb out of the project');
assert.equal(markdownImageTarget(doc, root, '../../project-other/x.png'), null, 'a sibling folder sharing the name prefix is outside');
assert.equal(markdownImageTarget(doc, root, '%2E%2E/%2E%2E/secret.png'), null, 'escaped ".." cannot climb out either');
assert.equal(markdownImageTarget('/elsewhere/notes.md', root, 'shot.png'), null, 'a file outside the project reads no local images');
assert.equal(markdownImageTarget(doc, null, 'img/shot.png'), null, 'no project, no local images');
assert.deepEqual(markdownImageTarget(doc, root, 'https://example.com/a.png'), { kind: 'https', url: 'https://example.com/a.png' });
assert.deepEqual(markdownImageTarget(doc, root, 'HTTP://example.com/a.png'), { kind: 'http', url: 'HTTP://example.com/a.png' });
{
  const remote = (path: string, profile = 'machine-a') => `assembly-remote://${profile}${path}`;
  const remoteDoc = remote('/home/me/project/docs/guide.md');
  const remoteRoot = remote('/home/me/project');
  assert.deepEqual(markdownImageTarget(remoteDoc, remoteRoot, 'img/shot.png'), { kind: 'file', path: remote('/home/me/project/docs/img/shot.png') }, 'a remote project reads its images from the same machine');
  assert.equal(markdownImageTarget(remoteDoc, remoteRoot, '../../x.png'), null, 'a remote image outside the project is refused');
  assert.equal(markdownImageTarget(remoteDoc, remote('/home/me/project', 'other'), 'img/shot.png'), null, 'a project on another machine is not this file\'s project');
  assert.equal(markdownImageTarget(remoteDoc, root, 'img/shot.png'), null, 'a Mac project does not own a remote file');
}
assert.equal(markdownImageTarget(doc, root, ''), null);
assert.equal(markdownImageTarget(doc, root, undefined), null, 'the sanitizer removed an unsafe URL');
assert.equal(markdownImageTarget(doc, root, 'data:image/png;base64,AAAA'), null);
assert.equal(markdownImageTarget(doc, root, '#anchor'), null);

// --- raw HTML attributes that would make the Preview fetch an address ----------

assert.deepEqual(
  withoutFetchingAttributes({ src: 'a.png', alt: 'logo', width: '40', align: 'right', srcset: 'http://127.0.0.1:7777/x 2x' }),
  { src: 'a.png', alt: 'logo', width: '40', align: 'right' },
  'an image keeps its size and alignment but not srcset'
);
assert.deepEqual(
  withoutFetchingAttributes({ class: 'note', STYLE: 'background:url(http://192.168.1.1/)' }),
  { class: 'note' },
  'style goes from any tag, whatever its case'
);
assert.deepEqual(withoutFetchingAttributes({ colspan: '2', Background: 'http://10.0.0.1/x.png' }), { colspan: '2' }, 'a table cell loses background');

console.log('markdownPreview: ok');
