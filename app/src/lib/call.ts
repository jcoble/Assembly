import { untrack } from 'svelte';
import { counters, debounce } from './debug.svelte';
import { BASE, TOKEN } from './host/backend';

// Every feature call and save goes through this file, so the plumbing is done once and can't be forgotten
// (lib/preferences/README.md). The backend's side is dotnet/Assembly.Infrastructure/Http/FeatureCalls.cs. The transport
// is HTTP today; only this file knows that.

const TIMEOUT_MS = 30_000;
const SAVE_AFTER_MS = 300;

// Where a failed call is told, once for the whole page (routes/+page.svelte); the console until then.
let report = (message: string) => console.warn(message);
export const onFailure = (handler: (message: string) => void) => (report = handler);

// POST /<feature>/<method> with the body as JSON, answering {result} or an {error}.
async function send<T>(feature: string, method: string, body: Record<string, unknown>, signal: AbortSignal, keepalive = false): Promise<T> {
  untrack(() => counters.fetches++);
  try {
    const res = await fetch(`${BASE}/${feature}/${encodeURIComponent(method)}`, {
      method: 'POST',
      signal,
      keepalive,
      headers: { authorization: `Bearer ${TOKEN ?? ''}`, 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    const answer = (await res.json().catch(() => ({}))) as { result?: T; error?: string };
    if (!res.ok) throw new Error(answer.error ?? `returned ${res.status}`);
    return answer.result as T;
  } finally {
    counters.fetches--;
  }
}

// What a call comes back with: its result, or why there's none. 'failed' was already told through onFailure;
// 'aborted' (a newer call took its place, its owner closed) isn't an error and shows nothing.
export type Outcome<T> = { ok: true; result: T } | { ok: false; why: 'failed' | 'aborted' };

// One of a feature's calls. With `key`, a newer call for the same key aborts the one still running, so a fast
// switch never waits on a stale load; `signal` (its owner closing) aborts it too, and so does running past 30 s. An
// aborted request closes, and the backend stops the work it started for it. counters.latest is how many keyed calls
// are running.
const running = new Map<string, AbortController>();
export async function call<T>(feature: string, method: string, body: Record<string, unknown> = {}, { key, signal }: { key?: string; signal?: AbortSignal } = {}): Promise<Outcome<T>> {
  const mine = new AbortController();
  if (key) {
    running.get(key)?.abort();
    running.set(key, mine);
    untrack(() => (counters.latest = running.size));
  }
  try {
    return { ok: true, result: await send<T>(feature, method, body, AbortSignal.any([mine.signal, AbortSignal.timeout(TIMEOUT_MS), ...(signal ? [signal] : [])])) };
  } catch (e) {
    if (mine.signal.aborted || signal?.aborted) return { ok: false, why: 'aborted' };
    report(`${feature}/${method} failed: ${e instanceof Error ? e.message : e}`);
    return { ok: false, why: 'failed' };
  } finally {
    if (key && running.get(key) === mine) running.delete(key);
    counters.latest = running.size;
  }
}

// One setting of a feature, saved through the feature's `set` call. Changes within 300 ms of each other go as one call
// with the newest value per key. A feature has one save on the way at a time, so an older save can never land after
// a newer one: what changes meanwhile waits and goes when it's back. A save is never cancelled once sent.
const unsaved = new Map<string, Record<string, unknown>>();
const sending = new Map<string, Record<string, unknown>>();
const saves = new Map<string, () => void>();
const page = new AbortController();
export function save(feature: string, key: string, value: unknown): void {
  const values = unsaved.get(feature) ?? unsaved.set(feature, {}).get(feature)!;
  values[key] = value;
  const later = saves.get(feature) ?? saves.set(feature, debounce(() => saveNow(feature), SAVE_AFTER_MS, page.signal)).get(feature)!;
  later();
}
// True while a change to the key hasn't reached the backend, so a load doesn't put an older value back.
export const unsavedKey = (feature: string, key: string): boolean => key in (unsaved.get(feature) ?? {}) || key in (sending.get(feature) ?? {});

function saveNow(feature: string) {
  const values = unsaved.get(feature);
  if (!values || sending.has(feature)) return;
  unsaved.delete(feature);
  sending.set(feature, values);
  send(feature, 'set', { values }, AbortSignal.timeout(TIMEOUT_MS), true)
    .catch((e) => report(`${feature} not saved: ${e instanceof Error ? e.message : e}`))
    .finally(() => {
      sending.delete(feature);
      saveNow(feature);
    });
}

// The window closing sends what's waiting now, with keepalive so it finishes after the page has gone. A save still
// on the way then goes again with the newer values on top, since the page can't wait for it. Not proven in the app's
// window yet (README in lib/preferences/).
function saveAll() {
  for (const feature of [...unsaved.keys()]) {
    const values = { ...sending.get(feature), ...unsaved.get(feature) };
    unsaved.delete(feature);
    send(feature, 'set', { values }, AbortSignal.timeout(TIMEOUT_MS), true).catch(() => {});
  }
}
window.addEventListener('pagehide', saveAll);
// Going to the background (minimize, hide, another Space) happens in normal use and the page lives on, so what's
// waiting goes now, but in order, one save at a time like any other.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') for (const feature of [...unsaved.keys()]) saveNow(feature);
});
