import { createAgentConversationRequestId, isNativeTauriRuntime } from '$lib/tauriSource.ts';

export interface NotionTaskProperty {
  name: string;
  kind: 'status' | 'select' | 'multi_select' | 'date' | 'relation' | 'people' | 'text'
    | 'url' | 'checkbox' | 'number' | 'unique_id' | 'created_time' | 'last_edited_time'
    | 'files' | 'other';
  values: string[];
  links: (string | null)[];
}

export interface NotionBlock {
  id: string;
  kind: string;
  markdown: string;
  checked: boolean | null;
  icon: string | null;
  children: NotionBlock[];
}

export interface NotionTaskComment {
  author: string;
  created: string;
  text: string;
}

export interface NotionTaskDetail {
  properties: NotionTaskProperty[];
  body: NotionBlock[];
  comments: NotionTaskComment[];
}

async function cancelNotionTaskDetail(requestId: number): Promise<void> {
  try {
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('cancel_notion_task_detail', { requestId });
  } catch {
    // The released viewer has no state to update if cancellation fails.
  }
}

export async function readNotionTaskDetail(
  sourceTaskId: string,
  signal: AbortSignal
): Promise<NotionTaskDetail> {
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
  if (!isNativeTauriRuntime()) throw new Error('Notion Tasks is available in the desktop app.');
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('@tauri-apps/api/core');
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
  const cancel = (): void => { void cancelNotionTaskDetail(requestId); };
  signal.addEventListener('abort', cancel, { once: true });
  try {
    const detail = await invoke<NotionTaskDetail>('read_notion_task_detail', { sourceTaskId, requestId });
    if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
    return detail;
  } finally {
    signal.removeEventListener('abort', cancel);
  }
}

export async function updateNotionTaskStatus(
  sourceTaskId: string,
  status: string,
  signal: AbortSignal
): Promise<void> {
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
  if (!isNativeTauriRuntime()) throw new Error('Notion Tasks is available in the desktop app.');
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('update_notion_task_status', { sourceTaskId, status });
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
}

/** Send one edit to Notion; the result is dropped once the task page has closed. */
async function invokeTaskEdit(command: string, args: Record<string, unknown>, signal: AbortSignal): Promise<void> {
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
  if (!isNativeTauriRuntime()) throw new Error('Notion Tasks is available in the desktop app.');
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke(command, args);
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
}

/** Change the task's title (`title`) or its `Priority`. */
export async function updateNotionTaskProperty(
  sourceTaskId: string,
  property: 'title' | 'Priority',
  value: string,
  signal: AbortSignal
): Promise<void> {
  await invokeTaskEdit('update_notion_task_property', { sourceTaskId, property, value }, signal);
}

/** Replace a block's text with Markdown, or tick a to-do. */
export async function updateNotionBlock(
  block: Pick<NotionBlock, 'id' | 'kind'>,
  change: { markdown: string } | { checked: boolean },
  signal: AbortSignal
): Promise<void> {
  await invokeTaskEdit('update_notion_block', { blockId: block.id, kind: block.kind, ...change }, signal);
}

/** Add a paragraph, written as Markdown, to the end of the task page. */
export async function appendNotionTaskText(sourceTaskId: string, markdown: string, signal: AbortSignal): Promise<void> {
  await invokeTaskEdit('append_notion_task_text', { sourceTaskId, markdown }, signal);
}

/** Upload an image and add it to the end of the task page. */
export async function appendNotionTaskImage(sourceTaskId: string, file: File, signal: AbortSignal): Promise<void> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  if (signal.aborted) throw signal.reason ?? new DOMException('Task closed', 'AbortError');
  let binary = '';
  for (let index = 0; index < bytes.length; index += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  }
  await invokeTaskEdit(
    'append_notion_task_image',
    { sourceTaskId, fileName: file.name || 'screenshot.png', contentType: file.type, dataBase64: btoa(binary) },
    signal
  );
}
