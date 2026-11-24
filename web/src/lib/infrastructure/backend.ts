/**
 * Single direct WebSocket client facade for Assembly's Native AOT .NET backend (`Assembly.Desktop`).
 * Manages `/socket`, `/socket/bulk`, and `/socket/terminal`, reading `window.__BACKEND__` and
 * reconnecting live on `assembly:backend-ready` events.
 */

import {
  SocketChannel,
  type BackendBootstrap,
  type ConnectionSnapshot,
  type EngineStreamFrame,
  type EngineSubscribeOptions,
  type EngineSubscriptionHandle,
  type TopicEvent
} from './socketChannel';

export {
  BackendCallError,
  isBackendAbort,
  type BackendBootstrap,
  type ConnectionSnapshot,
  type ConnectionStatus,
  type EngineStreamFrame,
  type EngineSubscribeOptions,
  type EngineSubscriptionHandle,
  type TopicEntry,
  type TopicEvent,
  type WelcomeInfo
} from './socketChannel';

export type SocketKind = 'main' | 'bulk' | 'terminal';

export interface CallOptions {
  bulk?: boolean;
  channel?: SocketKind;
  /** Supersedes any prior in-flight call with the same key via `{ t: "cancel", id }`. */
  key?: string;
  signal?: AbortSignal;
}

declare global {
  interface Window {
    __BACKEND__?: BackendBootstrap;
  }
}

const STARTUP_RETRY_MS = 100;
const STARTUP_TIMEOUT_MS = 3000;

function readBootstrapFromWindow(): BackendBootstrap | null {
  if (typeof window === 'undefined') return null;
  if (window.__BACKEND__?.url && window.__BACKEND__?.token) {
    return window.__BACKEND__;
  }
  const params = new URLSearchParams(window.location.search);
  const portRaw = params.get('port');
  const token = params.get('token');
  if (portRaw && token) {
    const port = Number(portRaw);
    if (Number.isInteger(port) && port > 0 && port <= 65535) {
      return {
        url: `ws://127.0.0.1:${port}/socket`,
        bulkUrl: `ws://127.0.0.1:${port}/socket/bulk`,
        terminalUrl: `ws://127.0.0.1:${port}/socket/terminal`,
        port,
        token
      };
    }
  }
  return null;
}

export class AssemblyBackendClient {
  private seqId = 0;
  private bootstrap: BackendBootstrap | null = null;
  private snapshot: ConnectionSnapshot = {
    status: 'waiting',
    welcome: null,
    error: null,
    port: null
  };
  private readonly statusListeners = new Set<(snapshot: ConnectionSnapshot) => void>();
  private readonly main: SocketChannel;
  private readonly bulk: SocketChannel;
  private readonly terminal: SocketChannel;
  private bulkStarted = false;
  private terminalStarted = false;
  private started = false;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private readyEventHandler: ((event: Event) => void) | null = null;

  constructor() {
    const nextId = () => ++this.seqId;
    this.main = new SocketChannel(
      (b) => b.url,
      nextId,
      (snap) => {
        this.snapshot = snap;
        for (const listener of this.statusListeners) listener(snap);
      }
    );
    this.bulk = new SocketChannel((b) => b.bulkUrl, nextId);
    this.terminal = new SocketChannel(
      (b) => b.terminalUrl ?? `ws://127.0.0.1:${b.port}/socket/terminal`,
      nextId
    );
  }

  start(): void {
    if (this.started || typeof window === 'undefined') return;
    this.started = true;

    this.readyEventHandler = (event: Event) => {
      const detail = (event as CustomEvent<BackendBootstrap>).detail ?? readBootstrapFromWindow();
      if (detail) this.applyBootstrap(detail);
    };
    window.addEventListener('assembly:backend-ready', this.readyEventHandler);

    const immediate = readBootstrapFromWindow();
    if (immediate) {
      this.applyBootstrap(immediate);
      return;
    }

    const startedAt = Date.now();
    this.pollTimer = setInterval(() => {
      const found = readBootstrapFromWindow();
      if (found) {
        this.stopPolling();
        this.applyBootstrap(found);
      } else if (Date.now() - startedAt >= STARTUP_TIMEOUT_MS) {
        this.stopPolling();
        if (!this.bootstrap) {
          this.snapshot = {
            status: 'waiting',
            welcome: null,
            error: 'Waiting for Assembly .NET backend (window.__BACKEND__ not injected yet).',
            port: null
          };
          for (const listener of this.statusListeners) listener(this.snapshot);
        }
      }
    }, STARTUP_RETRY_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private applyBootstrap(bootstrap: BackendBootstrap): void {
    this.bootstrap = bootstrap;
    this.main.connect(bootstrap);
    if (this.bulkStarted) this.bulk.connect(bootstrap);
    if (this.terminalStarted) this.terminal.connect(bootstrap);
  }

  private ensureChannel(channel: 'bulk' | 'terminal'): SocketChannel {
    if (channel === 'bulk') {
      if (!this.bulkStarted) {
        this.bulkStarted = true;
        if (this.bootstrap) this.bulk.connect(this.bootstrap);
      }
      return this.bulk;
    }
    if (!this.terminalStarted) {
      this.terminalStarted = true;
      if (this.bootstrap) this.terminal.connect(this.bootstrap);
    }
    return this.terminal;
  }

  getBulkGeneration(): number {
    return this.bulk.getGeneration();
  }

  onStatus(listener: (snapshot: ConnectionSnapshot) => void): () => void {
    this.statusListeners.add(listener);
    listener(this.snapshot);
    return () => this.statusListeners.delete(listener);
  }

  getStatus(): ConnectionSnapshot {
    return this.snapshot;
  }

  call<T>(
    feature: string,
    method: string,
    args: Record<string, unknown> = {},
    options?: CallOptions
  ): Promise<T> {
    this.start();
    const channel: SocketKind = options?.channel ?? (options?.bulk ? 'bulk' : 'main');
    if (channel === 'bulk' || channel === 'terminal') {
      return this.ensureChannel(channel).call<T>(feature, method, args, options?.signal, options?.key);
    }
    return this.main.call<T>(feature, method, args, options?.signal, options?.key);
  }

  subscribe<T>(
    topic: string,
    listener: (event: TopicEvent<T>) => void,
    key?: string,
    channel: 'main' | 'terminal' = 'main'
  ): () => void {
    this.start();
    if (channel === 'terminal') {
      return this.ensureChannel('terminal').subscribe<T>(topic, listener, key);
    }
    return this.main.subscribe<T>(topic, listener, key);
  }

  subscribeEngine(
    options: EngineSubscribeOptions,
    listener: (frame: EngineStreamFrame) => void
  ): EngineSubscriptionHandle {
    this.start();
    return this.main.subscribeEngine(options, listener);
  }

  dispose(): void {
    this.stopPolling();
    if (this.readyEventHandler && typeof window !== 'undefined') {
      window.removeEventListener('assembly:backend-ready', this.readyEventHandler);
      this.readyEventHandler = null;
    }
    this.main.dispose();
    this.bulk.dispose();
    this.terminal.dispose();
    this.statusListeners.clear();
    this.started = false;
  }
}

export const backend = new AssemblyBackendClient();
