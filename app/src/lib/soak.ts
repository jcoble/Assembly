// Heavy soak (?soak=1), 15 minutes. Thread A, the seeded thread with a long
// history, stays open across turns. Every SWITCH_EVERY-th turn runs on thread B
// instead: A closes (releasing everything), B opens for one turn and closes,
// then A reopens. After every turn a scroll pass goes to the top through real
// scroll events, so the conversation's own prefetch loads every older page,
// then back to the bottom. The live sockets drop mid-stream on every 3rd turn.
// Light (?soak=light&a=<thread>&b=<thread>): the same loop on two existing
// threads, 20 minutes, with a short sleep/echo prompt about every 2 minutes,
// so model use stays small; the scroll passes still render A's whole history.
// Scroll (?soak=scroll&a=<thread>&b=<thread>): A stays open with no turns;
// every 30 s, 20 times, a pass goes to the top at fast-wheel speed (a `top` row
// once every older page is in) and back (a `bottom` row). Every 4th pass turns
// back down halfway while a page is in flight, then carries on up; every 5th
// switches to B while a page is in flight and comes back. Then one `idle` row
// after 2 minutes.
// Switch (?soak=switch&a=<thread>&b=<thread>): three cycles of a short turn on
// A with switches during it: to B and back, then three quick switches, then the
// turn settles and A closes. The listener columns are the point.
// Seed (?soak=seed): turns on a new thread A only, with no drops or scrolling,
// until A renders more than SEED_CHARS characters or SEED_TURNS turns ran. Its
// `turn` rows are the progress.
// Every 10 s, and at each turn, close and switch, one CSV row goes to
// soak-results/ through the `soak.append` page call. scripts/soak-report.ts reads it.
import { dropLink, type MemorySample } from './api';
import { pageCall } from './host/pageCall';
import { counters, delay } from './debug.svelte';
import { lag, startLagMeter } from './lag.svelte';

const DURATION_MS = 5 * 60_000;
const LIGHT_DURATION_MS = 20 * 60_000;
const LIGHT_TURN_EVERY_MS = 2 * 60_000;
const SCROLL_PASSES = 20;
const SCROLL_EVERY_MS = 30_000;
const SCROLL_IDLE_MS = 2 * 60_000;
// A fast mouse wheel: about 400 px a frame.
const FLICK_PX = 400;
const FRAME_MS = 16;
const SAMPLE_MS = 10_000;
const STUCK_MS = 5 * 60_000;
const DROP_AFTER_MS = 8_000;
const CLOSE_WAIT_MS = 5_000;
const SWITCH_EVERY = 3;
const SEED_CHARS = 150_000;
const SEED_TURNS = 60;
const SCROLL_STEP_MS = 250;
const SCROLL_LIMIT_MS = 2 * 60_000;
// Thread A (seeded) works on SEED_REPO; each soak's thread B is new, on SOAK_REPO.
// The soak takes the newest SEED_REPO thread as A.
export const SEED_REPO = 'https://github.com/octocat/Spoon-Knife';
export const SOAK_REPO = 'https://github.com/octocat/Hello-World';
// Long, tool-heavy and markdown-rich, so every row kind appears.
const PROMPT = [
  'Work through this repository step by step. First use TodoWrite to plan the steps below, and keep the plan updated as you go.',
  '1. Use your tools to list every file, including hidden ones, and read every file.',
  '2. Run at least four shell commands that inspect the repo: sizes, line counts, file types and `git log --stat`.',
  '3. Create or update a file named soak-notes.md with a short summary, then edit it once more to add a new section.',
  '4. Search the repo for three words that appear in its files.',
  'Then write a long, thorough report of about 2,500 words in markdown, with several headings;',
  'a table of every file with its size and purpose; a table of the git commits;',
  "code blocks in TypeScript, Rust, Python, SQL, Bash and JSON that each work with this repo's contents;",
  'numbered lists, nested bullet lists, a task list and a blockquote.',
].join(' ');
const LIGHT_PROMPT =
  'Run each of these with the Bash tool, one call each, then reply with just: done. ' +
  [1, 2, 3, 4, 5].map((n) => `sleep 1; echo step ${n}`).join(' | ');

