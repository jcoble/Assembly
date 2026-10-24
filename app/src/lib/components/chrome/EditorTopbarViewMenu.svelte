<script lang="ts">
  /**
   * EditorTopbarViewMenu.svelte — the editor-topbar "View" dropdown.
   *
   * The single View affordance for the workbench. Four sections — (1) workspace layout presets +
   * save/reset override + focus/restore/compact, (2) side-pane position +
   * collapse-to-rail, (3) context-card placement/mode + collapse/hide, and
   * (4) the dock-panel manager (per-panel visibility + move-to-group rows) plus
   * the terminal-app picker.
   *
   * Presentational only: the page owns `viewMenuOpen` (bound here so the page's
   * View button + its Escape keydown handler stay in sync), every layout/Tauri
   * handler body, and all `source*`/`dock*` derived values. This component holds
   * NO `$state` of its own — every value the markup reads is a prop and every
   * action is an `on*` callback. Each action's `closeViewMenu()` wrapping stays
   * in the page's callback impls, not here.
   *
   * Renders inline in the `.view-menu-anchor` (no teleport). The wrapping anchor,
   * the `.view-menu-button` toggle and the `{#if viewMenuOpen}` gate stay in the
   * page; this component renders only the dropdown body.
   *
   * The shared menu chrome (`.view-menu`, `.view-menu-anchor`, `.view-menu-section`,
   * `.view-menu-button-grid(.two)`, `.view-menu-wide-button`, grid-button states,
   * and `.view-terminal-picker > span`) is GLOBAL in `src/app.css` because the
   * `.view-menu-anchor`/`.view-menu-button` toggle stays inline in `+page.svelte`
   * — a scoped copy here would hash its selectors and not reach that inline markup.
   * Only the dock-panel-manager rules and the terminal-picker layout (which were
   * page-scoped) move into the scoped style block below. (Do not write that tag
   * out in full here — see the note in `CommandPaletteOverlay.svelte`: the type
   * checker reads the rest of the file as CSS if it finds an opening style tag
   * inside this comment.)
   *
   * The dock-panel-manager rows call `dockGroupIDForPanel(dockLayout, panelID)` and
   * `dockPanelMoveTargets(panelID)` as fn props; passing `dockLayout` (= the rune
   * store's `dock.layout`) keeps the `class:active`/`disabled` states reactive when
   * the layout changes (same fn-prop pattern as ActivityWorktreesPanel).
   */
  import { MoreHorizontal } from '@lucide/svelte';
  import type {
    SourceDockGroupID,
    SourceDockLayout,
    SourceDockPanelID
  } from '$lib/sourceDockLayout';

  /**
   * Minimal shapes the menu reads. The page's full definitions live inline in
   * `+page.svelte` (not exported), so this component restates the fields it renders.
   */
  interface ViewMenuLayoutPreset {
    id: string;
    label: string;
    title: string;
  }
  /** Result of the page's `currentSaveableSourceLayoutPreset()`. */
  interface SaveableLayoutPreset {
    id: string;
    label: string;
  }
  type SidePanePosition = 'left' | 'right';
  type ContextPanelPlacement = 'top' | 'side' | 'bottom';
  type ContextPanelMode = 'grid' | 'stack';

  interface Props {
    /** Whether the dropdown is open (page owns `viewMenuOpen` + its Escape handler). */
    viewMenuOpen?: boolean;

    // --- Section 1: workspace layout presets ----------------------------------
    /** Selectable layout presets (= page `sourceLayoutPresets`). */
    layoutPresets: readonly ViewMenuLayoutPreset[];
    /** Currently-active preset id (= `dock.layoutPreset`). */
    activeLayoutPreset: string;
    /** Saved per-preset overrides (= page `sourceLayoutPresetOverrides`); keys the Reset disabled state. */
    layoutPresetOverrides: Record<string, unknown>;
    /** The preset Save/Reset target (= `currentSaveableSourceLayoutPreset()` result). */
    currentSaveablePreset: SaveableLayoutPreset;
    /** Snapshot to restore after Focus editor, or null (= `sourceFocusRestoreLayout`); keys Restore disabled. */
    focusRestoreLayout: unknown;
    /** Whether compact editor chrome is active (= `sourceChromeCompact`). */
    chromeCompact: boolean;

    // --- Section 2: side-pane (explorer) position -----------------------------
    /** Current side-pane side (= `sidePanePosition`). */
    sidePanePosition: SidePanePosition;
    /** Whether the explorer is already collapsed to the icon rail (= `activityPaneRailOnly()`). */
    activityRailOnly: boolean;

    // --- Section 3: context-card layout ---------------------------------------
    /** Current context-card placement (= `contextPanelPlacement`). */
    contextPanelPlacement: ContextPanelPlacement;
    /** Current context-card presentation mode (= `contextPanelMode`). */
    contextPanelMode: ContextPanelMode;
    /** Whether the context cards are already collapsed to the icon rail (= `contextPaneRailOnly()`). */
    contextRailOnly: boolean;
    /** Whether the context cards are hidden (= `contextPanelCollapsed`). */
    contextPanelCollapsed: boolean;

    // --- Section 4: dock-panel manager ----------------------------------------
    /** Manageable dock-panel ids, one row each (= page `managedDockPanelIDs`). */
    managedDockPanelIDs: readonly SourceDockPanelID[];
    /** The live dock layout (= `dock.layout`); passed so the row fn-props stay reactive. */
    dockLayout: SourceDockLayout;
    /** Human label for a dock panel (page `dockPanelLabel`). */
    dockPanelLabel: (panelID: SourceDockPanelID) => string;
    /** Short "where is it now" summary for a panel (page `dockPanelPlacementSummary`). */
    dockPanelPlacementSummary: (panelID: SourceDockPanelID) => string;
    /** Whether a panel may be hidden (page `dockPanelCanHide`). */
    dockPanelCanHide: (panelID: SourceDockPanelID) => boolean;
    /** Whether a panel is currently visible (page `sourceDockPanelVisible`). */
    dockPanelVisible: (panelID: SourceDockPanelID) => boolean;
    /** The group a panel currently lives in, for a given layout (page `dockGroupIDForPanel`). */
    dockGroupIDForPanel: (layout: SourceDockLayout, panelID: SourceDockPanelID) => SourceDockGroupID | null;
    /** The groups a panel may move to (page `dockPanelMoveTargets`). */
    dockPanelMoveTargets: (panelID: SourceDockPanelID) => SourceDockGroupID[];
    /** Label for a move-target group, contextual to a panel (page `dockGroupLabel`). */
    dockGroupLabel: (groupID: SourceDockGroupID, panelID?: SourceDockPanelID) => string;

    // --- Terminal-app picker --------------------------------------------------
    /** Selectable terminal apps (= page `sourceTerminalApps`). */
    terminalApps: readonly string[];
    /** Currently-selected terminal app (= `sourceTerminalApp`); two-way bound to the `<select>`. */
    terminalApp?: string;

    // --- callbacks out (page impls keep their own `closeViewMenu()` wrapping) --
    /** Apply a layout preset (page `applySourceLayoutPreset`). */
    onApplyPreset: (presetID: string) => void;
    /** Save the current arrangement onto the saveable preset (page `saveSourceLayoutPresetOverride`). */
    onSavePresetOverride: () => void;
    /** Reset the saveable preset's saved override (page `resetSourceLayoutPresetOverride`). */
    onResetPresetOverride: () => void;
    /** Focus the editor canvas (page `focusSourceEditorLayout`). */
    onFocusEditorLayout: () => void;
    /** Restore the pane arrangement saved before Focus editor (page `restoreSourceLayoutBeforeFocus`). */
    onRestoreLayoutBeforeFocus: () => void;
    /** Reset the whole dock layout (page `resetSourceDockLayout`). */
    onResetDockLayout: () => void;
    /** Choose compact vs comfortable editor chrome (page `selectSourceChromeCompact`). */
    onSelectChromeCompact: (compact: boolean) => void;
    /** Choose the side-pane side (page `selectSidePanePosition`). */
    onSelectSidePanePosition: (position: SidePanePosition) => void;
    /** Collapse the explorer to the icon rail (page `collapseActivityPaneToRail`). */
    onCollapseActivityRail: () => void;
    /** Choose context-card placement (page `selectContextPanelPlacement`). */
    onSelectContextPlacement: (placement: ContextPanelPlacement) => void;
    /** Move the context panel to the bottom dock group (page `moveDockPanelToGroup('context','bottom')`). */
    onMoveContextToBottom: () => void;
    /** Choose context-card presentation mode (page `selectContextPanelMode`). */
    onSelectContextMode: (mode: ContextPanelMode) => void;
    /** Collapse the context cards to the icon rail (page `collapseContextPaneToRail`). */
    onCollapseContextRail: () => void;
    /** Toggle context cards hidden/shown (page `toggleContextPanelCollapsed`). */
    onToggleContextCollapsed: () => void;
    /** Toggle a dock panel's visibility (page `toggleDockPanelVisibility`). */
    onToggleDockPanelVisibility: (panelID: SourceDockPanelID) => void;
    /** Move a dock panel into a managed group (page `moveDockPanelToManagedGroup`). */
    onMoveDockPanelToManagedGroup: (panelID: SourceDockPanelID, groupID: SourceDockGroupID) => void;
    /** Persist the picked terminal app (page `selectSourceTerminalApp`, an `onchange` handler). */
    onSelectTerminalApp: (event: Event) => void;
  }

  let {
    viewMenuOpen = $bindable(false),
    layoutPresets,
    activeLayoutPreset,
    layoutPresetOverrides,
    currentSaveablePreset,
    focusRestoreLayout,
    chromeCompact,
    sidePanePosition,
    activityRailOnly,
    contextPanelPlacement,
    contextPanelMode,
    contextRailOnly,
    contextPanelCollapsed,
    managedDockPanelIDs,
    dockLayout,
    dockPanelLabel,
    dockPanelPlacementSummary,
    dockPanelCanHide,
    dockPanelVisible,
    dockGroupIDForPanel,
    dockPanelMoveTargets,
    dockGroupLabel,
    terminalApps,
    terminalApp = $bindable('Warp'),
    onApplyPreset,
    onSavePresetOverride,
    onResetPresetOverride,
    onFocusEditorLayout,
    onRestoreLayoutBeforeFocus,
    onResetDockLayout,
    onSelectChromeCompact,
    onSelectSidePanePosition,
    onCollapseActivityRail,
    onSelectContextPlacement,
    onMoveContextToBottom,
    onSelectContextMode,
    onCollapseContextRail,
    onToggleContextCollapsed,
    onToggleDockPanelVisibility,
    onMoveDockPanelToManagedGroup,
    onSelectTerminalApp,
  }: Props = $props();
