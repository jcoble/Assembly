<script lang="ts">
  import { onDestroy, setContext, untrack } from 'svelte';
  import { SvelteSet } from 'svelte/reactivity';
  import { createVirtualizer } from '@tanstack/svelte-virtual';
  import { answerAsk, fetchAttachment, getSession, listAgents, rowsPage, sendText, stopRun, stopTask, toolDetail, type Session } from './api';
  import { chatClosed, chatOpened, counters, delay, frame, measures, rowDestroyed, rowMounted } from './debug.svelte';
  import { openLive } from './rows/client';
  import { foldBox, MARK, overlap, prependedTop, rowBox, type Box } from './rows/heights';
  import { Kept } from './rows/kept.svelte';
  import { clipBlocks, revealStep } from './rows/reveal';
  import {
    activeRun as runNow,
    applyRow,
    blocksChars,
    emptyView,
    keyOf,
    prependPage,
    renderedChars,
    runStartedAt,
    type Item,
    type Row,
    type SafeMarkdownBlock,
  } from './rows/store';
  import { shouldLoadOlder, viewUncovered } from './session/scroll';
  import { checkoutLine, turnLine } from './session/turns';
  import { markdownOf, turnFolds } from './session/fold';
  import { endAt as endsReply, inReply, LOADING, timelineEntries, type Entry } from './session/entries';
  import { splitAttachments } from './composer/attachments';
  import type { SoakChat } from './soak';
  import Message from './chat/Message.svelte';
  import { senderLabel } from './chat/sender';
  import Plan from './chat/tools/Plan.svelte';
  import { ASK_ANSWER, TASK_STOP, TOOL_DETAIL } from './chat/tools/tool';
  import TurnFold from './chat/TurnFold.svelte';
  import SessionHeader from './session/SessionHeader.svelte';
  import TimelineItem from './session/TimelineItem.svelte';
  import Composer from './composer/Composer.svelte';
  import { ATTACHMENT_FETCH } from './composer/attachments';

  let {
    session,
    token,
    onclose,
    onchat,
    onturn,
  }: { session: Session; token: string; onclose: () => void; onchat?: (chat: SoakChat) => void; onturn?: () => void } = $props();

  // Rows per page, newest first on open; older pages load as the reader scrolls up.
  const PAGE_ROWS = 100;
  // Memory bisect only. ?plain=markdown|tools|images|prompts draws that kind of row as plain text;
  // ?plain=boxes draws every row as an empty box of its estimated height.
  const bisect = new URLSearchParams(globalThis.location?.search ?? '');
  const plain = bisect.get('plain');
  // ?layout=transform places rows with translateY instead of top; ?contain=1 contains each row and the list.
  const shift = bisect.get('layout') === 'transform';
  const contain = bisect.get('contain') === '1';

  // Everything this conversation starts is owned here and ends in onDestroy:
  // the live socket and every fetch and timer, through `owner.signal`.
  const owner = new AbortController();
  // The page keys this component by session, so session and token are fixed for its life.
  // svelte-ignore state_referenced_locally
  const { sessionId, agent, machine } = session;
  const view = $state(emptyView());
  chatOpened();
  setContext(ATTACHMENT_FETCH, (name: string) => fetchAttachment(token, sessionId, name, owner.signal));
  setContext(TOOL_DETAIL, (itemId: string) => toolDetail(token, sessionId, itemId, owner.signal));
  setContext(ASK_ANSWER, (askId: string, answer: Record<string, unknown>) => answerAsk(token, sessionId, askId, answer, owner.signal));
  setContext(TASK_STOP, (taskId: string) => stopTask(token, sessionId, taskId, owner.signal));
  // Replies and the header name the session's agent, from its machine's list, or this computer's when that one
  // doesn't answer (both name the agents alike); its id until one does.
  let speaker = $state(agent);
  const nameIn = ({ agents }: { agents: { id: string; name: string }[] }) => agents.find((a) => a.id === agent)?.name;
  // svelte-ignore state_referenced_locally
  listAgents(token, owner.signal, machine ?? undefined)
    .then(nameIn, () => undefined)
    .then(async (name) => name ?? (machine ? nameIn(await listAgents(token, owner.signal)) : undefined))
    .then(
      (name) => name && (speaker = name),
      () => {},
    );
  // Prompts from other sessions, labelled with the session that sent them, by its session id.
  let senders = $state<Record<string, string>>({});
  $effect(() => {
    for (const item of view.items) {
      if (item.kind !== 'user' || !item.from || item.from in untrack(() => senders)) continue;
      const from = item.from;
      senders[from] = 'Another session';
      Promise.all([getSession(token, from, owner.signal), listAgents(token, owner.signal)]).then(
        ([sender, { agents }]) => (senders[from] = senderLabel(sender, agents)),
        () => {},
      );
    }
  });
  onDestroy(() => {
    chatClosed();
    owner.abort();
  });

  let notice = $state('');
  let loaded = $state(false);
  // The soak's sends wait here for the turn row that ends their run.
  const ended = new Map<string, () => void>();
  function onRow(row: Row) {
    // A message's first live delta starts its reveal, from what already shows.
    if (row.kind === 'delta' && reveal?.key !== keyOf(row)) {
      const key = keyOf(row);
      const item = view.items.findLast((i) => i.key === key);
      reveal = { key, shown: item?.kind === 'message' ? blocksChars(item.blocks) : 0 };
    }
    applyRow(view, row);
    if ((row.kind === 'delta' || row.kind === 'message') && keyOf(row) === reveal?.key && !framing) {
      lastFrame = performance.now();
      nextFrame();
    }
    if (row.kind === 'turn' && row.payload.state !== 'started' && row.runId) {
      ended.get(row.runId)?.();
      ended.delete(row.runId);
    }
  }
  // The newest page, then the live socket from its newest row on.
  async function start() {
    try {
      const page = await rowsPage(token, sessionId, null, PAGE_ROWS, owner.signal);
      prependPage(view, page.rows, page.hasBefore);
      knownRun = page.activeRunId;
      loaded = true;
      // The page is ordered by first seq, so the watermark is its highest seq.
      openLive({ token, sessionId, after: Math.max(0, ...page.rows.map((r) => r.seq)), signal: owner.signal, onRow });
    } catch (error) {
      if (!owner.signal.aborted) notice = String(error);
    }
  }
  void start();

  // The run going now (rows/store.ts activeRun). `knownRun` is the page's
  // activeRunId on open, then each run sent.
  let sending = $state(false);
  let knownRun = $state<string | null>(null);
  const activeRun = $derived(runNow(view, knownRun));
  const busy = $derived(sending || activeRun !== null);
  // The session's title can change as a run starts (from its first message) and while it runs (the agent's own),
  // so the list reads it again when a run appears and when it ends.
  $effect(() => {
    void activeRun;
    void busy;
    untrack(() => onturn?.());
  });

  async function sendRun(text: string): Promise<string> {
    sending = true;
    try {
      const { runId } = await sendText(token, sessionId, text, owner.signal);
      knownRun = runId;
      return runId;
    } finally {
      sending = false;
    }
  }
  async function send(text: string) {
    notice = '';
    // Sending always goes to the bottom, where the prompt lands, wherever the reader was.
    stick = true;
    if (scroller) scroller.scrollTop = scroller.scrollHeight;
    try {
      await sendRun(text);
    } catch (error) {
      if (!owner.signal.aborted) notice = String(error);
    }
  }
  async function stop() {
    const runId = activeRun;
    if (!runId) return;
    try {
      await stopRun(token, runId, owner.signal);
    } catch (error) {
      if (!owner.signal.aborted) notice = String(error);
    }
  }

  // What the soak drives: a send resolves when the relay's turn row ends the run.
  let soakRun: string | null = null;
  // svelte-ignore state_referenced_locally
  onchat?.({
    async sendMessage(text) {
      const runId = (soakRun = await sendRun(text));
      if (view.turn?.runId === runId && view.turn.state !== 'started') return;
      await new Promise<void>((resolve) => ended.set(runId, resolve));
    },
    stop: () => void stop(),
    get error() {
      const item = view.items.findLast((i) => i.kind === 'error' && i.runId === soakRun);
      return item?.kind === 'error' ? item.error : undefined;
    },
    renderedChars: () => renderedChars(view),
    renderedRows: () => scroller?.querySelectorAll('[data-message-id]').length ?? 0,
    loadedRows: () => view.items.length,
    domRows: () => scroller?.querySelectorAll('.row').length ?? 0,
    trackedRows: () => $virtualizer.elementsCache.size,
    scroller: () => scroller ?? null,
    olderPending: () => !loaded || view.hasBefore || loadingOlder,
    loadingOlder: () => loadingOlder,
  });

  // A live turn's clock ticks once a second until it ends.
  let live = $state<{ runId: string; seenAt: number } | null>(null);
  let now = $state(Date.now());
  $effect(() => {
    const runId = activeRun;
    // Reopened mid-turn, the clock counts from the turn's start, not from now.
    untrack(() => (live = runId ? { runId, seenAt: runStartedAt(view, runId) ?? Date.now() } : null));
    if (!runId) return;
    const stop = new AbortController();
    void tick(stop.signal);
    return () => stop.abort();
  });
  async function tick(signal: AbortSignal) {
    try {
      for (;;) {
        now = Date.now();
        await delay(1000, signal);
      }
    } catch {
      // stopped
    }
  }

  // The streaming message shows its text at a steady pace (rows/reveal.ts):
  // `reveal.shown` of its characters. The frame loop runs only while it is
  // behind, and stops for good on close.
  let reveal = $state<{ key: string; shown: number } | null>(null);
  let framing = false;
  let lastFrame = 0;
  function nextFrame() {
    if (framing || owner.signal.aborted) return;
    framing = true;
    frame(owner.signal).then(revealFrame, () => {});
  }
  function revealFrame(at: number) {
    framing = false;
    const r = reveal;
    const item = r && view.items.findLast((i) => i.key === r.key);
    if (!r || item?.kind !== 'message') return void (reveal = null);
    const target = blocksChars(item.blocks);
    r.shown = revealStep(r.shown, target, at - lastFrame);
    lastFrame = at;
    if (r.shown < target) nextFrame();
    else if (item.done) reveal = null;
  }
  const shown = (item: Item): SafeMarkdownBlock[] | undefined =>
    item.kind === 'message' && reveal?.key === item.key ? clipBlocks(item.blocks, reveal.shown) : undefined;

  // The timeline is virtualized. Each row's size is known before it is drawn
  // where the CSS fixes it and estimated otherwise (rows/heights.ts); the
  // virtualizer's one ResizeObserver measures rows once drawn and as they change.
  let scroller = $state<HTMLDivElement>();
  let scrollerWidth = $state(0);
  // The rows' content width: the 860px column less its 28px sides.
  const width = $derived(Math.min(scrollerWidth, 860) - 56);
  // What each row keeps while it is out of the DOM (rows/kept.svelte.ts), and
  // each drawn row's measured height with its margins.
  const kept = new Map<string, Kept>();
  const keptOf = (key: string) => kept.get(key) ?? kept.set(key, new Kept()).get(key)!;
  const heights = new Map<string, number>();

  // An older page loading (loadOlder below). Declared above `entries`, which
  // the `$effect.pre` below reads while the component is still starting.
  let loadingOlder = $state(false);
  // The virtual rows (session/entries.ts).
  const endAt = (i: number) => endsReply(view.items, i);
  // A finished turn folds its work under a heading above its reply (session/fold.ts).
  const folds = $derived(turnFolds(view.items, view.stats, activeRun));
  // The folds the reader opened. A turn folds by itself when it finishes: running turns have no fold.
  const openFolds = new SvelteSet<string>();
  // The first item under each heading, which leaves the reply's label to the heading.
  const headed = $derived(new Set([...folds.values()].map((f) => view.items.find((i) => f.work.has(i.key))?.key)));
  const entries = $derived(timelineEntries(view.items, folds, openFolds, loadingOlder, !!live));
  // Each drawn row's entry, found by the row's key: the virtualizer's rows catch
  // up with `entries` only after the `$effect.pre` below, so a row's index can
  // be stale (past the end once the working line goes).
  const entryOf = $derived(new Map<unknown, Entry>(entries.map((e) => [e.key, e])));
  const labelAt = (item: Item, at: number) => inReply(item) && !inReply(view.items[at - 1]) && !headed.has(item.key);
  // Copy and time go under each sent message and each finished turn's reply.
  const metaOf = (item: Item): { text: string; at: number; align: 'start' | 'end' } | null =>
    item.kind === 'user'
      ? { text: splitAttachments(item.text).text, at: item.at, align: 'end' }
      : item.kind === 'message' && item.runId && folds.get(item.runId)?.reply === item.key
        ? { text: markdownOf(item.blocks), at: item.at, align: 'start' }
        : null;

  function boxAt(i: number): Box {
    const { key, at, fold, end } = entries[i]!;
    const item = view.items[at];
    if (!item) return MARK;
    if (fold) return foldBox({ label: !inReply(view.items[at - 1]), end: !!end, open: false, checkout: false });
    const flags = {
      label: labelAt(item, at),
      end: endAt(at),
      open: kept.get(key)?.open ?? false,
      checkout: item.kind === 'user' && !!item.runId && !!checkoutLine(view.stats[item.runId]),
      meta: !!metaOf(item),
    };
    return rowBox(item, width, flags, shown(item));
  }
  // A row's size: its height less what its bottom margin shares with the next row's top.
  const overlapAt = (i: number) => (i + 1 < entries.length ? overlap(boxAt(i).bottom, boxAt(i + 1).top) : 0);
  const sizeAt = (i: number) => (heights.get(entries[i]!.key) ?? boxAt(i).h) - overlapAt(i);

  const virtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    count: 0,
    getScrollElement: () => scroller ?? null,
    estimateSize: () => 0,
    paddingStart: 24,
    paddingEnd: 8,
    overscan: 6,
    // A drawn row's size, counted in debug `measures`: `moved` when it is not
    // the size the virtualizer had.
    measureElement: (el, entry, v) =>
      untrack(() => {
        const i = v.indexFromElement(el);
        const key = entries[i]?.key;
        if (key === undefined) return 0;
        const h = entry?.borderBoxSize?.[0]?.blockSize ?? el.getBoundingClientRect().height;
        heights.set(key, h);
        const size = h - overlapAt(i);
        const was = v.itemSizeCache.get(key) ?? v.measurementsCache[i]?.size;
        measures.measured++;
        if (was !== undefined && Math.abs(size - was) >= 1) measures.moved++;
        return size;
      }),
  });
  const rows = $derived($virtualizer.getVirtualItems());
  const total = $derived($virtualizer.getTotalSize());
  function measure(node: HTMLDivElement) {
    rowMounted();
    $virtualizer.measureElement(node);
    return {
      destroy: () => {
        rowDestroyed();
        $virtualizer.measureElement(null);
      },
    };
  }

  // Before the rows are drawn: hand the virtualizer the rows. When rows
  // changed above the reader (an older page, the loading line), move the
  // offset by the estimated heights now above them less those that were, so
  // the row they are reading stays put.
  let keys: string[] = [];
  let moved = false;
  $effect.pre(() => {
    const list = entries;
    untrack(() => {
      const v = $virtualizer;
      const next = list.map((e) => e.key);
      const anchor = keys.find((k) => k !== LOADING);
      const was = anchor === undefined ? -1 : keys.indexOf(anchor);
      const now = anchor === undefined ? -1 : next.indexOf(anchor);
      // The last row that stays: rows added after it may change what its margin shares.
      const last = keys.findLast((k) => next.includes(k));
      const before = v.measurementsCache.slice(0, Math.max(0, was)).map((m) => m.size);
      const first = keys[0];
      keys = next;
      if (scroller && now >= 0 && (now !== was || next[0] !== first)) {
        v.scrollOffset = prependedTop(scroller.scrollTop, before, next.slice(0, now).map((_, i) => sizeAt(i)));
        moved = true;
      }
      v.setOptions({ count: next.length, getItemKey: (i) => next[i]!, estimateSize: (i) => untrack(() => sizeAt(i)) });
      const j = last === undefined ? -1 : next.indexOf(last);
      const h = last === undefined ? undefined : heights.get(last);
      if (h !== undefined && j >= 0 && j < next.length - 1) v.resizeItem(j, h - overlapAt(j));
    });
  });

  // The reader at the bottom stays there as rows grow, as the column-reverse
  // list did; otherwise the offset moved above is applied once the rows are in
  // the DOM. That is the virtualizer's offset: it also holds what measuring rows
  // drawn above the reader in the same update moved it by.
  let stick = true;
  // A fold opened or closed keeps its heading where it was on screen: its key and its top in the view.
  let foldAnchor: { key: string; top: number } | null = null;
  function toggleFold(key: string, index: number) {
    const heading = scroller?.querySelector(`.row[data-index="${index}"]`);
    if (scroller && heading) {
      foldAnchor = { key, top: heading.getBoundingClientRect().top - scroller.getBoundingClientRect().top };
      stick = false;
    }
    if (openFolds.has(key)) openFolds.delete(key);
    else openFolds.add(key);
  }
  $effect(() => {
    void total;
    untrack(() => {
      if (!scroller) return;
      const anchored = foldAnchor && entries.findIndex((e) => e.key === foldAnchor!.key);
      if (foldAnchor && anchored !== null && anchored >= 0) {
        scroller.scrollTop = ($virtualizer.measurementsCache[anchored]?.start ?? 0) - foldAnchor.top;
        foldAnchor = null;
      } else if (stick) scroller.scrollTop = scroller.scrollHeight;
      else if (moved) scroller.scrollTop = $virtualizer.scrollOffset!;
      moved = false;
      // The list shrinking under the reader (a fold opened onto rows measured smaller than their
      // estimates) clamps scrollTop; the window's WebKit fires no scroll event for that, and the
      // virtualizer would keep drawing rows for the old offset, past the end: a blank view.
      if (Math.abs(($virtualizer.scrollOffset ?? 0) - scroller.scrollTop) >= 1) scroller.dispatchEvent(new Event('scroll'));
      frame(owner.signal).then(coverView, () => {});
    });
  });
  // After layout: the drawn rows must cover the view, top to bottom. Where they leave a gap, the
  // virtualizer's sizes or offset are stale: measure the drawn rows again and hand it the real offset.
  function coverView() {
    if (!scroller || !entries.length) return;
    const view = scroller.getBoundingClientRect();
    const drawn = () =>
      [...scroller!.querySelectorAll<HTMLElement>('.row')].map((el) => {
        const box = el.getBoundingClientRect();
        return { start: box.top - view.top, end: box.bottom - view.top };
      });
    const listEnd = total - scroller.scrollTop;
    if (!viewUncovered(drawn(), 0, Math.min(scroller.clientHeight, listEnd))) return;
    console.warn(`timeline: drawn rows leave a gap in view (scrollTop ${Math.round(scroller.scrollTop)}, virtualizer offset ${Math.round($virtualizer.scrollOffset ?? 0)}, rows ${$virtualizer.getVirtualItems().length}); measuring again`);
    for (const el of scroller.querySelectorAll<HTMLDivElement>(".row")) $virtualizer.measureElement(el);
    scroller.dispatchEvent(new Event('scroll'));
  }

  // Older pages: asked for PREFETCH_PX before the top.
  let olderFailed = $state(false);
  function maybeLoadOlder() {
    if (olderFailed || !loaded || !scroller) return;
    if (shouldLoadOlder(scroller.scrollTop, { hasOlder: view.hasBefore, loading: loadingOlder })) void loadOlder();
  }
  async function loadOlder() {
    loadingOlder = true;
    try {
      const page = await rowsPage(token, sessionId, view.oldest, PAGE_ROWS, owner.signal);
      prependPage(view, page.rows, page.hasBefore);
    } catch (error) {
      olderFailed = true;
      if (!owner.signal.aborted) notice = String(error);
    } finally {
      loadingOlder = false;
    }
  }
  function onScroll() {
    if (!scroller) return;
    stick = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 2;
    olderFailed = false; // a scroll is the retry after a failed page
    maybeLoadOlder();
  }
  // A landed page may still leave the reader near the top: check again.
  $effect(() => {
    void [view.items.length, view.hasBefore, loaded, loadingOlder];
    untrack(maybeLoadOlder);
  });
