import type { RepositoryCheckout } from '../../tauriSource.ts';
import type { SessionLibraryRecord } from './library/sessionLibraryModel.ts';

export type RepositoryCheckouts = Record<string, RepositoryCheckout[]>;

/** What a project's load produced, and whether the reader can trust it. */
export type SessionHistoryLoadState = 'ready' | 'incomplete' | 'failed';

export interface SessionHistoryLoadOutcome {
  records: readonly SessionLibraryRecord[];
  checkouts: RepositoryCheckouts;
  state: SessionHistoryLoadState;
  /** Keys the view model listed that the scan could not produce. */
  missingKeys: readonly string[];
}

export function sessionHistoryLoadOutcome(input: {
  requestedKeys: ReadonlySet<string>;
  refreshed: PromiseSettledResult<readonly SessionLibraryRecord[]>;
  checkouts: PromiseSettledResult<RepositoryCheckouts | null>;
}): SessionHistoryLoadOutcome {
  const checkouts = input.checkouts.status === 'fulfilled'
    ? input.checkouts.value ?? {}
    : {};

  if (input.refreshed.status === 'rejected') {
    return {
      records: [],
      checkouts,
      state: 'failed',
      missingKeys: [...input.requestedKeys]
    };
  }

  const presentKeys = new Set(input.refreshed.value.map((record) => record.key));
  const missingKeys = [...input.requestedKeys].filter((key) => !presentKeys.has(key));
  return {
    records: input.refreshed.value,
    checkouts,
    state: missingKeys.length === 0 ? 'ready' : 'incomplete',
    missingKeys
  };
}
