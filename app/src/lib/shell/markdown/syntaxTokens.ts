/**
 * syntaxTokens.ts — the transcript's own syntax colouring.
 *
 * The transcript used to borrow the code editor's tokenizer, on the reasoning
 * that the app already shipped one. That coupling was a mistake and it cost a
 * day: colouring a fenced block registered Monaco's Monarch grammars into the
 * editor's GLOBAL language registry, which replaced the TextMate grammars the
 * real editors were using, whose tokenizer then threw on every token because
 * nothing sets a standalone theme when the VS Code services are running. One
 * code block in one conversation stripped the colour out of every editor in the
 * app until it was reloaded.
 *
 * So this file shares nothing with the editor and can reach nothing outside
 * itself. It is a plain scanner over a string, and its worst failure is a
 * word painted the wrong colour.
 *
 * Deliberately coarse. The transcript paints six classes (see CodeTokenClass),
 * the snippets are short, and they are read at a glance — a full grammar per
 * language would buy accuracy nobody is looking closely enough to see, at a
 * cost the app charges on every block that scrolls into view. What matters is
 * that strings, comments and keywords separate from the body text.
 *
 * PURE and synchronous: no DOM, no imports, no I/O.
 */

/** The small set of colours the transcript paints code with. */
export type CodeTokenClass = 'keyword' | 'string' | 'comment' | 'number' | 'type' | 'plain';

/** One run of characters that share a colour. */
export interface HighlightedSpan {
  value: string;
  className: CodeTokenClass;
}

export type HighlightedLine = HighlightedSpan[];

/**
 * How a language is scanned. The five shapes below cover every fence the
 * transcript accepts; a language is a shape plus its own words.
 */
interface Grammar {
  /** Everything from this run of characters to the end of the line. */
  lineComments: string[];
  /** Opening and closing run for a comment that spans lines. */
  blockComments: readonly (readonly [string, string])[];
  /** Quote characters that open a string. Closed by the same character. */
  quotes: string[];
  /** Three-character openers (Python's docstrings), matched before `quotes`. */
  longQuotes: string[];
  /** Words painted as keywords. */
  keywords: ReadonlySet<string>;
  /** Words painted as types. */
  types: ReadonlySet<string>;
  /** Paint any Capitalised word as a type. True for the curly-brace family. */
  capitalsAreTypes: boolean;
  /** Paint `@rule` and `#[attribute]` style words as keywords. */
  atWords: boolean;
  /** Paint `<tag` and `</tag` names as types. */
  markupTags: boolean;
}

const words = (list: string): ReadonlySet<string> => new Set(list.split(/\s+/).filter(Boolean));

const NO_WORDS: ReadonlySet<string> = new Set();

function grammar(shape: Partial<Grammar>): Grammar {
  return {
    lineComments: [],
    blockComments: [],
    quotes: ['"', "'"],
    longQuotes: [],
    keywords: NO_WORDS,
    types: NO_WORDS,
    capitalsAreTypes: false,
    atWords: false,
    markupTags: false,
    ...shape
  };
}

/** The curly-brace family: `//` and `/* *​/` comments, capitals read as types. */
const curly = (keywords: string, types = ''): Grammar =>
  grammar({
    lineComments: ['//'],
    blockComments: [['/*', '*/']],
    quotes: ['"', "'", '`'],
    keywords: words(keywords),
    types: words(types),
    capitalsAreTypes: true
  });

/** The `#`-comment family: shells, Python, Ruby, and the config formats. */
const hash = (keywords: string, types = ''): Grammar =>
  grammar({
    lineComments: ['#'],
    keywords: words(keywords),
    types: words(types)
  });

