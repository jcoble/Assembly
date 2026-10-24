/**
 * Ephemeral Session Library UI state only.
 *
 * Records remain in the owned rail/provider authorities. This singleton holds
 * search/filter/selection/page choices so center↔right movement can
 * reuse the same state without a second store or persistence key.
 */
export interface SessionLibraryUiState {
  query: string;
  provider: string;
  project: string;
  worktree: string;
  state: 'all' | 'working' | 'done' | 'settled' | 'resumable';
  model: string;
  dateFrom: string;
  dateTo: string;
  scope: 'workspace' | 'project' | 'all';
  selectedKey: string | null;
  expandedKey: string | null;
  page: number;
  pageSize: number;
}

export const sessionLibraryState = $state<SessionLibraryUiState>({
  query: '',
  provider: '',
  project: '',
  worktree: '',
  state: 'all',
  model: '',
  dateFrom: '',
  dateTo: '',
  scope: 'all',
  selectedKey: null,
  expandedKey: null,
  page: 1,
  pageSize: 50
});

/** Named alias makes the shared singleton explicit at mount sites. */
export const sessionLibraryStore = sessionLibraryState;

export function setSessionLibraryQuery(query: string): void {
  sessionLibraryState.query = query;
  sessionLibraryState.page = 1;
}

export function setSessionLibraryFilter<K extends keyof Pick<
  SessionLibraryUiState,
  'provider' | 'project' | 'worktree' | 'state' | 'model' | 'dateFrom' | 'dateTo'
>>(key: K, value: SessionLibraryUiState[K]): void {
  sessionLibraryState[key] = value;
  sessionLibraryState.page = 1;
}

export function setSessionLibrarySelection(selectedKey: string | null): void {
  sessionLibraryState.selectedKey = selectedKey;
  sessionLibraryState.expandedKey = selectedKey;
}

export function setSessionLibraryExpansion(expandedKey: string | null): void {
  sessionLibraryState.expandedKey = expandedKey;
  sessionLibraryState.selectedKey = expandedKey;
}

export function setSessionLibraryPage(page: number): void {
  sessionLibraryState.page = Number.isInteger(page) && page > 0 ? page : 1;
}

export function setSessionLibraryPageSize(pageSize: number): void {
  sessionLibraryState.pageSize = Number.isInteger(pageSize) && pageSize > 0 ? pageSize : 50;
  sessionLibraryState.page = 1;
}

export function resetSessionLibraryState(): void {
  sessionLibraryState.query = '';
  sessionLibraryState.provider = '';
  sessionLibraryState.project = '';
  sessionLibraryState.worktree = '';
  sessionLibraryState.state = 'all';
  sessionLibraryState.model = '';
  sessionLibraryState.dateFrom = '';
  sessionLibraryState.dateTo = '';
  sessionLibraryState.scope = 'all';
  sessionLibraryState.selectedKey = null;
  sessionLibraryState.expandedKey = null;
  sessionLibraryState.page = 1;
  sessionLibraryState.pageSize = 50;
}
