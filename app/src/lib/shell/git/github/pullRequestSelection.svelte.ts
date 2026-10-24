/**
 * The pull request the PR tab is showing. Shared so the top tab row can label
 * the tab "PR #N" without a second copy of the selection. `link` is a pull
 * request asked for from outside the tab (a github.com link in the chat); the
 * tab resolves it to a local project and clears it.
 */
import type { GithubPullRequestSummary } from '$lib/tauriSource';
import type { PullRequestLink } from '$lib/shell/workbenchNavigation';

export const pullRequestSelection = $state<{ selected: GithubPullRequestSummary | null; link: PullRequestLink | null }>({ selected: null, link: null });
