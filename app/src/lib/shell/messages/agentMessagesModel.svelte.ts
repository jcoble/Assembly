/** Resolve the sender's native id only on the machine its message names. */
export function messageSession(identity: string, sessions: readonly { ownedId: string; remoteProfileId?: string | null }[]) {
  const separator = identity.indexOf(':');
  const machine = separator < 0 ? null : identity.slice(0, separator);
  const ownedId = identity.slice(separator + 1);
  return sessions.find(session => session.ownedId === ownedId && (machine === null || (session.remoteProfileId ?? 'mac') === machine)) ?? null;
}

export function reloadLimit(loaded: number): number {
  return Math.min(500, Math.max(100, loaded));
}

export function createUnreadTracker(count: (session: string, afterMs: number, signal: AbortSignal) => Promise<number | null>) {
  const lastLooks = new Map<string, number>();
  let session: string | null = null;
  let request: AbortController | null = null;
  let unread = $state(0);

  async function refresh(selected: string, afterMs: number, signal: AbortSignal): Promise<void> {
    try {
      const result = await count(selected, afterMs, signal);
      if (signal.aborted) return;
      unread = result ?? 0;
    } catch {
      // A disconnected machine will be counted again on the next change or selection.
    }
  }

  function changed(): void {
    request?.abort();
    request = null;
    if (!session) return;
    request = new AbortController();
    void refresh(session, lastLooks.get(session)!, request.signal);
  }

  return {
    get unread() { return unread; },
    select(selected: string | null): void {
      session = selected;
      unread = 0;
      if (selected && !lastLooks.has(selected)) lastLooks.set(selected, Date.now());
      changed();
    },
    changed,
    seen(): void {
      request?.abort();
      request = null;
      if (session) lastLooks.set(session, Date.now());
      unread = 0;
    },
    dispose(): void {
      request?.abort();
      request = null;
      session = null;
    }
  };
}