const GRAMMARS: Record<string, Grammar> = {
  javascript: curly(
    `as async await break case catch class const continue debugger default delete do else export
     extends false finally for from function get if import in instanceof let new null of return set
     static super switch this throw true try typeof undefined var void while yield`
  ),
  typescript: curly(
    `abstract as async await break case catch class const continue debugger declare default delete
     do else enum export extends false finally for from function get if implements import in infer
     instanceof interface keyof let namespace new null of private protected public readonly return
     satisfies set static super switch this throw true try type typeof undefined var void while yield`,
    `any bigint boolean never number object string symbol unknown`
  ),
  rust: curly(
    `as async await break const continue crate dyn else enum extern false fn for if impl in let loop
     match mod move mut pub ref return self Self static struct super trait true type unsafe use where
     while`,
    `bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize`
  ),
  go: curly(
    `break case chan const continue default defer else fallthrough false for func go goto if import
     interface map nil package range return select struct switch true type var`,
    `bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint
     uint8 uint16 uint32 uint64 uintptr`
  ),
  java: curly(
    `abstract assert break case catch class const continue default do else enum extends final finally
     for goto if implements import instanceof interface native new null package private protected
     public return static strictfp super switch synchronized this throw throws transient true false
     try void volatile while`,
    `boolean byte char double float int long short`
  ),
  csharp: curly(
    `abstract as async await base break case catch checked class const continue default delegate do
     else enum event explicit extern false finally fixed for foreach get goto if implicit in interface
     internal is lock namespace new null operator out override params private protected public
     readonly record ref return sealed set sizeof stackalloc static struct switch this throw true try
     typeof unchecked unsafe using var virtual void volatile while yield`,
    `bool byte char decimal double float int long object sbyte short string uint ulong ushort`
  ),
  cpp: curly(
    `alignas alignof asm auto break case catch class const constexpr const_cast continue decltype
     default delete do dynamic_cast else enum explicit export extern false for friend goto if inline
     mutable namespace new noexcept nullptr operator private protected public register
     reinterpret_cast return sizeof static static_assert static_cast struct switch template this throw
     true try typedef typeid typename union using virtual volatile while`,
    `bool char double float int long short signed size_t unsigned void`
  ),
  swift: curly(
    `as associatedtype break case catch class continue defer deinit do else enum extension
     fileprivate for func guard if import in init inout internal is let nil open operator private
     protocol public repeat rethrows return self Self static struct subscript super switch throw
     throws true false try typealias var where while`,
    `Any Array Bool Dictionary Double Float Int Optional Set String`
  ),
  kotlin: curly(
    `as break by catch class companion const constructor continue crossinline data do else enum
     external false final finally for fun get if import in infix init inline inner interface internal
     is lateinit null object open operator out override package private protected public reified
     return sealed set super suspend tailrec this throw true try typealias val var vararg when where
     while`,
    `Any Boolean Byte Char Double Float Int List Long Map Short String Unit`
  ),
  php: curly(
    `abstract and array as break callable case catch class clone const continue declare default do
     echo else elseif empty enum extends false final finally fn for foreach function global goto if
     implements include include_once instanceof insteadof interface isset list match namespace new
     null or print private protected public readonly require require_once return static switch throw
     trait true try unset use var while xor yield`
  ),
  scss: grammar({
    lineComments: ['//'],
    blockComments: [['/*', '*/']],
    keywords: words('and from important not only through to'),
    atWords: true
  }),
  css: grammar({
    blockComments: [['/*', '*/']],
    keywords: words('and from important not only to'),
    atWords: true
  }),
  less: grammar({
    lineComments: ['//'],
    blockComments: [['/*', '*/']],
    keywords: words('and from important not only when to'),
    atWords: true
  }),

  python: grammar({
    lineComments: ['#'],
    longQuotes: ['"""', "'''"],
    keywords: words(
      `and as assert async await break class continue def del elif else except False finally for from
       global if import in is lambda None nonlocal not or pass raise return True try while with yield`
    ),
    types: words('bool bytes dict float frozenset int list set str tuple')
  }),
  ruby: hash(
    `alias and begin break case class def defined? do else elsif end ensure false for if in module
     next nil not or redo rescue retry return self super then true undef unless until when while
     yield`
  ),
  shell: hash(
    `alias break case continue declare do done elif else esac eval exec export false fi for function
     if in local read readonly return select set shift source then time trap true typeset unset until
     while`
  ),
  powershell: hash(
    `begin break catch class continue data define do dynamicparam else elseif end enum exit filter
     finally for foreach from function hidden if in param process return switch throw trap try until
     using while`
  ),
  dockerfile: hash(
    `ADD ARG CMD COPY ENTRYPOINT ENV EXPOSE FROM HEALTHCHECK LABEL MAINTAINER ONBUILD RUN SHELL
     STOPSIGNAL USER VOLUME WORKDIR AS`
  ),
  yaml: hash('false null true yes no on off'),
  ini: grammar({
    lineComments: ['#', ';'],
    keywords: words('false true')
  }),
  json: grammar({
    quotes: ['"'],
    keywords: words('false null true')
  }),

  sql: grammar({
    lineComments: ['--'],
    blockComments: [['/*', '*/']],
    keywords: words(
      `add all alter and as asc between by case cast check column constraint count create cross
       default delete desc distinct drop else end exists foreign from full group having in index
       inner insert into is join key left like limit not null offset on or order outer primary
       references returning right rollback select set sum table then transaction union unique update
       values view when where with`
    )
  }),

  html: grammar({
    blockComments: [['<!--', '-->']],
    markupTags: true
  }),
  xml: grammar({
    blockComments: [['<!--', '-->']],
    markupTags: true
  })
};

