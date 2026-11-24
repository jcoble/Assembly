/**
 * Reactive Left Sessions Rail controller backed by `.NET` `sessions/rail` + `sessionRail`.
 * - Keyed cancellation on every query (`sessions/rail`)
 * - In-flight coalescing (`again = true` if invalidation arrives while reading)
 * - Persists `rail.filters` and `rail.view` via `preferencesStore`
 * - Bounded pagination (`PAGE = 50`, `MOST = 200`, matching `rail_page.rs`)
 */

import { preferencesStore } from '$lib/application/preferences.svelte';
import {
  RAIL_PREF_FILTERS_KEY,
  RAIL_PREF_VIEW_KEY,
  fetchRailPage,
  markSessionState,
  subscribeRailInvalidations
} from './api';
import {
  cursorForEntry,
  isProjectDoneShelf,
  normalizeRailFilters,
  normalizeRailView,
  toRailListItems
} from './format';
import {
  DEFAULT_RAIL_VIEW,
  EMPTY_RAIL_FILTERS,
  type RailArgs,
  type RailCursor,
  type RailEntry,
  type RailFilters,
  type RailListItem,
  type RailTotals,
  type RailView,
  type SessionMarkState
} from './types';

const PAGE_SIZE = 50;
const MAX_HELD = 200;
const FILTER_DEBOUNCE_MS = 140;

type ReadMode = 'top' | 'more';

export class RailController {
  entries = $state<RailEntry[]>([]);
  totals = $state<RailTotals>({ needs: 0 });
  agents = $state<string[]>([]);
  next = $state<RailCursor | null>(null);
  ahead = $state<number>(0);

  filters = $state<RailFilters>({ ...EMPTY_RAIL_FILTERS });
  view = $state<RailView>({
    ...DEFAULT_RAIL_VIEW,
    groupBy: { ...DEFAULT_RAIL_VIEW.groupBy }
  });
  folded = $state<string[]>([]);
  filterText = $state<string>('');

  loaded = $state<boolean>(false);
  loading = $state<boolean>(false);
  loadingMore = $state<boolean>(false);
  error = $state<string | null>(null);
  rowError = $state<string | null>(null);

  private activeRead: { id: number; mode: ReadMode } | null = null;
  private nextReadId = 0;
  private again = false;
  private anchorSessionId: string | null = null;
  private disposed = false;
  private filterTimer: ReturnType<typeof setTimeout> | null = null;
  private prefsRestored = false;
  private prefsDirty = false;
  private unsubTopic: (() => void) | null = null;

  get listItems(): RailListItem[] {
    return toRailListItems(this.entries, this.folded, this.view);
  }

  get hasMore(): boolean {
    return this.next !== null;
  }

  start(): () => void {
    this.disposed = false;
    this.unsubTopic = subscribeRailInvalidations((event) => {
      if (this.disposed) return;
      if (event.type === 'state' || event.type === 'change') {
        this.requestRefresh();
      } else if (event.type === 'error') {
        this.error = event.message;
      }
    });

    // Kick an initial read once mounted (if already connected, answers immediately).
    this.requestRefresh();

    return () => {
      this.disposed = true;
      if (this.filterTimer !== null) {
        clearTimeout(this.filterTimer);
        this.filterTimer = null;
      }
      this.unsubTopic?.();
      this.unsubTopic = null;
    };
  }

  /** Restores saved `rail.filters` and `rail.view` once `preferencesStore` loads (unless user already edited). */
  syncSavedPreferences(): void {
    if (this.prefsRestored || this.prefsDirty || !preferencesStore.loaded) return;
    this.prefsRestored = true;
    const savedFilters = preferencesStore.get(RAIL_PREF_FILTERS_KEY);
    const savedView = preferencesStore.get(RAIL_PREF_VIEW_KEY);
    let changed = false;
    if (savedFilters !== undefined) {
      this.filters = normalizeRailFilters(savedFilters);
      changed = true;
    }
    if (savedView !== undefined) {
      this.view = normalizeRailView(savedView);
      changed = true;
    }
    if (changed) {
      this.requery();
    }
  }

  /** Called when Engine scopes (`sessions` / `needs_me` / `runs`) advance. */
  notifyExternalChange(): void {
    if (this.loaded) this.requestRefresh();
  }

  setFilters(next: RailFilters): void {
    this.prefsDirty = true;
    this.filters = normalizeRailFilters(next);
    void preferencesStore.set(RAIL_PREF_FILTERS_KEY, this.filters).catch(() => {});
    this.requery();
  }

  clearFilters(): void {
    this.setFilters({ provider: [], status: [], location: [] });
  }

  setView(next: RailView): void {
    this.prefsDirty = true;
    const normalized = normalizeRailView(next);
    if (isProjectDoneShelf(this.view) !== isProjectDoneShelf(normalized)) {
      this.folded = this.folded.filter((k) => k !== 'done' && k !== '+done');
    }
    this.view = normalized;
    void preferencesStore.set(RAIL_PREF_VIEW_KEY, this.view).catch(() => {});
    this.requery();
  }

