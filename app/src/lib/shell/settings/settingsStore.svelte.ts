/**
 * settingsStore.svelte.ts — Svelte 5 runes-based settings store.
 *
 * Holds appearance / editor / terminal / general preferences in a reactive
 * `$state` object. The active /next shell hydrates it from the native SQLite
 * app-settings row and writes changes through the shared Tauri bridge.
 *
 * Defaults mirror the app's current hardcoded values:
 *   - editor:   System code-face id / 13 / 21
 *   - terminal: the xterm config in +page.svelte (Google Sans Mono / 15 / 1.2)
 *   - general:  the source terminal app default ('Warp')
 *
 * NOTE: This module only owns the *values*. Applying them into Monaco / xterm
 * is a later wiring step in +page.svelte and is intentionally NOT done here.
 */

import {
	readAssemblySettingFromTauri,
	writeAssemblySettingFromTauri
} from '../../tauriSource';
import { DEFAULT_MONO_FONT_ID } from './themes/fontRegistry';
import { DEFAULT_THEME_ID, isThemeId } from './themes/themeRegistry';

// ── Types ──────────────────────────────────────────────────────────────────

export interface AppearanceSettings {
	themeId: string;
	/** Base UI font size in px (chrome/labels, not the editor). */
	appFontSize: number;
	/** The face the interface reads in — an id from fontRegistry.ts. */
	uiFontId: string;
	/** The face code is shown in outside the editor and terminal — same registry. */
	monoFontId: string;
}

export interface EditorSettings {
	/** A code-face id from fontRegistry.ts. */
	fontFamily: string;
	fontSize: number;
	/** Absolute line height in px (Monaco-style). */
	lineHeight: number;
	fontLigatures: boolean;
}

export interface TerminalSettings {
	fontFamily: string;
	fontSize: number;
	/** Unitless line-height multiplier (xterm-style). */
	lineHeight: number;
	cursorBlink: boolean;
}

export interface GeneralSettings {
	terminalApp: string;
}

/**
 * Where the Problems list is shown.
 *
 * 'bottom' is the strip under the middle of the shell it has always lived in;
 * 'hidden' shows it nowhere, which leaves the bottom strip with nothing in it,
 * so the shell closes the strip as well.
 *
 * There used to be a third answer, 'right', which put the list in the tool
 * column. That column is now eight fixed panels with no room for a ninth, so
 * the answer no longer names anywhere. A stored 'right' is not migrated by
 * hand: the check below already turns any unrecognised value back into
 * 'bottom', which is exactly the right outcome.
 */
export type ProblemsLocation = 'bottom' | 'hidden';

export const PROBLEMS_LOCATIONS: readonly ProblemsLocation[] = ['bottom', 'hidden'];

/** What each choice says in the settings dialog, in plain English. */
export const PROBLEMS_LOCATION_LABELS: Record<ProblemsLocation, string> = {
	bottom: 'In the strip along the bottom',
	hidden: 'Do not show it'
};

export interface PanelsSettings {
	problemsLocation: ProblemsLocation;
}

export interface IntelligenceSettings {
	/**
	 * Run language servers at all. Off stops every one that is running and
	 * nothing starts until it is on again — one switch over the whole app,
	 * because the per-project switch in each editor header was easy to find to
	 * turn on and hard to find again to turn off.
	 */
	languageServers: boolean;
	/** Per-server policy applied while Supercharged is on. */
	languageServerEnabled: {
		csharp: boolean;
		typescript: boolean;
		rust: boolean;
	};
}

/** The model, effort and access last chosen for one agent. */
export interface AgentConfigChoice {
	model: string | null;
	reasoningEffort: string | null;
	approvalPolicy: string | null;
}

export interface AgentsSettings {
	/**
	 * What was last chosen for each agent, by provider id. A new session opens
	 * on these. Without them a new session took its settings from whichever
	 * session of that agent happened to be loaded, or from nothing at all after
	 * a restart — which is why a choice never seemed to stick.
	 */
	lastChoiceByProvider: Record<string, AgentConfigChoice>;
}

export interface Settings {
	appearance: AppearanceSettings;
	editor: EditorSettings;
	terminal: TerminalSettings;
	general: GeneralSettings;
	panels: PanelsSettings;
	intelligence: IntelligenceSettings;
	agents: AgentsSettings;
}

