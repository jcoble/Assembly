/** The app setting that holds the one Tasks view every session shares. */
export const TASKS_VIEW_SETTING_KEY = 'tasks.view';

/** The Tasks panel's search, filters and sort; every value its query reads. */
export interface TasksView {
  search: string;
  filters: { status: string[]; priority: string[] };
  projectFilter: string;
  sortBy: string;
  sortDirection: string;
}

function strings(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return [...new Set(value.filter((entry): entry is string => typeof entry === 'string' && entry.length > 0))];
}

/** A stored view with junk fields turned into their defaults, or null if it is not a view at all. */
export function normalizeTasksView(value: unknown): TasksView | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const view = value as Record<string, unknown>;
  const filters = view.filters && typeof view.filters === 'object' ? (view.filters as Record<string, unknown>) : {};
  return {
    search: typeof view.search === 'string' ? view.search : '',
    filters: { status: strings(filters.status), priority: strings(filters.priority) },
    projectFilter: typeof view.projectFilter === 'string' ? view.projectFilter : '',
    sortBy: view.sortBy === 'title' ? 'title' : 'taskNumber',
    sortDirection: view.sortDirection === 'asc' ? 'asc' : 'desc'
  };
}

/** A session whose folder is a Notion project shows that project; any other keeps the saved one. */
export function projectToShow(sessionProject: string, savedProject: string): string {
  return sessionProject || savedProject;
}