  toggleFold(foldKey: string, headerSessionId: string): void {
    const wireKey =
      foldKey === 'done' && isProjectDoneShelf(this.view) ? '+done' : foldKey;
    if (this.folded.includes(wireKey)) {
      this.folded = this.folded.filter((k) => k !== wireKey);
    } else {
      this.folded = [...this.folded, wireKey];
    }
    this.anchorSessionId = headerSessionId;
    this.startRead('top');
  }

  setFilterText(raw: string): void {
    this.filterText = raw;
    if (this.filterTimer !== null) {
      clearTimeout(this.filterTimer);
      this.filterTimer = null;
    }
    if (raw.trim() === '') {
      this.requery();
      return;
    }
    this.filterTimer = setTimeout(() => {
      this.filterTimer = null;
      this.requery();
    }, FILTER_DEBOUNCE_MS);
  }

  clearFilterText(): void {
    if (!this.filterText) return;
    this.setFilterText('');
  }

  loadMore(): void {
    if (this.activeRead || !this.next) return;
    this.startRead('more');
  }

  async mark(sessionId: string, state: SessionMarkState): Promise<void> {
    this.rowError = null;
    try {
      await markSessionState(sessionId, state);
      this.requestRefresh();
    } catch (err) {
      this.rowError = `Couldn't move session: ${err instanceof Error ? err.message : String(err)}`;
    }
  }

  clearRowError(): void {
    this.rowError = null;
  }

  private requestRefresh(): void {
    if (this.activeRead) {
      this.again = true;
      return;
    }
    this.startRead('top');
  }

  private requery(): void {
    this.entries = [];
    this.next = null;
    this.ahead = 0;
    this.anchorSessionId = null;
    this.startRead('top');
  }

  private buildArgs(mode: ReadMode): RailArgs {
    const trimmedFilter = this.filterText.trim();
    const isMore = mode === 'more';
    let anchorEntry: RailEntry | null = null;
    let above = 0;

    if (!isMore && this.anchorSessionId) {
      const idx = this.entries.findIndex((e) => e.sessionId === this.anchorSessionId);
      if (idx >= 0 && (this.ahead > 0 || this.entries.slice(0, idx).some((e) => e.kind === 'session'))) {
        anchorEntry = this.entries[idx];
        above = idx;
      }
    }

    return {
      providers: this.filters.provider,
      locations: this.filters.location,
      statuses: this.filters.status,
      groupBy: this.view.groupBy,
      sort: this.view.sort,
      dir: this.view.dir,
      folded: this.folded,
      filter: trimmedFilter.length > 0 ? trimmedFilter : null,
      after: isMore ? this.next : anchorEntry ? cursorForEntry(anchorEntry, this.view) : null,
      limit: isMore ? PAGE_SIZE : Math.min(MAX_HELD, Math.max(PAGE_SIZE, this.entries.length + PAGE_SIZE)),
      anchor: anchorEntry?.sessionId ?? null,
      above
    };
  }

  private startRead(mode: ReadMode): void {
    const id = ++this.nextReadId;
    this.activeRead = { id, mode };
    this.again = false;
    if (mode === 'more') {
      this.loadingMore = true;
    } else {
      this.loading = true;
    }

    const args = this.buildArgs(mode);
    void fetchRailPage(args)
      .then((page) => {
        if (this.disposed || this.activeRead?.id !== id) return;
        this.activeRead = null;
        this.loading = false;
        this.loadingMore = false;
        this.error = null;
        this.loaded = true;
        this.totals = page.totals ?? { needs: 0 };
        this.agents = page.agents ?? [];

        if (mode === 'more') {
          const merged = [...this.entries, ...(page.sessions ?? [])];
          if (merged.length > MAX_HELD) {
            const drop = merged.length - MAX_HELD;
            this.entries = merged.slice(drop);
            this.ahead += drop;
          } else {
            this.entries = merged;
          }
          this.next = page.next ?? null;
        } else {
          this.entries = page.sessions ?? [];
          this.next = page.next ?? null;
          this.ahead = page.start ?? 0;
        }
        this.anchorSessionId = null;

        if (this.again) {
          this.again = false;
          this.startRead('top');
        }
      })
      .catch((err) => {
        if (this.disposed || this.activeRead?.id !== id) return;
        this.activeRead = null;
        this.loading = false;
        this.loadingMore = false;
        const msg = err instanceof Error ? err.message : String(err);
        // Ignore expected cancellations when a newer keyed query replaced this one.
        if (!msg.toLowerCase().includes('abort') && !msg.toLowerCase().includes('cancel')) {
          this.error = msg;
        }
        if (this.again) {
          this.again = false;
          this.startRead('top');
        }
      });
  }
}
