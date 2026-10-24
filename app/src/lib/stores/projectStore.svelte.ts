/**
 * projectStore.svelte.ts — Svelte 5 runes-based "project" data-spine state.
 *
 * Holds the core project STATE: the selected project id, user-added custom
 * project roots, the per-project last-open source path (the files↔project
 * bridge), and the project-keyed scan/index bookkeeping. Extracted from
 * `src/routes/+page.svelte` (master plan, Phase A). Consumers
 * `import { projectStore }` and read/write `projectStore.x`; reads/writes are
 * tracked by Svelte's runes runtime.
 *
 * NAME: exported as `projectStore` (not `project`) on purpose — `project` and
 * `projects` are both pervasive local/param names in `+page.svelte`
 * (`scanProject(project)`, `loadGitRepositorySummaries(projects)`, …), so a short
 * store name would shadow-collide. `projectStore` is collision-free.
 *
 * Pattern mirrors `src/lib/shell/settings/settingsStore.svelte.ts` / `dockLayoutStore.svelte.ts`.
 *
 * SCOPE: values only. All Tauri calls (`validate_project_root`,
 * `list_source_files`, …), every `$effect`, persistence, and the page `$derived`
 * graph STAY in `+page.svelte` and act on `projectStore.*`.
 *
 * NOTE — what deliberately stays page-`$derived` (NOT here): `selectedProject`
 * (the resolved root object) and `projectOptions` (the merged list) are page
 * `$derived` because they depend on the imported `defaultProjectRoots` /
 * `mergeProjectRoots`; and there is no standalone `root` string — the "root" is
 * `selectedProject.path`. Only the underlying `$state` (`selectedID`,
 * `customRoots`, …) lives here; the page derives the rest.
 */

import type { ProjectRoot, SourceScanCache, SourceScanEvidenceMode } from '$lib/sourceData';
import type { ProjectRootValidationResult } from '$lib/tauriSource';

// ── Reactive state ────────────────────────────────────────────────────────────

/**
 * The single reactive "project" state object. Read/write fields directly
 * (e.g. `projectStore.selectedID`, `projectStore.selectedSourcePaths`);
 * reads/writes are tracked by Svelte's runes runtime. `selectedProject` /
 * `projectOptions` remain page `$derived` over these fields + imported defaults
 * (see module note).
 */
export const projectStore = $state({
	/** The selected project's id. Seeded at hydrate with `defaultProjectRoots[0].id`. */
	selectedID: '',
	/** User-added custom project roots (merged with `defaultProjectRoots` in the page). */
	customRoots: [] as ProjectRoot[],

	/** Per-project last-open source path (project-id → path) — the files↔project bridge. */
	selectedSourcePaths: {} as Record<string, string>,

	// ── project-keyed scan / index bookkeeping ───────────────────────────────────
	scan: {
		/** Cached scan results keyed by project. */
		cache: {} as SourceScanCache,
		/** Resolved scan-evidence mode keyed by project. */
		modeByProject: {} as Record<string, SourceScanEvidenceMode>,
		/** Project ids currently being indexed in the background. */
		backgroundIndexingIDs: new Set<string>(),
		/** Last background-index error message keyed by project. */
		backgroundIndexErrorByProject: {} as Record<string, string>
	},

	/** Cached project-root validation results keyed by path. */
	rootValidationByPath: {} as Record<string, ProjectRootValidationResult>
});
