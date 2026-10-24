import { untrack } from 'svelte';
import { BASE, BASE_WS } from './host/backend';
import { counters } from './debug.svelte';
import type { Row, ToolDetail } from './rows/store';
import type { SlashCommand } from './composer/slashMenu';

// The Mac's .NET backend (dotnet/Assembly.Mac), which serves every route. One token.
export { BASE, BASE_WS };

// `parentThreadId` is set on a subagent's thread: the thread whose agent runs it.
export type Thread = { threadId: string; agent: string; repoUrl: string; title: string | null; machine: string | null; parentThreadId?: string | null; updatedAt: number };
export type MemorySample = { appPid: number | null; appRssKb: number | null; webContentPid: number | null; webContentRssKb: number | null; backendPid: number; backendRssKb: number | null };

// Every HTTP call takes its owner's signal and is counted while pending.
// A Blob body is sent as-is with its own type; anything else as JSON. An image
// response comes back as a Blob. The count is untracked, so an effect that
// starts a fetch (a tool row opening) does not rerun on it.
async function api<T>(token: string, url: string, signal: AbortSignal, method = 'GET', body?: unknown): Promise<T> {
  untrack(() => counters.fetches++);
  try {
    const blob = body instanceof Blob;
    const res = await fetch(url, {
      method,
      signal,
      headers: {
        authorization: `Bearer ${token}`,
        ...(body !== undefined && { 'content-type': blob ? body.type : 'application/json' }),
      },
      body: body === undefined ? undefined : blob ? body : JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`${method} ${url} returned ${res.status}`);
    if (res.status !== 200) return undefined as T;
    return (res.headers.get('content-type')?.startsWith('image/') ? await res.blob() : await res.json()) as T;
  } finally {
    counters.fetches--;
  }
}

const threadPath = (threadId: string) => `/threads/${encodeURIComponent(threadId)}`;

export const getThread = (token: string, threadId: string, signal: AbortSignal) =>
  api<Thread>(token, `${BASE}${threadPath(threadId)}`, signal);

export const listThreads = (token: string, signal: AbortSignal) =>
  api<{ threads: Thread[]; nextBefore: number | null }>(token, `${BASE}/threads`, signal);

// A new thread runs `agent`, one of listAgents(machine), on `machine`, one of listMachines(), or here when it's left out.
export const createThread = (token: string, repoUrl: string, signal: AbortSignal, { machine, agent = 'claude' }: { machine?: string; agent?: string } = {}) =>
  api<{ threadId: string }>(token, `${BASE}/threads`, signal, 'POST', { agent, repoUrl, ...(machine && { machine }) });

export type Agent = { id: string; name: string };

// The agents a new thread on `machine` (here when it's left out) can run.
export const listAgents = (token: string, signal: AbortSignal, machine?: string) =>
  api<{ agents: Agent[] }>(token, `${BASE}/agents${machine ? `?machine=${encodeURIComponent(machine)}` : ''}`, signal);

// A thread's session config, as its agent reports it (ACP configOptions): one menu per select option.
export type ConfigOption = {
  id: string;
  name: string;
  category?: string;
  type: string;
  currentValue?: string;
  options?: { value: string; name: string; description?: string }[];
};
// `options` is null until the agent has opened a session; `choices` are kept per thread and applied to every session.
// `commands` are the agent's slash commands, the thread's or else the agent's latest; null before it sent any.
export type ThreadConfig = { options: ConfigOption[] | null; choices: Record<string, string>; commands?: SlashCommand[] | null };

export const getConfig = (token: string, threadId: string, signal: AbortSignal) =>
  api<ThreadConfig>(token, `${BASE}${threadPath(threadId)}/config`, signal);

export const setConfig = (token: string, threadId: string, configId: string, value: string, signal: AbortSignal) =>
  api<ThreadConfig>(token, `${BASE}${threadPath(threadId)}/config`, signal, 'POST', { configId, value });

// The remote machines a new thread can run on, besides this one.
export const listMachines = (token: string, signal: AbortSignal) =>
  api<{ machines: string[] }>(token, `${BASE}/machines`, signal);

// Uploads one image to the thread's attachment dir on the box; returns its path there.
export const uploadAttachment = (token: string, threadId: string, file: File, signal: AbortSignal) =>
  api<{ path: string }>(token, `${BASE}${threadPath(threadId)}/attachments`, signal, 'POST', file);

// Reads back an image stored by uploadAttachment, by the file name in its path.
export const fetchAttachment = (token: string, threadId: string, name: string, signal: AbortSignal) =>
  api<Blob>(token, `${BASE}${threadPath(threadId)}/attachments/${encodeURIComponent(name)}`, signal);

// A page of rows ordered by first seq: the newest page when `before` is null.
// `activeRunId` is the run going now, or null when the thread is idle.
export const rowsPage = (token: string, threadId: string, before: number | null, limit: number, signal: AbortSignal) =>
  api<{ rows: Row[]; hasBefore: boolean; activeRunId: string | null }>(
    token,
    `${BASE}${threadPath(threadId)}/rows?${before === null ? '' : `before=${before}&`}limit=${limit}`,
    signal,
  );

// A tool call's output, diff and path, fetched when its row is opened.
export const toolDetail = (token: string, threadId: string, itemId: string, signal: AbortSignal) =>
  api<ToolDetail>(token, `${BASE}${threadPath(threadId)}/tools/${encodeURIComponent(itemId)}`, signal);

// Answers a card the agent opened: `{ optionId }` for a permission, `{ action, content? }` for a question.
// A card that is no longer open answers 409.
export const answerAsk = (token: string, threadId: string, askId: string, answer: Record<string, unknown>, signal: AbortSignal) =>
  api<{ answered: boolean }>(token, `${BASE}${threadPath(threadId)}/asks/${encodeURIComponent(askId)}`, signal, 'POST', answer);

// Stops background work the agent said it can stop. One that isn't running, or can't be stopped, answers 409.
export const stopTask = (token: string, threadId: string, taskId: string, signal: AbortSignal) =>
  api<{ stopped: boolean }>(token, `${BASE}${threadPath(threadId)}/tasks/${encodeURIComponent(taskId)}/stop`, signal, 'POST', {});

// The backend writes the user row, then starts the turn on the agent.
export const sendText = (token: string, threadId: string, text: string, signal: AbortSignal) =>
  api<{ runId: string }>(token, `${BASE}${threadPath(threadId)}/send`, signal, 'POST', { text });

// Closes the backend's live sockets, as a dropped link would; they reopen from their watermarks.
export const dropLink = (token: string, ms: number, signal: AbortSignal) =>
  api<void>(token, `${BASE}/debug/drop`, signal, 'POST', { ms });

export const stopRun = (token: string, runId: string, signal: AbortSignal) =>
  api<void>(token, `${BASE}/runs/${encodeURIComponent(runId)}/stop`, signal, 'POST');