// The part of the open conversation the soak drives. Sends and stops go to
// the relay; sendMessage resolves when the relay's turn row ends the run.
export type SoakChat = {
  sendMessage(text: string): Promise<void>;
  stop(): void;
  // The relay's error row for the last run sent, if it failed.
  readonly error: { message: string } | undefined;
  // Text and thinking characters the conversation renders.
  renderedChars(): number;
  // Display items mounted in the timeline, and items the store holds.
  renderedRows(): number;
  loadedRows(): number;
  // Every row in the DOM, and the row nodes the virtualizer still holds.
  domRows(): number;
  trackedRows(): number;
  // The timeline's scroller, whose first child is the list of items.
  scroller(): HTMLElement | null;
  // True until the first page is in and no older page is left or loading.
  olderPending(): boolean;
  // True while an older page is in flight.
  loadingOlder(): boolean;
};

type Snapshot = { chars: number; rendered: number; loaded: number; dom: number; tracked: number };
const snapshot = (chat: SoakChat | null): Snapshot => ({
  dom: chat?.domRows() ?? 0,
  tracked: chat?.trackedRows() ?? 0,
  chars: chat?.renderedChars() ?? 0,
  rendered: chat?.renderedRows() ?? 0,
  loaded: chat?.loadedRows() ?? 0,
});

export type SoakHooks = {
  token: string;
  seed: boolean;
  light: boolean;
  scroll: boolean;
  switching: boolean;
  threadA: string;
  // Unused when seeding.
  threadB: string | null;
  // Opens the thread's conversation and resolves with its chat client.
  open(threadId: string): Promise<SoakChat>;
  // Closes the open conversation (its onDestroy closes the live socket).
  close(): void;
  signal: AbortSignal;
};

const HEADER =
  'time,elapsed_s,event,app_rss_kb,webcontent_pid,webcontent_rss_kb,sockets,chats,fetches,timers,rendered_chars,' +
  'rendered_rows,loaded_rows,scrolling,turns,errors,stuck,drops,mounted_rows,dom_rows,tracked_rows,listeners,live_listeners,note';

// Scroll and resize listeners added and not removed, counted by wrapping
// EventTarget's methods. Svelte never removes an ordinary element's listener on
// teardown (it is dropped with the element), so `live` counts only those whose
// target is window, document or still in the document; the total counts all.
// Targets and listeners are held weakly, so the count keeps nothing alive.
type Added = { target: WeakRef<EventTarget>; type: string; listener: WeakRef<object> | null };
const added = new Set<Added>();
let listeners = 0;
export const listenerCount = () => listeners;
// Entries still held: they shrink as closed conversations are collected.
export const listenerEntries = () => added.size;
export const liveListenerCount = () => {
  let live = 0;
  for (const entry of added) {
    const target = entry.target.deref();
    if (!target || (entry.listener && !entry.listener.deref())) added.delete(entry);
    else if (target === window || target === document || (target instanceof Node && target.isConnected)) live++;
  }
  return live;
};
export function countListeners() {
  const add = EventTarget.prototype.addEventListener;
  const remove = EventTarget.prototype.removeEventListener;
  const counted = (type: string) => type === 'scroll' || type === 'resize';
  EventTarget.prototype.addEventListener = function (type, listener, ...rest) {
    if (counted(type)) {
      listeners++;
      added.add({ target: new WeakRef(this), type, listener: listener ? new WeakRef(listener) : null });
    }
    return add.call(this, type, listener, ...rest);
  };
  EventTarget.prototype.removeEventListener = function (type, listener, ...rest) {
    if (counted(type)) {
      listeners--;
      for (const entry of added)
        if (entry.type === type && (entry.listener?.deref() ?? null) === (listener ?? null) && entry.target.deref() === this) {
          added.delete(entry);
          break;
        }
    }
    return remove.call(this, type, listener, ...rest);
  };
}

