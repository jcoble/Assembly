<!-- Settings screen using the shared row layout and native settings store. -->
<script lang="ts">
  import { onDestroy, type Snippet } from 'svelte';
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import Palette from '@lucide/svelte/icons/palette';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import Search from '@lucide/svelte/icons/search';
  import SlidersHorizontal from '@lucide/svelte/icons/sliders-horizontal';
  import Sparkles from '@lucide/svelte/icons/sparkles';
  import SquareTerminal from '@lucide/svelte/icons/square-terminal';
  import Type from '@lucide/svelte/icons/type';
  import Server from '@lucide/svelte/icons/server';
  import Puzzle from '@lucide/svelte/icons/puzzle';
  import RemoteConnections from '../connections/RemoteConnections.svelte';
  import ProviderList from './ProviderList.svelte';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import Download from '@lucide/svelte/icons/download';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import SettingsSelect from '$lib/shell/settings/SettingsSelect.svelte';
  import HelperSessionSettings from '$lib/shell/settings/HelperSessionSettings.svelte';
  import ProviderCliUpdateControl from '$lib/shell/providers/ProviderCliUpdateControl.svelte';
  import ProviderUpdateControl from '$lib/shell/providers/ProviderUpdateControl.svelte';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { Slider } from '$lib/components/ui/slider/index.js';
  import { Switch } from '$lib/components/ui/switch/index.js';
  import {
    PROBLEMS_LOCATIONS,
    PROBLEMS_LOCATION_LABELS,
    defaultSettings,
    resetSettings,
    settings,
    type ProblemsLocation,
    type SettingsSection
  } from '$lib/shell/settings/settingsStore.svelte';
  import {
    setLanguageServerEnabled,
    setLanguageServersEnabled,
    type LanguageServerId
  } from '$lib/shell/files/editor/sourceIntelligence';
  import {
    readHelperSettingsFromTauri,
    listInstalledFontFamiliesFromTauri,
    setHelperKeyFromTauri,
    testHelperFromTauri,
    writeHelperSettingsFromTauri,
    type HelperSettingsView,
    type HelperRoute,
    type HelperVendor
  } from '$lib/tauriSource';
  import { DEFAULT_THEME_ID } from '$lib/shell/settings/themes/themeRegistry';
  import { apply as applyTheme, themeChoices } from '$lib/shell/settings/themes/themeService';
  import { applyUiFont, applyMonoFont } from '$lib/shell/settings/themes/fontService';
  import { getMonoFont, mergeFontChoices, UI_FONTS, MONO_FONTS } from '$lib/shell/settings/themes/fontRegistry';
  import { applyTerminalSettings } from '$lib/shell/terminal/ghosttyFactory';
  import {
    appUpdateState,
    checkForAppUpdate,
    installAppUpdate
  } from '$lib/shell/appUpdateService.svelte';

  interface Props {
    /** Whether the settings screen is showing. */
    open?: boolean;
    /**
     * The user moved the Problems list. The setting is already written by the
     * time this runs — this is only so the shell can close or reopen the strip
     * along the bottom straight away, rather than on the next launch.
     */
    onProblemsLocationChange?: (location: ProblemsLocation) => void;
    onRemoteConnected?: (profileId: string) => void;
    /** The section on screen; the host sets it to open Settings at one section. */
    activeSection?: string;
    /** The search text; the host clears it when it opens at a section. */
    query?: string;
  }

  let { open = $bindable(false), onProblemsLocationChange, onRemoteConnected, activeSection = $bindable('appearance'), query = $bindable('') }: Props = $props();

  // ── Option lists ────────────────────────────────────────────────────────
  /** The themes the app actually ships, straight from the registry. */
  const themeItems = themeChoices();
  let installedFontFamilies = $state<string[]>([]);
  let installedMonospacedFamilies = $state<string[]>([]);
  const uiFontPresets = UI_FONTS.map((font) => ({ value: font.id, label: font.label }));
  const monoFontPresets = MONO_FONTS.map((font) => ({ value: font.id, label: font.label }));
  const terminalFontPresets = MONO_FONTS
    .filter((font) => font.id !== 'system')
    .map((font) => ({ value: font.label, label: font.label }));
  const uiFontItems = $derived(mergeFontChoices(uiFontPresets, installedFontFamilies));
  const monoFontItems = $derived(mergeFontChoices(monoFontPresets, installedMonospacedFamilies));
  const editorFontFamilyItems = $derived(mergeFontChoices(monoFontPresets, installedMonospacedFamilies));
  const fontFamilyItems = $derived(mergeFontChoices(terminalFontPresets, installedMonospacedFamilies));

  // Mirrors the SourceTerminalApp options in +page.svelte.
  const terminalAppItems = [
    { value: 'Warp', label: 'Warp' },
    { value: 'Terminal', label: 'Terminal' },
    { value: 'iTerm', label: 'iTerm' },
    { value: 'iTerm2', label: 'iTerm2' },
    { value: 'Ghostty', label: 'Ghostty' },
    { value: 'WezTerm', label: 'WezTerm' },
    { value: 'Alacritty', label: 'Alacritty' }
  ];

  const problemsLocationItems = PROBLEMS_LOCATIONS.map((value) => ({
    value,
    label: PROBLEMS_LOCATION_LABELS[value]
  }));

  // ── The screen's own map ────────────────────────────────────────────────
  /**
   * Sections in the order the left-hand list shows them, under the group each
   * one belongs to. The groups are labels only — nothing collapses.
   */
  const sections = [
    { id: 'appearance', group: 'Interface', label: 'Appearance', icon: Palette },
    { id: 'editor', group: 'Workspace', label: 'Editor', icon: Type },
    { id: 'terminal', group: 'Workspace', label: 'Terminal', icon: SquareTerminal },
    { id: 'general', group: 'Application', label: 'General', icon: SlidersHorizontal },
    { id: 'connections', group: 'Application', label: 'Connections', icon: Server },
    { id: 'updates', group: 'Application', label: 'Updates', icon: Download },
    { id: 'agents', group: 'Application', label: 'Agents', icon: Sparkles },
    { id: 'plugins', group: 'Application', label: 'Plugins and Providers', icon: Puzzle },
    { id: 'helper', group: 'Application', label: 'Utility model', icon: Sparkles }
  ];

  /**
   * Every row on the screen, once, so that search has something to read and
   * the markup below has one source for its wording. `card` groups rows into
   * the boxes within a section; `keywords` catches the words someone is likely
   * to type that the row does not itself say.
   */
  const rows = [
    { id: 'helper_auto_done_minutes', section: 'agents', card: 'Helper sessions', title: 'Mark finished helpers Done after',
      description: 'Minutes to wait on this machine; 0 means Never.', keywords: 'agents helpers sessions minutes done never' },
    { id: 'helpers_per_session', section: 'agents', card: 'Helper sessions', title: 'Helpers per session',
      description: 'Maximum unfinished helpers for each session on this machine.', keywords: 'agents helpers sessions limit' },
    { id: 'providers', section: 'plugins', card: 'Providers', title: 'Providers',
      description: 'Each agent Assembly can run, its sign-in state and models.', keywords: 'claude codex antigravity models sign in acp' },
    { id: 'api-endpoints', section: 'plugins', card: 'API endpoints', title: 'API endpoints',
      description: 'API models reached through an endpoint.', keywords: 'api keys keyring grok openrouter ollama' },
    { id: 'plugins', section: 'plugins', card: 'Plugins', title: 'Plugins',
      description: 'Installed plugins.', keywords: 'plugins tools extensions' },
    { id: 'remote-connections', section: 'connections', card: 'Remote machines', title: 'Saved connections',
      description: 'Connect or disconnect an Assembly backend.', keywords: 'ssh workbox server remote reconnect' },
    { id: 'theme', section: 'appearance', card: 'Colors', title: 'Theme',
      description: 'Overall color scheme for the app.', keywords: 'dark light palette houston' },
    { id: 'ui-font', section: 'appearance', card: 'Text', title: 'Interface font',
      description: 'The face for labels, menus and messages.', keywords: 'typeface family inter manrope figtree sans' },
    {
      id: 'mono-font',
      section: 'appearance',
      card: 'Text',
      title: 'Code font',
      description: 'The face for paths, commands and code outside the editor.',
      keywords: 'typeface family monospace jetbrains fira geist'
    },
    {
      id: 'app-font-size',
      section: 'appearance',
      card: 'Text',
      title: 'Interface font size',
      description: 'Base size for chrome, labels and menus.',
      keywords: 'type scale bigger smaller'
    },
    {
      id: 'editor-font-family',
      section: 'editor',
      card: 'Text',
      title: 'Font family',
      description: 'Typeface used in the code editor.',
      keywords: 'monospace typeface'
    },
    {
      id: 'editor-font-size',
      section: 'editor',
      card: 'Text',
      title: 'Font size',
      description: 'Editor text size in pixels.',
      keywords: 'type scale'
    },
    {
      id: 'editor-line-height',
      section: 'editor',
      card: 'Text',
      title: 'Line height',
      description: 'Absolute line height in pixels.',
      keywords: 'spacing leading'
    },
    {
      id: 'editor-ligatures',
      section: 'editor',
      card: 'Text',
      title: 'Font ligatures',
      description: 'Render combined glyphs like => and !==.',
      keywords: 'glyphs arrows'
    },
    {
      id: 'terminal-font-family',
      section: 'terminal',
      card: 'Text',
      title: 'Font family',
      description: 'Typeface used in the embedded terminal.',
      keywords: 'monospace typeface'
    },
    {
      id: 'terminal-font-size',
      section: 'terminal',
      card: 'Text',
      title: 'Font size',
      description: 'Terminal text size in pixels.',
      keywords: 'type scale'
    },
    {
      id: 'terminal-line-height',
      section: 'terminal',
      card: 'Text',
      title: 'Line height',
      description: 'Line spacing multiplier (relative to font size).',
      keywords: 'spacing leading'
    },
    {
      id: 'terminal-cursor-blink',
      section: 'terminal',
      card: 'Behaviour',
      title: 'Cursor blink',
      description: 'Pulse the terminal cursor when idle.',
      keywords: 'caret flash'
    },
    {
      id: 'terminal-app',
      section: 'general',
      card: 'Outside the app',
      title: 'External terminal app',
      description: 'Used when opening a folder in a terminal.',
      keywords: 'warp iterm ghostty wezterm alacritty'
    },
    {
      id: 'problems-location',
      section: 'general',
      card: 'Panels',
      title: 'Where the problems list sits',
      description: 'The list of mistakes the language server has found in this project.',
      keywords: 'errors warnings bottom right hidden panel'
    },
    {
      id: 'language-servers',
      section: 'general',
      card: 'Supercharged',
      title: 'Supercharged',
      description:
        'One switch over every language server. Off stops the ones running and leaves files in Editor-only mode.',
      keywords: 'lsp intelligence typescript rust svelte roslyn memory off'
    },
    {
      id: 'csharp-language-server',
      section: 'general',
      card: 'Supercharged',
      title: 'C#',
      description:
        'Off saves about 800MB of memory. Reference counts and project-wide search keep working. What you lose is the squiggles under mistakes, and the precision of go-to-definition when a name is used in more than one place.',
      keywords: 'roslyn omnisharp intellisense memory'
    },
    {
      id: 'typescript-language-server',
      section: 'general',
      card: 'Supercharged',
      title: 'TypeScript / JavaScript',
      description: 'Allow the TypeScript language server while Supercharged is on.',
      keywords: 'typescript javascript ts js lsp'
    },
    {
      id: 'rust-language-server',
      section: 'general',
      card: 'Supercharged',
      title: 'Rust',
      description: 'Allow rust-analyzer while Supercharged is on.',
      keywords: 'rust rust-analyzer lsp'
    },
    {
      id: 'app-update-check',
      section: 'updates',
      card: 'Assembly',
      title: 'Check for updates',
      description: 'Look for a newer signed Assembly release.',
      keywords: 'version release upgrade'
    },
    {
      id: 'app-update-install',
      section: 'updates',
      card: 'Assembly',
      title: 'Install update',
      description: 'Download, verify, install and restart Assembly.',
      keywords: 'download restart upgrade'
    },
    {
      id: 'provider-updates',
      section: 'updates',
      card: 'Provider adapters',
      title: 'Codex, Claude and Antigravity',
      description: 'Check for signed adapter updates without replacing Assembly.',
      keywords: 'agents acp adapters codex claude antigravity update'
    },
    {
      id: 'provider-cli-updates',
      section: 'updates',
      card: 'Provider CLIs',
      title: 'Codex and Claude',
      description: 'Read and update the command-line tools on This Mac.',
      keywords: 'cli command line codex claude version update'
    },
    {
      id: 'helper-route',
      section: 'helper',
      card: 'Which model',
      title: 'Connection',
      description: 'Use your signed-in Codex or Claude CLI. Choose API only if you want to use your own API key.',
      keywords: 'cli api connection transport'
    },
    {
      id: 'helper-vendor',
      section: 'helper',
      card: 'Which model',
      title: 'Service',
      description: 'The selected provider runs session titles, Inspect, and pull request review drafts.',
      keywords: 'openai anthropic provider vendor byok'
    },
    {
      id: 'helper-model',
      section: 'helper',
      card: 'Which model',
      title: 'Model',
      description: 'Choose the model used by the selected connection.',
      keywords: 'gpt claude haiku cheap small'
    },
    {
      id: 'helper-key',
      section: 'helper',
      card: 'Connection',
      title: 'API key',
      description: 'Used only when Connection is API. Stored in your login Keychain and never shown again after saving.',
      keywords: 'secret token keychain password credentials'
    },
    {
      id: 'helper-test',
      section: 'helper',
      card: 'Connection',
      title: 'Test',
      description: 'Makes one tiny call and says what came back.',
      keywords: 'check verify connection try'
    }
  ];

  let searchField = $state<HTMLElement | null>(null);
  let resetSectionPending = $state<'appearance' | 'editor' | 'terminal' | 'general' | null>(null);
  const resetSectionLabel = $derived(
    sections.find((section) => section.id === resetSectionPending)?.label ?? ''
  );

  /** Rows the current search leaves visible. An empty search leaves them all. */
  const matches = $derived.by(() => {
    const needle = query.trim().toLowerCase();
    const visible = new Set<string>();
    for (const row of rows) {
      if (!needle) {
        visible.add(row.id);
        continue;
      }
      const sectionLabel = sections.find((section) => section.id === row.section)?.label ?? '';
      const haystack =
        `${row.title} ${row.description} ${row.keywords} ${row.card} ${sectionLabel}`.toLowerCase();
      if (haystack.includes(needle)) visible.add(row.id);
    }
    return visible;
  });

  const searching = $derived(query.trim().length > 0);

  /** Sections with at least one row left after the search. */
  const shownSections = $derived(
    sections.filter((section) =>
      rows.some((row) => row.section === section.id && matches.has(row.id))
    )
  );

  /**
   * Which section the content pane shows. A search that empties the chosen
   * section moves to the first one that still has something in it, so the pane
   * is never blank while results exist elsewhere.
   */
  const shownSection = $derived(
    shownSections.find((section) => section.id === activeSection) ?? shownSections[0] ?? null
  );

  /** The cards of the shown section, in the order their rows first appear. */
  const shownCards = $derived.by(() => {
    if (!shownSection) return [] as { name: string; rowIds: string[] }[];
    const cards: { name: string; rowIds: string[] }[] = [];
    for (const row of rows) {
      if (row.section !== shownSection.id || !matches.has(row.id)) continue;
      const existing = cards.find((card) => card.name === row.card);
      if (existing) existing.rowIds.push(row.id);
      else cards.push({ name: row.card, rowIds: [row.id] });
    }
    return cards;
  });

  function rowMeta(id: string) {
    // Every id passed below is one of the entries above, so this cannot miss.
    return rows.find((row) => row.id === id)!;
  }

  /**
   * What the app said the last time the C# language server was switched. Shown
   * as-is under the switch: the backend answers in whole sentences, including
   * the one that says this build of the app cannot do it at all.
   */
  let languageServersNote = $state<string | null>(null);
  let languageServersSupported = $state(true);
  let languageServerNote = $state<string | null>(null);
  const updateOwner = new AbortController();
  let installedFontRequest = 0;

  onDestroy(() => updateOwner.abort());

  async function loadInstalledFonts(request: number, signal: AbortSignal): Promise<void> {
    try {
      const installed = await listInstalledFontFamiliesFromTauri(signal);
      if (signal.aborted || !open || request !== installedFontRequest || !installed) return;
      installedFontFamilies = installed.families;
      installedMonospacedFamilies = installed.monospacedFamilies;
    } catch {
      // The shipped choices remain available if native enumeration is unavailable.
    }
  }

  function moveProblems(location: ProblemsLocation): void {
    settings.panels.problemsLocation = location;
    onProblemsLocationChange?.(location);
  }

  function setEditorFontFamily(id: string): void {
    const font = getMonoFont(id);
    settings.editor.fontFamily = font.id;
  }

  async function switchLanguageServers(enabled: boolean): Promise<void> {
    const before = settings.intelligence.languageServers;
    settings.intelligence.languageServers = enabled;
    const result = await setLanguageServersEnabled(enabled);
    languageServersSupported = result.supported;
    languageServersNote = result.message;
    if (!result.supported) settings.intelligence.languageServers = before;
  }

  async function switchLanguageServer(id: LanguageServerId, enabled: boolean): Promise<void> {
    const before = settings.intelligence.languageServerEnabled[id];
    settings.intelligence.languageServerEnabled[id] = enabled;
    const result = await setLanguageServerEnabled(id, enabled);
    languageServerNote = result.message;
    if (!result.supported) settings.intelligence.languageServerEnabled[id] = before;
  }

  /**
   * The utility model's settings, as the app holds them. Not part of the store
   * above: the app runs helper jobs itself, so vendor and model live in a file
   * it can read with no window open, and the key lives in the Keychain. This
   * screen is a view onto those, not a second copy of them.
   */
  let helper = $state<HelperSettingsView | null>(null);
  /** What has been typed into the key field but not saved yet. */
  let helperKey = $state('');
  /** The last thing the Test button, or saving a key, had to say. */
  let helperNote = $state<string | null>(null);
  let helperTesting = $state(false);

  const helperVendorItems = $derived([
    { value: 'openai', label: helper?.route === 'api' ? 'OpenAI' : 'Codex CLI' },
    { value: 'anthropic', label: helper?.route === 'api' ? 'Anthropic' : 'Claude CLI' }
  ]);
  const helperRouteItems = [
    { value: 'cli', label: 'Signed-in CLI' },
    { value: 'api', label: 'API key' }
  ];

  /** The models the chosen service offers, cheapest first. */
  const helperModelItems = $derived(
    (helper?.vendor == null
      ? []
      : helper.vendor === 'anthropic'
        ? helper.anthropicModels
        : helper.openaiModels
    ).map((id) => ({ value: id, label: id }))
  );

  async function loadHelper(): Promise<void> {
    try {
      helper = await readHelperSettingsFromTauri();
    } catch (error) {
      helperNote = String(error);
    }
  }

  async function chooseHelperVendor(value: string): Promise<void> {
    if (!helper) return;
    const before = helper;
    const vendor = value as HelperVendor;
    const models = vendor === 'anthropic' ? helper.anthropicModels : helper.openaiModels;
    const model = models[0] ?? helper.model;
    helper = { ...helper, vendor, model };
    helperNote = null;
    helperKey = '';
    try {
      await writeHelperSettingsFromTauri({ vendor, model, route: helper.route });
    } catch (error) {
      helper = before;
      helperNote = String(error);
      return;
    }
    await loadHelper();
  }

  async function chooseHelperModel(model: string): Promise<void> {
    if (!helper) return;
    const before = helper;
    helper = { ...helper, model };
    try {
      await writeHelperSettingsFromTauri({ vendor: helper.vendor, model, route: helper.route });
    } catch (error) {
      helper = before;
      helperNote = String(error);
    }
  }

  async function chooseHelperRoute(value: string): Promise<void> {
    if (!helper) return;
    const before = helper;
    const route = value as HelperRoute;
    helper = { ...helper, route };
    helperNote = null;
    try {
      await writeHelperSettingsFromTauri({ vendor: helper.vendor, model: helper.model, route });
      await loadHelper();
    } catch (error) {
      helper = before;
      helperNote = String(error);
    }
  }

  /** Saving hands the key to the Keychain and forgets it here. */
  async function saveHelperKey(): Promise<void> {
    if (!helper?.vendor) { helperNote = 'Choose a service first.'; return; }
    const removing = helperKey.trim().length === 0;
    try {
      await setHelperKeyFromTauri(helper.vendor, helperKey);
    } catch (error) {
      // What was typed stays in the field. Whatever went wrong, retyping a key
      // is the last thing anyone wants to be asked to do.
      helperNote = String(error);
      return;
    }
    helperKey = '';
    helperNote = removing ? 'Key removed.' : 'Key saved to your Keychain.';
    await loadHelper();
  }

  async function testHelper(): Promise<void> {
    helperTesting = true;
    helperNote = 'Asking…';
    try {
      const result = await testHelperFromTauri();
      helperNote = result?.message ?? 'The utility model only works in the desktop app.';
    } catch (error) {
      helperNote = String(error);
    } finally {
      // In a `finally` so a refused test does not leave the button dead.
      helperTesting = false;
    }
  }

  /** The label to show on a closed dropdown, given what is selected. */
  function labelFor(items: { value: string; label: string }[], value: string): string {
    return items.find((item) => item.value === value)?.label ?? value;
  }

  /**
   * The theme this screen should show as chosen.
   *
   * Every install made before themes existed has `dark` stored, which is not one
   * of the themes the app ships. The screen renders the shipped theme in that
   * case, so this says so rather than naming a theme nobody can see.
   */
  const shownThemeId = $derived(
    themeItems.some((item) => item.value === settings.appearance.themeId)
      ? settings.appearance.themeId
      : DEFAULT_THEME_ID
  );

  async function resetSection(section: 'appearance' | 'editor' | 'terminal' | 'general'): Promise<void> {
    if (section === 'general') {
      resetSettings('general');
      resetSettings('panels');
      resetSettings('intelligence');
      onProblemsLocationChange?.(settings.panels.problemsLocation);
      const defaults = defaultSettings().intelligence;
      await switchLanguageServers(defaults.languageServers);
      for (const id of ['csharp', 'typescript', 'rust'] as const) {
        await switchLanguageServer(id, defaults.languageServerEnabled[id]);
      }
    } else {
      resetSettings(section as SettingsSection);
    }
    // Putting the settings back has to put the SCREEN back too, or the app says
    // one thing and shows another. A reset stores `dark`, which resolves to the
    // theme the app ships with.
    if (section === 'appearance') applyTheme(settings.appearance.themeId);
  }

  async function confirmSectionReset(): Promise<void> {
    const section = resetSectionPending;
    resetSectionPending = null;
    if (section) await resetSection(section);
  }

  function backToApp(): void {
    open = false;
  }

  /**
   * Escape leaves the screen — unless something inside it has already used the
   * key, which is what closing an open dropdown looks like from here.
   */
  function onWindowKeyDown(event: KeyboardEvent): void {
    if (!open || event.key !== 'Escape' || event.defaultPrevented) return;
    event.preventDefault();
    backToApp();
  }

  // Opening the screen puts the caret in the search field: it is the fastest
  // way to reach a setting, and it costs a person nothing to ignore.
  $effect(() => {
    if (open) searchField?.focus();
  });

  $effect(() => {
    const request = ++installedFontRequest;
    if (!open) {
      installedFontFamilies = [];
      installedMonospacedFamilies = [];
      return;
    }
    void loadInstalledFonts(request, updateOwner.signal);
  });

  // Read the helper's settings each time the screen opens, so a key stored or
  // removed elsewhere is not shown stale.
  $effect(() => {
    if (open) void loadHelper();
  });

  $effect(() => {
    settings.terminal.fontFamily;
    settings.terminal.fontSize;
    settings.terminal.lineHeight;
    settings.terminal.cursorBlink;
    applyTerminalSettings();
  });

