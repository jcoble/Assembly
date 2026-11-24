/**
 * Shared 1 Hz backend clock store (subscribes to .NET `clock` topic).
 * Features read `clockStore.nowMs` instead of creating per-row timers.
 */

import { backend } from '$lib/infrastructure/backend';

class ClockStore {
  nowMs = $state<number>(Date.now());
  private refCount = 0;
  private unsub: (() => void) | null = null;

  acquire(): () => void {
    this.refCount += 1;
    if (this.refCount === 1) {
      this.unsub = backend.subscribe<unknown>('clock', (event) => {
        if (event.type === 'state' && event.items.length > 0) {
          this.applyTick(event.items[0].item);
        } else if (event.type === 'change') {
          this.applyTick(event.item);
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

  private applyTick(raw: unknown): void {
    if (typeof raw === 'number') {
      this.nowMs = raw;
      return;
    }
    if (raw && typeof raw === 'object') {
      const obj = raw as Record<string, unknown>;
      const ms = obj.unixMs ?? obj.nowMs ?? obj.ms ?? obj.timestamp;
      if (typeof ms === 'number') {
        this.nowMs = ms;
      } else if (typeof obj.iso === 'string') {
        const parsed = Date.parse(obj.iso);
        if (!Number.isNaN(parsed)) this.nowMs = parsed;
      }
    }
  }
}

export const clockStore = new ClockStore();
