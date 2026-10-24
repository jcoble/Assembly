export interface ConversationDraftBackend {
  set(ownedId: string, text: string): Promise<void>;
  get(ownedId: string): Promise<string | null>;
  clear(ownedId: string): Promise<void>;
}

/** One ordered write queue per owned session. */
export class ConversationDraftPersistence {
  private readonly writes = new Map<string, Promise<void>>();
  private readonly revisions = new Map<string, number>();
  private readonly backend: ConversationDraftBackend;
  private readonly applyLoadedDraft: (ownedId: string, text: string) => void;

  constructor(
    backend: ConversationDraftBackend,
    applyLoadedDraft: (ownedId: string, text: string) => void,
    _delayMs = 0
  ) {
    this.backend = backend;
    this.applyLoadedDraft = applyLoadedDraft;
  }

  schedule(ownedId: string, text: string): void {
    this.bumpRevision(ownedId);
    void this.persistDraft(ownedId, text);
  }

  async flush(ownedId: string): Promise<void> {
    const write = this.writes.get(ownedId);
    if (write) await write;
  }

  async load(ownedId: string): Promise<void> {
    // A read takes its own revision so a dropped counter can never look like
    // the number this read started from.
    const revision = this.bumpRevision(ownedId);
    try {
      await this.writes.get(ownedId);
    } catch {
      // A failed queued write should not prevent reading the last durable draft.
    }
    const text = await this.backend.get(ownedId);
    if (this.revisions.get(ownedId) === revision) {
      this.applyLoadedDraft(ownedId, text ?? '');
    }
  }

  async clear(ownedId: string): Promise<void> {
    const revision = this.bumpRevision(ownedId);
    try {
      await this.enqueue(ownedId, () => this.backend.clear(ownedId));
    } finally {
      // A cleared session holds no draft to invalidate, so its counter is
      // dropped once the clear has settled and nothing newer has bumped it.
      if (this.revisions.get(ownedId) === revision) this.revisions.delete(ownedId);
    }
  }

  private async persistDraft(ownedId: string, text: string): Promise<void> {
    try {
      await this.enqueue(ownedId, () => this.backend.set(ownedId, text));
    } catch {
      // The caller has no useful recovery path; a later schedule or flush writes
      // the latest visible draft again.
    }
  }

  private bumpRevision(ownedId: string): number {
    const revision = (this.revisions.get(ownedId) ?? 0) + 1;
    this.revisions.set(ownedId, revision);
    return revision;
  }

  private async enqueue(ownedId: string, action: () => Promise<void>): Promise<void> {
    const previous = this.writes.get(ownedId);
    let work!: Promise<void>;
    work = this.runQueuedWrite(ownedId, previous, action, () => this.writes.get(ownedId) === work);
    this.writes.set(ownedId, work);
    await work;
  }

  private async runQueuedWrite(
    ownedId: string,
    previous: Promise<void> | undefined,
    action: () => Promise<void>,
    ownsQueueSlot: () => boolean
  ): Promise<void> {
    try {
      if (previous) await previous;
    } catch {
      // A failed older write must not block the latest draft operation.
    }
    try {
      await action();
    } finally {
      if (ownsQueueSlot()) this.writes.delete(ownedId);
    }
  }
}
