/**
 * Coherent-cut `Assembly.Engine` stream cache (mirrors `desktop/src/core/engine/engine_store.rs`).
 *
 * - Reference-counted scope leases (`watch(scope, listener)`): multiple features watching the
 *   same `(machine, topic, key)` share ONE socket subscription; dropping the last lease sends `unsub`.
 * - Multi-page `engine_snapshot` frames are buffered until `more === false`, then committed atomically.
 * - Sparse `engine_events` frames read updated entities in bounded chunks of 200 over `/socket/bulk`
 *   (`engine/entities`), checking bulk connection generation before committing and sending `ack`.
 */

import {
  engineKeyId,
  engineScopeId,
  sessionKeyId,
  type EngineCursor,
  type EngineEvent,
  type EngineItem,
  type EngineKey,
  type EngineScope,
  type SessionKey
} from '$lib/domain/types';
import { backend, type EngineStreamFrame, type EngineSubscriptionHandle } from './backend';

interface EntityPage {
  items: Record<string, unknown>[];
  next: string | null;
}

interface ScopeLeaseState {
  scope: EngineScope;
  scopeId: string;
  handle: EngineSubscriptionHandle;
  listeners: Set<() => void>;
  pendingSnapshot: Record<string, unknown>[];
  eventChain: Promise<void>;
  abort: AbortController;
  coherent: boolean;
  error: string | null;
}

const ENTITY_BATCH_SIZE = 200;

function decodeRow(machine: string, value: Record<string, unknown>): EngineItem | null {
  if (typeof value.machine === 'string' && value.machine !== machine) return null;
  const kind = typeof value.entityKind === 'string' ? value.entityKind : null;
  const id = typeof value.id === 'string' ? value.id : null;
  const version = typeof value.version === 'number' && value.version >= 0 ? value.version : null;
  if (!kind || !id || version === null) return null;
  return {
    key: { machine, kind, id },
    version,
    value
  };
}

async function fetchEntityBatch(
  refs: EngineKey[],
  signal: AbortSignal
): Promise<Record<string, unknown>[]> {
  if (refs.length === 0) return [];
  const rows: Record<string, unknown>[] = [];
  const generation = backend.getBulkGeneration();

  for (let i = 0; i < refs.length; i += ENTITY_BATCH_SIZE) {
    const chunk = refs.slice(i, i + ENTITY_BATCH_SIZE);
    const machine = chunk[0].machine;
    const references = chunk.map((k) => ({ kind: k.kind, id: k.id }));
    let after: string | null = null;
    const seen = new Set<string>();

    while (true) {
      if (signal.aborted) throw new Error('aborted');
      const page: EntityPage = await backend.call<EntityPage>(
        'engine',
        'entities',
        { refs: references, after, machine },
        { bulk: true, signal }
      );
      if (backend.getBulkGeneration() !== generation) {
        throw new Error('bulk_generation_changed');
      }
      if (Array.isArray(page.items)) {
        rows.push(...page.items);
      }
      if (!page.next) break;
      if (seen.has(page.next)) throw new Error('entity_continuation_loop');
      seen.add(page.next);
      after = page.next;
    }
  }
  return rows;
}

export class EngineStreamCache {
  private readonly items = new Map<string, EngineItem>();
  private readonly scopes = new Map<string, Set<string>>();
  private readonly cursors = new Map<string, EngineCursor>();
  private readonly needsBySession = new Map<string, Set<string>>();
  private readonly childrenByParent = new Map<string, Set<string>>();
  private readonly leases = new Map<string, ScopeLeaseState>();
  private readonly globalListeners = new Set<() => void>();

  onChange(listener: () => void): () => void {
    this.globalListeners.add(listener);
    return () => this.globalListeners.delete(listener);
  }

  private notify(lease?: ScopeLeaseState): void {
    if (lease) {
      for (const listener of lease.listeners) listener();
    }
    for (const listener of this.globalListeners) listener();
  }

