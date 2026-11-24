/**
 * Authenticated WebSocket channel (`/socket`, `/socket/bulk`, or `/socket/terminal`) with:
 *   - First-frame `{ t: "hello", protocol: 1, token }` handshake
 *   - Keyed RPC superseding (`{ t: "cancel", id }`) + AbortSignal support
 *   - Automatic topic and `Assembly.Engine` stream re-subscription on reconnect
 */

import type { CursorAt, EngineEvent, EngineTopic } from '$lib/domain/types';

export interface BackendBootstrap {
  url: string;
  bulkUrl: string;
  terminalUrl?: string;
  port: number;
  token: string;
}

export interface WelcomeInfo {
  protocol: number;
  machine: string;
  format: number;
  commit?: string | null;
  engine?: string | null;
  backendIdentity?: string | null;
  version?: string | null;
}

export type ConnectionStatus = 'waiting' | 'connecting' | 'connected' | 'disconnected' | 'refused';

export interface ConnectionSnapshot {
  status: ConnectionStatus;
  welcome: WelcomeInfo | null;
  error: string | null;
  port: number | null;
}

export interface TopicEntry<T = unknown> {
  key: string;
  item: T;
}

export type TopicEvent<T = unknown> =
  | { type: 'state'; seq: number | null; items: TopicEntry<T>[] }
  | { type: 'change'; key: string; item: T }
  | { type: 'error'; code: string; message: string };

export type EngineStreamFrame =
  | {
      type: 'snapshot';
      machine: string;
      through: number;
      items: Record<string, unknown>[];
      more: boolean;
      at: CursorAt | null;
    }
  | {
      type: 'events';
      machine: string;
      after: number;
      through: number;
      events: EngineEvent[];
      more: boolean;
      rows: Record<string, unknown>[] | null;
      at: CursorAt | null;
    }
  | { type: 'error'; code: string; message: string }
  | { type: 'reconnected' };

export interface EngineSubscribeOptions {
  topic: EngineTopic;
  key?: string | null;
  after?: number | null;
  at?: CursorAt | null;
  machine?: string | null;
}

export interface EngineSubscriptionHandle {
  ack: (after: number) => void;
  next: () => void;
  resubscribe: (after: number | null, at: CursorAt | null) => void;
  unsubscribe: () => void;
}

export class BackendCallError extends Error {
  readonly code: string;
  readonly machine?: string;

  constructor(code: string, message: string, machine?: string) {
    super(message);
    this.name = 'BackendCallError';
    this.code = code;
    this.machine = machine;
  }
}

export function isBackendAbort(err: unknown): boolean {
  return err instanceof BackendCallError && (err.code === 'aborted' || err.code === 'cancelled');
}

interface PendingCall {
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
  key?: string;
  cleanupAbort?: () => void;
}

interface ActiveTopicSub {
  kind: 'topic';
  id: number;
  topic: string;
  key?: string;
  listener: (event: TopicEvent<unknown>) => void;
}

interface ActiveEngineSub {
  kind: 'engine';
  id: number;
  options: EngineSubscribeOptions;
  listener: (frame: EngineStreamFrame) => void;
}

type ActiveSub = ActiveTopicSub | ActiveEngineSub;

const PROTOCOL_VERSION = 1;
const RECONNECT_MIN_MS = 500;
const RECONNECT_MAX_MS = 5000;

export class SocketChannel {
  private ws: WebSocket | null = null;
  private ready = false;
  private disposed = false;
  private generation = 0;
  private bootstrap: BackendBootstrap | null = null;
  private reconnectDelay = RECONNECT_MIN_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly pending = new Map<number, PendingCall>();
  private readonly keys = new Map<string, number>();
  private readonly subs = new Map<number, ActiveSub>();
  private readonly sendQueue: string[] = [];

  constructor(
    private readonly urlOf: (b: BackendBootstrap) => string,
    private readonly nextId: () => number,
    private readonly onStatus?: (snapshot: ConnectionSnapshot) => void
  ) {}

  getGeneration(): number {
    return this.generation;
  }

