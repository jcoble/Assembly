/**
 * gitStore.svelte.ts — Svelte 5 runes-based "git" data-spine state.
 *
 * Holds the core git STATE: the selected project's working-tree status, the
 * cross-project repository summaries, the commit history + selected commit, the
 * selected-file diff, and the working-tree action (stage/unstage/fetch/pull/
 * push/commit) status used by the Git insights panel. Extracted from
 * `src/routes/+page.svelte` (master plan, Phase B) so the git data-spine lives
 * in one shared module instead of inline page `let` declarations. Consumers
 * `import { gitStore }` and read/write `gitStore.x`; reads/writes are tracked by
 * Svelte's runes runtime.
 *
 * NAME: exported as `gitStore` (not `git`) on purpose — consistent with the
 * `projectStore`/`filesStore` lesson (avoid short names that read like locals;
 * `gitStore` reads clearly next to the `gitStatus` local in the Activity loop).
 *
 * Pattern mirrors `src/lib/shell/settings/settingsStore.svelte.ts` / `filesStore.svelte.ts` /
 * `projectStore.svelte.ts` exactly: module-level `export const <store> = $state({...})`.
 *
 * SCOPE: this module owns the *values* only. ALL git loaders
 * (`loadProjectGitStatus`, `loadGitRepositorySummaries`, `loadGitCommitHistory`,
 * `loadSelectedSourceGitDiff`, …), the request-id guards (`projectGitStatusRequestID`,
 * `selectedSourceGitDiffRequestID`), every Tauri git call (`stageGitPathsFromTauri`,
 * `commitGitRepositoryFromTauri`, …), the git action handlers, and the page
 * `$derived` graph (`selectedProjectGitChangedFiles`, `selectedProjectGitGraph`,
 * `gitCommitHistorySummary`, …) STAY in `+page.svelte` and act on `gitStore.*`.
 * (`.svelte.ts` cannot host `$effect` — and there are no git `$effect`s anyway;
 * the git loaders are 100% imperative.)
 *
 * NOTE — what deliberately stays page-`$derived` (NOT here): the git derivations
 * have cross-domain deps (`files.selectedRecord`, `selectedProject`,
 * `sourceActivityFilter`, worktrees/orchestration runs), so they cannot be store
 * getters; they are computed in the page over `gitStore.*` + those other domains
 * and passed into `GitInsightsPanel` as plain value props.
 */

import type {
	ProjectGitStatus,
	GitRepositorySummary,
	GitCommitHistoryEntry,
	SourceGitDiff
} from '$lib/tauriSource';

// ── Reactive state ────────────────────────────────────────────────────────────

/**
 * The single reactive "git" state object. Read/write fields directly
 * (e.g. `gitStore.status`, `gitStore.actionBusy`); reads/writes are tracked by
 * Svelte's runes runtime. Loaders, request-id guards, Tauri calls, action
 * handlers, and the page `$derived` graph that react to these live in
 * `+page.svelte`.
 */
export const gitStore = $state({
	// ── working-tree status (selected project) ──────────────────────────────────
	/** The selected project's working-tree git status, or null. */
	status: null as ProjectGitStatus | null,
	/** A project git-status read is in flight. */
	statusLoading: false,
	/** Last project git-status error message. */
	statusError: '',

	// ── repository summaries (cross-project list; filtered page-side) ────────────
	/** Cross-project repository summaries (page filters by selected project). */
	repositorySummaries: [] as GitRepositorySummary[],
	/** A repository-summaries list read is in flight. */
	repositorySummariesLoading: false,
	/** Last repository-summaries error message. */
	repositorySummaryError: '',
	/** Source label for the repository summaries (e.g. 'browser preview'). */
	repositorySummarySource: 'browser preview',

	// ── commit history ───────────────────────────────────────────────────────────
	/** Commit history entries for the selected project. */
	commitHistory: [] as GitCommitHistoryEntry[],
	/** SHA of the currently-selected commit in the history list, or ''. */
	selectedCommitSha: '',
	/** A commit-history read is in flight. */
	commitHistoryLoading: false,
	/** Last commit-history error message. */
	commitHistoryError: '',
	/** Source label for the commit history (e.g. 'browser preview'). */
	commitHistorySource: 'browser preview',

	// ── selected-file diff ─────────────────────────────────────────────────────────
	/** Git diff for the selected source file, or null. */
	selectedDiff: null as SourceGitDiff | null,
	/** A selected-file diff read is in flight. */
	selectedDiffLoading: false,
	/** Last selected-file diff error message. */
	selectedDiffError: '',

	// ── working-tree actions (command drawer) ─────────────────────────────────────
	/** Draft commit message (bound to the commit textarea). */
	commitMessage: '',
	/** In-flight git working-tree action discriminator, or ''. */
	actionBusy: '' as 'stage' | 'unstage' | 'commit' | 'fetch' | 'pull' | 'push' | '',
	/** Human-readable status line for the last git action. */
	actionStatus: '',
	/** Last git action error message. */
	actionError: '',

	// ── row action menu ───────────────────────────────────────────────────────────
	/** Id of the git status row whose action menu is open, or null. */
	activeRowActionMenu: null as string | null
});
