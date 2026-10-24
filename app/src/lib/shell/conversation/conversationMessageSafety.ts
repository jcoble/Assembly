/**
 * Markdown adapter for conversation output.
 *
 * The renderer deliberately returns data rather than HTML. Svelte renders every
 * text value as text, so a partial fence or a provider supplied `<script>` can
 * never become executable markup while a turn is streaming. Nothing here
 * produces a string of markup, and nothing downstream needs a sanitizer.
 *
 * The reading of the Markdown itself is `marked`'s GFM tokenizer rather than
 * the hand-written scanner this used to carry. That scanner could not nest: a
 * sub-list flattened into its parent, emphasis holding code broke apart,
 * emphasis across a wrapped line was left as asterisks, and a backslash escape
 * printed the backslash. Those are most of what an agent writes.
 */

import { marked, type Token, type Tokens } from 'marked';

export type SafeInlinePart =
  | { kind: 'text'; value: string }
  | { kind: 'code'; value: string }
  | { kind: 'strong'; parts: SafeInlinePart[] }
  | { kind: 'emphasis'; parts: SafeInlinePart[] }
  | { kind: 'strike'; parts: SafeInlinePart[] }
  | { kind: 'link'; href: string; parts: SafeInlinePart[] }
  | { kind: 'image'; href: string; alt: string }
  | { kind: 'file-link'; path: string; parts: SafeInlinePart[] };

/** A row of a list, with whatever blocks are nested underneath it. */
export interface SafeListItem {
  task: boolean;
  checked: boolean;
  parts: SafeInlinePart[];
  blocks: SafeMarkdownBlock[];
}

export type SafeMarkdownBlock =
  | { kind: 'paragraph'; parts: SafeInlinePart[] }
  | { kind: 'heading'; level: number; parts: SafeInlinePart[] }
  | { kind: 'code'; language: string; value: string; complete: boolean }
  | { kind: 'quote'; blocks: SafeMarkdownBlock[] }
  | { kind: 'list'; ordered: boolean; items: SafeListItem[] }
  | { kind: 'table'; headers: SafeInlinePart[][]; rows: SafeInlinePart[][][] }
  | { kind: 'rule' };