  connect(bootstrap: BackendBootstrap): void {
    const changed =
      !this.bootstrap ||
      this.bootstrap.token !== bootstrap.token ||
      this.urlOf(this.bootstrap) !== this.urlOf(bootstrap);
    this.bootstrap = bootstrap;
    if (this.disposed) return;
    if (
      this.ws &&
      !changed &&
      (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)
    ) {
      return;
    }
    this.clearReconnect();
    this.reconnectDelay = RECONNECT_MIN_MS;
    this.openSocket();
  }

  private openSocket(): void {
    if (this.disposed || !this.bootstrap) return;
    if (this.ws) {
      const old = this.ws;
      this.ws = null;
      old.onopen = null;
      old.onmessage = null;
      old.onerror = null;
      old.onclose = null;
      try {
        old.close();
      } catch {
        // Ignore stale socket close errors.
      }
    }

    this.ready = false;
    const bootstrap = this.bootstrap;
    this.onStatus?.({ status: 'connecting', welcome: null, error: null, port: bootstrap.port });

    let ws: WebSocket;
    try {
      ws = new WebSocket(this.urlOf(bootstrap));
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      this.onStatus?.({ status: 'disconnected', welcome: null, error: msg, port: bootstrap.port });
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;

    ws.onopen = () => {
      if (this.ws !== ws) return;
      ws.send(JSON.stringify({ t: 'hello', protocol: PROTOCOL_VERSION, token: bootstrap.token }));
    };

    ws.onmessage = (event) => {
      if (this.ws !== ws || typeof event.data !== 'string') return;
      this.handleFrame(event.data);
    };

    ws.onerror = () => {
      // `onclose` handles reconnect transitions.
    };

    ws.onclose = (event) => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.ready = false;
      this.generation += 1;
      this.rejectAllPending(new BackendCallError('aborted', 'WebSocket connection closed'));

      if (event.code === 4401 || event.code === 4400) {
        this.onStatus?.({
          status: 'refused',
          welcome: null,
          error: event.reason || `Connection refused (${event.code})`,
          port: bootstrap.port
        });
        return;
      }

      this.onStatus?.({
        status: 'disconnected',
        welcome: null,
        error: event.reason || null,
        port: bootstrap.port
      });
      this.scheduleReconnect();
    };
  }

  private sendTopicSub(sub: ActiveTopicSub): void {
    this.sendRaw(
      JSON.stringify({
        t: 'sub',
        id: sub.id,
        topic: sub.topic,
        ...(sub.key !== undefined ? { key: sub.key } : {})
      })
    );
  }

  private sendEngineSub(sub: ActiveEngineSub): void {
    const { topic, key, after, at, machine } = sub.options;
    this.sendRaw(
      JSON.stringify({
        t: 'sub',
        id: sub.id,
        stream: 'engine',
        topic,
        ...(key !== undefined && key !== null ? { key } : {}),
        ...(after !== undefined && after !== null ? { after } : {}),
        ...(at !== undefined && at !== null ? { at } : {}),
        ...(machine !== undefined && machine !== null ? { machine } : {})
      })
    );
  }

