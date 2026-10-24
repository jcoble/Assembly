// The streaming message's newest text shows at a steady pace, one step per
// animation frame, from the characters received but not yet shown. Display
// only: it cuts the relay's blocks short and never parses text.
import { blockChars, blocksChars, partChars, partsChars, type SafeInlinePart, type SafeMarkdownBlock } from './store.ts';

// The steady pace while little is waiting, in characters a second.
export const MIN_CPS = 90;
// The most the reveal is behind: the waiting characters always drain within
// this at the current pace, so the pace grows with the buffer.
export const MAX_LAG_MS = 250;

// How many characters show after a frame `dtMs` long.
export function revealStep(shown: number, target: number, dtMs: number): number {
  if (shown >= target) return target;
  const waiting = target - shown;
  const step = Math.max((MIN_CPS * dtMs) / 1000, (waiting * dtMs) / MAX_LAG_MS);
  return Math.min(target, shown + Math.max(1, Math.ceil(step)));
}

function clipParts(parts: SafeInlinePart[], chars: number): SafeInlinePart[] {
  const out: SafeInlinePart[] = [];
  for (const part of parts) {
    const size = partChars(part);
    if (size <= chars) {
      out.push(part);
      chars -= size;
      continue;
    }
    if (chars > 0) out.push('value' in part ? { ...part, value: part.value.slice(0, chars) } : { ...part, parts: clipParts(part.parts, chars) });
    break;
  }
  return out;
}

function clipBlock(block: SafeMarkdownBlock, chars: number): SafeMarkdownBlock {
  switch (block.kind) {
    case 'paragraph':
    case 'heading':
      return { ...block, parts: clipParts(block.parts, chars) };
    case 'code':
      // The coloured lines are whole-block; the cut part shows plain.
      return { ...block, value: block.value.slice(0, chars), lines: undefined };
    case 'quote':
      return { ...block, blocks: clipBlocks(block.blocks, chars) };
    case 'list': {
      const items = [];
      for (const item of block.items) {
        const head = Math.min(chars, partsChars(item.parts));
        const blocks = clipBlocks(item.blocks, chars - head);
        items.push({ ...item, parts: clipParts(item.parts, head), blocks });
        chars -= head + blocksChars(blocks);
        if (chars <= 0) break;
      }
      return { ...block, items };
    }
    case 'table': {
      // Cells in reading order, each cut to what is left.
      const cut = (cell: SafeInlinePart[]) => {
        const parts = clipParts(cell, Math.max(0, chars));
        chars -= partsChars(parts);
        return parts;
      };
      const headers = block.headers.map(cut);
      const rows = [];
      for (const row of block.rows) {
        if (chars <= 0) break;
        rows.push(row.map(cut));
      }
      return { ...block, headers, rows };
    }
    case 'rule':
      return block;
  }
}

// The first `chars` characters of `blocks`. Blocks shown whole are the same
// objects, so only the block being revealed redraws.
export function clipBlocks(blocks: SafeMarkdownBlock[], chars: number): SafeMarkdownBlock[] {
  const out: SafeMarkdownBlock[] = [];
  for (const block of blocks) {
    const size = blockChars(block);
    if (size <= chars) {
      out.push(block);
      chars -= size;
      continue;
    }
    if (chars > 0) out.push(clipBlock(block, chars));
    break;
  }
  return out;
}
