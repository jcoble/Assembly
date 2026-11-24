/**
 * Shared reactive Sessions store backed by the .NET `sessions` WebSocket topic.
 * Selecting a session notifies native GPUIKit `SessionPane` via `nativeBridge`.
 */

import type { SessionSummary } from '$lib/domain/types';
import { backend } from '$lib/infrastructure/backend';
import { selectNativeChatSession } from '$lib/infrastructure/nativeBridge';

class SessionsStore {
  private byId = $state<Map<string, SessionSummary>>(new Map());
  selectedId = $state<string | null>(null);
  error = $state<string | null>(null);
  private refCount = 0;
  private unsub: (() => void) | null = null;

  get list(): SessionSummary[] {
    return [...this.byId.values()];
  }

  get selected(): SessionSummary | null {
    return this.selectedId ? (this.byId.get(this.selectedId) ?? null) : null;
  }

  acquire(): () => void {
    this.refCount += 1;
    if (this.refCount === 1) {
      this.unsub = backend.subscribe<SessionSummary | null>('sessions', (event) => {
        if (event.type === 'state') {
          const next = new Map<string, SessionSummary>();
          for (const entry of event.items) {
            if (entry.item && entry.item.id) next.set(entry.key || entry.item.id, entry.item);
          }
          this.byId = next;
          this.error = null;
          if (!this.selectedId && next.size > 0) {
            const first = next.values().next().value;
            if (first) this.select(first.id);
          }
        } else if (event.type === 'change') {
          const next = new Map(this.byId);
          if (event.item === null || event.item === undefined) {
            next.delete(event.key);
          } else {
            next.set(event.key || event.item.id, event.item);
          }
          this.byId = next;
        } else if (event.type === 'error') {
          this.error = event.message;
        }
      });
    }
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.refCount = Math.max(0, this.refCount - 1);
      if (this.refCount === 0) {
        this.unsub?.();
        this.unsub = null;
      }
    };
  }

  select(id: string): void {
    const session = this.byId.get(id) ?? null;
    this.selectedId = id;
    selectNativeChatSession(id, session?.machine ?? null);
  }
}

export const sessionsStore = new SessionsStore();
