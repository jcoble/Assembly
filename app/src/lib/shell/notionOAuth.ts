import type { NotionTaskSettings } from './notionTasks.ts';

interface NotionOAuthAttempt {
  authorizationUrl: string;
  state: string;
  verifier: string;
}

const LOGIN_TIMEOUT_MS = 5 * 60 * 1_000;

/** The sign-in waiting for the Notion callback page to hand back its claim code. */
let waitingSignIn: { state: string; receive: (code: string) => void } | null = null;

function assertActive(signal: AbortSignal): void {
  if (signal.aborted) throw signal.reason ?? new DOMException('Notion sign-in stopped', 'AbortError');
}

async function invokeDesktop<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  const { isNativeTauriRuntime } = await import('$lib/tauriSource.ts');
  if (!isNativeTauriRuntime()) throw new Error('Notion sign-in is available in the desktop app.');
  const { invoke } = await import('@tauri-apps/api/core');
  return await invoke<T>(command, args);
}

/** Take the claim code from an `assembly://notion-oauth` link. Only the
 * sign-in this app started with that state takes it; a link for any other
 * state was not started here and is ignored. */
export function receiveNotionOAuthCode(state: string, code: string): void {
  if (waitingSignIn?.state === state) waitingSignIn.receive(code);
}

/** Wait for the claim code of the sign-in that started with `state`. */
export async function waitForClaimCode(state: string, signal: AbortSignal, openBrowser: () => Promise<void>): Promise<string> {
  const opening = new AbortController();
  signal = AbortSignal.any([signal, opening.signal]);
  const code = new Promise<string>((resolve, reject) => {
    const finish = (settle: () => void): void => {
      clearTimeout(timeout);
      signal.removeEventListener('abort', stop);
      if (waitingSignIn === waiting) waitingSignIn = null;
      settle();
    };
    const stop = (): void =>
      finish(() => reject(signal.reason ?? new DOMException('Notion sign-in stopped', 'AbortError')));
    const waiting = { state, receive: (code: string) => finish(() => resolve(code)) };
    const timeout = setTimeout(
      () => finish(() => reject(new Error('Notion sign-in timed out. Try connecting again.'))),
      LOGIN_TIMEOUT_MS
    );
    waitingSignIn = waiting;
    signal.addEventListener('abort', stop, { once: true });
    if (signal.aborted) stop();
  });
  try {
    const [claimCode] = await Promise.all([code, openBrowser()]);
    return claimCode;
  } finally {
    opening.abort();
  }
}

export async function connectNotion(signal: AbortSignal): Promise<NotionTaskSettings> {
  assertActive(signal);
  const attempt = await invokeDesktop<NotionOAuthAttempt>('begin_notion_oauth');
  assertActive(signal);
  const { openUrl } = await import('@tauri-apps/plugin-opener');
  const claimCode = await waitForClaimCode(attempt.state, signal, () => openUrl(attempt.authorizationUrl));
  assertActive(signal);
  return await invokeDesktop<NotionTaskSettings>('claim_notion_oauth', {
    state: attempt.state,
    verifier: attempt.verifier,
    claimCode
  });
}
