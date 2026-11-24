/**
 * Reactive Svelte 5 facade over `EngineStreamCache` (`Assembly.Engine`).
 * Consumers lease scopes via `watchScope(...)` inside `$effect` and return the unsub function.
 */

import type { EngineItem, EngineScope, EngineTopic, SessionKey } from '$lib/domain/types';
import { engineStream } from '$lib/infrastructure/engineStream';

class EngineStore {
  /** Bumped atomically whenever any Engine snapshot or event batch commits. */
  revision = $state<number>(0);

  watchScope(scope: EngineScope): () => void {
    return engineStream.watch(scope, () => {
      this.revision += 1;
    });
  }

  items(scope: EngineScope): EngineItem[] {
    void this.revision;
    return engineStream.getItems(scope);
  }

  topicItems(topic: EngineTopic): EngineItem[] {
    void this.revision;
    return engineStream.getTopicItems(topic);
  }

  needsFor(session: SessionKey): EngineItem[] {
    void this.revision;
    return engineStream.getNeedsFor(session);
  }

  childrenOf(parent: SessionKey): EngineItem[] {
    void this.revision;
    return engineStream.getChildrenOf(parent);
  }

  isLoaded(scope: EngineScope): boolean {
    void this.revision;
    return engineStream.isScopeLoaded(scope);
  }

  get needCount(): number {
    return this.topicItems('needs_me').length;
  }

  get runCount(): number {
    return this.topicItems('runs').length;
  }
}

export const engineStore = new EngineStore();