  private handleFrame(raw: string): void {
    let frame: Record<string, unknown>;
    try {
      frame = JSON.parse(raw) as Record<string, unknown>;
    } catch {
      return;
    }

    const type = frame.t;
    if (type === 'welcome') {
      this.ready = true;
      this.generation += 1;
      this.reconnectDelay = RECONNECT_MIN_MS;
      const welcome: WelcomeInfo = {
        protocol: Number(frame.protocol ?? PROTOCOL_VERSION),
        machine: String(frame.machine ?? ''),
        format: Number(frame.format ?? 0),
        commit: (frame.commit as string | null | undefined) ?? null,
        engine: (frame.engine as string | null | undefined) ?? null,
        backendIdentity: (frame.backendIdentity as string | null | undefined) ?? null,
        version: (frame.version as string | null | undefined) ?? null
      };
      this.onStatus?.({
        status: 'connected',
        welcome,
        error: null,
        port: this.bootstrap?.port ?? null
      });

      for (const sub of this.subs.values()) {
        if (sub.kind === 'topic') {
          this.sendTopicSub(sub);
        } else {
          sub.listener({ type: 'reconnected' });
          this.sendEngineSub(sub);
        }
      }
      while (this.sendQueue.length > 0 && this.ready) {
        const queued = this.sendQueue.shift();
        if (queued) this.sendRaw(queued);
      }
      return;
    }

    if (type === 'result') {
      const id = Number(frame.id);
      const pending = this.pending.get(id);
      if (pending) {
        this.pending.delete(id);
        this.releaseKey(pending.key, id);
        pending.cleanupAbort?.();
        pending.resolve(frame.result);
      }
      return;
    }

    if (type === 'error') {
      const id = Number(frame.id);
      const code = String(frame.code ?? 'failed');
      const message = String(frame.message ?? 'Backend call failed');
      const machine = typeof frame.machine === 'string' ? frame.machine : undefined;
      const pending = this.pending.get(id);
      if (pending) {
        this.pending.delete(id);
        this.releaseKey(pending.key, id);
        pending.cleanupAbort?.();
        pending.reject(new BackendCallError(code, message, machine));
        return;
      }
      const sub = this.subs.get(id);
      if (sub) {
        this.subs.delete(id);
        sub.listener({ type: 'error', code, message });
      }
      return;
    }

    if (type === 'state') {
      const sub = this.subs.get(Number(frame.sub));
      if (sub?.kind === 'topic') {
        const items = Array.isArray(frame.items) ? (frame.items as TopicEntry<unknown>[]) : [];
        const seq = typeof frame.seq === 'number' ? frame.seq : null;
        sub.listener({ type: 'state', seq, items });
      }
      return;
    }

    if (type === 'change') {
      const sub = this.subs.get(Number(frame.sub));
      if (sub?.kind === 'topic') {
        sub.listener({ type: 'change', key: String(frame.key ?? ''), item: frame.item });
      }
      return;
    }

    if (type === 'engine_snapshot') {
      const sub = this.subs.get(Number(frame.id));
      if (sub?.kind === 'engine') {
        sub.listener({
          type: 'snapshot',
          machine: String(frame.machine ?? ''),
          through: Number(frame.through ?? 0),
          items: Array.isArray(frame.items) ? (frame.items as Record<string, unknown>[]) : [],
          more: Boolean(frame.more),
          at: (frame.at as CursorAt | null | undefined) ?? null
        });
      }
      return;
    }

    if (type === 'engine_events') {
      const sub = this.subs.get(Number(frame.id));
      if (sub?.kind === 'engine') {
        sub.listener({
          type: 'events',
          machine: String(frame.machine ?? ''),
          after: Number(frame.after ?? 0),
          through: Number(frame.through ?? 0),
          events: Array.isArray(frame.events) ? (frame.events as EngineEvent[]) : [],
          more: Boolean(frame.more),
          rows: Array.isArray(frame.rows) ? (frame.rows as Record<string, unknown>[]) : null,
          at: (frame.at as CursorAt | null | undefined) ?? null
        });
      }
    }
  }

  private releaseKey(key: string | undefined, id: number): void {
    if (key !== undefined && this.keys.get(key) === id) {
      this.keys.delete(key);
    }
  }

  private cancelInFlight(id: number, code: 'aborted' | 'cancelled', message: string): void {
    const existing = this.pending.get(id);
    if (!existing) return;
    this.pending.delete(id);
    this.releaseKey(existing.key, id);
    existing.cleanupAbort?.();
    if (this.ready) {
      this.sendRaw(JSON.stringify({ t: 'cancel', id }));
    }
    existing.reject(new BackendCallError(code, message));
  }