/** A section key of {@link Settings}. */
export type SettingsSection = keyof Settings;

// ── Defaults ────────────────────────────────────────────────────────────────

export const SETTINGS_SETTING_KEY = 'workbench.settings';

/** Produces a fresh, deeply-independent copy of the default settings. */
export function defaultSettings(): Settings {
	return {
		appearance: {
			themeId: DEFAULT_THEME_ID,
			appFontSize: 13,
			uiFontId: 'system',
			monoFontId: 'system'
		},
		editor: {
			fontFamily: DEFAULT_MONO_FONT_ID,
			fontSize: 13,
			lineHeight: 21,
			fontLigatures: false
		},
		terminal: {
			fontFamily: 'JetBrains Mono',
			fontSize: 15,
			lineHeight: 1.2,
			cursorBlink: true
		},
		general: {
			terminalApp: 'Warp'
		},
		panels: {
			// Hidden by default: the strip reserved 180px under the conversation
			// whether or not there was anything in it, which read as dead space
			// above the status bar. Settings turns it back on.
			problemsLocation: 'hidden'
		},
		intelligence: {
			languageServers: true,
			languageServerEnabled: { csharp: true, typescript: true, rust: true }
		},
		agents: {
			lastChoiceByProvider: {}
		}
	};
}

// ── Persistence helpers ──────────────────────────────────────────────────────

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Merge a parsed/untrusted object onto the defaults, keeping only known keys
 * and only when the stored value has the same primitive type as the default.
 * This makes loading resilient to partial / stale / corrupt payloads and to
 * future schema additions (new default keys simply fill in).
 */
function mergeWithDefaults(raw: unknown): Settings {
	const base = defaultSettings();
	if (!isRecord(raw)) return base;

	for (const section of Object.keys(base) as SettingsSection[]) {
		const incoming = raw[section];
		if (!isRecord(incoming)) continue;
		const target = base[section] as unknown as Record<string, unknown>;
		for (const key of Object.keys(target)) {
			if (section === 'intelligence' && key === 'languageServerEnabled') continue;
			const next = incoming[key];
			if (next !== undefined && typeof next === typeof target[key]) {
				target[key] = next;
			}
		}
	}
	const intelligence = isRecord(raw.intelligence) ? raw.intelligence : {};
	const storedServers = isRecord(intelligence.languageServerEnabled)
		? intelligence.languageServerEnabled
		: {};
	for (const id of ['csharp', 'typescript', 'rust'] as const) {
		if (typeof storedServers[id] === 'boolean') base.intelligence.languageServerEnabled[id] = storedServers[id];
	}

	// The type check above only asks "is it a string?", which is not enough for a
	// setting whose value has to be one of two words: a stored "Right", or any
	// leftover from an older build, would pass it and then match no branch — and
	// for THIS setting the failure is silent and hard to escape, because the
	// non-default answer closes the strip that holds the control for changing it
	// back. Anything unrecognised goes back to showing the list where it has
	// always been.
	if (!PROBLEMS_LOCATIONS.includes(base.panels.problemsLocation)) {
		base.panels.problemsLocation = 'hidden';
	}
	if (!isThemeId(base.appearance.themeId)) {
		base.appearance.themeId = DEFAULT_THEME_ID;
	}
	if (typeof base.editor.fontFamily !== 'string' || !base.editor.fontFamily.trim()) {
		base.editor.fontFamily = DEFAULT_MONO_FONT_ID;
	}
	if (typeof base.terminal.fontFamily !== 'string' || !base.terminal.fontFamily.trim()) {
		base.terminal.fontFamily = 'JetBrains Mono';
	}

	return base;
}

// ── Reactive state ────────────────────────────────────────────────────────────

/**
 * The single reactive settings object. Read fields directly in components
 * (e.g. `settings.editor.fontSize`) and bind to them; reads/writes are
 * tracked by Svelte's runes runtime and auto-persisted.
 */
export const settings = $state<Settings>(defaultSettings());
const initialSettingsSnapshot = JSON.stringify($state.snapshot(settings) as Settings);

