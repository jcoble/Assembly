/**
 * prList.ts — the view model for "what pull requests are open on this repo".
 *
 * The list comes from `gh pr list`, so the one failure worth its own state is
 * `gh` not being installed at all. That is not an error the person did — it is
 * a missing tool with a one-line fix — so it gets its own state and its own
 * sentence instead of a red box with a spawn failure in it.
 *
 * Pure: every function takes a model and returns a new one. The test drives the
 * whole lifecycle without a browser or a GitHub CLI.
 */

import type { PullRequestSummary } from '$lib/tauriSource';

export type PullRequestListState = 'idle' | 'loading' | 'ready' | 'failed' | 'no-gh';

export interface PullRequestListModel {
  state: PullRequestListState;
  pullRequests: PullRequestSummary[];
  error: string;
}

/** Said when the GitHub CLI is not on the machine. Matches the backend's wording. */
export const NO_GH_MESSAGE = 'GitHub CLI (`gh`) is not installed or not on PATH';

/** What to do about it, in one line the panel shows under the message. */
export const NO_GH_HINT = 'Install it with `brew install gh`, then sign in with `gh auth login`.';

export function createPullRequestListModel(): PullRequestListModel {
  return { state: 'idle', pullRequests: [], error: '' };
}

export function beginPullRequestListLoad(model: PullRequestListModel): PullRequestListModel {
  return { ...model, state: 'loading', error: '' };
}

export function finishPullRequestListLoad(
  model: PullRequestListModel,
  pullRequests: PullRequestSummary[]
): PullRequestListModel {
  return { ...model, state: 'ready', pullRequests, error: '' };
}

/** True when the message is `gh` missing rather than anything about the repository. */
export function isMissingGhError(error: unknown): boolean {
  const message = (error instanceof Error ? error.message : String(error ?? '')).toLowerCase();
  return message.includes('gh') && (message.includes('not installed') || message.includes('not on path'));
}

export function failPullRequestList(
  model: PullRequestListModel,
  error: unknown
): PullRequestListModel {
  if (isMissingGhError(error)) {
    return { ...model, state: 'no-gh', pullRequests: [], error: NO_GH_MESSAGE };
  }
  const message = error instanceof Error ? error.message : String(error ?? '');
  return {
    ...model,
    state: 'failed',
    error: message.trim() === '' ? 'Could not read the open pull requests.' : message.trim()
  };
}

/** The short words beside a pull request: its checks, and whether it is a draft. */
export function describePullRequestRow(pullRequest: PullRequestSummary): string {
  const parts: string[] = [];
  if (pullRequest.isDraft) parts.push('Draft');
  parts.push(pullRequest.headBranch || 'unknown branch');
  parts.push(describeChecks(pullRequest));
  return parts.join(' · ');
}

/** The checks in one readable phrase, never a bare status word. */
export function describeChecks(pullRequest: PullRequestSummary): string {
  if (pullRequest.checks === 'none') return 'no checks';
  if (pullRequest.checkSummary) return pullRequest.checkSummary;
  return `checks ${pullRequest.checks}`;
}

/** Which colour token the check state deserves. Named, so the test can read it. */
export function checkTone(pullRequest: PullRequestSummary): 'good' | 'bad' | 'attention' | 'quiet' {
  if (pullRequest.checks === 'failing') return 'bad';
  if (pullRequest.checks === 'pending') return 'attention';
  if (pullRequest.checks === 'passing') return 'good';
  return 'quiet';
}

/** The heading's count, or the reason there is none to count. */
export function describePullRequestListSummary(model: PullRequestListModel): string {
  if (model.state === 'loading') return 'Reading open pull requests…';
  if (model.state === 'no-gh') return NO_GH_MESSAGE;
  if (model.state === 'failed') return model.error;
  if (model.state === 'idle') return '';
  const count = model.pullRequests.length;
  if (count === 0) return 'No open pull requests.';
  return count === 1 ? '1 open pull request' : `${count} open pull requests`;
}
