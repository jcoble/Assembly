<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { Action } from 'svelte/action';

  type Props = {
    shellClass: string;
    hostClass: string;
    errorClass: string;
    enabled: boolean;
    ready: boolean;
    error?: string;
    hostAction: Action<HTMLElement>;
    children?: Snippet;
  };

  let {
    shellClass,
    hostClass,
    errorClass,
    enabled,
    ready,
    error = '',
    hostAction,
    children
  }: Props = $props();
</script>

<div
  class={shellClass}
  class:dockview-enabled={enabled}
  class:dockview-ready={ready}
  class:dockview-error={Boolean(error)}
>
  {#if enabled}
    <div class={`${hostClass} source-dockview-host`} aria-hidden={!ready} use:hostAction></div>
  {/if}

  {#if children}
    {@render children()}
  {/if}

  {#if error}
    <div class={errorClass} role="status">
      {error}
    </div>
  {/if}
</div>

<style>
  /*
   * Borderless workbench theme — maps dockview-core 6 `--dv-*` vars onto our design
   * tokens. Both `.dockview-theme-dark` and `.dockview-theme-dracula` are applied to the
   * same host (see sourceDockviewWorkspace.ts), and the upstream `.dockview-theme-dracula`
   * block reasserts its own hardcoded hex AFTER ours, so every scoped block below targets
   * both classes to win the cascade. The shared variable set lives in `--dv-*` here so the
   * look is defined once and reused per scope.
   *
   * Note: dockview-core 6 consumes `--dv-group-view-background-color` for panel/group
   * surfaces (the old `--dv-background-color` name does not exist in v6 and was a no-op).
   */
  :global(.source-dockview-workbench-shell) {
    position: absolute;
    inset: 0;
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr);
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    background: var(--color-bg);
  }

  :global(.source-dockview-workbench-host),
  :global(.source-dockview-workbench-shell > :not(.source-dockview-workbench-error)) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-workbench-host) {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-workbench-shell.dockview-ready .source-dockview-workbench-host) {
    visibility: visible;
  }

  :global(.source-dockview-workbench-shell.dockview-ready > :not(.source-dockview-workbench-host):not(.source-dockview-workbench-error)) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-workbench-shell .dockview-theme-dark),
  :global(.source-dockview-workbench-shell .dockview-theme-dracula),
  :global(.source-dockview-activity-shell .dockview-theme-dark),
  :global(.source-dockview-activity-shell .dockview-theme-dracula),
  :global(.source-dockview-center-shell .dockview-theme-dark),
  :global(.source-dockview-center-shell .dockview-theme-dracula),
  :global(.source-dockview-editor-files-shell .dockview-theme-dark),
  :global(.source-dockview-editor-files-shell .dockview-theme-dracula),
  :global(.source-dockview-bottom-shell .dockview-theme-dark),
  :global(.source-dockview-bottom-shell .dockview-theme-dracula),
  :global(.source-dockview-files-shell .dockview-theme-dark),
  :global(.source-dockview-files-shell .dockview-theme-dracula),
  :global(.source-dockview-conversation-shell .dockview-theme-dark),
  :global(.source-dockview-conversation-shell .dockview-theme-dracula),
  :global(.source-dockview-context-shell .dockview-theme-dark),
  :global(.source-dockview-context-shell .dockview-theme-dracula),
  :global(.source-dockview-insights-shell .dockview-theme-dark),
  :global(.source-dockview-insights-shell .dockview-theme-dracula) {
    /* surfaces */
    --dv-group-view-background-color: var(--color-bg);
    --dv-tabs-and-actions-container-background-color: var(--color-bg);

    /* active tab: subtle surface fill + bright text */
    --dv-activegroup-visiblepanel-tab-background-color: var(--color-surface);
    --dv-activegroup-visiblepanel-tab-color: var(--color-text);
    --dv-activegroup-hiddenpanel-tab-background-color: transparent;
    --dv-activegroup-hiddenpanel-tab-color: var(--color-text-3);

    /* inactive tabs: transparent, muted text */
    --dv-inactivegroup-visiblepanel-tab-background-color: transparent;
    --dv-inactivegroup-visiblepanel-tab-color: var(--color-text-3);
    --dv-inactivegroup-hiddenpanel-tab-background-color: transparent;
    --dv-inactivegroup-hiddenpanel-tab-color: var(--color-text-3);

    /* borderless: hairline / transparent seams, no thick gutters */
    --dv-separator-border: var(--color-border);
    --dv-tab-divider-color: transparent;
    --dv-paneview-header-border-color: transparent;
    --dv-paneview-active-outline-color: var(--color-accent);
    /* subtle neutral drop wash — the teal accent/live-bg here painted a
       loud green flash across the editor on sash resize (pointer dnd
       momentarily registers a drag-over during a splitter drag) */
    /* Neutral, subtle drop affordance — NOT the teal accent. The accent
       (`--color-focus`) flooded the editor green on every pane drag and the
       0.13s fade read as a flicker; a faint white wash + soft border still
       reads as "drop here" without the green flash. */
    --dv-drag-over-background-color: rgba(255, 255, 255, 0.05);
    --dv-drag-over-border-color: rgba(255, 255, 255, 0.28);
    --dv-floating-box-shadow: var(--shadow-lg);

    /* spacious, comfortable tabs */
    --dv-tabs-and-actions-container-height: 38px;
    --dv-tabs-and-actions-container-font-size: var(--text-sm);
    --dv-tab-border-radius: var(--radius-sm);

    /* thin scrollbars + smooth motion */
    --dv-scrollbar-background-color: var(--color-text-3);
    --dv-tabs-container-scrollbar-color: var(--color-text-3);
    --dv-transition-duration: 0.13s;

    /* sashes invisible at rest, faint accent when active */
    --dv-sash-color: transparent;
    --dv-active-sash-color: var(--color-focus);

    /* context menu / icon hover surfaces */
    --dv-icon-hover-background-color: var(--color-surface);
    --dv-context-menu-background-color: var(--color-elevated);
    --dv-context-menu-color: var(--color-text);
  }

  /* ----------------------------------------------------------------------
   * Borderless structural polish. The `--dv-*` vars above carry colors, but a
   * few seams (group/tab-strip/content borders) and the active-tab indicator
   * are painted by hardcoded upstream rules — including the dracula theme's
   * pink `::after` underline. These rules flatten the seams and retheme the
   * indicator to a clean accent underline. Targets `.source-dockview-host`,
   * which is present on every workbench/dock/paneview container.
   * ---------------------------------------------------------------------- */
  :global(.source-dockview-host .dv-groupview),
  :global(.source-dockview-host .dv-tabs-and-actions-container),
  :global(.source-dockview-host .dv-content-container),
  :global(.source-dockview-host .dv-tabs-container),
  :global(.source-dockview-host .dv-tab) {
    border-color: transparent;
    box-shadow: none;
  }

  /* Spacious, calm tabs — generous padding, no cramped seams, soft hover. */
  :global(.source-dockview-host .dv-tab) {
    gap: var(--space-2);
    align-items: center;
    padding: 0 var(--space-3);
    font-size: var(--text-sm);
    font-weight: var(--weight-medium);
    transition:
      background-color 0.13s ease,
      color 0.13s ease;
  }

  :global(.source-dockview-host .dv-groupview:not(.dv-active-group) .dv-tab.dv-inactive-tab:hover),
  :global(.source-dockview-host .dv-groupview.dv-active-group .dv-tab.dv-inactive-tab:hover) {
    color: var(--color-text-2);
    background-color: var(--color-surface);
  }

  /* Active tab: bright text on a faint surface fill + a thin accent underline.
     Overrides the dracula theme's hardcoded pink `::after`. The `.dockview-theme-dracula`
     class (present on the same host node) is included so this out-specifies — and reliably
     wins the source-order tie against — the upstream dracula rule without `!important`.
     Active + inactive group variants both use the accent so focus state stays consistent. */
  :global(.source-dockview-host.dockview-theme-dracula .dv-groupview > .dv-tabs-and-actions-container .dv-tabs-container > .dv-tab.dv-active-tab) {
    position: relative;
    color: var(--color-text);
  }

  :global(.source-dockview-host.dockview-theme-dracula .dv-groupview > .dv-tabs-and-actions-container .dv-tabs-container > .dv-tab.dv-active-tab::after) {
    position: absolute;
    right: 0;
    bottom: 0;
    left: 0;
    top: auto;
    height: 2px;
    width: 100%;
    content: '';
    background-color: var(--color-accent);
    z-index: 5;
  }

  :global(.source-dockview-host.dockview-theme-dracula .dv-groupview.dv-inactive-group > .dv-tabs-and-actions-container .dv-tabs-container > .dv-tab.dv-active-tab::after) {
    background-color: var(--color-text-3);
  }

  /* Thin, unobtrusive overlay scrollbars on the dockview scroll surfaces. */
  :global(.source-dockview-host .dv-scrollable .dv-scrollbar-horizontal) {
    height: 6px;
  }

  :global(.source-dockview-host .dv-scrollable .dv-scrollbar-vertical) {
    width: 6px;
  }

  :global(.source-dockview-activity-shell) {
    position: relative;
    display: grid;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    background: var(--color-bg);
  }

  :global(.source-dockview-activity-host),
  :global(.source-dockview-activity-shell > .activity-shell) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-activity-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-activity-shell > .activity-shell) {
    width: 100%;
    height: 100%;
  }

  :global(.source-dockview-activity-shell.dockview-ready .source-dockview-activity-host) {
    visibility: visible;
  }

  :global(.source-dockview-activity-shell.dockview-ready > .activity-shell) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-center-shell) {
    position: relative;
    display: grid;
    flex: 1 1 auto;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  :global(.source-dockview-center-host),
  :global(.source-dockview-center-shell > .source-editor-dock-panel) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-center-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-center-shell > .source-editor-dock-panel) {
    width: 100%;
    height: 100%;
  }

  :global(.source-dockview-center-shell.dockview-ready .source-dockview-center-host) {
    visibility: visible;
  }

  :global(.source-dockview-center-shell.dockview-ready > .source-editor-dock-panel) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-editor-files-shell) {
    position: relative;
    display: grid;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  :global(.source-dockview-editor-files-host),
  :global(.source-dockview-editor-files-shell > .source-editor-file-pane) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-editor-files-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-editor-files-shell.dockview-ready .source-dockview-editor-files-host) {
    /*
     * `inherit`, not `visible`: this editor-files dockview is nested INSIDE the editor
     * panel, which is itself a tab in the center dockview. When the editor is the inactive
     * center tab, dockview hides its render-overlay via `visibility: hidden`. A blanket
     * `visibility: visible` here would escape that hidden ancestor and bleed the file-tab
     * strip + Monaco over the active terminal/browser panel. `inherit` keeps the anti-flash
     * hide (the host starts hidden during mount) while still respecting a hidden ancestor.
     */
    visibility: inherit;
  }

  /*
   * dockview-core force-shows the active tab's close button with an own
   * `.dv-tab.dv-active-tab … .dv-default-tab-action { visibility: visible }` rule. Inside the
   * nested editor-files dockview that escapes a hidden editor panel just like the host above,
   * leaving a stray ✕ painted over the active terminal/browser. Re-inherit so the close button
   * follows the editor-files host's (and thus the editor panel's) visibility.
   */
  :global(.source-dockview-editor-files-host .dv-tab.dv-active-tab .dv-default-tab .dv-default-tab-action) {
    visibility: inherit;
  }

  :global(.source-dockview-editor-files-shell.dockview-ready > .source-editor-file-pane) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-editor-files-shell .source-dockview-attached-panel.source-editor-file-pane) {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  :global(.source-dockview-bottom-shell) {
    position: relative;
    display: grid;
    flex: 0 0 auto;
    height: var(--bottom-dock-height);
    min-width: 0;
    min-height: 96px;
    max-height: none;
    margin-top: 6px;
    overflow: hidden;
  }

  :global(.source-dockview-bottom-shell.runtime-parking-only) {
    position: absolute;
    width: 1px;
    height: 1px;
    min-height: 0;
    margin: 0;
    opacity: 0;
    pointer-events: none;
  }

  :global(.source-dockview-bottom-host),
  :global(.source-dockview-bottom-shell > .terminal-launchpad),
  :global(.source-dockview-bottom-shell > .browser-dock) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-bottom-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-bottom-shell > .terminal-launchpad),
  :global(.source-dockview-bottom-shell > .browser-dock) {
    height: 100%;
    max-height: none;
    margin-top: 0;
  }

  :global(.source-dockview-bottom-shell.dockview-ready .source-dockview-bottom-host) {
    visibility: visible;
  }

  :global(.source-dockview-bottom-shell.dockview-ready > .terminal-launchpad),
  :global(.source-dockview-bottom-shell.dockview-ready > .browser-dock) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-bottom-shell .source-dockview-panel-host),
  :global(.source-dockview-workbench-shell .source-dockview-panel-host),
  :global(.source-dockview-center-shell .source-dockview-panel-host),
  :global(.source-dockview-activity-shell .source-dockview-panel-host),
  :global(.source-dockview-files-shell .source-dockview-panel-host),
  :global(.source-dockview-conversation-shell .source-dockview-panel-host),
  :global(.source-dockview-context-shell .source-dockview-panel-host),
  :global(.source-dockview-insights-shell .source-dockview-panel-host),
  :global(.source-paneview-panel-host) {
    display: flex;
    align-items: stretch;
    min-width: 0;
    min-height: 0;
    width: 100%;
    height: 100%;
  }

  :global(.source-dockview-host),
  :global(.source-dockview-host .source-dockview-core),
  :global(.source-dockview-host .dv-dockview),
  :global(.source-dockview-host .dv-split-view-container),
  :global(.source-dockview-host .dv-view-container),
  :global(.source-dockview-host .dv-view),
  :global(.source-dockview-host .dv-groupview),
  :global(.source-dockview-host .dv-content-container),
  :global(.source-paneview-host),
  :global(.source-paneview-host .source-paneview-core),
  :global(.source-paneview-host .dv-pane-container),
  :global(.source-paneview-host .dv-split-view-container),
  :global(.source-paneview-host .dv-view-container),
  :global(.source-paneview-host .dv-view),
  :global(.source-paneview-host .dv-pane) {
    min-width: 0;
    min-height: 0;
    width: 100%;
    height: 100%;
  }

  :global(.source-paneview-host .dv-pane .dv-pane-body) {
    flex: 1 1 auto;
    width: auto;
    height: auto;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    background: transparent;
  }

  :global(.source-dockview-center-shell .source-dockview-panel-host) {
    display: grid;
    grid-template-rows: minmax(0, 1fr);
    align-items: stretch;
    justify-items: stretch;
  }

  :global(.source-dockview-workbench-shell .source-dockview-panel-host) {
    display: grid;
    grid-template-rows: minmax(0, 1fr);
    align-items: stretch;
    justify-items: stretch;
  }

  :global(.source-dockview-workbench-shell .source-dockview-attached-panel) {
    grid-area: 1 / 1;
    align-self: stretch;
    justify-self: stretch;
  }

  :global(.source-dockview-center-shell .source-dockview-attached-panel) {
    grid-area: 1 / 1;
    align-self: stretch;
    justify-self: stretch;
  }

  :global(.source-dockview-bottom-shell .source-dockview-attached-panel),
  :global(.source-dockview-workbench-shell .source-dockview-attached-panel),
  :global(.source-dockview-center-shell .source-dockview-attached-panel),
  :global(.source-dockview-activity-shell .source-dockview-attached-panel),
  :global(.source-dockview-files-shell .source-dockview-attached-panel),
  :global(.source-dockview-conversation-shell .source-dockview-attached-panel),
  :global(.source-dockview-context-shell .source-dockview-attached-panel),
  :global(.source-dockview-insights-shell .source-dockview-attached-panel),
  :global(.source-paneview-attached-panel) {
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  :global(.source-paneview-host .dv-pane-header) {
    color: var(--color-text-2);
    border-color: transparent;
    background: var(--color-bg);
  }

  :global(.source-paneview-host .dv-default-header) {
    display: flex;
    align-items: center;
    gap: 7px;
    min-width: 0;
    height: 100%;
    padding: 0 8px;
    overflow: hidden;
    font-size: var(--text-sm);
    font-weight: var(--weight-semibold);
  }

  :global(.source-paneview-host .dv-default-header span) {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  :global(.source-paneview-host .dv-pane-header-icon) {
    display: grid;
    place-items: center;
    flex: 0 0 14px;
    width: 14px;
    height: 14px;
    color: var(--color-text-2);
  }

  :global(.source-paneview-host .dv-pane-body) {
    overflow: hidden;
    background: transparent;
  }

  :global(.source-paneview-host .dv-sash) {
    --dv-sash-color: transparent;
    --dv-active-sash-color: var(--color-focus);
  }

  :global(.source-paneview-host .dv-sash.dv-enabled:hover) {
    background: var(--color-live-bg);
  }

  :global(.source-dockview-files-shell) {
    position: relative;
    display: grid;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  :global(.source-dockview-files-host),
  :global(.source-dockview-files-shell > .source-files-pane) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-files-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-files-shell.dockview-ready .source-dockview-files-host) {
    visibility: visible;
  }

  :global(.source-dockview-files-shell.dockview-ready > .source-files-pane) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-conversation-shell) {
    position: relative;
    display: grid;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  :global(.source-dockview-conversation-host),
  :global(.source-dockview-conversation-shell > .conversation-dockview-pane) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-conversation-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-conversation-shell.dockview-ready .source-dockview-conversation-host) {
    visibility: visible;
  }

  :global(.source-dockview-conversation-shell.dockview-ready > .conversation-dockview-pane) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-conversation-shell .source-dockview-attached-panel.conversation-active-pane) {
    display: grid;
    align-content: start;
    grid-auto-flow: row;
    grid-auto-rows: max-content;
    gap: 7px;
    overflow-x: hidden;
    overflow-y: auto;
  }


  :global(.source-dockview-insights-shell) {
    position: relative;
    display: grid;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    background: var(--color-bg);
  }

  :global(.source-dockview-insights-parking) {
    position: fixed;
    top: 0;
    left: 0;
    width: 1px;
    height: 1px;
    overflow: hidden;
    visibility: hidden;
    pointer-events: none;
    contain: layout paint;
  }

  :global(.source-dockview-insights-host),
  :global(.source-dockview-insights-shell > .source-intelligence-panel) {
    grid-area: 1 / 1;
    min-width: 0;
    min-height: 0;
  }

  :global(.source-dockview-insights-host) {
    width: 100%;
    height: 100%;
    overflow: hidden;
    visibility: hidden;
    contain: layout paint;
  }

  :global(.source-dockview-insights-shell.dockview-ready .source-dockview-insights-host) {
    visibility: visible;
  }

  :global(.source-dockview-insights-shell.dockview-ready > .source-intelligence-panel) {
    visibility: hidden;
    pointer-events: none;
  }

  :global(.source-dockview-host.dockview-theme-dark),
  :global(.source-dockview-host .dockview-theme-dark),
  :global(.source-dockview-host.dockview-theme-dracula),
  :global(.source-dockview-host .dockview-theme-dracula) {
    --dv-separator-border: var(--color-border);
    --dv-tab-divider-color: transparent;
    --dv-paneview-header-border-color: transparent;
  }

  :global(.source-dockview-host .dv-groupview),
  :global(.source-dockview-host .dv-tabs-and-actions-container),
  :global(.source-dockview-host .dv-content-container) {
    border-color: transparent;
    box-shadow: none;
  }

  :global(.source-dockview-activity-shell .dv-groupview),
  :global(.source-dockview-activity-shell .dv-tabs-and-actions-container),
  :global(.source-dockview-activity-shell .dv-content-container),
  :global(.source-dockview-context-shell .dv-groupview),
  :global(.source-dockview-context-shell .dv-tabs-and-actions-container),
  :global(.source-dockview-context-shell .dv-content-container),
  :global(.source-dockview-insights-shell .dv-groupview),
  :global(.source-dockview-insights-shell .dv-tabs-and-actions-container),
  :global(.source-dockview-insights-shell .dv-content-container) {
    border-color: transparent;
    box-shadow: none;
  }

  :global(.source-dockview-activity-error),
  :global(.source-dockview-bottom-error),
  :global(.source-dockview-context-error),
  :global(.source-dockview-insights-error) {
    position: absolute;
    right: var(--space-2);
    bottom: var(--space-2);
    z-index: 4;
    max-width: calc(100% - 16px);
    overflow: hidden;
    color: var(--color-bad);
    border: 1px solid var(--color-bad-bg-strong);
    border-radius: var(--radius-sm);
    background: var(--color-bad-bg);
    padding: var(--space-1) var(--space-2);
    font-size: var(--text-xs);
    font-weight: var(--weight-semibold);
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