</script>

<section class="conversation">
  <SessionHeader {session} {speaker} {onclose} />

  <div class="timeline">
    <div class="scroll" bind:this={scroller} bind:clientWidth={scrollerWidth} onscroll={onScroll}>
      <div class="list" style:height="{total}px" class:contain>
        {#each rows as row (row.key)}
          {@const entry = entryOf.get(row.key)}
          {@const at = entry?.at ?? -1}
          {@const item = view.items[at]}
          {#if plain === 'boxes'}
            <div class="row" data-index={row.index} style:top="{row.start}px" style:height="{boxAt(row.index).h}px" use:measure></div>
          {:else if !item}
            <div class="row" data-index={row.index} style:top={shift ? '0px' : `${row.start}px`} style:transform={shift ? `translateY(${row.start}px)` : null} class:contain use:measure>
              {#if row.key === LOADING}<div class="mark">Loading earlier messages…</div>
              {:else if live}<div class="mark turn">{turnLine(undefined, live.seenAt, now)}</div>{/if}
            </div>
          {:else if entry?.fold}
            <div class="row" data-index={row.index} style:top={shift ? '0px' : `${row.start}px`} style:transform={shift ? `translateY(${row.start}px)` : null} class:contain use:measure class:end={entry.end}>
              <Message role="assistant" label={!inReply(view.items[at - 1])} {speaker}><TurnFold fold={entry.fold} open={openFolds.has(entry.key)} ontoggle={() => toggleFold(entry.key, row.index)} /></Message>
            </div>
          {:else}
            {@const label = labelAt(item, at)}
            {@const k = keptOf(item.key)}
            {@const meta = metaOf(item)}
            <div class="row" data-index={row.index} style:top={shift ? '0px' : `${row.start}px`} style:transform={shift ? `translateY(${row.start}px)` : null} class:contain use:measure data-message-id={item.key} class:end={endAt(at)}>
              <TimelineItem {item} {label} kept={k} {meta} {speaker} {senders} stats={view.stats} {now} {plain} blocks={shown(item)} />
            </div>
          {/if}
        {/each}
      </div>
    </div>
  </div>

  <footer>
    {#if notice}<div class="error">{notice}</div>{/if}
    <Plan steps={view.plan?.items ?? null} running={activeRun !== null} />
    {#if !session.parentSessionId}<Composer {token} {sessionId} machine={machine ?? null} signal={owner.signal} {busy} context={view.context} onsend={send} onstop={() => void stop()} />{/if}
  </footer>
</section>

<style>
  .conversation {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
  }
  footer {
    max-width: 860px;
    width: 100%;
    margin: 0 auto;
    padding: 8px 28px 18px;
    box-sizing: border-box;
  }
  /* The old Layout's look: the 860px column, padded 24/28/8 (the top and
     bottom padding are the virtualizer's), sitting at the bottom when short. */
  .timeline {
    display: contents;
  }
  .scroll {
    flex: 1;
    overflow-y: auto;
    overflow-anchor: none;
    display: flex;
    flex-direction: column;
  }
  .list {
    position: relative;
    flex-shrink: 0;
    max-width: 860px;
    width: 100%;
    margin: auto auto 0;
  }
  .row {
    position: absolute;
    left: 28px;
    right: 28px;
  }
  .row.contain {
    contain: layout paint style;
  }
  .list.contain {
    contain: strict;
  }
  /* The end of a reply leaves the gap a message did. */
  .end {
    padding-bottom: 10px;
  }
  /* The copy-and-time line shows while the pointer is over its row or focus is inside it. */
  .row:hover :global(.message-meta),
  .row:focus-within :global(.message-meta) {
    opacity: 1;
    pointer-events: auto;
  }
  /* It sits just under the prompt's bubble or the reply's text, inside their bottom margins. */
  .row :global(.message.user + .message-meta) {
    margin: -14px 0 18px;
  }
  .row :global(.message.assistant + .message-meta) {
    margin: -6px 0 8px;
  }
  .mark {
    color: var(--muted);
    font-size: 12px;
    margin: -10px 0 18px;
    font-variant-numeric: tabular-nums;
  }
  .error {
    color: var(--danger);
    font-size: 13px;
    margin-bottom: 6px;
  }
</style>
