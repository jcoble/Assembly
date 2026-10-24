/**
 * codeHighlight.test.ts — the transcript's colouring, end to end and pure.
 *
 * Two things are worth pinning down. Which language a fence or a filename
 * means, and that the scanner separates strings, comments, numbers and
 * keywords from body text without ever losing a line of the code it was given.
 *
 * Run: node --experimental-strip-types scripts/codeHighlight.test.ts
 */
import assert from 'node:assert/strict';
import {
  cachedHighlight,
  fenceLanguage,
  highlightCode,
  languageForPath,
  plainHighlightedLines,
  type CodeTokenClass,
  type HighlightedLine
} from '../src/lib/shell/markdown/codeHighlight.ts';

/** The classes a snippet's text ends up painted with, in source order. */
const classesIn = (lines: HighlightedLine[]): CodeTokenClass[] =>
  lines.flat().map((span) => span.className);

/** The text of every span whose class is `wanted`. */
const textOf = (lines: HighlightedLine[], wanted: CodeTokenClass): string[] =>
  lines
    .flat()
    .filter((span) => span.className === wanted)
    .map((span) => span.value);

/** Colouring must never change the code — only how it is painted. */
function assertKeepsSource(source: string, language: string): void {
  const lines = highlightCode(source, language);
  const rebuilt = lines.map((line) => line.map((span) => span.value).join('')).join('\n');
  assert.equal(rebuilt, source, `${language}: colouring changed the code`);
  assert.equal(
    lines.length,
    source.split('\n').length,
    `${language}: line count changed`
  );
}

// Common fence spellings reach the language ids the scanner knows.
assert.equal(fenceLanguage('ts'), 'typescript');
assert.equal(fenceLanguage('TSX'), 'typescript');
assert.equal(fenceLanguage('js'), 'javascript');
assert.equal(fenceLanguage('py'), 'python');
assert.equal(fenceLanguage('bash'), 'shell');
assert.equal(fenceLanguage('sh'), 'shell');
assert.equal(fenceLanguage('rs'), 'rust');
assert.equal(fenceLanguage('cs'), 'csharp');
assert.equal(fenceLanguage('yml'), 'yaml');
assert.equal(fenceLanguage('json'), 'json');
assert.equal(fenceLanguage('rust'), 'rust');

// A fence can carry more than the language ("ts title=foo"); the first word wins.
assert.equal(fenceLanguage('ts title=example.ts'), 'typescript');

// Anything unknown or missing falls back to plain text rather than guessing.
assert.equal(fenceLanguage(''), 'plaintext');
assert.equal(fenceLanguage('   '), 'plaintext');
assert.equal(fenceLanguage('not-a-language'), 'plaintext');

// A file's language comes from its extension, with Dockerfile named outright.
assert.equal(languageForPath('src/app/main.ts'), 'typescript');
assert.equal(languageForPath('Dockerfile'), 'dockerfile');
assert.equal(languageForPath('README'), 'plaintext');
assert.equal(languageForPath(null), 'plaintext');

// The fallback rendering keeps every line, uncoloured, so the code still shows.
assert.deepEqual(plainHighlightedLines('a\nb'), [
  [{ value: 'a', className: 'plain' }],
  [{ value: 'b', className: 'plain' }]
]);
assert.deepEqual(plainHighlightedLines(''), [[{ value: '', className: 'plain' }]]);

// A language with no grammar is left alone entirely.
assert.deepEqual(highlightCode('anything at all', 'plaintext'), [
  [{ value: 'anything at all', className: 'plain' }]
]);

// Keywords, strings, numbers and comments separate from the body text.
{
  const lines = highlightCode('const total = 42; // done\nreturn "ok";', 'typescript');
  assert.deepEqual(textOf(lines, 'keyword'), ['const', 'return']);
  assert.deepEqual(textOf(lines, 'number'), ['42']);
  assert.deepEqual(textOf(lines, 'comment'), ['// done']);
  assert.deepEqual(textOf(lines, 'string'), ['"ok"']);
  assert.equal(lines.length, 2);
}

