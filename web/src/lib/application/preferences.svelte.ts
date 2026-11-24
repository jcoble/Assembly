/**
 * Shared reactive Preferences & Theme store backed by `.NET` `preferences` topic and RPCs.
 * Glass is the primary default theme, layered on the tightened Spotify dark surface ladder.
 */

import type { ThemeMode } from '$lib/domain/types';
import { backend } from '$lib/infrastructure/backend';

class PreferencesStore {
  values = $state<Record<string, unknown>>({});
  theme = $state<ThemeMode>('glass');
  private refCount = 0;
  private unsub: (() => void) | null = null;

  acquire(): () => void {
    this.refCount += 1;
    if (this.refCount === 1) {
      this.unsub = backend.subscribe<unknown>('preferences', (event) => {
        if (event.type === 'state') {
          const next: Record<string, unknown> = {};
          for (const entry of event.items) {
            next[entry.key] = entry.item;
          }
          this.values = next;
          this.syncThemeFromValues(next);
        } else if (event.type === 'change') {
          const next = { ...this.values };
          if (event.item === null || event.item === undefined) {
            delete next[event.key];
          } else {
            next[event.key] = event.item;
          }
          this.values = next;
          this.syncThemeFromValues(next);
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

  private syncThemeFromValues(map: Record<string, unknown>): void {
    const raw = map['web.theme'];
    if (raw === 'glass' || raw === 'assembly' || raw === 'houston') {
      this.theme = raw;
    }
  }

  async set(key: string, value: unknown): Promise<void> {
    this.values = { ...this.values, [key]: value };
    if (key === 'web.theme' && (value === 'glass' || value === 'assembly' || value === 'houston')) {
      this.theme = value;
    }
    await backend.call('preferences', 'set', { key, value }, { key: `prefs:${key}` });
  }
}

export const preferencesStore = new PreferencesStore();
