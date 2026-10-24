// Row heights for the virtualizer, worked out before a row is drawn from the
// components' CSS as it is (the look must not change to suit the estimates).
// Rows whose CSS fixes their height are exact; text rows are estimated from
// their characters and the row's width, and the virtualizer's measuring
// corrects them once drawn.
//
// A row is drawn absolutely, so its margins sit inside it: `h` is its height
// with its margins, and `top` / `bottom` are the margins that, in the old
// flowing list, merged with the next row's. `overlap` gives back what that
// merge saved, so rows land where they did.
import { errorNotice } from '../chat/notices.ts';
import { splitAttachments } from '../composer/attachments.ts';
import { MESSAGE_FOLD_LINES } from '../thread/fold.ts';
import type { Item, SafeInlinePart, SafeMarkdownBlock } from './store.ts';

export type Box = { h: number; top: number; bottom: number };
// The row's place in its reply (the "Claude" label above it, the 10px `.end`
// gap below it), an opened tool call or thinking, and a user row's checkout line.
// `meta`: the copy-and-time line under a sent message or a reply.
export type RowFlags = { label: boolean; end: boolean; open: boolean; checkout: boolean; meta?: boolean };

// Line heights: body 14px × 1.45, text 14px × 1.55, 12px × 1.45, 13px × 1.45,
// code 13px × 1.6, table 13px × 1.55, error text 13px × 1.5.
const LINE = 20.3;
const TEXT = 21.7;
const SMALL = 17.4;
const TOOL = 18.85;
const CODE = 20.8;
const TABLE = 20.15;
const ERROR = 19.5;
// Average character widths, 14px and 13px.
const CHAR = 7;
const CHAR13 = 6.5;

// Two touching vertical margins merge into one, as CSS does.
export function collapse(a: number, b: number): number {
  if (a >= 0 && b >= 0) return Math.max(a, b);
  if (a < 0 && b < 0) return Math.min(a, b);
  return a + b;
}
// What a row gives back to the next because their margins merged.
export const overlap = (bottom: number, top: number) => bottom + top - collapse(bottom, top);

const textOf = (parts: SafeInlinePart[]): string => parts.map((p) => ('value' in p ? p.value : textOf(p.parts))).join('');

// Wrapped lines of pre-wrap text set `width` px wide.
function lines(text: string, width: number, char = CHAR): number {
  const cols = Math.max(8, Math.floor(width / char));
  let n = 0;
  for (const line of text.split('\n')) n += Math.max(1, Math.ceil(line.length / cols));
  return n;
}

// Text.svelte's h3–h6 (a heading of level n is h(n + 2)), at the UA's sizes.
const HEADING = [16.38, 14, 11.62, 9.38];

function blockBox(block: SafeMarkdownBlock, width: number): Box {
  switch (block.kind) {
    case 'paragraph':
      return { top: 0, h: lines(textOf(block.parts), width) * TEXT, bottom: 10 };
    case 'heading': {
      const size = HEADING[Math.min(block.level + 2, 6) - 3]!;
      return { top: 16, h: lines(textOf(block.parts), width, size * 0.55) * size * 1.55, bottom: 8 };
    }
    case 'code': {
      // borders 2, the language bar 37, padding 24, and at most 360px of code
      const n = block.value.replace(/\n$/, '').split('\n').length;
      return { top: 12, h: 63 + Math.min(360, n * CODE), bottom: 12 };
    }
    case 'quote': {
      const inner = blocksBox(block.blocks, width - 15);
      return { top: inner.top, h: inner.h, bottom: Math.max(10, inner.bottom) };
    }
    case 'list': {
      let h = 0;
      for (const item of block.items) {
        if (item.parts.length) h += lines(textOf(item.parts), width - 40) * TEXT;
        if (item.blocks.length) {
          const inner = blocksBox(item.blocks, width - 40);
          h += inner.top + inner.h + inner.bottom;
        }
      }
      return { top: 0, h, bottom: 10 };
    }
    case 'table': {
      const cell = Math.max(40, (width - 2) / Math.max(1, block.headers.length) - 20);
      const rows = block.rows.reduce((h, row) => h + Math.max(1, ...row.map((c) => lines(textOf(c), cell, CHAR13))) * TABLE + 15, 0);
      return { top: 0, h: 2 + TABLE + 15 + rows - 1, bottom: 8 };
    }
    case 'rule':
      return { top: 10, h: 1, bottom: 10 };
  }
}

function blocksBox(blocks: SafeMarkdownBlock[], width: number): Box {
  let box: Box | null = null;
  for (const block of blocks) {
    const next = blockBox(block, width);
    box = box ? { top: box.top, h: box.h + collapse(box.bottom, next.top) + next.h, bottom: next.bottom } : next;
  }
  return box ?? { top: 0, h: 0, bottom: 0 };
}

