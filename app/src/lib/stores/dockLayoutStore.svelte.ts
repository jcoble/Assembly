/**
 * dockLayoutStore.svelte.ts — Svelte 5 runes-based dock-layout state.
 *
 * Holds the core dock-layout STATE for the Source workbench shell in a single
 * reactive `$state` object. Extracted from `src/routes/+page.svelte` (Phase 0b,
 * Task 6) so the layout state lives in one shared module instead of inline page
 * `let` declarations. Consumers `import { dock }` and read/write `dock.x`;
 * reads/writes are tracked by Svelte's runes runtime.
 *
 * Pattern mirrors `src/lib/shell/settings/settingsStore.svelte.ts` exactly: module-level
 * `export const <store> = $state({...})`.
 *
 * SCOPE (Task 6, re-scoped per plan review C2): this module owns the *values*
 * only. All effects, persistence, DOM/terminal/workbench wiring, dockview
 * teleport plumbing, presets, snapshots, and resize handlers STAY in the page
 * and act on `dock.*`. (`.svelte.ts` modules cannot host `$effect`.)
 *
 * NOTE: `SourceActivityMode` / `SourceLayoutPresetID` mirror the identical
 * string-literal unions still declared in `+page.svelte`; the structural unions
 * assign cleanly in both directions. The numeric width defaults mirror the
 * page consts `sidePaneDefaultWidth` (407) / `contextPaneDefaultWidth` (330) —
 * the page remains the source of truth for those consts (used by its resize /
 * preset / snapshot code); only the initial values are duplicated here.
 */

import {
	createDefaultSourceDockLayout,
	type SourceDockLayout
} from '$lib/sourceDockLayout';

// ── Types (mirror the page's local aliases) ──────────────────────────────────

export type SourceActivityMode =
	| 'files'
	| 'clipboard'
	| 'conversations'
	| 'runs'
	| 'sessions'
	| 'agents'
	| 'worktrees'
	| 'git';

export type SourceLayoutPresetID = 'review' | 'code' | 'git' | 'runs' | 'sessions' | 'custom';

// ── Default widths (mirror page consts; see module note) ──────────────────────

const sidePaneDefaultWidth = 407;
const contextPaneDefaultWidth = 330;

// ── Reactive state ────────────────────────────────────────────────────────────

/**
 * The single reactive dock-layout state object. Read/write fields directly
 * (e.g. `dock.activityMode`, `dock.layout`); reads/writes are tracked by
 * Svelte's runes runtime. Effects, persistence, and DOM/dockview wiring that
 * react to these live in `+page.svelte`.
 */
export const dock = $state({
	/** Which activity-rail mode the left pane is showing. */
	activityMode: 'files' as SourceActivityMode,
	/** The currently-applied layout preset id. */
	layoutPreset: 'code' as SourceLayoutPresetID,
	/** Left (activity/explorer) pane width in px. */
	sidePaneWidth: sidePaneDefaultWidth,
	/** Right (context) pane width in px. */
	contextPaneWidth: contextPaneDefaultWidth,
	/** The full dockview layout model (groups / panels / hidden / active). */
	layout: createDefaultSourceDockLayout() as SourceDockLayout
});