</script>

<svelte:window onkeydown={onWindowKeyDown} />

<!--
  One row of the screen: what the setting is, on the left; the control, on the
  right. Rows that the search has filtered out are simply not rendered.
-->
{#snippet settingRow(id: string, control: Snippet, wide = false)}
  {@const meta = rowMeta(id)}
  <div
    class="border-border/60 flex gap-6 border-b px-4 py-3 last:border-b-0 {wide
      ? 'flex-col items-stretch'
      : 'items-center justify-between'}"
    data-setting={id}
  >
    <div class="flex min-w-0 flex-col gap-1">
      <span class="text-[13px] leading-[1.3] font-medium">{meta.title}</span>
      <span class="text-[12px] leading-[1.45] text-[var(--color-text-3)]">{meta.description}</span>
    </div>
    <div class={wide ? 'w-full' : 'flex w-[220px] shrink-0 justify-end'}>
      {@render control()}
    </div>
  </div>
{/snippet}

<!-- A slider with the number it is currently on, so the value is not a guess. -->
{#snippet sliderReading(value: number, unit: string)}
  <span class="text-muted-foreground w-12 shrink-0 text-right text-[12px] tabular-nums">
    {value}{unit}
  </span>
{/snippet}

{#if open}
  <div class="settings-screen" role="dialog" aria-modal="true" aria-label="Settings">
    <!-- The left inset keeps the title clear of the macOS window buttons,
         which the overlay title bar draws over this corner. -->
    <header class="border-border/70 flex items-center gap-3 border-b py-2.5 pr-4 pl-20">
      <span class="text-[13px] font-semibold">Settings</span>
      <div class="relative ml-auto w-[280px]">
        <Search
          aria-hidden="true"
          class="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2"
        />
        <Input
          bind:ref={searchField}
          bind:value={query}
          type="search"
          placeholder="Search settings"
          aria-label="Search settings"
          class="h-7 pl-7"
        />
      </div>
    </header>

    <div class="flex min-h-0 flex-1">
      <nav class="border-border/70 flex w-[208px] shrink-0 flex-col border-r">
        <div class="min-h-0 flex-1 overflow-y-auto px-2 py-3">
        {#each shownSections as section, index (section.id)}
          {@const newGroup = index === 0 || shownSections[index - 1].group !== section.group}
          {#if newGroup}
            <div
              class="text-muted-foreground px-2 pb-1 text-[11px] font-medium tracking-wide uppercase {index >
              0
                ? 'pt-4'
                : ''}"
            >
              {section.group}
            </div>
          {/if}
          {@const Icon = section.icon}
          <button
            type="button"
            class="focus-visible:ring-ring/50 mb-0.5 flex h-8 w-full items-center gap-2 rounded-lg px-2 text-left text-[13px] transition-colors outline-none focus-visible:ring-3 {shownSection?.id ===
            section.id
              ? 'bg-secondary text-foreground'
              : 'text-muted-foreground hover:bg-accent/60 hover:text-foreground'}"
            aria-current={shownSection?.id === section.id ? 'page' : undefined}
            onclick={() => (activeSection = section.id)}
          >
            <Icon aria-hidden="true" class="size-4" />
            {section.label}
          </button>
        {/each}
        </div>
        <div class="border-border/70 border-t p-2">
          <Button variant="ghost" size="sm" onclick={backToApp} class="w-full justify-start gap-1.5 px-2">
            <ArrowLeft aria-hidden="true" />
            Back to app
          </Button>
        </div>
      </nav>

      <main class="min-w-0 flex-1 overflow-y-auto px-6 py-5">
        {#if !shownSection}
          <p class="text-muted-foreground text-[13px]">
            Nothing matches “{query.trim()}”.
          </p>
        {:else}
          <div class="mx-auto flex w-full max-w-[720px] flex-col gap-5">
            <div class="flex items-center justify-between gap-4">
              <div class="flex flex-col gap-1">
                <h2 class="text-[15px] leading-[1.3] font-semibold">{shownSection.label}</h2>
                {#if searching}
                  <p class="text-muted-foreground text-[12px]">
                    Showing the rows that match “{query.trim()}”.
                  </p>
                {/if}
              </div>
              <!-- The Helper section has nothing in the settings store to put
                   back, so it is not offered a reset it could not carry out. -->
              {#if shownSection.id !== 'agents' && shownSection.id !== 'helper' && shownSection.id !== 'updates' && shownSection.id !== 'connections' && shownSection.id !== 'plugins'}
                <Button
                  variant="ghost"
                  size="sm"
                  onclick={() => (resetSectionPending = shownSection.id as 'appearance' | 'editor' | 'terminal' | 'general')}
                  class="gap-1.5"
                >
                  <RotateCcw aria-hidden="true" />
                  Reset section
                </Button>
              {/if}
            </div>

            {#each shownCards as card (card.name)}
              <section class="flex flex-col gap-2">
                <h3 class="text-muted-foreground px-1 text-[12px] font-medium">{card.name}</h3>
                <div class="border-border/70 bg-card rounded-xl border">
                  {#each card.rowIds as id (id)}
                    {#if id === 'providers'}<ProviderList />
                    {:else if id === 'api-endpoints' || id === 'plugins'}<EmptyState title={id === 'plugins' ? 'No plugins installed' : 'No API endpoints'} class="py-5" />
                    {:else if id === 'remote-connections'}
                      <RemoteConnections onConnected={(profile) => onRemoteConnected?.(profile.id)} />
                    {:else if id === 'theme'}
                      {@render settingRow(id, themeControl)}
                    {:else if id === 'ui-font'}
                      {@render settingRow(id, uiFontControl)}
                    {:else if id === 'mono-font'}
                      {@render settingRow(id, monoFontControl)}
                    {:else if id === 'app-font-size'}
                      {@render settingRow(id, appFontSizeControl, true)}
                    {:else if id === 'editor-font-family'}
                      {@render settingRow(id, editorFontFamilyControl)}
                    {:else if id === 'editor-font-size'}
                      {@render settingRow(id, editorFontSizeControl, true)}
                    {:else if id === 'editor-line-height'}
                      {@render settingRow(id, editorLineHeightControl, true)}
                    {:else if id === 'editor-ligatures'}
                      {@render settingRow(id, editorLigaturesControl)}
                    {:else if id === 'terminal-font-family'}
                      {@render settingRow(id, terminalFontFamilyControl)}
                    {:else if id === 'terminal-font-size'}
                      {@render settingRow(id, terminalFontSizeControl, true)}
                    {:else if id === 'terminal-line-height'}
                      {@render settingRow(id, terminalLineHeightControl, true)}
                    {:else if id === 'terminal-cursor-blink'}
                      {@render settingRow(id, terminalCursorBlinkControl)}
                    {:else if id === 'terminal-app'}
                      {@render settingRow(id, terminalAppControl)}
                    {:else if id === 'problems-location'}
                      {@render settingRow(id, problemsLocationControl)}
                    {:else if id === 'language-servers'}
                      {@render settingRow(id, languageServersControl)}
                    {:else if id === 'csharp-language-server'}
                      {@render settingRow(id, csharpLanguageServerControl)}
                    {:else if id === 'typescript-language-server'}
                      {@render settingRow(id, typescriptLanguageServerControl)}
                    {:else if id === 'rust-language-server'}
                      {@render settingRow(id, rustLanguageServerControl)}
                    {:else if id === 'app-update-check'}
                      {@render settingRow(id, appUpdateCheckControl)}
                    {:else if id === 'app-update-install'}
                      {@render settingRow(id, appUpdateInstallControl)}
                    {:else if id === 'provider-updates'}
                      {@render settingRow(id, providerUpdateControl, true)}
                    {:else if id === 'provider-cli-updates'}
                      {@render settingRow(id, providerCliUpdateControl, true)}
                    {:else if id === 'helper_auto_done_minutes'}
                      {@render settingRow(id, helperAutoDoneControl)}
                    {:else if id === 'helpers_per_session'}
                      {@render settingRow(id, helperLimitControl)}
                    {:else if id === 'helper-route'}
                      {@render settingRow(id, helperRouteControl)}
                    {:else if id === 'helper-vendor'}
                      {@render settingRow(id, helperVendorControl)}
                    {:else if id === 'helper-model'}
                      {@render settingRow(id, helperModelControl)}
                    {:else if id === 'helper-key'}
                      {@render settingRow(id, helperKeyControl, true)}
                    {:else if id === 'helper-test'}
                      {@render settingRow(id, helperTestControl)}
                    {/if}
                  {/each}
                </div>
              </section>
            {/each}
          </div>
        {/if}
      </main>
    </div>
  </div>
{/if}

<AlertDialog.Root
  open={resetSectionPending !== null}
  onOpenChange={(next) => { if (!next) resetSectionPending = null; }}
>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Reset {resetSectionLabel} settings?</AlertDialog.Title>
      <AlertDialog.Description>
        This replaces every setting in {resetSectionLabel} with its default. Your current choices will be lost.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action variant="destructive" onclick={() => void confirmSectionReset()}>
        Reset
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<!-- ── The controls themselves ──────────────────────────────────────────── -->

{#snippet themeControl()}
  <!-- Not `bind:value`: applying a theme writes the setting itself, and a
       binding would fight it. -->
  <SettingsSelect
    items={themeItems}
    value={shownThemeId}
    ariaLabel="Theme"
    onChange={(value) => applyTheme(value)}
  />
{/snippet}

{#snippet uiFontControl()}
  <!-- Not `bind:value`, for the same reason as the theme: applying writes the
       setting itself. -->
  <SettingsSelect
    items={uiFontItems}
    value={settings.appearance.uiFontId}
    ariaLabel="Interface font"
    searchable
    onChange={(value) => applyUiFont(value)}
  />
{/snippet}

{#snippet monoFontControl()}
  <SettingsSelect
    items={monoFontItems}
    value={settings.appearance.monoFontId}
    ariaLabel="Code font"
    searchable
    onChange={(value) => applyMonoFont(value)}
  />
{/snippet}

{#snippet appFontSizeControl()}
  <div class="flex items-center gap-3">
    <Slider
      type="single"
      bind:value={settings.appearance.appFontSize}
      min={11}
      max={18}
      step={1}
      aria-label="Interface font size"
    />
    {@render sliderReading(settings.appearance.appFontSize, 'px')}
  </div>
{/snippet}

{#snippet editorFontFamilyControl()}
  <SettingsSelect
    items={editorFontFamilyItems}
    value={getMonoFont(settings.editor.fontFamily).id}
    ariaLabel="Editor font family"
    searchable
    onChange={setEditorFontFamily}
  />
{/snippet}

{#snippet editorFontSizeControl()}
  <div class="flex items-center gap-3">
    <Slider
      type="single"
      bind:value={settings.editor.fontSize}
      min={10}
      max={24}
      step={1}
      aria-label="Editor font size"
    />
    {@render sliderReading(settings.editor.fontSize, 'px')}
  </div>
{/snippet}

{#snippet editorLineHeightControl()}
  <div class="flex items-center gap-3">
    <Slider
      type="single"
      bind:value={settings.editor.lineHeight}
      min={14}
      max={36}
      step={1}
      aria-label="Editor line height"
    />
    {@render sliderReading(settings.editor.lineHeight, 'px')}
  </div>
{/snippet}

{#snippet editorLigaturesControl()}
  <Switch bind:checked={settings.editor.fontLigatures} aria-label="Font ligatures" />
{/snippet}

{#snippet terminalFontFamilyControl()}
  <SettingsSelect
    items={fontFamilyItems}
    value={settings.terminal.fontFamily}
    ariaLabel="Terminal font family"
    searchable
    onChange={(value) => (settings.terminal.fontFamily = value)}
  />
{/snippet}

{#snippet terminalFontSizeControl()}
  <div class="flex items-center gap-3">
    <Slider
      type="single"
      bind:value={settings.terminal.fontSize}
      min={10}
      max={24}
      step={1}
      aria-label="Terminal font size"
    />
    {@render sliderReading(settings.terminal.fontSize, 'px')}
  </div>
{/snippet}

{#snippet terminalLineHeightControl()}
  <div class="flex items-center gap-3">
    <Slider
      type="single"
      bind:value={settings.terminal.lineHeight}
      min={1}
      max={2}
      step={0.1}
      aria-label="Terminal line height"
    />
    {@render sliderReading(Math.round(settings.terminal.lineHeight * 10) / 10, '×')}
  </div>
{/snippet}

{#snippet terminalCursorBlinkControl()}
  <Switch bind:checked={settings.terminal.cursorBlink} aria-label="Cursor blink" />
{/snippet}

{#snippet terminalAppControl()}
  <SettingsSelect
    items={terminalAppItems}
    value={settings.general.terminalApp}
    ariaLabel="External terminal app"
    onChange={(value) => (settings.general.terminalApp = value)}
  />
{/snippet}

{#snippet problemsLocationControl()}
  <!-- Three choices, which would suit a segmented control if the labels were
       short; they are whole phrases ("In the strip along the bottom"), so this
       stays a dropdown. Not `bind:value`: the shell has to be told as well as
       the store, so the strip along the bottom opens or closes now rather than
       at the next launch. -->
  <SettingsSelect
    items={problemsLocationItems}
    value={settings.panels.problemsLocation}
    ariaLabel="Where the problems list sits"
    onChange={(value) => moveProblems(value as ProblemsLocation)}
  />
{/snippet}

{#snippet languageServersControl()}
  <div class="flex flex-col items-end gap-1.5">
    <Switch
      checked={settings.intelligence.languageServers}
      disabled={!languageServersSupported}
      onCheckedChange={(checked) => void switchLanguageServers(checked)}
      aria-label="Language servers"
    />
    {#if languageServersNote}
      <p class="text-[12px] leading-[1.4] text-[var(--color-text-2)]">{languageServersNote}</p>
    {/if}
  </div>
{/snippet}

{#snippet languageServerControl(id: LanguageServerId)}
  <div class="flex flex-col items-end gap-1.5">
    <Switch
      checked={settings.intelligence.languageServerEnabled[id]}
      onCheckedChange={(checked) => void switchLanguageServer(id, checked)}
      aria-label={`${id} language server`}
    />
    {#if languageServerNote}
      <p class="text-[12px] leading-[1.4] text-[var(--color-text-2)]">{languageServerNote}</p>
    {/if}
  </div>
{/snippet}

{#snippet csharpLanguageServerControl()}{@render languageServerControl('csharp')}{/snippet}
{#snippet typescriptLanguageServerControl()}{@render languageServerControl('typescript')}{/snippet}
{#snippet rustLanguageServerControl()}{@render languageServerControl('rust')}{/snippet}

{#snippet appUpdateCheckControl()}
  <div class="flex flex-col items-end gap-1.5">
    <Button
      variant="secondary"
      size="sm"
      disabled={appUpdateState.phase === 'checking' || appUpdateState.phase === 'installing'}
      onclick={() => void checkForAppUpdate(updateOwner.signal)}
    >
      {#if appUpdateState.phase === 'checking'}<WorkingSpinner size={12} />Checking…{:else}Check now{/if}
    </Button>
    {#if appUpdateState.message}
      <p class="text-right text-[12px] leading-[1.4] text-[var(--color-text-2)]">
        {appUpdateState.message}
      </p>
    {/if}
  </div>
{/snippet}

{#snippet appUpdateInstallControl()}
  <Button
    variant="secondary"
    size="sm"
    disabled={appUpdateState.phase !== 'available'}
    onclick={() => void installAppUpdate(updateOwner.signal)}
  >
    {#if appUpdateState.phase === 'installing'}<WorkingSpinner size={12} />Installing…{:else}Install and restart{/if}
  </Button>
{/snippet}

{#snippet providerUpdateControl()}
  <ProviderUpdateControl />
{/snippet}

{#snippet providerCliUpdateControl()}
  <ProviderCliUpdateControl />
{/snippet}

{#snippet helperVendorControl()}
  <SettingsSelect
    items={helperVendorItems}
    value={helper?.vendor ?? ''}
    ariaLabel="Utility model service"
    onChange={(value) => void chooseHelperVendor(value)}
  />
{/snippet}

{#snippet helperAutoDoneControl()}<HelperSessionSettings setting="helper_auto_done_minutes" />{/snippet}

{#snippet helperLimitControl()}<HelperSessionSettings setting="helpers_per_session" />{/snippet}

{#snippet helperRouteControl()}
  <SettingsSelect
    items={helperRouteItems}
    value={helper?.route ?? 'cli'}
    ariaLabel="Utility model connection"
    onChange={(value) => void chooseHelperRoute(value)}
  />
{/snippet}

{#snippet helperModelControl()}
  <SettingsSelect
    items={helperModelItems}
    value={helper?.model ?? ''}
    ariaLabel="Utility model"
    onChange={(value) => void chooseHelperModel(value)}
  />
{/snippet}

{#snippet helperKeyControl()}
  {#if helper?.route === 'api'}
  <div class="flex flex-col gap-1.5">
    <div class="flex items-center gap-2">
      <Input
        bind:value={helperKey}
        type="password"
        autocomplete="off"
        spellcheck="false"
        placeholder={helper?.hasKey ? 'Replace the stored key' : 'Paste your key'}
        aria-label="Utility model API key"
        class="h-7 flex-1"
      />
      <Button variant="secondary" size="sm" onclick={() => void saveHelperKey()}>Save</Button>
    </div>
    <p class="text-[12px] leading-[1.4] text-[var(--color-text-2)]">
      {helper?.hasKey ? 'Key saved' : 'No key'}
    </p>
  </div>
  {:else}<p class="text-[12px] text-[var(--color-text-2)]">Your selected CLI uses its existing sign-in.</p>{/if}
{/snippet}

{#snippet helperTestControl()}
  <div class="flex flex-col items-end gap-1.5">
    <Button variant="secondary" size="sm" disabled={helperTesting} onclick={() => void testHelper()}>
      Test
    </Button>
    {#if helperNote}
      <p class="text-right text-[12px] leading-[1.4] text-[var(--color-text-2)]">{helperNote}</p>
    {/if}
  </div>
{/snippet}

<style>
  /* A screen, not a sheet: it covers the window and the app waits behind it. */
  .settings-screen {
    position: fixed;
    inset: 0;
    /* Above every strip and rail of the shell: this screen replaces the app
       while it is up, and a status bar showing through the bottom of it reads
       as a rendering fault. */
    z-index: 200;
    display: flex;
    flex-direction: column;
    background: var(--color-bg);
    color: var(--color-text);
  }
</style>
