/**
 * Pure, DOM-free-testable multi-terminal manager — the core of
 * "P3 — conversations as live workspaces".
 *
 * Keeps N conversations live while holding ONE terminal buffer. Switching
 * conversations must NEVER kill a PTY — but it must not keep the outgoing
 * conversation's scrollback in memory either. So hiding a view RELEASES it:
 * the view is disposed and the record survives, keeping the PTY binding and
 * the DOM host, and the next show rebuilds the view and replays the backend's
 * ring into it. Output for a released conversation keeps arriving at the
 * backend, which is the authority for everything a hidden conversation
 * printed.
 *
 * All collaborators are INJECTED so the manager can be unit-tested with spies
 * and no real xterm/DOM/Tauri dependency.
 */

/**
 * The injected terminal's surface (e.g. a thin wrapper over an xterm instance).
 * The manager only ever talks to terminals through this interface.
 */
export type TerminalView = {
  write(data: string): void;
  fit(): void;
  focus(): void;
  setVisible(visible: boolean): void;
  /**
   * OPTIONAL: force the view's grid to an exact size, independent of its host's
   * measured geometry. `fit()` cannot do this — it measures the DOM, so a view
   * whose host is hidden (zero-sized) keeps the default 80x24 and any replayed
   * scrollback wraps at the wrong width. The owner uses this to match a
   * surviving PTY's real cols/rows BEFORE hydrating it.
   *
   * Optional by design: the manager never calls it, and a view that cannot
   * resize independently simply omits it.
   */
  resize?(cols: number, rows: number): void;
  dispose(): void;
};

/**
 * Session-level side effects (PTY writes/resizes, scrollback reads). These are
 * injected so the manager stays pure and testable; in production they bridge to
 * the Tauri terminal backend.
 */
export type LiveConversationTerminalDeps = {
  /** Create a terminal view bound to the given DOM host. */
  createTerminal: (host: HTMLElement) => TerminalView;
  /** Write user/manager input to a live PTY session. */
  writeSession?: (sessionId: string, data: string) => void;
  /** Inform the PTY backend of the visible terminal's new size. */
  resizeSession?: (sessionId: string) => void;
  /** Read saved scrollback for a session (used when (re)hydrating a view). */
  readScrollback?: (sessionId: string) => string | null | undefined;
};

export type EnsureViewOptions = {
  host: HTMLElement;
  sessionId?: string | null;
};

/**
 * Internal bookkeeping record for one conversation's terminal.
 */
export type LiveConversationTerminalRecord = {
  key: string;
  /**
   * The DOM host this conversation's view is built on. Kept because a released
   * record has to be able to build a new view without the surface handing the
   * host over again.
   */
  host: HTMLElement;
  /** `null` while the record is RELEASED: the PTY lives on, the buffer does not. */
  view: TerminalView | null;
  sessionId: string | null;
  visible: boolean;
  terminated: boolean;
};

export type LiveConversationTerminals = {
  ensureView(key: string, opts: EnsureViewOptions): TerminalView;
  /**
   * The EXISTING view for `key`, or `null` when there is none. Read-only on
   * purpose: unlike `ensureView` it never builds a view, so a caller that just
   * wants to talk to a terminal that is already on screen cannot accidentally
   * conjure one for a key that has none. A RELEASED record answers `null` too —
   * its conversation is still live, its buffer is not.
   */
  viewFor(key: string): TerminalView | null;
  /** The DOM host `key` is (or was) built on, so its owner can rebuild it. */
  hostFor(key: string): HTMLElement | null;
  showView(key: string): void;
  activeKey(): string | null;
  liveKeys(): string[];
  hasView(key: string): boolean;
  bindSession(key: string, sessionId: string): void;
  keyForSession(sessionId: string): string | null;
  feed(key: string, data: string): void;
  feedSession(sessionId: string, data: string): void;
  markTerminated(key: string): void;
  /** Dispose one view's buffer, keeping its record, PTY binding and host. */
  releaseView(key: string): void;
  closeView(key: string): void;
  disposeAll(): void;
};