const isWordStart = (ch: string): boolean => /[A-Za-z_$]/.test(ch);
const isWordPart = (ch: string): boolean => /[\w$?]/.test(ch);
const isDigit = (ch: string): boolean => ch >= '0' && ch <= '9';

/** Does `source` carry `run` starting at `at`? */
function startsWith(source: string, at: number, run: string): boolean {
  return source.startsWith(run, at);
}

/**
 * Colour one snippet, as a flat run of spans in source order. Newlines stay
 * inside the spans; {@link toLines} is what splits them.
 */
function scan(source: string, grammarFor: Grammar): HighlightedSpan[] {
  const spans: HighlightedSpan[] = [];
  let plainFrom = 0;
  let at = 0;

  /** Everything since the last emitted span is body text. */
  const flush = (upTo: number): void => {
    if (upTo > plainFrom) spans.push({ value: source.slice(plainFrom, upTo), className: 'plain' });
  };
  const emit = (from: number, upTo: number, className: CodeTokenClass): void => {
    flush(from);
    spans.push({ value: source.slice(from, upTo), className });
    plainFrom = upTo;
    at = upTo;
  };

  while (at < source.length) {
    const ch = source[at];

    // A comment to the end of the line.
    const lineComment = grammarFor.lineComments.find((run) => startsWith(source, at, run));
    if (lineComment) {
      const newline = source.indexOf('\n', at);
      emit(at, newline === -1 ? source.length : newline, 'comment');
      continue;
    }

    // A comment that runs until its closing marker, or to the end if unclosed.
    const block = grammarFor.blockComments.find(([open]) => startsWith(source, at, open));
    if (block) {
      const closed = source.indexOf(block[1], at + block[0].length);
      emit(at, closed === -1 ? source.length : closed + block[1].length, 'comment');
      continue;
    }

    // A long string (Python's docstrings) before the single-character quotes,
    // or `"""a"""` would read as an empty string followed by a word.
    const longQuote = grammarFor.longQuotes.find((run) => startsWith(source, at, run));
    if (longQuote) {
      const closed = source.indexOf(longQuote, at + longQuote.length);
      emit(at, closed === -1 ? source.length : closed + longQuote.length, 'string');
      continue;
    }

    // A string, which a backslash can hold open and a newline cannot close —
    // an unterminated quote colours to the end of the line, not the snippet.
    if (grammarFor.quotes.includes(ch)) {
      let cursor = at + 1;
      while (cursor < source.length) {
        const inner = source[cursor];
        if (inner === '\\') {
          cursor += 2;
          continue;
        }
        if (inner === ch) {
          cursor += 1;
          break;
        }
        if (inner === '\n' && ch !== '`') break;
        cursor += 1;
      }
      emit(at, Math.min(cursor, source.length), 'string');
      continue;
    }

    // A markup tag's name, opening or closing.
    if (grammarFor.markupTags && ch === '<') {
      const name = /^<\/?[A-Za-z][\w:.-]*/.exec(source.slice(at, at + 64));
      if (name) {
        emit(at, at + name[0].length, 'type');
        continue;
      }
    }

    // An at-rule (`@media`) or a Rust-style attribute (`#[derive]`).
    if (grammarFor.atWords && ch === '@') {
      const rule = /^@[\w-]+/.exec(source.slice(at, at + 64));
      if (rule) {
        emit(at, at + rule[0].length, 'keyword');
        continue;
      }
    }

    // A number, including hex, exponents, digit separators and unit suffixes.
    if (isDigit(ch) || (ch === '.' && isDigit(source[at + 1] ?? ''))) {
      let cursor = at;
      while (cursor < source.length && /[\w.]/.test(source[cursor])) cursor += 1;
      emit(at, cursor, 'number');
      continue;
    }

    // A word, which is a keyword, a type, or body text.
    if (isWordStart(ch)) {
      let cursor = at + 1;
      while (cursor < source.length && isWordPart(source[cursor])) cursor += 1;
      const word = source.slice(at, cursor);
      const className: CodeTokenClass = grammarFor.keywords.has(word)
        ? 'keyword'
        : grammarFor.types.has(word)
          ? 'type'
          : grammarFor.capitalsAreTypes && /^[A-Z][a-z\d]/.test(word)
            ? 'type'
            : 'plain';
      if (className === 'plain') {
        at = cursor;
        continue;
      }
      emit(at, cursor, className);
      continue;
    }

    at += 1;
  }

  flush(source.length);
  return spans;
}

