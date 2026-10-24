/**
 * filesStore.svelte.ts — Svelte 5 runes-based "files" data-spine state.
 *
 * Holds the core source-files STATE (the scanned record list, the open/selected
 * record, its preview content, editor tabs, dirty-draft tracking, navigation
 * history, the tree filter/expansion UI, file-action status, and the files-side
 * view of the active scan) in a single reactive `$state` object. Extracted from
 * `src/routes/+page.svelte` (master plan, Phase A) so the data-spine lives in one
 * shared module instead of inline page `let` declarations. Consumers
 * `import { files }` and read/write `files.x`; reads/writes are tracked by
 * Svelte's runes runtime.
 *
 * Pattern mirrors `src/lib/shell/settings/settingsStore.svelte.ts` / `dockLayoutStore.svelte.ts`
 * exactly: module-level `export const <store> = $state({...})`.
 *
 * SCOPE: this module owns the *values* only. All `read_source_file` /
 * `write_source_file` / `list_source_files` / scan Tauri calls, every `$effect`,
 * persistence, the dockview teleport plumbing, and the page `$derived` graph
 * (`filteredRecords`, `sourceTree`, `visibleTreeRows`, `selectedSourceDirty`, …)
 * STAY in `+page.svelte` and act on `files.*`. (`.svelte.ts` cannot host `$effect`.)
 *
 * NOTES:
 * - There is intentionally NO single `dirtyByPath` map. Dirty is derived by
 *   comparing `draftByPath` vs `savedByPath` via the page predicate
 *   `isSourcePathDirty` (relocated to a store helper in a later sub-step).
 * - `runtime` and `error` are deliberately NOT held here: they are cross-cutting
 *   status/error strings also written by git/terminal/LSP paths, so they remain
 *   page-level `$state`.
 * - `SourceEditorDisplayMode` mirrors the identical string-literal union still
 *   declared in `+page.svelte` (`'source' | 'preview'`); the structural unions
 *   assign cleanly in both directions (same approach `dockLayoutStore` uses for
 *   `SourceActivityMode`).
 */

import type {
	SourceRecord,
	SourcePreview,
	SourceOpenTab,
	SourceRecentRecord,
	SourceNavigationLocation,
	SourceScanStats
} from '$lib/sourceData';
import type { NativeSourceScanProgress } from '$lib/tauriSource';

// ── Types (mirror the page's local alias) ─────────────────────────────────────

export type SourceEditorDisplayMode = 'source' | 'preview';

// ── Reactive state ────────────────────────────────────────────────────────────

/**
 * The single reactive "files" state object. Read/write fields directly
 * (e.g. `files.records`, `files.selectedRecord`); reads/writes are tracked by
 * Svelte's runes runtime. Effects, persistence, Tauri calls, and the page
 * `$derived` graph that react to these live in `+page.svelte`.
 */
export const files = $state({
	// ── records / selection / preview ──────────────────────────────────────────
	/** The scanned source-file records for the active project. */
	records: [] as SourceRecord[],
	/** The currently-selected record (open in the editor), or null. */
	selectedRecord: null as SourceRecord | null,
	/** The loaded preview/content for the selected record, or null. */
	preview: null as SourcePreview | null,
	/** Target line to reveal in the editor (1-based), or null. */
	selectedSourceLine: null as number | null,
	/** Monotonic counter bumped to re-trigger an editor scroll-to-line. */
	selectedSourceLineRequestId: 0,

	// ── tabs / recents ──────────────────────────────────────────────────────────
	/** Open editor tabs (across projects; the page filters by selected project). */
	openTabs: [] as SourceOpenTab[],
	/** Recently-opened records (across projects; page filters by project). */
	recentRecords: [] as SourceRecentRecord[],

	// ── dirty / drafts (NO single dirty map — see module note) ───────────────────
	/** In-editor draft content keyed by path. */
	draftByPath: {} as Record<string, string>,
	/** Last-saved-on-disk content keyed by path (dirty = draft !== saved). */
	savedByPath: {} as Record<string, string>,
	/** Synthetic SourceRecords for paths edited but not present in the scan. */
	workspaceEditRecordsByPath: {} as Record<string, SourceRecord>,
	/** Per-path editor display mode (source vs rendered markdown preview). */
	markdownPreviewModeByPath: {} as Record<string, SourceEditorDisplayMode>,

	// ── navigation history ───────────────────────────────────────────────────────
	/** Back stack for in-editor navigation. */
	navBackStack: [] as SourceNavigationLocation[],
	/** Forward stack for in-editor navigation. */
	navForwardStack: [] as SourceNavigationLocation[],

	// ── tree / filter UI ──────────────────────────────────────────────────────────
	/** The file-tree filter query. */
	query: '',
	/** Expanded folder ids in the file tree. */
	expandedFolderIds: new Set<string>(),

	// ── file-action status ──────────────────────────────────────────────────────
	/** Human-readable status line for the last file action. */
	fileActionStatus: '',
	/** In-flight file action discriminator ('copy'|'save'|'save-all'|'paste-read'|…) or ''. */
	fileActionBusy: '',

	// ── files-side view of the active scan ────────────────────────────────────────
	scan: {
		/** Initial/whole-index loading. */
		loading: true,
		/** A scan is actively running. */
		scanning: false,
		/** The id of the active scan (for progress/cancel correlation), or ''. */
		activeScanId: '',
		/** Latest native scan progress event, or null. */
		progress: null as NativeSourceScanProgress | null,
		/** Stats for the most recent completed scan, or null. */
		stats: null as SourceScanStats | null,
		/** Whether the scan hit the record cap. */
		limitReached: false
	}
});

// ── Derived predicates ─────────────────────────────────────────────────────────

/**
 * Whether the file at `path` has unsaved edits — i.e. an in-editor draft that
 * differs from the last-saved-on-disk content. Reads `files.draftByPath` /
 * `files.savedByPath` at call time, so callers in the page's `$derived`/`$effect`
 * graph stay reactive (the tracked read happens when the deriving runs).
 *
 * Relocated from `+page.svelte` (Phase A3); there is no single dirty map.
 */
export function isSourcePathDirty(path: string): boolean {
	const draft = files.draftByPath[path];
	const saved = files.savedByPath[path];
	return draft !== undefined && saved !== undefined && draft !== saved;
}