export function createLiveConversationTerminals(
  deps: LiveConversationTerminalDeps
): LiveConversationTerminals {
  if (!deps || typeof deps.createTerminal !== 'function') {
    throw new TypeError(
      'createLiveConversationTerminals requires deps.createTerminal to be a function'
    );
  }

  // `writeSession` stays on the deps TYPE (callers pass it) but this module has
  // no write path of its own — the service owns every backend write.
  const { createTerminal, resizeSession, readScrollback } = deps;

  /** key -> record. Insertion order is preserved (used to pick a fallback active key). */
  const records = new Map<string, LiveConversationTerminalRecord>();
  /** sessionId -> key, the inverse of record.sessionId. */
  const sessionToKey = new Map<string, string>();
  let active: string | null = null;

  /**
   * Detach any session->key mapping that points at `key`. Defensive: a record
   * only ever owns one sessionId, but we sweep so a stale reverse entry can
   * never outlive its record.
   */
  function clearSessionMappingFor(key: string): void {
    const record = records.get(key);
    if (record?.sessionId != null && sessionToKey.get(record.sessionId) === key) {
      sessionToKey.delete(record.sessionId);
    }
    for (const [sessionId, mappedKey] of sessionToKey) {
      if (mappedKey === key) {
        sessionToKey.delete(sessionId);
      }
    }
  }

  /**
   * Build this record's view and hydrate it from whatever scrollback the owner
   * has staged for its session. Used for a first view and for rebuilding one
   * that was released while hidden — the two are the same job.
   */
  function buildView(record: LiveConversationTerminalRecord): TerminalView {
    const view = createTerminal(record.host);
    record.view = view;
    if (record.sessionId != null) {
      const scrollback = readScrollback?.(record.sessionId);
      if (scrollback) {
        view.write(scrollback);
      }
    }
    return view;
  }

  function ensureView(key: string, opts: EnsureViewOptions): TerminalView {
    const existing = records.get(key);
    if (existing) {
      // Idempotent: never double-create a view for the same key. If a sessionId
      // is supplied and the record has none yet, adopt it.
      if (opts?.sessionId != null && existing.sessionId == null) {
        bindSession(key, opts.sessionId);
      }
      // A remounted surface hands over a fresh host. Take it: the previous one
      // is off the page, and a rebuilt view attached to it would be invisible.
      if (opts?.host != null) {
        existing.host = opts.host;
      }
      // No view means this record was released while hidden — build it again
      // and replay whatever the owner staged for its session.
      return existing.view ?? buildView(existing);
    }

    if (!opts || opts.host == null) {
      throw new TypeError(`ensureView("${key}") requires opts.host`);
    }

    const sessionId = opts.sessionId ?? null;
    const record: LiveConversationTerminalRecord = {
      key,
      host: opts.host,
      view: null,
      sessionId,
      visible: false,
      terminated: false
    };
    records.set(key, record);
    if (sessionId != null) {
      sessionToKey.set(sessionId, key);
    }
    // Map the session before the view exists, so output that arrives during
    // construction already routes here.
    const view = buildView(record);

    // The first view created becomes active when nothing else is showing.
    if (active === null) {
      showView(key);
    }

    return view;
  }

  function viewFor(key: string): TerminalView | null {
    return records.get(key)?.view ?? null;
  }

  function hostFor(key: string): HTMLElement | null {
    return records.get(key)?.host ?? null;
  }

  function showView(key: string): void {
    const target = records.get(key);
    if (!target) {
      return;
    }

    for (const record of records.values()) {
      if (record === target) {
        continue;
      }
      record.visible = false;
      if (record.terminated) {
        // Kept on purpose (see `releaseView`), so it has to be hidden instead.
        record.view?.setVisible(false);
      } else {
        // A hidden buffer is wasted memory. Releasing keeps the record, so the
        // PTY stays bound and the next show rebuilds from the backend's ring.
        releaseView(record.key);
      }
    }

    target.visible = true;
    // A released target has no buffer yet. Rebuilding one means replaying the
    // backend ring, which is an asynchronous read this manager cannot do — its
    // owner calls `ensureView` and then shows it again.
    target.view?.setVisible(true);
    target.view?.fit();
    target.view?.focus();
    active = key;

    // Only the shown terminal needs a PTY resize; inactive ones are untouched.
    if (target.sessionId != null) {
      resizeSession?.(target.sessionId);
    }
  }

  function activeKey(): string | null {
    return active;
  }

  function liveKeys(): string[] {
    return Array.from(records.keys());
  }

  function hasView(key: string): boolean {
    return records.has(key);
  }

  function bindSession(key: string, sessionId: string): void {
    const record = records.get(key);
    if (!record) {
      return;
    }

    // Drop any previous sessionId this key owned so the reverse map stays 1:1.
    if (record.sessionId != null && record.sessionId !== sessionId) {
      if (sessionToKey.get(record.sessionId) === key) {
        sessionToKey.delete(record.sessionId);
      }
    }
    // If another key claimed this sessionId, release it from that key.
    const priorKey = sessionToKey.get(sessionId);
    if (priorKey != null && priorKey !== key) {
      const priorRecord = records.get(priorKey);
      if (priorRecord && priorRecord.sessionId === sessionId) {
        priorRecord.sessionId = null;
      }
    }

    record.sessionId = sessionId;
    sessionToKey.set(sessionId, key);
  }

  function keyForSession(sessionId: string): string | null {
    return sessionToKey.get(sessionId) ?? null;
  }

  function feed(key: string, data: string): void {
    const record = records.get(key);
    if (!record) {
      return;
    }
    // Write REGARDLESS of visibility — a kept-but-hidden view still shows this
    // when it is looked at. A RELEASED record has no buffer to write to, and
    // dropping the bytes here is the point: the backend's ring keeps them and a
    // later show replays them.
    record.view?.write(data);
  }

  function feedSession(sessionId: string, data: string): void {
    const key = sessionToKey.get(sessionId);
    if (key == null) {
      return;
    }
    feed(key, data);
  }

  function markTerminated(key: string): void {
    const record = records.get(key);
    if (!record) {
      return;
    }
    // Keep the view + scrollback on screen; do NOT auto-remove. The user can
    // still read the final output of a finished conversation, and `releaseView`
    // leaves a terminated record's buffer alone for the same reason.
    record.terminated = true;
  }

  /**
   * Dispose one view's buffer while keeping everything that makes the
   * conversation live: the record, its `sessionId` binding and the host it was
   * built on. `ensureView` rebuilds it, `showView` shows it again.
   *
   * A TERMINATED conversation is the exception. Its process is gone, so nothing
   * can tell a rebuilt view the width its output was written at and the final
   * frame would re-wrap at xterm's 80-column default. That buffer has also
   * stopped growing, so keeping it until `closeView` costs a fixed amount.
   */
  function releaseView(key: string): void {
    const record = records.get(key);
    if (!record || record.view == null || record.terminated) {
      return;
    }
    const view = record.view;
    record.view = null;
    record.visible = false;
    if (active === key) {
      active = null;
    }
    // Hide BEFORE disposing: the host keeps whatever `display` the view last
    // set it to, and a leftover `display: block` host covers the live terminal.
    view.setVisible(false);
    view.dispose();
  }

  function closeView(key: string): void {
    const record = records.get(key);
    if (!record) {
      return;
    }

    clearSessionMappingFor(key);
    records.delete(key);
    // Dispose ONLY this view — never touch the others' PTYs/views. A released
    // record has none left to dispose.
    record.view?.dispose();

    if (active === key) {
      // Pick the next surviving view (most-recently-inserted) as active, or
      // clear if none remain.
      const remaining = Array.from(records.keys());
      const next = remaining.length > 0 ? remaining[remaining.length - 1] : null;
      active = null;
      if (next != null) {
        showView(next);
      }
    }
  }

  function disposeAll(): void {
    for (const record of records.values()) {
      record.view?.dispose();
    }
    records.clear();
    sessionToKey.clear();
    active = null;
  }

  return {
    ensureView,
    viewFor,
    hostFor,
    showView,
    activeKey,
    liveKeys,
    hasView,
    bindSession,
    keyForSession,
    feed,
    feedSession,
    markTerminated,
    releaseView,
    closeView,
    disposeAll
  };
}