  watch(scope: EngineScope, listener: () => void): () => void {
    const scopeId = engineScopeId(scope);
    let lease = this.leases.get(scopeId);
    if (!lease) {
      const abort = new AbortController();
      const handle = backend.subscribeEngine(
        { topic: scope.topic, key: scope.key, machine: scope.machine },
        (frame) => this.handleFrame(scopeId, frame)
      );
      lease = {
        scope,
        scopeId,
        handle,
        listeners: new Set(),
        pendingSnapshot: [],
        eventChain: Promise.resolve(),
        abort,
        coherent: false,
        error: null
      };
      this.leases.set(scopeId, lease);
    }

    lease.listeners.add(listener);
    return () => {
      const current = this.leases.get(scopeId);
      if (!current) return;
      current.listeners.delete(listener);
      if (current.listeners.size === 0) {
        current.abort.abort();
        current.handle.unsubscribe();
        this.leases.delete(scopeId);
        this.scopes.delete(scopeId);
        this.cursors.delete(scopeId);
        this.pruneUnreferencedItems();
        this.notify();
      }
    };
  }

  getItems(scope: EngineScope): EngineItem[] {
    const ids = this.scopes.get(engineScopeId(scope));
    if (!ids) return [];
    const out: EngineItem[] = [];
    for (const id of ids) {
      const item = this.items.get(id);
      if (item) out.push(item);
    }
    return out;
  }

  getTopicItems(topic: EngineScope['topic']): EngineItem[] {
    const out: EngineItem[] = [];
    for (const lease of this.leases.values()) {
      if (lease.scope.topic === topic && lease.scope.key === null && lease.coherent) {
        out.push(...this.getItems(lease.scope));
      }
    }
    return out;
  }

  getNeedsFor(session: SessionKey): EngineItem[] {
    const ids = this.needsBySession.get(sessionKeyId(session));
    if (!ids) return [];
    const out: EngineItem[] = [];
    for (const id of ids) {
      const item = this.items.get(id);
      if (item) out.push(item);
    }
    return out;
  }

  getChildrenOf(parent: SessionKey): EngineItem[] {
    const ids = this.childrenByParent.get(sessionKeyId(parent));
    if (!ids) return [];
    const out: EngineItem[] = [];
    for (const id of ids) {
      const item = this.items.get(id);
      if (item) out.push(item);
    }
    return out;
  }

  isScopeLoaded(scope: EngineScope): boolean {
    const lease = this.leases.get(engineScopeId(scope));
    return Boolean(lease?.coherent && this.cursors.has(engineScopeId(scope)));
  }

  private handleFrame(scopeId: string, frame: EngineStreamFrame): void {
    const lease = this.leases.get(scopeId);
    if (!lease) return;

    if (frame.type === 'reconnected') {
      lease.pendingSnapshot = [];
      lease.coherent = false;
      lease.error = null;
      return;
    }

    if (frame.type === 'error') {
      lease.coherent = false;
      lease.error = frame.message;
      this.notify(lease);
      return;
    }

    if (frame.type === 'snapshot') {
      lease.pendingSnapshot.push(...frame.items);
      if (frame.more) {
        if (lease.scope.topic === 'mirror') lease.handle.next();
        return;
      }
      const all = lease.pendingSnapshot;
      lease.pendingSnapshot = [];
      this.commitSnapshot(lease, { through: frame.through, at: frame.at }, all);
      lease.handle.ack(frame.through);
      return;
    }

    if (frame.type === 'events') {
      lease.eventChain = lease.eventChain
        .then(() => this.applyEventsFrame(lease, frame))
        .catch(() => {
          // On batch/generation mismatch, request a clean re-snapshot.
          if (this.leases.get(scopeId) === lease) {
            lease.coherent = false;
            lease.pendingSnapshot = [];
            lease.handle.resubscribe(null, null);
          }
        });
    }
  }

  private commitSnapshot(
    lease: ScopeLeaseState,
    cursor: EngineCursor,
    rawRows: Record<string, unknown>[]
  ): void {
    const membership = new Set<string>();
    for (const raw of rawRows) {
      const decoded = decodeRow(lease.scope.machine, raw);
      if (!decoded) continue;
      const keyStr = engineKeyId(decoded.key);
      membership.add(keyStr);
      const held = this.items.get(keyStr);
      if (!held || held.version <= decoded.version) {
        this.installItem(keyStr, decoded);
      }
    }
    this.scopes.set(lease.scopeId, membership);
    this.cursors.set(lease.scopeId, cursor);
    this.pruneUnreferencedItems();
    lease.coherent = true;
    lease.error = null;
    this.notify(lease);
  }

