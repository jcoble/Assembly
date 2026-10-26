// The timeline's virtual rows (Conversation.svelte): the loading line, the items, the working line. A
// cancelled run's error row that draws nothing is left out, so the rows around it meet as they did in the
// flowing list. Plain functions, for node tests.
import { errorNotice } from '../chat/notices.ts';
import type { Item } from '../rows/store';
import type { Fold } from './fold';

export const LOADING = 'line:loading';
export const WORKING = 'line:working';
// `at`: the item's index, or -1 for a line. A fold's heading row has its turn's
// fold and the index of the first item it hides; `end` when, closed, it ends the reply.
export type Entry = { key: string; at: number; fold?: Fold; end?: boolean };

// A reply's pieces sit under one agent-name label; the reply ends before the
// next prompt or turn line.
export const inReply = (item: Item | undefined) => !!item && item.kind !== 'user' && item.kind !== 'stats';
export const endAt = (items: Item[], i: number) => inReply(items[i]) && !inReply(items[i + 1]);
export const foldKey = (runId: string) => `fold:${runId}`;

// `open`: the folds the reader opened. `loading`: an older page is loading. `working`: a live turn's line shows.
export function timelineEntries(items: Item[], folds: Map<string, Fold>, open: { has(key: string): boolean }, loading: boolean, working: boolean): Entry[] {
  const list: Entry[] = loading ? [{ key: LOADING, at: -1 }] : [];
  const heads = new Map<string, Entry>();
  items.forEach((item, at) => {
    const fold = item.runId ? folds.get(item.runId) : undefined;
    if (fold?.work.has(item.key)) {
      let head = heads.get(fold.runId);
      if (!head) list.push((head = { key: foldKey(fold.runId), at, fold })), heads.set(fold.runId, head);
      if (!open.has(head.key)) {
        if (endAt(items, at)) head.end = true;
        return;
      }
    }
    if (item.kind !== 'error' || errorNotice(item.error) || endAt(items, at)) list.push({ key: item.key, at });
  });
  if (working) list.push({ key: WORKING, at: -1 });
  return list;
}