/** Break spans at their newlines, so every line is its own list of runs. */
function toLines(spans: readonly HighlightedSpan[]): HighlightedLine[] {
  const lines: HighlightedLine[] = [[]];
  for (const span of spans) {
    const pieces = span.value.split('\n');
    pieces.forEach((piece, index) => {
      if (index > 0) lines.push([]);
      if (piece) lines[lines.length - 1].push({ value: piece, className: span.className });
    });
  }
  return lines;
}

/** The uncoloured rendering: every line as a single plain run. */
export function plainHighlightedLines(value: string): HighlightedLine[] {
  return normalizeNewlines(value)
    .split('\n')
    .map((line) => [{ value: line, className: 'plain' as const }]);
}

function normalizeNewlines(value: string): string {
  return value.replaceAll('\r\n', '\n').replaceAll('\r', '\n');
}

/** Is there a grammar for this language, or does it stay plain? */
export function hasGrammar(language: string): boolean {
  return language in GRAMMARS;
}

/**
 * Colour one snippet. A language with no grammar, and a line that is entirely
 * body text, both come back as the plain rendering — the code is never lost.
 */
export function highlightSource(value: string, language: string): HighlightedLine[] {
  const grammarFor = GRAMMARS[language];
  const text = normalizeNewlines(value);
  if (!grammarFor) return plainHighlightedLines(text);
  const sourceLines = text.split('\n');
  const lines = toLines(scan(text, grammarFor));
  // Every caller lines these up against the raw text — the diff view pairs them
  // with its own rows — so the count has to match the source exactly, whatever
  // the scan produced.
  lines.length = sourceLines.length;
  return sourceLines.map((line, index) => {
    const scanned = lines[index];
    return scanned?.length ? scanned : [{ value: line, className: 'plain' as const }];
  });
}
