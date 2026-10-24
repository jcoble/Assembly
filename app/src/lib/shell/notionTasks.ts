import type { ChipTone } from '$lib/components/ui/chip/index.js';
import { isNativeTauriRuntime } from '$lib/tauriSource.ts';

export interface NotionTaskSettings {
  dataSourceId: string;
  hasToken: boolean;
}

export interface NotionTaskRow {
  sourceTaskId: string;
  title: string;
  project: string;
  status: string;
  priority: string | null;
  assignee: string | null;
  dueDate: string | null;
  sourceUrl: string;
  fetchedAtMs: number;
}

export interface NotionTaskRefreshReceipt {
  count: number;
  fetchedAtMs: number;
}

export interface NotionTaskPage {
  tasks: NotionTaskRow[];
  projects: string[];
  statuses: string[];
  priorities: string[];
  hasMore: boolean;
}

async function invokeDesktop<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  if (!isNativeTauriRuntime()) throw new Error('Notion Tasks is available in the desktop app.');
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<T>(command, args);
}

export async function readNotionTaskSettings(): Promise<NotionTaskSettings> {
  return await invokeDesktop('read_notion_task_settings');
}

export async function saveNotionTaskSettings(
  dataSourceId: string,
  token: string
): Promise<NotionTaskSettings> {
  return await invokeDesktop('save_notion_task_settings', { dataSourceId, token });
}

export async function clearNotionTaskSettings(): Promise<void> {
  return await invokeDesktop('clear_notion_task_settings');
}

export async function listNotionTasks(
  offset: number,
  limit = 100,
  search = '',
  project = '',
  statuses: string[] = [],
  priorities: string[] = [],
  sortBy = 'taskNumber',
  sortDirection = 'desc'
): Promise<NotionTaskPage> {
  return await invokeDesktop('list_notion_tasks', {
    offset,
    limit,
    search,
    project,
    statuses,
    priorities,
    sortBy,
    sortDirection
  });
}

/** The cached task a Notion page id belongs to, or null when the cache does not hold it. */
export async function readCachedNotionTask(sourceTaskId: string): Promise<NotionTaskRow | null> {
  return await invokeDesktop('read_cached_notion_task', { sourceTaskId });
}

export async function refreshNotionTasks(): Promise<NotionTaskRefreshReceipt> {
  return await invokeDesktop('refresh_notion_tasks');
}

export function taskStatusTone(status: string): ChipTone {
  switch (status.trim().toLowerCase()) {
    case 'done':
    case 'complete':
    case 'completed':
      return 'good';
    case 'doing':
    case 'in progress':
    case 'working':
      return 'live';
    case 'blocked':
      return 'bad';
    case 'todo':
    case 'to do':
    case 'not started':
      return 'attention';
    default:
      return 'neutral';
  }
}

export function taskPriorityTone(priority: string): ChipTone {
  switch (priority.trim().toLowerCase()) {
    case 'high':
      return 'bad';
    case 'medium':
      return 'attention';
    default:
      return 'neutral';
  }
}

/** A title that starts with its task key, like "[TSK-12] Fix login", shows the key apart. */
export function splitTaskTitle(title: string): { key: string; text: string } {
  const match = /^\[([A-Z]+-\d+)\]\s*/.exec(title);
  return match ? { key: match[1], text: title.slice(match[0].length) } : { key: '', text: title };
}
