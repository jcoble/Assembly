// Whether a host that was just started is up, from the host itself. Nothing here needs a spare inotify watch: `journalctl
// --follow` does, and on a machine out of them it reads nothing, so a running host looked as if it had failed.
import { setTimeout as delay } from 'node:timers/promises';

export class HostDown extends Error {}
export class HostUnseen extends Error {}

export type HostState = { active: boolean; pid: string };

// The first start can migrate stored data, so this matches the release install's wait.
const LIMIT_MS = 120_000, RETRY_MS = 500;

// Asks until the host answers. Stops at once when the started process is gone; the limit only decides how long a host
// that's still running can stay silent. The signal's owner ends the wait.
export async function waitForAnswer<T>(options: { pid: string; state: () => Promise<HostState>; answer: () => Promise<T>; signal: AbortSignal; limitMs?: number; retryMs?: number }): Promise<T> {
  const { pid, state, answer, signal, limitMs = LIMIT_MS, retryMs = RETRY_MS } = options;
  const deadline = Date.now() + limitMs;
  const cancelled = () => { if (signal.aborted) throw Error('sync cancelled before the host answered'); };
  let last = 'no answer yet';
  for (;;) {
    cancelled();
    try { return await answer(); } catch (error) { cancelled(); last = error instanceof Error ? error.message : String(error); }
    let seen: HostState | undefined;
    try { seen = await state(); } catch (error) { cancelled(); last = error instanceof Error ? error.message : String(error); }
    if (seen && (!seen.active || seen.pid !== pid))
      throw new HostDown(`Remote backend failed to start: the host process ${pid} is no longer running. Inspect it before retrying. No automatic rollback was attempted because startup may have migrated the database.`);
    if (Date.now() >= deadline)
      throw new HostUnseen(`Couldn't confirm the remote backend: ${seen ? `the host is still running, but it didn't answer` : `the service couldn't be read and the host didn't answer`} within ${Math.round(limitMs / 1000)} seconds (${last}). This doesn't mean it's down; check it before retrying.`);
    await delay(retryMs, undefined, { signal }).catch(() => {});
  }
}