export async function runSoak({ token, seed, light, scroll, switching, threadA, threadB, open, close, signal }: SoakHooks): Promise<void> {
  const file = `${seed ? 'seed' : light ? 'light' : scroll ? 'scroll' : switching ? 'switch' : 'soak'}-${new Date().toISOString().replace(/[:.]/g, '-')}.csv`;
  const started = Date.now();
  let turns = 0;
  let errors = 0;
  let stuck = 0;
  let drops = 0;
  let current: SoakChat | null = null;
  let openThread: string | null = null;
  let scrolling = false;
  // True while the sampler waits in its own delay(); that one timer is left
  // out of the timers column so its baseline is 0.
  let samplerWaiting = false;

  async function row(event: string, note = '', snap = snapshot(current)) {
    // Counters are read before any await, at the moment the row is due.
    const c = { ...counters, timers: counters.timers - (samplerWaiting ? 1 : 0) };
    const scrollingNow = scrolling ? 1 : 0;
    let m: Partial<MemorySample> = {};
    try {
      m = await pageCall<MemorySample>('memory.sample');
    } catch (e) {
      // Recorded as an error with blank memory fields, never as a number.
      errors++;
      note = `${note} memory.sample failed: ${e}`.trim();
    }
    const fields = [
      new Date().toISOString(),
      ((Date.now() - started) / 1000).toFixed(1),
      event,
      m.appRssKb ?? '',
      m.webContentPid ?? '',
      m.webContentRssKb ?? '',
      c.sockets,
      c.chats,
      c.fetches,
      c.timers,
      snap.chars,
      snap.rendered,
      snap.loaded,
      scrollingNow,
      turns,
      errors,
      stuck,
      drops,
      c.rows,
      snap.dom,
      snap.tracked,
      listeners,
      liveListenerCount(),
      JSON.stringify(note.replace(/"/g, "'")),
    ];
    await pageCall('soak.append', { name: file, line: fields.join(',') });
  }

  // Closes the open conversation. The close row's counters are read after the
  // release; its chars and rows are the conversation just closed.
  async function closeOpen() {
    const snap = snapshot(current);
    const note = `closed ${openThread?.slice(0, 8)} after turn ${turns}`;
    current = null;
    openThread = null;
    close();
    await delay(CLOSE_WAIT_MS, signal);
    await row('close', note, snap);
  }

  // One turn; returns a note when it went wrong.
  async function turn(chat: SoakChat, turnNo: number): Promise<string> {
    // Owns this turn's drop timer and stuck timer.
    const owner = new AbortController();
    try {
      if (!seed && turnNo % 3 === 0) {
        void (async () => {
          try {
            await delay(DROP_AFTER_MS, owner.signal);
            await dropLink(token, 5000, owner.signal);
            drops++;
          } catch {
            // Turn ended before the drop was due.
          }
        })();
      }
      // Both settle to a value, so the loser of the race never rejects unhandled.
      const finished = chat.sendMessage(light ? LIGHT_PROMPT : PROMPT).then(
        () => 'done',
        (e: unknown) => `threw: ${e}`,
      );
      const timedOut = delay(STUCK_MS, owner.signal).then(
        () => 'stuck',
        () => 'ended',
      );
      const result = await Promise.race([finished, timedOut]);
      if (result.startsWith('threw')) {
        errors++;
        return `turn ${turnNo} ${result}`;
      }
      if (result === 'stuck') {
        stuck++;
        errors++;
        chat.stop();
        return `turn ${turnNo} stuck after 5 min`;
      }
      if (chat.error) {
        errors++;
        return `turn ${turnNo} error: ${chat.error.message}`;
      }
      return '';
    } finally {
      owner.abort();
    }
  }

  // To the top until every older page is in, then back to the bottom. The
  // virtualized list only has nearby rows in the DOM, so each step sets the
  // scroller's position, which fires a real scroll event.
  async function scrollPass(chat: SoakChat): Promise<string> {
    scrolling = true;
    try {
      const until = Date.now() + SCROLL_LIMIT_MS;
      while (chat.olderPending()) {
        if (Date.now() > until) return 'scroll pass failed: older pages still pending after 2 min';
        const s = chat.scroller();
        if (s) s.scrollTop = 0;
        await delay(SCROLL_STEP_MS, signal);
      }
      const s = chat.scroller();
      if (s) s.scrollTop = s.scrollHeight;
      await delay(SCROLL_STEP_MS, signal);
      return '';
    } finally {
      scrolling = false;
    }
  }

  await pageCall('soak.append', { name: file, line: HEADER });
  // Baseline: nothing open, before the sampler starts.
  await row('baseline');

  // The sampler waits through the counted delay(); stopSampler ends it.
  const stopSampler = new AbortController();
  void (async () => {
    try {
      while (true) {
        samplerWaiting = true;
        await delay(SAMPLE_MS, stopSampler.signal);
        samplerWaiting = false;
        await row('sample');
      }
    } catch {
      samplerWaiting = false;
    }
  })();

  // One frame of wheel: `dy` pixels (negative is up).
  async function wheel(dy: number) {
    const s = current?.scroller();
    if (s) s.scrollTop = Math.max(0, s.scrollTop + dy);
    await delay(FRAME_MS, signal);
  }
  // Up at fast-wheel speed until every older page is in; `twist` runs once, at
  // the first frame past halfway with a page in flight.
  async function wheelUp(twist?: () => Promise<void>) {
    const until = Date.now() + SCROLL_LIMIT_MS;
    const half = (current?.scroller()?.scrollTop ?? 0) / 2;
    while (current!.olderPending() || (current!.scroller()?.scrollTop ?? 0) > 0) {
      if (Date.now() > until) throw new Error('older pages still pending after 2 min');
      if (twist && current!.loadingOlder() && (current!.scroller()?.scrollTop ?? 0) <= half) {
        const run = twist;
        twist = undefined;
        await run();
        continue;
      }
      await wheel(-FLICK_PX);
    }
  }
  if (switching) {
    countListeners();
    const show = async (threadId: string) => {
      if (openThread) await closeOpen();
      current = await open(threadId);
      openThread = threadId;
    };
    const flip = async (threadId: string) => {
      close();
      current = await open(threadId);
      openThread = threadId;
      await delay(300, signal);
    };
    try {
      for (let cycle = 1; cycle <= 3 && !signal.aborted; cycle++) {
        await show(threadA);
        await delay(CLOSE_WAIT_MS, signal);
        await row('open', `cycle ${cycle}`);
        void current!.sendMessage(LIGHT_PROMPT).catch(() => undefined);
        turns++;
        await delay(3000, signal);
        await show(threadB!);
        await delay(3000, signal);
        await show(threadA);
        for (let i = 0; i < 3; i++) {
          await flip(threadB!);
          await flip(threadA);
        }
        await row('switched', `cycle ${cycle}, A open mid-turn`);
        await delay(30_000, signal);
        await row('settled', `cycle ${cycle}, entries ${listenerEntries()}`);
        await closeOpen();
      }
      for (let i = 1; i <= 4; i++) {
        await delay(15_000, signal);
        await row('gc', `+${i * 15} s after the last close, entries ${listenerEntries()}`);
      }
    } finally {
      samplerWaiting = false;
      stopSampler.abort();
      await row('end');
    }
    return;
  }
  if (scroll) {
    countListeners();
    try {
      current = await open(threadA);
      openThread = threadA;
      await delay(CLOSE_WAIT_MS, signal);
      await row('opened');
      for (let pass = 1; pass <= SCROLL_PASSES && !signal.aborted; pass++) {
        const passStarted = Date.now();
        let note = `pass ${pass}`;
        scrolling = true;
        try {
          if (pass % 5 === 0 && threadB) {
            // Leave while a page is in flight, then come back and go up again.
            await wheelUp(async () => {
              note += ', switched to B mid-load';
              close();
              current = await open(threadB);
              await delay(CLOSE_WAIT_MS, signal);
              close();
              current = await open(threadA);
              await delay(CLOSE_WAIT_MS, signal);
            });
            await wheelUp();
          } else if (pass % 4 === 0) {
            await wheelUp(async () => {
              note += ', reversed mid-load';
              for (let i = 0; i < 15; i++) await wheel(FLICK_PX);
            });
          } else {
            await wheelUp();
          }
          await row('top', note);
          for (let s = current!.scroller(); s && s.scrollTop + s.clientHeight < s.scrollHeight - 1; s = current!.scroller()) await wheel(FLICK_PX);
        } catch (e) {
          errors++;
          note = `${note} failed: ${e}`;
        } finally {
          scrolling = false;
        }
        await row('bottom', note);
        await delay(Math.max(0, passStarted + SCROLL_EVERY_MS - Date.now()), signal);
      }
      await delay(SCROLL_IDLE_MS, signal);
      await row('idle', `${SCROLL_IDLE_MS / 60_000} min after the last pass`);
      await closeOpen();
    } finally {
      samplerWaiting = false;
      stopSampler.abort();
      await row('end');
    }
    return;
  }

  const more = () =>
    seed
      ? turns < SEED_TURNS && snapshot(current).chars <= SEED_CHARS
      : Date.now() - started < (light ? LIGHT_DURATION_MS : DURATION_MS);
  try {
    while (more() && !signal.aborted) {
      const turnNo = turns + 1;
      const threadId = !seed && turnNo % SWITCH_EVERY === 0 ? threadB! : threadA;
      if (openThread !== threadId) {
        const back = threadId === threadA && openThread !== null;
        if (openThread === threadA) await row('away', `A before switch-away, turn ${turnNo} on B`);
        if (openThread) await closeOpen();
        current = await open(threadId);
        openThread = threadId;
        if (back) {
          await delay(CLOSE_WAIT_MS, signal);
          await row('back', `A after switch-back, before turn ${turnNo}`);
        }
      }
      const chat = current!;
      const turnStarted = Date.now();
      let note = await turn(chat, turnNo);
      turns++;
      if (!seed) {
        const failed = await scrollPass(chat);
        if (failed) {
          errors++;
          note = `${note} turn ${turnNo} ${failed}`.trim();
        }
      }
      await row('turn', note || `turn ${turnNo} on ${threadId.slice(0, 8)}`);
      if (light) await delay(Math.max(0, turnStarted + LIGHT_TURN_EVERY_MS - Date.now()), signal);
    }
    if (current) await closeOpen();
  } finally {
    // Cleared first: abort() removes the sampler's timer synchronously.
    samplerWaiting = false;
    stopSampler.abort();
    await row('end');
  }
}

// ?counters=1: every 2 s, the open conversation's counters go to soak-results/counters-<time>.csv, for a run
// driven by hand, until the signal ends it.
export async function logCounters(open: () => SoakChat | undefined, openId: () => string | null, signal: AbortSignal) {
  countListeners();
  startLagMeter();
  const file = `counters-${new Date().toISOString().replace(/[:.]/g, '-')}.csv`;
  await pageCall('soak.append', { name: file, line: 'time,thread,mounted_rows,dom_rows,tracked_rows,rendered_rows,loaded_rows,fetches,sockets,chats,timers,scroll_top,tauri_listeners,scroll_listeners,live_scroll_listeners,jank_50,jank_100,worst_5s,worst_run,key_worst,key_median' });
  for (;;) {
    const chat = open();
    const fields = [new Date().toISOString(), openId() ?? '', counters.rows, chat?.domRows() ?? 0, chat?.trackedRows() ?? 0,
      chat?.renderedRows() ?? 0, chat?.loadedRows() ?? 0, counters.fetches, counters.sockets, counters.chats, counters.timers,
      Math.round(chat?.scroller()?.scrollTop ?? 0), counters.tauri, listenerCount(), liveListenerCount(),
      lag.jank50, lag.jank100, lag.worst5s, lag.worstRun, lag.keyWorst, lag.keyMedian];
    await pageCall('soak.append', { name: file, line: fields.join(',') });
    await delay(2000, signal);
  }
}
