/**
 * Shared reactive Machines store backed by the .NET `machines` WebSocket topic.
 */

import type { MachineSummary } from '$lib/domain/types';
import { backend } from '$lib/infrastructure/backend';

class MachinesStore {
  private byName = $state<Map<string, MachineSummary>>(new Map());
  error = $state<string | null>(null);
  private refCount = 0;
  private unsub: (() => void) | null = null;

  get list(): MachineSummary[] {
    return [...this.byName.values()];
  }

  get connected(): MachineSummary[] {
    return this.list.filter((m) => m.connected || m.local || m.status === 'connected');
  }

  acquire(): () => void {
    this.refCount += 1;
    if (this.refCount === 1) {
      this.unsub = backend.subscribe<MachineSummary | null>('machines', (event) => {
        if (event.type === 'state') {
          const next = new Map<string, MachineSummary>();
          for (const entry of event.items) {
            if (entry.item) next.set(entry.key || entry.item.name, entry.item);
          }
          this.byName = next;
          this.error = null;
        } else if (event.type === 'change') {
          const next = new Map(this.byName);
          if (event.item === null || event.item === undefined) {
            next.delete(event.key);
          } else {
            next.set(event.key || event.item.name, event.item);
          }
          this.byName = next;
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
}

export const machinesStore = new MachinesStore();