// A comment ends at its line, and never swallows the code beneath it.
{
  const lines = highlightCode('# note\nls -la', 'shell');
  assert.deepEqual(textOf(lines, 'comment'), ['# note']);
  assert.equal(lines[1].map((span) => span.value).join(''), 'ls -la');
}

// A block comment spans lines and closes where it says it does.
{
  const lines = highlightCode('/* one\n   two */ x', 'javascript');
  assert.deepEqual(textOf(lines, 'comment'), ['/* one', '   two */']);
}

// An unterminated quote stops at the newline rather than eating the snippet.
{
  const lines = highlightCode("print('oops\nvalue = 1", 'python');
  assert.deepEqual(textOf(lines, 'number'), ['1']);
}

// A Python docstring is one string across several lines.
{
  const lines = highlightCode('"""doc\nstring"""\nx = 2', 'python');
  assert.deepEqual(textOf(lines, 'string'), ['"""doc', 'string"""']);
  assert.deepEqual(textOf(lines, 'number'), ['2']);
}

// Markup tag names are painted, and their attribute values read as strings.
{
  const lines = highlightCode('<a href="/x">hi</a>', 'html');
  assert.deepEqual(textOf(lines, 'type'), ['<a', '</a']);
  assert.deepEqual(textOf(lines, 'string'), ['"/x"']);
}

// An at-rule is a keyword in the stylesheet languages.
assert.deepEqual(textOf(highlightCode('@media print { a { color: red } }', 'css'), 'keyword'), [
  '@media'
]);

// A word that is nothing in particular stays body text.
assert.deepEqual(classesIn(highlightCode('somethingOrdinary', 'typescript')), ['plain']);

// Whatever the language, the code itself comes back untouched.
for (const [source, language] of [
  ['fn main() { println!("hi {}", 1_000); }', 'rust'],
  ['SELECT id FROM t WHERE x = 1 -- note', 'sql'],
  ['{ "a": [1, 2, null] }', 'json'],
  ['key: value # trailing', 'yaml'],
  ['def f(x):\n    return x * 2', 'python'],
  ['', 'typescript'],
  ['\n\n', 'typescript'],
  ['no trailing newline', 'shell']
] as const) {
  assertKeepsSource(source, language);
}

// highlighted_code_is_reused_and_the_cache_stays_capped
{
  // A block coloured once comes back as the same lines, so a remount draws it coloured at once.
  const source = 'const cached = 1; // first block';
  assert.equal(cachedHighlight(source, 'typescript'), undefined);
  const first = highlightCode(source, 'typescript');
  assert.equal(cachedHighlight(source, 'typescript'), first);
  assert.equal(highlightCode(source, 'typescript'), first);
  // The same code in another language is coloured on its own.
  assert.equal(cachedHighlight(source, 'rust'), undefined);
  // Only the latest 300 blocks are kept; the oldest go first.
  for (let index = 0; index < 300; index += 1) highlightCode(`let filler = ${index};`, 'typescript');
  assert.equal(cachedHighlight(source, 'typescript'), undefined);
  assert.ok(cachedHighlight('let filler = 0;', 'typescript'));
  assert.ok(cachedHighlight('let filler = 299;', 'typescript'));
  // A block that streams in is coloured once per version; together the kept versions stay within
  // 100,000 characters, so a long streamed block cannot pin hundreds of large colourings.
  const streamed = 'let streamed = 1;\n'.repeat(2000);
  for (let length = 4_000; length <= streamed.length; length += 4_000) highlightCode(streamed.slice(0, length), 'typescript');
  assert.ok(cachedHighlight(streamed, 'typescript'));
  assert.equal(cachedHighlight(streamed.slice(0, 4_000), 'typescript'), undefined);
  assert.equal(cachedHighlight('let filler = 299;', 'typescript'), undefined);
  // Plain blocks are not kept: they cost nothing to draw again.
  highlightCode('plain words', 'plaintext');
  assert.equal(cachedHighlight('plain words', 'plaintext'), undefined);
}

console.log('codeHighlight.test.ts passed');
