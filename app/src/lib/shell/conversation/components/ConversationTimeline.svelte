<script lang="ts">
  import { flushSync, onDestroy, tick, untrack, setContext } from 'svelte';
  import { createVirtualizer, type VirtualItem } from '@tanstack/svelte-virtual';
  import ArrowDown from '@lucide/svelte/icons/arrow-down';
  import Bot from '@lucide/svelte/icons/bot';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import BookOpen from '@lucide/svelte/icons/book-open';
  import Pencil from '@lucide/svelte/icons/pencil';
  import Terminal from '@lucide/svelte/icons/terminal';
  import Search from '@lucide/svelte/icons/search';
  import Sparkles from '@lucide/svelte/icons/sparkles';
  import type { AgentConversationTurnFacts } from '$lib/shell/conversation/conversationTypes.ts';
  import { anchorRowIndex, continueHistoryPaging, conversationTurnGroups, foldToolRuns, summarizeCompletedWork, toolFilePath, turnRows, type ConversationTurnGroup, type ConversationDisplayItem, type ConversationFileLinkProvenance } from '$lib/shell/conversation/conversationTimeline.ts';
  import { USER_SEND_ANCHOR_OFFSET_PX, estimateRowHeight, sendTurnRunning, type ConversationSendAnchorRequest } from '$lib/shell/conversation/conversationScrollAnchor.ts';
  import { conversationDisclosureContext, type ConversationDisclosureContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import type { ConversationViewState } from '$lib/shell/sessionWorkspaces.ts';
  import { backgroundWorkLabel, backgroundWorkTarget, formatBackgroundElapsed } from '$lib/shell/ownedSessions.ts';
  import type { BackgroundWorkItem, HistoryDownloadProgress } from '$lib/tauriSource.ts';
  import { conversationItemHasVisibleContent } from '$lib/shell/conversation/conversationItemVisibility.ts';
  import { setConversationTimelineDiagnostics } from '$lib/shell/resourceDiagnostics.svelte';
  import { Button } from '$lib/components/ui/button/index.js';
  import TimelineItem from './TimelineItem.svelte';
  import ConversationToolGroup from './ConversationToolGroup.svelte';
  import ConversationTurnElapsed from './ConversationTurnElapsed.svelte';
  import TurnFileCard from './TurnFileCard.svelte';
  import PendingFirstMessage from './PendingFirstMessage.svelte';
  import WorkingSpinner from '../../components/feedback/WorkingSpinner.svelte';

  interface Props {
    items: readonly ConversationDisplayItem[];
    conversationId: string;
    historyOwnedId: string;
    renderWindowId?: string;
    timelineRevision: number;
    viewState: ConversationViewState;
    onViewChange(ownedId: string, historyOwnedId: string, view: ConversationViewState): void;
    itemFirstSequence(itemId: string): number | undefined;
    anchorRequest?: ConversationSendAnchorRequest | null;
    activeTurnId?: string | null;
    turnFacts?: readonly AgentConversationTurnFacts[];
    localTurnActive?: boolean;
    activityLabel?: string | null;
    /** Sub-agents and commands still running between turns, shown in the activity line's place. */
    backgroundWork?: readonly BackgroundWorkItem[];
    onOpenChild?(childId: string): void;
    composerHeight?: number;
    assistantLabel?: string;
    emptyText?: string;
    pendingFirstMessage?: string | null;
    hasOlder?: boolean;
    loadingOlder?: boolean;
    pageError?: string;
    onLoadOlder?(): void;
    hasNewer?: boolean;
    loadingNewer?: boolean;
    onLoadNewer?(): void;
    oldestSequence?: number;
    newestSequence?: number;
    onJumpToLatest?(): void | Promise<void>;
    onApprovalDecision?(requestId: string, decision: string): void;
    onFileLink?(path: string, provenance?: ConversationFileLinkProvenance): void;
    onPlanOpen?(): void;
    /** This history's download from its remote machine, if one is running. */
    historyDownload?: HistoryDownloadProgress | null;
  }
  let {
    items, conversationId, historyOwnedId, renderWindowId = conversationId, timelineRevision,
    viewState, onViewChange, itemFirstSequence, anchorRequest = null, activeTurnId = null,
    turnFacts = [], localTurnActive = false, activityLabel = null, backgroundWork, onOpenChild, composerHeight = 0,
    assistantLabel = 'Assistant', emptyText = 'Start the conversation below.', pendingFirstMessage = null,
    hasOlder = false, loadingOlder = false, pageError = '', onLoadOlder, hasNewer = false, loadingNewer = false,
    onLoadNewer, oldestSequence = 0, newestSequence = 0, onJumpToLatest, onApprovalDecision,
    onFileLink, onPlanOpen, historyDownload = null
  }: Props = $props();

  let host = $state<HTMLDivElement | null>(null);
  let hostHeight = $state(0);
  let follow = $state(true);
  let disclosures = $state<Record<string, boolean>>({});
  let openedWindow = '';
  let openedOwnedId = '';
  let openedHistoryId = '';
  let restoring = $state(true);
  let savedAnchor: ConversationViewState['anchor'];
  let jumping = $state(false);
  let seenSendRequest = 0;
  let anchoredSendItemId = $state<string | null>(null);
  let userDirection: 'older' | 'newer' | null = null;
  let dragging = false;
  let lastScrollTop = 0;
  let pendingSendAnchor = $state<ConversationSendAnchorRequest | null>(null);
  let pageRequest: { older: boolean; edge: number; windowId: string; height: number } | null = null;
  const renderedItems = $derived(items.filter(conversationItemHasVisibleContent));
  const groups = $derived(conversationTurnGroups(renderedItems, activeTurnId, turnFacts));
  // A boolean, so a child's progress update does not rebuild the rows.
  const hasBackgroundWork = $derived(!!backgroundWork?.length);
  const download = $derived(historyDownload?.phase === 'complete' ? null : historyDownload);
  const downloadPercent = $derived(download?.totalBytes ? Math.floor(download.coveredBytes * 100 / download.totalBytes) : 0);

  type Row = {
    key: string;
    anchorItemId?: string;
    item?: ConversationDisplayItem;
    group?: ConversationTurnGroup;
    run?: Extract<ConversationDisplayItem, { kind: 'toolRun' }>;
    edit?: { path: string; added: number; removed: number };
    activity?: boolean;
    workSummary?: string;
    folded?: boolean;
  };
  // Retain a run's identity when older calls are prepended to that same run.
  let previousRunWindow = '';
  let previousRuns: { id: string; nativeItemIds: readonly string[] }[] = [];
  function runOpen(run: Extract<ConversationDisplayItem, { kind: 'toolRun' }>): boolean {
    return disclosures[`${run.itemId}:open`]
      ?? (run.turnId === activeTurnId && run.items.at(-1)?.itemId === renderedItems.at(-1)?.itemId);
  }
  // A finished edit's diff is counted once, not on every chunk. Declared before `rows`, which reads it during setup.
  const diffCounts = new WeakMap<ConversationDisplayItem, { added: number; removed: number }>();
  const rows = $derived.by((): Row[] => {
    if (previousRunWindow !== renderWindowId) {
      previousRunWindow = renderWindowId;
      previousRuns = [];
    }
    const result: Row[] = [];
    const nextRuns: typeof previousRuns = [];
    const claimed = new Set<string>();
    const foldedGroups = groups.map((group) => ({ group, items: foldToolRuns(group.items) }));
    const seeds = new Set(foldedGroups.flatMap(({ items }) =>
      items.filter((item) => item.kind === 'toolRun').map((item) => item.itemId)));
    // Another turn's rows can split a turn into two groups; each part needs its own row keys.
    const turnParts = new Map<string | null, number>();
    for (const { group, items: groupedItems } of foldedGroups) {
      const part = turnParts.get(group.turnId) ?? 0;
      turnParts.set(group.turnId, part + 1);
      const turnKey = part === 0 ? group.turnId : `${group.turnId}:${part}`;
      const summary = group.completed ? summarizeCompletedWork(group.items) : null;
      const opened = disclosures[`turn:${group.turnId}:open`];
      for (const item of turnRows(group, groupedItems, opened)) {
        if (item === 'heading') {
          result.push({ key: `turn-work:${turnKey}`, group, workSummary: summary ?? '',
            folded: group.completed && group.workItemIds.length ? opened !== true : undefined });
          continue;
        }
        let run: Row['run'];
        if (item.kind === 'toolRun') {
          const nativeItemIds = item.items.map((child) => child.itemId);
          const nativeIds = new Set(nativeItemIds);
          const previous = previousRuns.find((candidate) => !claimed.has(candidate.id)
            && (candidate.id === item.itemId || !seeds.has(candidate.id))
            && candidate.nativeItemIds.some((id) => nativeIds.has(id)));
          const id = previous?.id ?? item.itemId;
          claimed.add(id);
          nextRuns.push({ id, nativeItemIds });
          run = { ...item, itemId: id, turnId: group.turnId };
        }
        if (run) {
          result.push({ key: run.itemId, group, run, anchorItemId: run.items[0]?.itemId });
        } else {
          result.push({ key: item.itemId, anchorItemId: item.itemId, group, item });
        }
      }
      if (group.completed) {
        const edits = getTurnFileEdits(group);
        for (const edit of edits) result.push({ key: `turn-file:${turnKey}:${edit.path}`, group, edit });
      }
    }
    previousRuns = nextRuns;
    if (activityLabel || hasBackgroundWork) result.push({ key: `activity:${result.at(-1)?.key ?? 'empty'}`, activity: true });
    return result;
  });
  // A new key function makes the virtualizer rebuild every row position, so these are replaced only when row keys
  // change. Each pair reads its own rows: the virtualizer reads its old layout through the old pair while switching.
  const rowFunctions = (rows: Row[]) => ({ key: (index: number) => rows[index].key, size: (index: number) => estimateRowHeight(rows[index]) });
  let keyedRows: Row[] = [];
  let rowFns = rowFunctions(keyedRows);
  const virtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    count: 0, getScrollElement: () => host, estimateSize: () => 90, overscan: 3,
    anchorTo: 'end', followOnAppend: false, scrollEndThreshold: -1
  });
  const virtualRows = $derived($virtualizer.getVirtualItems());
  const totalSize = $derived($virtualizer.getTotalSize());
  const viewportHeight = $derived($virtualizer.scrollRect?.height ?? 0);
  // Work deferred to after the next render stops when the timeline unmounts. An effect
  // re-run must not cancel it: the send anchor's effect re-runs as it clears its request.
  const stop = new AbortController();
  onDestroy(() => { stop.abort(); cancelAnimationFrame(viewFrame); });

  $effect.pre(() => {
    const currentRows = rows;
    const element = host;
    const following = follow && !hasNewer && !restoring && !jumping;
    const topInset = element ? Number.parseFloat(getComputedStyle(element).getPropertyValue('--center-head-height')) || 0 : 0;
    const footer = Math.max(composerHeight, 120) + 60;
    const paddingEnd = anchoredSendItemId ? Math.max(viewportHeight, footer) : footer;
    untrack(() => {
      const keys = new Set(currentRows.map((row) => row.key));
      // An older page can rename the row at the top (a turn's heading takes its
      // id from the first item loaded), and the virtualizer then drops its own
      // place-keeping. Note the first visible row that survives the insert.
      const top = $virtualizer.scrollOffset ?? 0;
      const anchor = pageRequest?.older && $virtualizer.options.count
        ? $virtualizer.getVirtualItemForOffset(top)
        : undefined;
      const kept = anchor && !keys.has(String(anchor.key))
        ? $virtualizer.getVirtualItems().find((row) => row.end > top && keys.has(String(row.key)))
        : undefined;
      if (currentRows.some((row, index) => row.key !== keyedRows[index]?.key)) rowFns = rowFunctions(keyedRows = currentRows);
      $virtualizer.setOptions({
        count: currentRows.length, getScrollElement: () => element, getItemKey: rowFns.key, estimateSize: rowFns.size,
        paddingStart: topInset, paddingEnd,
        followOnAppend: following ? 'smooth' : false,
        scrollEndThreshold: following ? 80 : -1
      });
      // Put it back once the taller page is in the DOM, in one write as the
      // virtualizer's own restore does, so the reader can keep scrolling.
      if (kept) void restoreKeptRow(stop.signal, renderWindowId, kept, top);
      for (const key of $virtualizer.itemSizeCache.keys()) {
        if (!keys.has(String(key))) $virtualizer.itemSizeCache.delete(key);
      }
    });
  });

  async function restoreKeptRow(signal: AbortSignal, windowId: string, kept: VirtualItem, top: number): Promise<void> {
    await tick();
    if (signal.aborted || windowId !== renderWindowId) return;
    const row = $virtualizer.measurementsCache.find((item) => item.key === kept.key);
    if (row) $virtualizer.scrollToOffset(row.start + top - kept.start);
  }

  function atEnd(): boolean { return $virtualizer.isAtEnd(80); }
  function scrollToEnd(): void { $virtualizer.scrollToEnd(); }
  function positionRow(key: string, offsetPx: number, behavior: ScrollBehavior = 'auto'): boolean {
    const index = rows.findIndex((row) => row.key === key);
    if (index < 0) return false;
    $virtualizer.setOptions({ scrollPaddingStart: offsetPx });
    $virtualizer.scrollToIndex(index, { align: 'start', behavior });
    return true;
  }
  function measureRow(node: HTMLDivElement) {
    $virtualizer.measureElement(node);
    return { destroy() { $virtualizer.measureElement(null); } };
  }

  setContext<ConversationDisclosureContext>(conversationDisclosureContext, {
    get ownedId() { return historyOwnedId; }, get(key) { return disclosures[key]; },
    set(key, open) { if (disclosures[key] === open) return; follow = false; disclosures = { ...disclosures, [key]: open }; saveView(); }
  });

  function countDiffLines(item: ConversationDisplayItem, diffText: string): { added: number; removed: number } {
    const known = diffCounts.get(item);
    if (known) return known;
    const counts = { added: 0, removed: 0 };
    for (const line of diffText.split('\n')) {
      if (line.startsWith('+') && !line.startsWith('++')) counts.added++;
      else if (line.startsWith('-') && !line.startsWith('--')) counts.removed++;
    }
    diffCounts.set(item, counts);
    return counts;
  }

  function getTurnFileEdits(group: ConversationTurnGroup): { path: string; added: number; removed: number }[] {
    const edits: { path: string; added: number; removed: number }[] = [];
    const seenPaths = new Set<string>();

    for (const item of group.items) {
      if (item.kind === 'fileEdits') {
        for (const edit of item.edits) {
          if (!seenPaths.has(edit.path)) {
            seenPaths.add(edit.path);
            edits.push({ path: edit.path, added: edit.added, removed: edit.removed });
          }
        }
      } else if (item.kind === 'tool' && item.state === 'completed' && item.toolKind === 'file-edit') {
        const path = toolFilePath(item);
        if (path && !seenPaths.has(path)) {
          seenPaths.add(path);
          edits.push({ path, ...(item.diff || item.added == null ? countDiffLines(item, item.diff ?? '') : { added: item.added, removed: item.removed ?? 0 }) });
        }
      } else if (item.kind === 'file' && item.completed) {
        const p = typeof item.metadata?.path === 'string' ? item.metadata.path : '';
        if (p && !seenPaths.has(p)) {
          seenPaths.add(p);
          edits.push({ path: p, ...countDiffLines(item, typeof item.metadata?.diff === 'string' ? item.metadata.diff : item.text) });
        }
      } else if (item.kind === 'toolRun') {
        for (const sub of item.items) {
          if (sub.kind === 'fileEdits') {
            for (const edit of sub.edits) {
              if (!seenPaths.has(edit.path)) {
                seenPaths.add(edit.path);
                edits.push({ path: edit.path, added: edit.added, removed: edit.removed });
              }
            }
          } else if (sub.kind === 'file') {
            const p = typeof sub.metadata?.path === 'string' ? sub.metadata.path : '';
            if (p && !seenPaths.has(p)) {
              seenPaths.add(p);
              edits.push({ path: p, ...countDiffLines(sub, typeof sub.metadata?.diff === 'string' ? sub.metadata.diff : sub.text) });
            }
          }
        }
      }
    }
    return edits;
  }

  function captureAnchor(): ConversationViewState['anchor'] {
    if (!host) return savedAnchor;
    const viewportTop = host.getBoundingClientRect().top;
    const candidates = [...host.querySelectorAll<HTMLElement>('[data-anchor-item-id]')];
    const node = candidates.find((candidate) => candidate.getBoundingClientRect().bottom > viewportTop);
    const itemId = node?.dataset.anchorItemId;
    const firstSequence = itemId ? itemFirstSequence(itemId) : undefined;
    return node && itemId && firstSequence !== undefined
      ? { itemId, firstSequence, offsetPx: node.getBoundingClientRect().top - viewportTop }
      : savedAnchor;
  }

  function saveView(): void {
    if (restoring || !openedOwnedId || openedWindow !== renderWindowId) return;
    savedAnchor = captureAnchor();
    onViewChange(openedOwnedId, openedHistoryId, {
      followLatest: follow, ...(savedAnchor ? { anchor: savedAnchor } : {}),
      expandedTurns: {}, ...(Object.keys(disclosures).length ? { disclosures: { ...disclosures } } : {})
    });
  }

  $effect(() => {
    const windowId = renderWindowId;
    if (openedWindow === windowId) return;
    openedWindow = windowId;
    openedOwnedId = conversationId;
    openedHistoryId = historyOwnedId;
    userDirection = null;
    dragging = false;
    lastScrollTop = 0;
    const saved = untrack(() => viewState);
    follow = saved.followLatest;
    disclosures = { ...saved.disclosures };
    savedAnchor = saved.anchor;
    restoring = true;
    pendingSendAnchor = null;
    // A view saved with its row pinned to the top keeps the room below that
    // pinning needs; without it the saved place is past the end of the list,
    // the restore lands short and the row ends up under the composer.
    anchoredSendItemId = saved.followLatest ? null : saved.anchor?.itemId ?? null;
    pageRequest = null;
    jumping = false;
    seenSendRequest = anchorRequest?.requestId ?? 0;
    untrack(() => $virtualizer.measure());
  });

  // A new session's first message is sent before this timeline exists, so no
  // send anchor reaches it; anchor its row the same way, or following the reply
  // scrolls the message off the top.
  let firstSendWindow = '';
  $effect(() => {
    const currentRows = rows;
    if (!pendingFirstMessage || firstSendWindow === renderWindowId) return;
    const firstUser = currentRows.find((row) => row.item?.kind === 'user');
    if (!firstUser) return;
    firstSendWindow = renderWindowId;
    pendingSendAnchor = { requestId: 0, conversationId, userItemId: firstUser.key };
    follow = false;
  });

  $effect(() => {
    void composerHeight;
    // A shorter window keeps the latest line above the composer too.
    void hostHeight;
    if (!follow || hasNewer || restoring || jumping || !host) return;
    void followAfterRender(stop.signal, renderWindowId);
  });
  async function followAfterRender(signal: AbortSignal, windowId: string): Promise<void> {
    await tick();
    if (signal.aborted || windowId !== renderWindowId) return;
    if (follow && !hasNewer && !restoring && !jumping) scrollToEnd();
  }

  function restoreAnchor(anchor: NonNullable<ConversationViewState['anchor']>): boolean {
    const index = anchorRowIndex(rows, anchor.itemId);
    return index >= 0 && positionRow(rows[index].key, anchor.offsetPx);
  }

  $effect(() => {
    if (!restoring || !host || rows.length === 0) return;
    void restoreView(stop.signal, renderWindowId);
  });
  async function restoreView(signal: AbortSignal, windowId: string): Promise<void> {
    await tick();
    if (signal.aborted || windowId !== renderWindowId || !restoring) return;
    if (follow) scrollToEnd();
    else if (savedAnchor && !restoreAnchor(savedAnchor)) {
      // Rows appear only once the saved place's page is admitted, so an anchor
      // missing from them never arrives. Open at the latest instead of staying
      // stuck in restore, which would also block paging and saving the view.
      savedAnchor = undefined;
      follow = true;
      scrollToEnd();
    }
    // Restore after the admitted content has mounted.
    await tick();
    if (signal.aborted || windowId !== renderWindowId || !restoring) return;
    if (follow) scrollToEnd();
    else if (savedAnchor) restoreAnchor(savedAnchor);
    restoring = false;
    saveView();
  }

  function toggleRun(run: NonNullable<Row['run']>): void {
    const key = run.itemId;
    const node = host?.querySelector<HTMLElement>(`[data-run-id="${CSS.escape(key)}"]`);
    const offsetPx = node && host ? node.getBoundingClientRect().top - host.getBoundingClientRect().top : null;
    follow = false;
    disclosures = { ...disclosures, [`${key}:open`]: !runOpen(run) };
    const windowId = renderWindowId;
    flushSync();
    if (windowId !== renderWindowId) return;
    const index = rows.findIndex((row) => row.key === key);
    if (index >= 0 && offsetPx !== null) positionRow(rows[index].key, offsetPx);
    saveView();
  }

  function toggleTurn(group: ConversationTurnGroup): void {
    const key = rows.find((row) => row.group === group && row.workSummary !== undefined)?.key ?? '';
    const node = host?.querySelector<HTMLElement>(`[data-row-key="${CSS.escape(key)}"]`);
    const offsetPx = node && host ? node.getBoundingClientRect().top - host.getBoundingClientRect().top : null;
    follow = false;
    disclosures = { ...disclosures, [`turn:${group.turnId}:open`]: disclosures[`turn:${group.turnId}:open`] !== true };
    const windowId = renderWindowId;
    flushSync();
    if (windowId !== renderWindowId) return;
    if (offsetPx !== null) positionRow(key, offsetPx);
    saveView();
  }
  /** A command link: open its folded run if need be, then bring its row to the top. */
  function showToolItem(itemId: string): void {
    const findRow = () => rows.find((candidate) => candidate.key === itemId
      || candidate.run?.items.some((item) => item.itemId === itemId));
    const row = findRow();
    if (!row) {
      // A finished turn keeps its work folded; open that turn, then look again.
      const group = groups.find((candidate) => candidate.items.some((item) => item.itemId === itemId));
      if (!group || disclosures[`turn:${group.turnId}:open`] === true) return;
      disclosures = { ...disclosures, [`turn:${group.turnId}:open`]: true };
      flushSync();
      if (findRow()) showToolItem(itemId);
      return;
    }
    follow = false;
    if (row.run && !runOpen(row.run)) disclosures = { ...disclosures, [`${row.run.itemId}:open`]: true };
    const windowId = renderWindowId;
    flushSync();
    if (windowId !== renderWindowId) return;
    const topInset = host ? Number.parseFloat(getComputedStyle(host).getPropertyValue('--center-head-height')) || 0 : 0;
    if (positionRow(row.key, topInset + USER_SEND_ANCHOR_OFFSET_PX)) saveView();
  }
  function openBackgroundWork(work: BackgroundWorkItem): void {
    const target = backgroundWorkTarget(work);
    if (target.kind === 'child') onOpenChild?.(target.childId);
    else showToolItem(target.itemId);
  }
  /* One clock for the background line's elapsed times. It runs only while the
     line is on screen and the window is visible; the line leaving the render
     window unmounts it and stops the clock. */
  let backgroundNow = $state(Date.now());
  function backgroundClock(node: HTMLElement) {
    let onScreen = false;
    let timer: ReturnType<typeof setInterval> | null = null;
    const sync = (): void => {
      const run = onScreen && document.visibilityState === 'visible';
      if (run && !timer) {
        backgroundNow = Date.now();
        timer = setInterval(() => { backgroundNow = Date.now(); }, 1000);
      } else if (!run && timer) {
        clearInterval(timer);
        timer = null;
      }
    };
    const observer = new IntersectionObserver(([entry]) => { onScreen = entry.isIntersecting; sync(); });
    observer.observe(node);
    document.addEventListener('visibilitychange', sync);
    return {
      destroy() {
        observer.disconnect();
        document.removeEventListener('visibilitychange', sync);
        if (timer) clearInterval(timer);
      }
    };
  }

  let disclosureWindow = '';
  let previousLiveTail: string | undefined;
  let previousActiveTurn: string | null = null;
  $effect(() => {
    const tail = renderedItems.at(-1)?.itemId;
    const currentRows = rows;
    const turn = activeTurnId;
    if (disclosureWindow === renderWindowId && previousActiveTurn
      && (tail !== previousLiveTail || turn !== previousActiveTurn)) {
      const finishedRuns = currentRows.filter((row) => row.run?.completed
        && row.group?.turnId === previousActiveTurn
        && (turn !== previousActiveTurn || row.run.items.at(-1)?.itemId !== tail));
      untrack(() => {
        const next = { ...disclosures };
        let changed = false;
        for (const { run } of finishedRuns) {
          const key = `${run!.itemId}:open`;
          if (next[key]) { next[key] = false; changed = true; }
        }
        if (changed) disclosures = next;
      });
    }
    disclosureWindow = renderWindowId;
    previousLiveTail = tail;
    previousActiveTurn = turn;
  });

  function requestPage(older: boolean): void {
    if (!host || restoring || jumping || pageRequest) return;
    if (older ? !hasOlder || loadingOlder || host.scrollTop > 80 : !hasNewer || loadingNewer || !atEnd()) return;
    pageRequest = { older, edge: older ? oldestSequence : newestSequence, windowId: renderWindowId,
      height: host.scrollHeight };
    if (older) onLoadOlder?.(); else onLoadNewer?.();
  }

  $effect(() => {
    if (loadingOlder || loadingNewer || !pageRequest) return;
    const request = pageRequest;
    const landed = request.windowId === renderWindowId && (request.older ? oldestSequence < request.edge : newestSequence > request.edge);
    pageRequest = null;
    // Folded work can add no visible height: keep reading until a visible page arrives.
    if (landed) void continuePaging(stop.signal, request);
  });
  async function continuePaging(signal: AbortSignal, request: NonNullable<typeof pageRequest>): Promise<void> {
    await tick();
    if (signal.aborted || request.windowId !== renderWindowId) return;
    if (host && continueHistoryPaging(true,
      request.older ? host.scrollTop <= 80 : atEnd(), request.height, host.scrollHeight)) requestPage(request.older);
  }

  function handleScroll(): void {
    if (!host || restoring || jumping) return;
    const direction = dragging ? (host.scrollTop < lastScrollTop ? 'older' : 'newer') : userDirection;
    lastScrollTop = host.scrollTop;
    if (direction === 'older') follow = false;
    else if (direction === 'newer' && !hasNewer && atEnd()) follow = true;
    userDirection = null;
    // Wheel events fire before the scroll they cause, so a gesture that coasts
    // onto the end of the loaded rows asks for nothing; ask once it lands there.
    scheduleViewWork(direction === 'older');
  }
  // Saving the place and asking for a page read the layout, so they run once a frame, after its
  // scrolls; the frame's last event says which way to page.
  let viewFrame = 0, frameOlder = false;
  function scheduleViewWork(older: boolean): void {
    frameOlder = older;
    viewFrame ||= requestAnimationFrame(() => { viewFrame = 0; saveView(); requestPage(frameOlder); });
  }
  function handleWheel(event: WheelEvent): void {
    if (event.target instanceof Element && event.target.closest('[data-tool-scroll]')) return;
    anchoredSendItemId = null;
    userDirection = event.deltaY < 0 ? 'older' : 'newer';
    if (event.deltaY < 0) follow = false;
    else if (event.deltaY > 0 && !hasNewer && atEnd()) follow = true;
    if (event.deltaY) scheduleViewWork(event.deltaY < 0);
  }

  function readerInput(node: HTMLDivElement) {
    const interactive = 'input, textarea, select, button, a, [contenteditable], [role="button"]';
    const pointerDown = (event: PointerEvent) => {
      if (event.target instanceof Element && event.target.closest('[data-tool-scroll]')) return;
      if (!(event.target instanceof Element && event.target.closest(interactive))) {
        node.focus({ preventScroll: true });
      }
      if (event.target !== node) return;
      dragging = true;
      lastScrollTop = node.scrollTop;
      anchoredSendItemId = null;
      follow = false;
    };
    const pointerUp = () => { dragging = false; };
    const touchStart = (event: TouchEvent) => {
      if (event.target instanceof Element && event.target.closest('[data-tool-scroll]')) return;
      dragging = true;
      lastScrollTop = node.scrollTop;
      anchoredSendItemId = null;
      follow = false;
    };
    const keydown = (event: KeyboardEvent) => {
      if (event.target instanceof Element && event.target.closest('[data-tool-scroll]')) return;
      if (event.target instanceof Element && event.target.closest(interactive)) return;
      const older = ['ArrowUp', 'PageUp', 'Home'].includes(event.key) || (event.key === ' ' && event.shiftKey);
      const newer = ['ArrowDown', 'PageDown', 'End'].includes(event.key) || (event.key === ' ' && !event.shiftKey);
      if (!older && !newer) return;
      anchoredSendItemId = null;
      userDirection = older ? 'older' : 'newer';
      if (older) follow = false;
      scheduleViewWork(older);
    };
    node.addEventListener('pointerdown', pointerDown);
    node.addEventListener('touchstart', touchStart, { passive: true });
    window.addEventListener('pointerup', pointerUp);
    window.addEventListener('touchend', pointerUp);
    node.addEventListener('keydown', keydown);
    node.addEventListener('wheel', handleWheel, { passive: true }); // Scrolling never waits for it.
    return { destroy() {
      node.removeEventListener('pointerdown', pointerDown);
      node.removeEventListener('touchstart', touchStart);
      window.removeEventListener('pointerup', pointerUp);
      window.removeEventListener('touchend', pointerUp);
      node.removeEventListener('keydown', keydown);
      node.removeEventListener('wheel', handleWheel);
    } };
  }

  async function jumpToLatest(): Promise<void> {
    if (jumping) return;
    const windowId = renderWindowId;
    jumping = true;
    anchoredSendItemId = null;
    pageRequest = null; userDirection = null; // A wheel at the top left a direction no scroll consumed.
    try {
      if (hasNewer) await onJumpToLatest?.();
      if (windowId !== renderWindowId) return;
      follow = true;
      savedAnchor = undefined;
      flushSync();
      if (windowId !== renderWindowId) return;
      scrollToEnd();
    } finally {
      if (windowId === renderWindowId) { jumping = false; saveView(); }
    }
  }

  $effect(() => {
    if (!anchorRequest || anchorRequest.conversationId !== conversationId || anchorRequest.requestId === seenSendRequest) return;
    seenSendRequest = anchorRequest.requestId;
    pendingSendAnchor = anchorRequest;
    follow = false;
  });
  $effect(() => {
    void timelineRevision;
    if (!pendingSendAnchor) return;
    const itemId = pendingSendAnchor.userItemId;
    if (!rows.some((row) => row.key === itemId)) return;
    pendingSendAnchor = null;
    anchoredSendItemId = itemId;
    void anchorSentRow(stop.signal, renderWindowId, itemId);
  });
  async function anchorSentRow(signal: AbortSignal, windowId: string, itemId: string): Promise<void> {
    await tick();
    if (signal.aborted || windowId !== renderWindowId || anchoredSendItemId !== itemId) return;
    const index = rows.findIndex((row) => row.key === itemId);
    if (index < 0) return;
    const topInset = host ? Number.parseFloat(getComputedStyle(host).getPropertyValue('--center-head-height')) || 0 : 0;
    positionRow(itemId, topInset + USER_SEND_ANCHOR_OFFSET_PX, 'smooth');
    saveView();
  }

  // When the turn ends, give back the screen of room the send anchor held under
  // the reply, then follow again if nothing is left below: a short reply
  // settles above the composer and the Jump to latest arrow goes.
  let turnWindow = '';
  let turnWasRunning = false;
  $effect(() => {
    const running = sendTurnRunning(localTurnActive, activeTurnId);
    const ended = turnWindow === renderWindowId && turnWasRunning && !running;
    turnWindow = renderWindowId;
    turnWasRunning = running;
    if (!ended || !untrack(() => anchoredSendItemId)) return;
    anchoredSendItemId = null;
    void followAfterTurn(stop.signal, renderWindowId);
  });
  async function followAfterTurn(signal: AbortSignal, windowId: string): Promise<void> {
    await tick();
    if (signal.aborted || windowId !== renderWindowId || restoring || jumping || hasNewer || !atEnd()) return;
    follow = true;
    saveView();
  }

  $effect(() => {
    void timelineRevision;
    if (!restoring) untrack(saveView);
  });
  $effect(() => {
    setConversationTimelineDiagnostics(rows.length, virtualRows.length, $virtualizer.elementsCache.size, $virtualizer.itemSizeCache.size);
    return () => setConversationTimelineDiagnostics(0, 0, 0, 0);
  });

