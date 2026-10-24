// What a row keeps while the virtualizer has it out of the DOM, so it is drawn
// again as it was left: one per item, in the conversation's map.
import { SvelteSet } from 'svelte/reactivity';
import type { ToolDetail } from './store';

export class Kept {
  // An opened tool call, thinking, folded turn or long sent message.
  open = $state(false);
  // A long diff shown in full.
  allDiff = $state(false);
  // A tool's output lines mounted ("Show earlier" adds more); Tool's TAIL.
  shown = $state(200);
  // A tool's fetched detail, only while it is open, and the state it was fetched for.
  detail = $state<ToolDetail | null>(null);
  detailFor = $state<string | null>(null);
  // Code blocks set to wrap, and copied, by their place in the message.
  wrapped = new SvelteSet<string>();
  copied = new SvelteSet<string>();
}
