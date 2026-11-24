/**
 * Shared reactive Sessions store backed by the .NET `sessions` WebSocket topic.
 * Selecting a session notifies native GPUIKit `SessionPane` via `nativeBridge`.
 */

import type { SessionSummary } from '$lib/domain/types';
import { backend } from '$lib/infrastructure/backend';
import { selectNativeChatSession } from '$lib/infrastructure/nativeBridge';

interface WireSessionItem {
  id?: string;
  sessionId?: string;
  title?: string | null;
  root?: string | null;
  project?: string | null;
  machine?: string | null;
  provider?: string | null;
  agent?: string | null;
  status?: string | null;
  updatedAt?: string | number | null;
  branch?: string | null;
}

function normalizeSession(key: string, raw: WireSessionItem): SessionSummary | null {
  const id = raw.sessionId || raw.id || key;
  if (!id) return null;
  return {
    id,
    title: raw.title ?? null,
    root: raw.root ?? null,
    project: raw.project ?? null,
    machine: raw.machine ?? null,
    provider: raw.agent ?? raw.provider ?? null,
    status: raw.status ?? null,
    updatedAt: raw.updatedAt ?? null,
    branch: raw.branch ?? null
  };
}

class SessionsStore {
  private byId = $state<Map<string, SessionSummary>>(new Map());
  selectedId = $state<string | null>(null);
  private selectedOverride = $state<SessionSummary | null>(null);
  error = $state<string | null>(null);
  private refCount = 0;
  private unsub: (() => void) | null = null;

  get list(): SessionSummary[] {
    return [...this.byId.values()];
  }

  get selected(): SessionSummary | null {
    if (!this.selectedId) return null;
    return this.byId.get(this.selectedId) ?? this.selectedOverride;
  }

  acquire(): () => void {
    this.refCount += 1;
    if (this.refCount === 1) {
      this.unsub = backend.subscribe<WireSessionItem | null>('sessions', (event) => {
        if (event.type === 'state') {
          const next = new Map<string, SessionSummary>();
          for (const entry of event.items) {
            if (!entry.item) continue;
            const normalized = normalizeSession(entry.key, entry.item);
            if (normalized) next.set(normalized.id, normalized);
          }
          this.byId = next;
          this.error = null;
          if (!this.selectedId && next.size > 0) {
            const first = next.values().next().value;
            if (first) this.select(first.id, first.machine ?? null, first.title ?? null);
          }
        } else if (event.type === 'change') {
          const next = new Map(this.byId);
          if (event.item === null || event.item === undefined) {
            next.delete(event.key);
          } else {
            const normalized = normalizeSession(event.key, event.item);
            if (normalized) next.set(normalized.id, normalized);
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

  select(id: string, machine?: string | null, title?: string | null): void {
    const known = this.byId.get(id) ?? null;
    const resolvedMachine = machine !== undefined ? machine : (known?.machine ?? null);
    this.selectedId = id;
    this.selectedOverride = known ?? {
      id,
      machine: resolvedMachine,
      title: title ?? null
    };
    selectNativeChatSession(id, resolvedMachine);
  }
}

export const sessionsStore = new SessionsStore();