  call<T>(
    feature: string,
    method: string,
    args: Record<string, unknown> = {},
    signal?: AbortSignal,
    key?: string
  ): Promise<T> {
    if (signal?.aborted) {
      return Promise.reject(new BackendCallError('cancelled', 'Call aborted'));
    }
    if (key !== undefined) {
      const previousId = this.keys.get(key);
      if (previousId !== undefined) {
        this.cancelInFlight(previousId, 'aborted', 'Superseded by a newer call');
      }
    }

    const id = this.nextId();
    const payload = JSON.stringify({ t: 'call', id, feature, method, args });

    return new Promise<T>((resolve, reject) => {
      const entry: PendingCall = {
        resolve: resolve as (value: unknown) => void,
        reject,
        key
      };
      if (signal) {
        const onAbort = () => this.cancelInFlight(id, 'cancelled', 'Call aborted');
        signal.addEventListener('abort', onAbort, { once: true });
        entry.cleanupAbort = () => signal.removeEventListener('abort', onAbort);
      }

      this.pending.set(id, entry);
      if (key !== undefined) this.keys.set(key, id);
      if (this.ready) this.sendRaw(payload);
      else this.sendQueue.push(payload);
    });
  }

  subscribe<T>(topic: string, listener: (event: TopicEvent<T>) => void, key?: string): () => void {
    const id = this.nextId();
    const sub: ActiveTopicSub = {
      kind: 'topic',
      id,
      topic,
      key,
      listener: listener as (event: TopicEvent<unknown>) => void
    };
    this.subs.set(id, sub);
    if (this.ready) this.sendTopicSub(sub);

    return () => {
      if (!this.subs.delete(id)) return;
      if (this.ready) this.sendRaw(JSON.stringify({ t: 'unsub', id }));
    };
  }

  subscribeEngine(
    options: EngineSubscribeOptions,
    listener: (frame: EngineStreamFrame) => void
  ): EngineSubscriptionHandle {
    let activeId = this.nextId();
    let sub: ActiveEngineSub = { kind: 'engine', id: activeId, options: { ...options }, listener };
    this.subs.set(activeId, sub);
    if (this.ready) this.sendEngineSub(sub);

    return {
      ack: (after: number) => {
        if (!this.subs.has(activeId) || !this.ready) return;
        sub.options.after = after;
        this.sendRaw(JSON.stringify({ t: 'ack', id: activeId, stream: 'engine', after }));
      },
      next: () => {
        if (!this.subs.has(activeId) || !this.ready) return;
        this.sendRaw(JSON.stringify({ t: 'next', id: activeId }));
      },
      resubscribe: (after: number | null, at: CursorAt | null) => {
        if (this.subs.delete(activeId) && this.ready) {
          this.sendRaw(JSON.stringify({ t: 'unsub', id: activeId }));
        }
        activeId = this.nextId();
        sub = {
          kind: 'engine',
          id: activeId,
          options: { ...sub.options, after, at },
          listener
        };
        this.subs.set(activeId, sub);
        if (this.ready) this.sendEngineSub(sub);
      },
      unsubscribe: () => {
        if (!this.subs.delete(activeId)) return;
        if (this.ready) this.sendRaw(JSON.stringify({ t: 'unsub', id: activeId }));
      }
    };
  }

  private sendRaw(frame: string): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(frame);
    }
  }

  private rejectAllPending(error: Error): void {
    for (const [, pending] of this.pending) {
      pending.cleanupAbort?.();
      pending.reject(error);
    }
    this.pending.clear();
    this.keys.clear();
    this.sendQueue.length = 0;
  }

  private scheduleReconnect(): void {
    if (this.disposed || !this.bootstrap) return;
    this.clearReconnect();
    const delay = this.reconnectDelay;
    this.reconnectDelay = Math.min(this.reconnectDelay * 2, RECONNECT_MAX_MS);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.openSocket();
    }, delay);
  }

  private clearReconnect(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  dispose(): void {
    this.disposed = true;
    this.clearReconnect();
    this.subs.clear();
    this.rejectAllPending(new BackendCallError('cancelled', 'Client disposed'));
    if (this.ws) {
      try {
        this.ws.close();
      } catch {
        // Ignore teardown close errors.
      }
      this.ws = null;
    }
  }
}