</script>

<div class="timeline-wrap" data-testid="conversation-timeline-wrap" style={`--composer-height:${composerHeight}px`}>
  {#if pageError}<p role="alert" class="px-2 py-2 text-sm text-muted-foreground">{pageError}</p>{/if}
  {#if download?.phase === 'error'}<p role="alert" class="px-2 py-2 text-sm text-muted-foreground">History download stopped: {download.error}</p>{/if}
  {#if loadingOlder}<p class="older-loading" data-testid="conversation-older-loading" role="status"><WorkingSpinner size={12} />Loading earlier messages</p>
  {:else if download?.ready && download.phase !== 'error'}<p class="older-loading" role="status"><span class="download-line">{download.phase === 'paused' ? 'History download paused · reconnecting' : `Downloading earlier messages · ${downloadPercent}%`}</span></p>{/if}
  <div class="timeline-scroll" tabindex="0" data-testid="conversation-timeline-scroll" bind:this={host} bind:clientHeight={hostHeight} onscroll={handleScroll} onmousedown={(event) => { if (event.target === event.currentTarget && event.offsetX < event.currentTarget.clientWidth) event.preventDefault(); }} use:readerInput>
    {#if renderedItems.length === 0}
      {#if pendingFirstMessage}<PendingFirstMessage text={pendingFirstMessage} seed={conversationId} />{:else}<p class="empty" data-testid="conversation-timeline-empty">{emptyText}</p>{/if}
    {/if}
    <div class="timeline-list" data-testid="conversation-timeline-list" style:height={`${totalSize}px`}>
      {#each virtualRows as virtualRow (virtualRow.key)}
        {@const row = rows[virtualRow.index]}
          <div class="turn-row" data-index={virtualRow.index} style:top={`${virtualRow.start}px`} use:measureRow class:compact-tool={row.item?.kind === 'tool' || !!row.run} data-row-key={row.key} data-anchor-item-id={row.anchorItemId} data-turn-id={row.group?.turnId} data-testid="conversation-timeline-row">
            {#if row.run}
              <button class="run-header" data-run-id={row.run.itemId} type="button" aria-expanded={runOpen(row.run)} onclick={() => toggleRun(row.run!)}>
                <span class="run-icon" aria-hidden="true">
                  {#if row.run.icon === 'pencil'}<Pencil size={13} />
                  {:else if row.run.icon === 'book'}<BookOpen size={13} />
                  {:else if row.run.icon === 'terminal'}<Terminal size={13} />
                  {:else if row.run.icon === 'search'}<Search size={13} />
                  {:else}<Sparkles size={13} />{/if}
                </span>
                <span class="run-summary">{row.run.summary}</span>
                <span class="turn-fold-chevron" class:open={runOpen(row.run)} aria-hidden="true"><ChevronRight size={13} /></span>
              </button>
              {#if runOpen(row.run)}
                <ConversationToolGroup items={row.run.items} active={!!row.group?.running}
                  {assistantLabel} {onApprovalDecision} {onFileLink} {onPlanOpen} />
              {/if}
            {/if}
            {#if row.workSummary !== undefined && row.group}
              <div class="work-summary" data-testid="conversation-work-summary">
                {#if row.folded !== undefined}
                  <button class="run-header work-toggle" type="button" aria-expanded={!row.folded} onclick={() => toggleTurn(row.group!)}>
                    <span class="work-duration"><ConversationTurnElapsed running={row.group.running} completed={row.group.completed} startedAtMs={row.group.startedAtMs} elapsedMs={row.group.elapsedMs} /></span>
                    <span class="run-summary">{row.workSummary}</span>
                    <span class="turn-fold-chevron" class:open={!row.folded} aria-hidden="true"><ChevronRight size={13} /></span>
                  </button>
                {:else}
                  <span class="work-duration"><ConversationTurnElapsed running={row.group.running} completed={row.group.completed} startedAtMs={row.group.startedAtMs} elapsedMs={row.group.elapsedMs} /></span>
                  {#if row.workSummary}<span>{row.workSummary}</span>{/if}
                {/if}
              </div>
            {/if}
            {#if row.item}<TimelineItem item={row.item} {assistantLabel} {onApprovalDecision} {onFileLink} {onPlanOpen} />{/if}
            {#if row.edit}<TurnFileCard path={row.edit.path} added={row.edit.added} removed={row.edit.removed} onReview={onFileLink} />{/if}
            {#if row.activity && activityLabel}<div class="working-row" data-testid="conversation-working-indicator" role="status"><WorkingSpinner seed={conversationId} /><span>{activityLabel}…</span></div>
            {:else if row.activity && backgroundWork?.length}
              <div class="working-row background-row" data-testid="conversation-background-work" role="group" aria-label="Running in the background" use:backgroundClock>
                <WorkingSpinner seed={conversationId} />
                {#each backgroundWork as work, index (work.id)}
                  {#if index > 0}<span class="background-sep" aria-hidden="true">·</span>{/if}
                  <span class="background-item">
                    <Button variant="ghost" size="xs" iconPosition="start" class="min-w-0 text-[13px] font-normal text-muted-foreground"
                      title={work.kind === 'subagent' ? 'Open this sub-agent in the Agents panel' : 'Show this command in the conversation'}
                      onclick={() => openBackgroundWork(work)}>
                      {#if work.kind === 'subagent'}<Bot data-icon="inline-start" aria-hidden="true" />{:else}<Terminal data-icon="inline-start" aria-hidden="true" />{/if}
                      <span class="max-w-[24ch] truncate">{backgroundWorkLabel(work)}</span>
                    </Button>
                    <span class="background-elapsed">{formatBackgroundElapsed(backgroundNow - work.startedAtMs)}</span>
                  </span>
                {/each}
              </div>
            {/if}
          </div>
      {/each}
    </div>
  </div>
  {#if (!follow || hasNewer) && renderedItems.length > 0}<button class="jump-latest" data-testid="conversation-jump-latest" type="button" aria-label="Jump to latest" onclick={() => void jumpToLatest()}><ArrowDown size={16} strokeWidth={2} aria-hidden="true" /></button>{/if}
</div>

<style>
  .timeline-wrap{position:relative;display:flex;flex-direction:column;width:100%;height:100%;flex:1 1 auto;min-height:0;overflow:hidden}
  .older-loading{position:absolute;top:calc(var(--center-head-height, 0px) + 6px);left:0;right:0;z-index:2;display:flex;align-items:center;justify-content:center;gap:6px;margin:0;font-size:12px;color:var(--color-text-2);pointer-events:none;animation:older-appear 1ms 300ms backwards}
  /* Sits over the rows, so it carries its own face to stay readable. */
  .download-line{padding:2px 8px;border-radius:999px;background:var(--color-elevated)}
  /* A page read from the local copy lands well inside 300 ms; only a slow one shows this. */
  @keyframes older-appear{from{opacity:0}}
  .timeline-scroll{box-sizing:border-box;display:flex;flex-direction:column;width:100%;height:100%;flex:1 1 auto;min-height:0;overflow-y:auto;overflow-x:hidden;overflow-anchor:none;padding:0 28px;scrollbar-width:thin;scrollbar-color:var(--scrollbar-thumb) transparent;overscroll-behavior:contain}
  .timeline-list{flex:none;position:relative;width:min(820px,100%);min-height:1px;margin:0 auto}
  .turn-row{position:absolute;top:0;left:0;display:flex;flex-direction:column;gap:12px;width:100%;padding-bottom:12px}
  .turn-row.compact-tool{gap:0;padding-bottom:6px}
  .work-summary{display:flex;flex-wrap:wrap;align-items:baseline;gap:8px 16px;border-top:1px solid var(--color-border);padding-top:12px;color:var(--color-text-2);font-size:13px}
  .work-duration{flex:none;font-variant-numeric:tabular-nums}
  .work-toggle{flex:1;margin:-3px -6px}
  .turn-fold-chevron{display:grid;place-items:center;color:var(--color-text-3)}
  .turn-fold-chevron.open{transform:rotate(90deg)}
  .run-header{display:flex;align-items:center;gap:8px;min-height:30px;padding:3px 6px;border:0;border-radius:8px;background:transparent;color:var(--color-text-2);font-size:13px;text-align:left;cursor:pointer}
  .run-header:hover{background:color-mix(in srgb,var(--color-hover) 50%,transparent)}
  .run-header:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:2px}
  .run-icon{display:grid;place-items:center;flex:none;color:var(--color-text-3)}
  .run-summary{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  /* A tool group's arrow sits beside its label, as each call's does. */
  [data-run-id] .run-summary{flex:0 1 auto}
  .empty{display:grid;flex:1;place-items:center;min-height:100%;margin:0;color:var(--color-text-2);font-size:13px}
  .working-row{flex:none;display:flex;align-items:center;gap:8px;height:24px;overflow:hidden;white-space:nowrap;color:var(--color-text-3);font-size:13px}
  /* Taller than the turn's line so the kit button's focus ring is not clipped.
     It wraps: the kit button never shrinks, so on one line many labels overlap. */
  .background-row{flex-wrap:wrap;gap:2px 6px;height:auto;min-height:32px;padding:4px 0}
  .background-item{display:inline-flex;align-items:center;gap:6px;min-width:0}
  .background-elapsed{font-variant-numeric:tabular-nums}
  .background-sep{color:var(--color-text-3)}
  /* A disc under the middle of the transcript, holding one arrow. It sits over
     the column it scrolls rather than off in the corner, and it says what it
     does by pointing, so it stays out of the reading it is offering to move. */
  .jump-latest{position:absolute;left:50%;bottom:calc(max(var(--composer-height, 0px), 120px) + 20px);display:grid;place-items:center;width:32px;height:32px;padding:0;transform:translateX(-50%);border:1px solid color-mix(in srgb,var(--color-border) 68%,transparent);border-radius:999px;background:color-mix(in srgb,var(--color-elevated) 94%,var(--color-accent) 6%);color:var(--color-text);box-shadow:var(--shadow-sm);cursor:pointer}
  .jump-latest:hover{background:var(--color-hover)}
  .jump-latest:focus-visible{outline:2px solid var(--color-focus-solid);outline-offset:2px}
  @media (prefers-reduced-motion:no-preference){.turn-fold-chevron{transition:transform .14s ease}.jump-latest{transition:background .14s ease,box-shadow .14s ease}}
</style>