  private async applyEventsFrame(
    lease: ScopeLeaseState,
    frame: Extract<EngineStreamFrame, { type: 'events' }>
  ): Promise<void> {
    const heldCursor = this.cursors.get(lease.scopeId);
    if (!heldCursor) {
      lease.handle.resubscribe(null, null);
      return;
    }
    if (frame.through <= heldCursor.through) return;

    const changedKeys = new Map<string, EngineKey>();
    for (const ev of frame.events as EngineEvent[]) {
      if (ev.machine !== lease.scope.machine) continue;
      if (!ev.entityKind || !ev.entityId) continue;
      const key: EngineKey = { machine: lease.scope.machine, kind: ev.entityKind, id: ev.entityId };
      changedKeys.set(engineKeyId(key), key);
    }

    const rawRows =
      frame.rows !== null
        ? frame.rows
        : await fetchEntityBatch([...changedKeys.values()], lease.abort.signal);

    if (this.leases.get(lease.scopeId) !== lease) return;

    const membership = this.scopes.get(lease.scopeId) ?? new Set<string>();
    const present = new Set<string>();

    for (const raw of rawRows) {
      const decoded = decodeRow(lease.scope.machine, raw);
      if (!decoded) continue;
      const keyStr = engineKeyId(decoded.key);
      present.add(keyStr);
      const held = this.items.get(keyStr);
      if (!held || held.version <= decoded.version) {
        this.installItem(keyStr, decoded);
      }
    }

    for (const keyStr of changedKeys.keys()) {
      if (present.has(keyStr)) membership.add(keyStr);
      else membership.delete(keyStr);
    }

    this.scopes.set(lease.scopeId, membership);
    this.cursors.set(lease.scopeId, { through: frame.through, at: frame.at });
    this.pruneUnreferencedItems();
    lease.coherent = true;
    lease.error = null;
    lease.handle.ack(frame.through);
    this.notify(lease);
  }

  private needSessionIds(item: EngineItem): string[] {
    if (item.key.kind !== 'needs_me') return [];
    const ids: string[] = [];
    const sessionId = item.value.sessionId;
    if (typeof sessionId === 'string' && sessionId.length > 0) {
      ids.push(sessionKeyId({ machine: item.key.machine, id: sessionId }));
    }
    const addrMachine = item.value.addresseeMachine;
    const addrSession = item.value.addresseeSessionId;
    if (
      typeof addrMachine === 'string' &&
      addrMachine.length > 0 &&
      typeof addrSession === 'string' &&
      addrSession.length > 0
    ) {
      const key = sessionKeyId({ machine: addrMachine, id: addrSession });
      if (!ids.includes(key)) ids.push(key);
    }
    return ids;
  }

  private parentSessionId(item: EngineItem): string | null {
    if (item.key.kind !== 'session') return null;
    const parentId = item.value.parentId;
    const parentMachine = item.value.parentMachine;
    if (
      typeof parentId === 'string' &&
      parentId.length > 0 &&
      typeof parentMachine === 'string' &&
      parentMachine.length > 0
    ) {
      return sessionKeyId({ machine: parentMachine, id: parentId });
    }
    return null;
  }

  private unindexItem(keyStr: string): void {
    const prev = this.items.get(keyStr);
    if (!prev) return;
    const parent = this.parentSessionId(prev);
    if (parent) {
      const set = this.childrenByParent.get(parent);
      set?.delete(keyStr);
      if (set && set.size === 0) this.childrenByParent.delete(parent);
    }
    for (const sid of this.needSessionIds(prev)) {
      const set = this.needsBySession.get(sid);
      set?.delete(keyStr);
      if (set && set.size === 0) this.needsBySession.delete(sid);
    }
  }

  private installItem(keyStr: string, item: EngineItem): void {
    this.unindexItem(keyStr);
    this.items.set(keyStr, item);
    const parent = this.parentSessionId(item);
    if (parent) {
      let set = this.childrenByParent.get(parent);
      if (!set) {
        set = new Set();
        this.childrenByParent.set(parent, set);
      }
      set.add(keyStr);
    }
    for (const sid of this.needSessionIds(item)) {
      let set = this.needsBySession.get(sid);
      if (!set) {
        set = new Set();
        this.needsBySession.set(sid, set);
      }
      set.add(keyStr);
    }
  }

  private pruneUnreferencedItems(): void {
    for (const keyStr of [...this.items.keys()]) {
      let referenced = false;
      for (const membership of this.scopes.values()) {
        if (membership.has(keyStr)) {
          referenced = true;
          break;
        }
      }
      if (!referenced) {
        this.unindexItem(keyStr);
        this.items.delete(keyStr);
      }
    }
  }
}

export const engineStream = new EngineStreamCache();
