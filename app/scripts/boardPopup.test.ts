import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = (path: string): string => readFileSync(new URL(path, import.meta.url), 'utf8');

{ // the_open_board_hides_a_live_browser_tab
  // A live browser tab is a native view above the page; it hides only while an open popup
  // matches POPUP_SELECTOR. The kit's popover content has no role, so the board must carry one.
  const selector = /const POPUP_SELECTOR =\s*'([^']+)'/.exec(read('../src/routes/+page.svelte'))?.[1];
  assert.ok(selector, 'the page names the popups a live browser tab hides under');
  const content = /<Popover\.Content\b[^>]*>/.exec(read('../src/lib/shell/board/Board.svelte'))?.[0];
  assert.ok(content, 'the board draws its drop-down as popover content');
  const attributes = selector.split(',').map((part) => /^\[([a-z-]+)="([^"]+)"\]$/.exec(part.trim())).filter((match) => match !== null);
  assert.ok(
    attributes.some(([, name, value]) => content.includes(`${name}="${value}"`)),
    'the open board matches POPUP_SELECTOR'
  );
}
console.log('board popup tests passed');