const SAFE_PROTOCOL = /^(?:https?:|mailto:|assembly:|#)/i;

/** A URL scheme at the front of a target, which a file path never has. */
const SCHEME_PREFIX = /^[a-z][a-z0-9+.-]*:/i;

const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/;

const FILE_SCHEME_PREFIX = /^file:/i;

/** Turn file URI spellings into the same paths used by bare file links. */
export function normalizeConversationFileHref(value: string): string {
  const href = value.trim();
  if (!FILE_SCHEME_PREFIX.test(href)) return href;
  return href.replace(/^file:\/\//i, '').replace(/^file:/i, '');
}

/** Separate the supported editor line suffixes from a file path. */
export function splitConversationFileReference(value: string): { path: string; line?: number } {
  const reference = value.trim();
  const suffix = /(?:#L?|:)([1-9]\d*)$/i.exec(reference);
  if (!suffix) return { path: reference };
  return { path: reference.slice(0, suffix.index), line: Number(suffix[1]) };
}

/** Return a URL only when it is safe to put in an href attribute. */
export function sanitizeConversationHref(value: string): string | null {
  const href = value.trim();
  if (!href || CONTROL_CHARACTERS.test(href)) return null;
  // Relative links are intentionally not treated as external URLs. File/path
  // links are routed through the editor bus by a later controller receipt.
  return SAFE_PROTOCOL.test(href) ? href : null;
}

/** Escape text for callers that need a safe plain-text representation. */
export function sanitizeConversationMarkdown(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function text(value: string): SafeInlinePart {
  return { kind: 'text', value };
}

/**
 * A link target that is not a URL at all.
 *
 * What must not reach a link is a URL scheme — `javascript:`, `data:` and their
 * kind. Rejecting every target that held a colon did that, but it also rejected
 * the most common link an agent writes: a file with the line it means,
 * `src/thing.ts:71`. A scheme can only sit at the front, so that is where one
 * is looked for.
 */
function filePath(href: string): string | null {
  const path = href.trim();
  if (!path || SCHEME_PREFIX.test(path) || CONTROL_CHARACTERS.test(path)) return null;
  return path;
}

/** The writing a part holds, with its marks dropped. */
function partText(part: SafeInlinePart): string {
  if (part.kind === 'image') return part.alt;
  return part.kind === 'text' || part.kind === 'code'
    ? part.value
    : part.parts.map(partText).join('');
}

function linkPart(href: string, parts: SafeInlinePart[]): SafeInlinePart {
  if (FILE_SCHEME_PREFIX.test(href.trim())) {
    const path = filePath(normalizeConversationFileHref(href));
    return path ? { kind: 'file-link', path, parts } : text(parts.map(partText).join(''));
  }
  const safe = sanitizeConversationHref(href);
  if (safe) return { kind: 'link', href: safe, parts };
  const path = filePath(href);
  if (path) return { kind: 'file-link', path, parts };
  return text(parts.map(partText).join(''));
}

function inlineParts(tokens: Token[] | undefined, raw = ''): SafeInlinePart[] {
  if (!tokens?.length) return [text(raw)];
  const parts: SafeInlinePart[] = [];
  for (const token of tokens) {
    switch (token.type) {
      case 'text':
      case 'escape': {
        const inner = token as Tokens.Text;
        // A text token carries children only when it holds marks of its own.
        if (inner.tokens?.length) parts.push(...inlineParts(inner.tokens));
        else parts.push(text(inner.text));
        break;
      }
      case 'codespan':
        parts.push({ kind: 'code', value: (token as Tokens.Codespan).text });
        break;
      case 'strong':
        parts.push({ kind: 'strong', parts: inlineParts((token as Tokens.Strong).tokens) });
        break;
      case 'em':
        parts.push({ kind: 'emphasis', parts: inlineParts((token as Tokens.Em).tokens) });
        break;
      case 'del':
        parts.push({ kind: 'strike', parts: inlineParts((token as Tokens.Del).tokens) });
        break;
      case 'link': {
        const link = token as Tokens.Link;
        parts.push(linkPart(link.href, inlineParts(link.tokens, link.text)));
        break;
      }
      case 'image': {
        const image = token as Tokens.Image;
        const href = sanitizeConversationHref(image.href);
        // The PR reader can opt into loading HTTPS images. Agent transcripts
        // and file previews continue to show the link without loading it.
        parts.push(href && /^https:\/\//i.test(href)
          ? { kind: 'image', href, alt: image.text || 'PR image' }
          : linkPart(image.href, [text(image.text || image.href)]));
        break;
      }
      case 'br':
        parts.push(text('\n'));
        break;
      // Raw markup an agent typed stays the characters it typed. This is the
      // one branch where turning a token into anything but text would matter.
      case 'html':
      default: {
        const other = token as { raw?: string; text?: string };
        parts.push(text(other.raw ?? other.text ?? ''));
        break;
      }
    }
  }
  return parts.length ? parts : [text('')];
}

/**
 * Whether a fenced block was closed.
 *
 * `marked` reads an unterminated fence as code and says nothing about it having
 * run off the end, which is the ordinary state of a code block while a turn is
 * still being written. The opening fence is in the token's raw text; a closed
 * block has a second one.
 */
function fenceIsClosed(raw: string): boolean {
  const lines = raw.replace(/\n$/, '').split('\n');
  const opening = /^\s*(`{3,}|~{3,})/.exec(lines[0] ?? '');
  // An indented code block has no fence to close.
  if (!opening) return true;
  const marker = opening[1][0] === '`' ? '`' : '~';
  const closing = new RegExp(`^\\s*${marker}{${opening[1].length},}\\s*$`);
  return lines.slice(1).some((line) => closing.test(line));
}

function listItem(item: Tokens.ListItem): SafeListItem {
  const parts: SafeInlinePart[] = [];
  const blocks: SafeMarkdownBlock[] = [];
  for (const token of item.tokens ?? []) {
    // The box a task row draws comes from `task`/`checked`, which the tokenizer
    // has already read off the front of the row. Its leftover `[x]` token is the
    // same fact written twice, and it was landing under the row as a paragraph
    // of its own.
    if (token.type === 'checkbox') continue;
    if (token.type === 'text' || token.type === 'paragraph') {
      const inner = token as Tokens.Text;
      if (parts.length) parts.push(text('\n'));
      parts.push(...inlineParts(inner.tokens, inner.text));
    } else {
      blocks.push(...blocksFrom([token]));
    }
  }
  return {
    task: item.task === true,
    checked: item.checked === true,
    parts: parts.length ? parts : [text('')],
    blocks
  };
}

function blocksFrom(tokens: Token[]): SafeMarkdownBlock[] {
  const blocks: SafeMarkdownBlock[] = [];
  for (const token of tokens) {
    switch (token.type) {
      case 'space':
      case 'def':
        break;
      case 'heading': {
        const heading = token as Tokens.Heading;
        blocks.push({
          kind: 'heading',
          level: heading.depth,
          parts: inlineParts(heading.tokens, heading.text)
        });
        break;
      }
      case 'code': {
        const code = token as Tokens.Code;
        blocks.push({
          kind: 'code',
          language: code.lang?.trim().split(/\s+/)[0] ?? '',
          value: code.text,
          complete: fenceIsClosed(code.raw)
        });
        break;
      }
      case 'blockquote':
        blocks.push({
          kind: 'quote',
          blocks: blocksFrom((token as Tokens.Blockquote).tokens ?? [])
        });
        break;
      case 'list': {
        const list = token as Tokens.List;
        blocks.push({
          kind: 'list',
          ordered: list.ordered === true,
          items: list.items.map(listItem)
        });
        break;
      }
      case 'table': {
        const table = token as Tokens.Table;
        blocks.push({
          kind: 'table',
          headers: table.header.map((cell) => inlineParts(cell.tokens, cell.text)),
          rows: table.rows.map((row) => row.map((cell) => inlineParts(cell.tokens, cell.text)))
        });
        break;
      }
      case 'hr':
        blocks.push({ kind: 'rule' });
        break;
      case 'paragraph': {
        const paragraph = token as Tokens.Paragraph;
        blocks.push({ kind: 'paragraph', parts: inlineParts(paragraph.tokens, paragraph.text) });
        break;
      }
      // Markup an agent typed is writing, not structure. It reaches the reader
      // as the characters it is.
      case 'html':
      default: {
        const other = token as { raw?: string; text?: string };
        const value = other.raw ?? other.text ?? '';
        if (value.trim()) blocks.push({ kind: 'paragraph', parts: [text(value)] });
        break;
      }
    }
  }
  return blocks;
}

/** Parse GFM Markdown into safe, streaming-friendly blocks. */
export function parseSafeMarkdown(source: string): SafeMarkdownBlock[] {
  if (!source) return [];
  return blocksFrom(marked.lexer(source, { gfm: true }));
}

/** Compatibility name used by safety-focused tests and adapters. */
export const parseConversationMarkdown = parseSafeMarkdown;
