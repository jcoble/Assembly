/**
 * Reactive .NET WebSocket connection state shared across the shell and features.
 */

import { backend, type ConnectionSnapshot } from '$lib/infrastructure/backend';

class ConnectionStore {
  snapshot = $state<ConnectionSnapshot>(backend.getStatus());
  private unsub: (() => void) | null = null;

  start(): () => void {
    if (!this.unsub) {
      backend.start();
      this.unsub = backend.onStatus((next) => {
        this.snapshot = next;
      });
    }
    return () => {
      this.unsub?.();
      this.unsub = null;
    };
  }

  get status() {
    return this.snapshot.status;
  }

  get welcome() {
    return this.snapshot.welcome;
  }

  get localEngineMachine(): string | null {
    return this.snapshot.welcome?.engine ?? this.snapshot.welcome?.machine ?? null;
  }
}

export const connectionStore = new ConnectionStore();