</script>

{#if viewMenuOpen}
  <div class="view-menu" role="menu" aria-label="View options">
    <section class="view-menu-section" aria-label="Workspace layout presets">
      <span>Layout</span>
      <div class="view-menu-button-grid">
        {#each layoutPresets as preset (preset.id)}
          <button
            class:active={activeLayoutPreset === preset.id}
            type="button"
            role="menuitem"
            aria-label={`Use ${preset.label} layout`}
            title={preset.title}
            onclick={() => onApplyPreset(preset.id)}
          >
            {preset.label}
          </button>
        {/each}
      </div>
      <div class="view-menu-button-grid two">
        <button
          type="button"
          role="menuitem"
          aria-label="Save current layout preset"
          title={`Save current arrangement as ${currentSaveablePreset.label}`}
          onclick={onSavePresetOverride}
        >
          Save
        </button>
        <button
          type="button"
          role="menuitem"
          aria-label="Reset saved layout preset"
          title={`Reset saved ${currentSaveablePreset.label} layout`}
          disabled={!layoutPresetOverrides[currentSaveablePreset.id]}
          onclick={onResetPresetOverride}
        >
          Reset
        </button>
      </div>
      <button
        class="view-menu-wide-button"
        type="button"
        role="menuitem"
        aria-label="Focus editor canvas"
        title="Hide context, insights, terminal, and browser"
        onclick={onFocusEditorLayout}
      >
        Focus editor
      </button>
      <button
        class="view-menu-wide-button"
        type="button"
        role="menuitem"
        aria-label="Restore layout before focus"
        title="Restore the pane arrangement saved before Focus editor"
        disabled={!focusRestoreLayout}
        onclick={onRestoreLayoutBeforeFocus}
      >
        Restore previous
      </button>
      <div class="view-menu-button-grid two">
        <button
          class:active={chromeCompact}
          type="button"
          role="menuitem"
          aria-label="Use compact editor chrome"
          onclick={() => onSelectChromeCompact(true)}
        >
          Compact
        </button>
        <button
          class:active={!chromeCompact}
          type="button"
          role="menuitem"
          aria-label="Use comfortable editor chrome"
          onclick={() => onSelectChromeCompact(false)}
        >
          Comfort
        </button>
      </div>
    </section>

    <section class="view-menu-section" aria-label="Side pane position">
      <span>Explorer</span>
      <div class="view-menu-button-grid two">
        <button
          class:active={sidePanePosition === 'left'}
          type="button"
          role="menuitem"
          aria-label="Put side pane on the left"
          onclick={() => onSelectSidePanePosition('left')}
        >
          Left
        </button>
        <button
          class:active={sidePanePosition === 'right'}
          type="button"
          role="menuitem"
          aria-label="Put side pane on the right"
          onclick={() => onSelectSidePanePosition('right')}
        >
          Right
        </button>
      </div>
      <button
        class="view-menu-wide-button"
        class:active={activityRailOnly}
        type="button"
        role="menuitem"
        aria-label="Collapse explorer to icon rail"
        title="Shrink the source browser to the activity icon rail"
        disabled={activityRailOnly}
        onclick={onCollapseActivityRail}
      >
        Collapse to icon rail
      </button>
    </section>

    <section class="view-menu-section" aria-label="Context card layout">
      <span>Context</span>
      <div class="view-menu-button-grid two">
        <button
          class:active={contextPanelPlacement === 'top'}
          type="button"
          role="menuitem"
          aria-label="Put context above editor"
          onclick={() => onSelectContextPlacement('top')}
        >
          Top
        </button>
        <button
          class:active={contextPanelPlacement === 'side'}
          type="button"
          role="menuitem"
          aria-label="Put context beside editor"
          onclick={() => onSelectContextPlacement('side')}
        >
          Side
        </button>
        <button
          class:active={contextPanelPlacement === 'bottom'}
          type="button"
          role="menuitem"
          aria-label="Put context below editor"
          onclick={onMoveContextToBottom}
        >
          Bottom
        </button>
        <button
          class:active={contextPanelMode === 'grid'}
          type="button"
          role="menuitem"
          aria-label="Use grid context cards"
          onclick={() => onSelectContextMode('grid')}
        >
          Grid
        </button>
        <button
          class:active={contextPanelMode === 'stack'}
          type="button"
          role="menuitem"
          aria-label="Use stacked context cards"
          onclick={() => onSelectContextMode('stack')}
        >
          Stack
        </button>
      </div>
      <button
        class="view-menu-wide-button"
        class:active={contextRailOnly}
        type="button"
        role="menuitem"
        aria-label="Collapse context to icon rail"
        title="Shrink the context cards to the icon rail"
        disabled={contextRailOnly}
        onclick={onCollapseContextRail}
      >
        Collapse to icon rail
      </button>
      <button
        class="view-menu-wide-button"
        type="button"
        role="menuitem"
        onclick={onToggleContextCollapsed}
      >
        {contextPanelCollapsed ? 'Show context cards' : 'Hide context cards'}
      </button>
    </section>

    <section class="view-menu-section dock-panel-manager" aria-label="Dock panels">
      <span>Panels</span>
      <div class="dock-panel-manager-list">
        {#each managedDockPanelIDs as panelID (panelID)}
          <div class="dock-panel-manager-row">
            <div>
              <strong>{dockPanelLabel(panelID)}</strong>
              <small>{dockPanelPlacementSummary(panelID)}</small>
            </div>
            <div class="dock-panel-manager-actions">
              {#if dockPanelCanHide(panelID)}
                <button
                  class:active={dockPanelVisible(panelID)}
                  type="button"
                  role="menuitem"
                  aria-label={`${dockPanelVisible(panelID) ? 'Hide' : 'Show'} ${dockPanelLabel(panelID)} panel`}
                  onclick={() => onToggleDockPanelVisibility(panelID)}
                >
                  {dockPanelVisible(panelID) ? 'Hide' : 'Show'}
                </button>
              {/if}
              {#each dockPanelMoveTargets(panelID) as groupID (groupID)}
                <button
                  class:active={dockGroupIDForPanel(dockLayout, panelID) === groupID}
                  type="button"
                  role="menuitem"
                  aria-label={`Move ${dockPanelLabel(panelID)} to ${dockGroupLabel(groupID, panelID)}`}
                  disabled={dockGroupIDForPanel(dockLayout, panelID) === groupID}
                  onclick={() => onMoveDockPanelToManagedGroup(panelID, groupID)}
                >
                  {dockGroupLabel(groupID, panelID)}
                </button>
              {/each}
            </div>
          </div>
        {/each}
      </div>
      <button
        class="view-menu-wide-button"
        type="button"
        role="menuitem"
        onclick={onResetDockLayout}
      >
        Reset dock layout
      </button>
    </section>

    <label class="view-terminal-picker" title={`Open directories in ${terminalApp}`}>
      <span>Terminal</span>
      <select
        bind:value={terminalApp}
        aria-label="Terminal app"
        onchange={onSelectTerminalApp}
      >
        {#each terminalApps as app (app)}
          <option value={app}>{app}</option>
        {/each}
      </select>
    </label>
  </div>
{/if}

<style>
  /*
   * Page-scoped CSS moved verbatim from `+page.svelte`'s `<style>` (the only
   * View-menu CSS that was NOT already global). The shared `.view-menu*` chrome
   * and `.view-terminal-picker > span` stay GLOBAL in `src/app.css` (the
   * editor-topbar's `.view-menu-anchor`/`.view-menu-button` toggle stays inline
   * in the page); only this component renders the dock-panel-manager rows and the
   * terminal-picker layout, so these are safe to scope here.
   */
  .dock-panel-manager-list {
    display: grid;
    gap: 4px;
    min-width: 0;
  }

  .dock-panel-manager-row {
    display: grid;
    grid-template-columns: minmax(0, 74px) minmax(0, 1fr);
    align-items: center;
    gap: 6px;
    min-width: 0;
    min-height: 30px;
    padding: 4px;
    border: 1px solid rgba(255, 255, 255, 0.055);
    border-radius: 7px;
    background: rgba(255, 255, 255, 0.025);
  }

  .dock-panel-manager-row > div:first-child {
    display: grid;
    gap: 1px;
    min-width: 0;
  }

  .dock-panel-manager-row strong,
  .dock-panel-manager-row small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .dock-panel-manager-row strong {
    color: #e8eeec;
    font-size: 9.5px;
    font-weight: 850;
  }

  .dock-panel-manager-row small {
    color: #7f8b88;
    font-size: 8.5px;
    font-weight: 760;
  }

  .dock-panel-manager-actions {
    display: flex;
    justify-content: flex-end;
    gap: 3px;
    min-width: 0;
    overflow: hidden;
  }

  .dock-panel-manager-actions button {
    flex: 0 1 auto;
    min-width: 0;
    height: 22px;
    padding: 0 6px;
    color: #aab6b2;
    border: 1px solid rgba(255, 255, 255, 0.075);
    border-radius: 5px;
    background: rgba(255, 255, 255, 0.035);
    font: inherit;
    font-size: 8.5px;
    font-weight: 820;
    white-space: nowrap;
    cursor: pointer;
  }

  .dock-panel-manager-actions button:hover,
  .dock-panel-manager-actions button:focus-visible {
    color: #edf4f2;
    outline: 0;
    background: rgba(255, 255, 255, 0.08);
  }

  .dock-panel-manager-actions button.active,
  .dock-panel-manager-actions button:disabled {
    color: #dffdf8;
    background: rgba(92, 226, 207, 0.16);
    cursor: default;
  }

  /* `.view-terminal-picker > span` stays GLOBAL (shared label style in app.css). */
  .view-terminal-picker {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: 8px;
    min-width: 0;
    padding-top: 2px;
  }

  .view-terminal-picker select {
    width: 100%;
    height: 26px;
    min-width: 0;
    color: #dffdf8;
    border: 1px solid rgba(255, 255, 255, 0.075);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.04);
    font-size: 10px;
    font-weight: 820;
  }
</style>