let settingsHydrated = $state(false);
let hydrationPromise: Promise<void> | null = null;
let hydrationBaseline: string | null = null;
let hydrationSawMutation = false;
let lastPersistedSettings: string | null = null;

function persistSettings(value: Settings): void {
	const serialized = JSON.stringify(value);
	lastPersistedSettings = serialized;
	void persistSettingsToStorage(value, serialized);
}

async function persistSettingsToStorage(value: Settings, serialized: string): Promise<void> {
	try {
		await writeAssemblySettingFromTauri(SETTINGS_SETTING_KEY, value);
	} catch {
		if (lastPersistedSettings === serialized) lastPersistedSettings = null;
	}
}

function noteSettingsMutation(): void {
	if (!settingsHydrated) hydrationSawMutation = true;
}

/**
 * Read the one native settings row. Calls made while a read is in flight share
 * it; a rejected read clears the in-flight handle so the next call can retry.
 * A value changed after this read began is left alone rather than replaced by
 * the late SQLite result.
 */
export async function hydrateSettings(): Promise<void> {
	if (settingsHydrated) return;
	if (hydrationPromise) return hydrationPromise;

	const baseline = JSON.stringify($state.snapshot(settings) as Settings);
	hydrationBaseline = baseline;
	const hydration = loadSettings(baseline);
	hydrationPromise = hydration;
	try {
		await hydration;
	} finally {
		if (hydrationPromise === hydration) hydrationPromise = null;
		hydrationBaseline = null;
	}
}

async function loadSettings(baseline: string): Promise<void> {
	const stored = await readAssemblySettingFromTauri(SETTINGS_SETTING_KEY);
	if (hydrationBaseline !== baseline) return;
	const current = $state.snapshot(settings) as Settings;
	const changed =
		hydrationSawMutation ||
		JSON.stringify(current) !== baseline ||
		JSON.stringify(current) !== initialSettingsSnapshot;
	if (!changed) {
		const hydrated = mergeWithDefaults(stored);
		for (const key of Object.keys(hydrated) as SettingsSection[]) {
			settings[key] = hydrated[key] as never;
		}
		lastPersistedSettings = JSON.stringify($state.snapshot(settings) as Settings);
	} else {
		lastPersistedSettings = null;
	}
	settingsHydrated = true;
}

// Save on any change. `$effect.root` lets us own an effect outside of a
// component, with a detach handle for teardown (tests / HMR). Reading the
// nested fields registers the dependency graph.
const disposeAutosave = $effect.root(() => {
	$effect(() => {
		const value = $state.snapshot(settings) as Settings;
		const serialized = JSON.stringify(value);
		if (!settingsHydrated) {
			if (hydrationBaseline !== null && serialized !== hydrationBaseline) {
				hydrationSawMutation = true;
			}
			return;
		}
		if (serialized !== lastPersistedSettings) persistSettings(value);
	});
});

/** Tear down the autosave effect (for tests / hot-reload). Rarely needed. */
export function disposeSettingsStore(): void {
	disposeAutosave();
}

// ── Update / reset API ────────────────────────────────────────────────────────

/**
 * Patch a single settings section. Only provided keys are changed; the rest of
 * the section is preserved. Persistence happens automatically via the effect.
 *
 * @example updateSettings('editor', { fontSize: 14 })
 */
export function updateSettings<S extends SettingsSection>(
	section: S,
	patch: Partial<Settings[S]>
): void {
	noteSettingsMutation();
	Object.assign(settings[section] as object, patch);
}

/**
 * Replace a settings section wholesale with the given values.
 */
export function setSection<S extends SettingsSection>(
	section: S,
	value: Settings[S]
): void {
	noteSettingsMutation();
	settings[section] = value;
}

/**
 * Reset everything (or a single section) back to defaults.
 *
 * @example resetSettings()            // all sections
 * @example resetSettings('terminal')  // just the terminal section
 */
export function resetSettings(section?: SettingsSection): void {
	noteSettingsMutation();
	const defaults = defaultSettings();
	if (section) {
		settings[section] = defaults[section] as never;
		return;
	}
	for (const key of Object.keys(defaults) as SettingsSection[]) {
		settings[key] = defaults[key] as never;
	}
}