// An assistant piece: the label (12px line, 4px below it, which merges with
// the content's top margin), the content, and the `.end` gap that holds the
// content's bottom margin inside the row.
function piece(content: Box, f: RowFlags): Box {
  const head = f.label ? SMALL + collapse(4, content.top) : content.top;
  return {
    h: head + content.h + content.bottom + (f.end ? 10 : 0),
    top: f.label ? 0 : content.top,
    bottom: f.end ? 0 : content.bottom,
  };
}

// A finished turn's fold heading (TurnFold.svelte): one 13px line, 8px below.
export const foldBox = (f: RowFlags): Box => piece({ top: 0, h: 22, bottom: 8 }, f);

// The copy-and-time line (MessageMeta.svelte): 24px, and what its margins add.
const USER_META = 28;
const REPLY_META = 26;

// The turn, loading and working lines: a 12px line, margin -10px 0 18px.
export const MARK: Box = { h: -10 + SMALL + 18, top: -10, bottom: 18 };

// A row's box. `width` is the row's content width; `blocks` are a streaming
// message's blocks as shown so far.
export function rowBox(item: Item, width: number, f: RowFlags, blocks?: SafeMarkdownBlock[]): Box {
  switch (item.kind) {
    case 'user': {
      // The checkout line (8 + 1 + line + 12), the label, the bubble's 20px of
      // padding around the thumbnails and text, and 18px below.
      const { text, attachments } = splitAttachments(item.text);
      const inner = width - 28;
      const rows = Math.ceil(attachments.length / Math.max(1, Math.floor((inner + 8) / 228)));
      const thumbs = rows ? rows * 203.4 + (rows - 1) * 8 + 8 : 0;
      // Past MESSAGE_FOLD_LINES the text folds, with a 22px chevron under it, until opened.
      const textLines = text ? lines(text, inner) : 0;
      const shownLines = textLines > MESSAGE_FOLD_LINES && !f.open ? MESSAGE_FOLD_LINES : textLines;
      const body = 20 + thumbs + shownLines * TEXT + (textLines > MESSAGE_FOLD_LINES ? 22 : 0);
      return { h: (f.checkout ? 21 + SMALL : 0) + SMALL + 4 + body + 18 + (f.meta ? USER_META : 0), top: 0, bottom: 18 };
    }
    case 'stats':
    case 'note':
      return MARK;
    case 'subagent':
      // Subagent.svelte: a 13px line, and its description under it; measured once drawn.
      return piece({ top: 0, h: item.subagent.description ? 40 : 22, bottom: 8 }, f);
    case 'task':
      // Task.svelte: one 13px line with a small Stop button, 8px below; measured once drawn.
      return piece({ top: 0, h: 22, bottom: 8 }, f);
    case 'compaction':
      // Compaction.svelte: one small line, 18px below; measured once drawn.
      return { h: SMALL + 18, top: 0, bottom: 18 };
    case 'other':
      return piece({ top: 0, h: SMALL, bottom: 8 }, f);
    case 'ask': {
      // The card's border and padding (26), its label, its text, and a row of buttons or the form's
      // fields; measured once drawn.
      const fields = Object.keys(item.ask.schema?.properties ?? {}).length;
      const text = item.ask.title ?? item.ask.message ?? '';
      return piece({ top: 0, h: 26 + SMALL + 8 + lines(text, width - 26, CHAR13) * ERROR + 12 + 30 + fields * 56, bottom: 8 }, f);
    }
    case 'tool':
      // Border 2 and padding 12 around the 13px line; opened, the body is estimated.
      return piece({ top: 0, h: 14 + TOOL + (f.open ? 23.4 : 0), bottom: 8 }, f);
    case 'thinking':
      return piece({ top: 0, h: LINE + (f.open ? 4 + lines(item.text, width) * LINE : 0), bottom: 8 }, f);
    case 'message': {
      // `.markdown`'s 8px below merges with the last block's margin.
      const b = blocksBox(blocks ?? item.blocks, width);
      const box = piece({ top: b.top, h: b.h, bottom: Math.max(8, b.bottom) }, f);
      return f.meta ? { ...box, h: box.h + REPLY_META } : box;
    }
    case 'error': {
      // A cancelled run draws no card: only the `.end` gap, if it has one.
      const card = errorNotice(item.error);
      if (!card) return { h: f.end ? 10 : 0, top: 0, bottom: 0 };
      const para = (text: string, gap: number) => gap + lines(text, width - 26, CHAR13) * ERROR;
      return piece({ top: 0, h: 26 + SMALL + para(card.message, 8) + (card.hint ? para(card.hint, 6) : 0), bottom: 8 }, f);
    }
  }
}

// The scrollTop that keeps the reader's row in place when rows above it
// change (an older page in, the loading line in or out): the offset moves by
// the estimated heights now above it less those that were.
export const prependedTop = (scrollTop: number, before: number[], after: number[]): number =>
  scrollTop + after.reduce((a, b) => a + b, 0) - before.reduce((a, b) => a + b, 0);
