<script lang="ts">
  /**
   * ShellFrame.svelte — mounts the Gridview root and teleports Svelte-owned
   * content into its regions. All content is authored in the
   * hidden parking stage below, so dockview NEVER owns app DOM; if the frame
   * fails to mount, content simply stays parked (invisible) and the page's
   * error rail reports it. Layout persistence is supplied by SQLite settings.
   */
  import 'dockview-core/dist/styles/dockview.css';
  import { onMount, type Snippet } from 'svelte';

  import {
    createShellFrame,
    type RegionHeightLimits,
    type RegionWidthLimits,
    type ShellFrame as Frame,
    type ShellRegionId
  } from '$lib/shell/layout/frame';
  import {
    readAssemblySettingFromTauri,
    writeAssemblySettingFromTauri
  } from '$lib/tauriSource';
  const GRID_LAYOUT_SETTING_KEY = 'shell.grid-layout';

  interface Props {
    /** The left column: the sessions list, and nothing else. */
    sessions: Snippet;
    /** The middle column: the chat, always mounted. */
    center: Snippet;
    /** The right column: the tab pane, in the grid only while it has tabs. */
    tools: Snippet;
    dock: Snippet;
    onSessionsWidthChange?: (width: number) => void;
    onToolsWidthChange?: (width: number) => void;
    onReady?: (controls: {
      resetLayout: () => void;
      /** Give one region a width — how a column asks to be folded up or
       * opened out. See `setRegionWidth` in `frame.ts`. */
      setRegionWidth: (id: ShellRegionId, width: number, limits?: RegionWidthLimits) => void;
      /** Give one region a height — how the bottom strip is closed when the
       * Problems list has been moved elsewhere or hidden. See
       * `setRegionHeight` in `frame.ts`. */
      setRegionHeight: (id: ShellRegionId, height: number, limits?: RegionHeightLimits) => void;
      /** Put the bottom dock in the grid or take it out. A shut strip is
       * removed rather than shortened, because a region of zero height still
       * keeps its divider and the room around it. See `setDockPresent` in
       * `frame.ts`. */
      setDockPresent: (present: boolean) => void;
      /** Put the right tools region in the grid or remove it completely. */
      setToolsPresent: (present: boolean) => void;
      /** Widen the right region over the center, or put it back. See `setToolsExpanded` in `frame.ts`. */
      setToolsExpanded: (expanded: boolean) => void;
      /** Say what a region may be dragged to without moving it. See
       * `setRegionLimits` in `frame.ts`. */
      setRegionLimits: (id: ShellRegionId, limits: RegionWidthLimits) => void;
      /** How wide a region is right now, or null if the frame is gone. */
      regionWidth: (id: ShellRegionId) => number | null;
    }) => void;
    onError?: (message: string) => void;
  }
  let {
    sessions,
    center,
    tools,
    dock,
    onSessionsWidthChange,
    onToolsWidthChange,
    onReady,
    onError
  }: Props = $props();

  let gridHost: HTMLElement;
  let sessionsSlot: HTMLElement;
  let centerRegionSlot: HTMLElement;
  let toolsSlot: HTMLElement;
  let dockSlot: HTMLElement;

  let frame: Frame | null = null;
  let frameLayoutListener: { dispose(): void } | null = null;
  let ready = $state(false);

  onMount(() => {
    let observer: ResizeObserver | null = null;
    const owner = { active: true };
    let laidOutWidth = -1;
    let laidOutHeight = -1;
    let layoutRequest = 0;
    let lastLayoutAt = 0;
    const reportRegionWidths = (): void => {
      const sessionsWidth = frame?.regionWidth('sessions');
      if (sessionsWidth !== null && sessionsWidth !== undefined) onSessionsWidthChange?.(sessionsWidth);
      const toolsWidth = frame?.regionWidth('tools');
      onToolsWidthChange?.(toolsWidth ?? 0);
    };
    const layoutFrame = () => {
      // The grid fills the content box: `clientWidth` includes the frame's
      // padding, and laying out at that size pushed the right and bottom
      // panels past the gutter to the window edge.
      const style = getComputedStyle(gridHost);
      const width =
        gridHost.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
      const height =
        gridHost.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
      if (width === laidOutWidth && height === laidOutHeight) return;
      // A one-off size change (the drawer opening or closing) gets the same one-frame WebKit
      // release as `setToolsPresent` in frame.ts: hide the center, lay out a frame later, reveal
      // a frame after that. The first layout, and changes that arrive every frame (a drag, a
      // window resize), lay out at once, so the center is not blank for the whole drag.
      const release = laidOutWidth >= 0 && performance.now() - lastLayoutAt >= 100;
      lastLayoutAt = performance.now();
      laidOutWidth = width;
      laidOutHeight = height;
      const apply = (): void => {
        frame?.layout(width, height);
        reportRegionWidths();
      };
      cancelAnimationFrame(layoutRequest);
      centerRegionSlot.style.visibility = release ? 'hidden' : '';
      if (!release) return apply();
      layoutRequest = requestAnimationFrame(() => {
        apply();
        layoutRequest = requestAnimationFrame(() => (centerRegionSlot.style.visibility = ''));
      });
    };
    const scheduleFrameLayout = () => {
      layoutFrame();
    };

    async function initializeShellFrame(): Promise<void> {
      try {
        frame = createShellFrame(gridHost, {
          readLayout: () => readAssemblySettingFromTauri(GRID_LAYOUT_SETTING_KEY),
          writeLayout: (layout) => writeAssemblySettingFromTauri(GRID_LAYOUT_SETTING_KEY, layout),
          regions: {
            sessions: sessionsSlot,
            center: centerRegionSlot,
            tools: toolsSlot,
            dock: dockSlot
          }
        });
        await frame.ready;
        if (!owner.active || !frame) return;
        layoutFrame();
        frameLayoutListener = frame.api.onDidLayoutChange(reportRegionWidths);
        reportRegionWidths();
        observer = new ResizeObserver(scheduleFrameLayout);
        observer.observe(gridHost);
        ready = true;
        onReady?.({
          resetLayout: () => frame?.resetLayout(),
          setRegionWidth: (id, width, limits) => frame?.setRegionWidth(id, width, limits),
          setRegionHeight: (id, height, limits) => frame?.setRegionHeight(id, height, limits),
          setDockPresent: (present) => frame?.setDockPresent(present),
          setToolsPresent: (present) => frame?.setToolsPresent(present),
          setToolsExpanded: (expanded) => frame?.setToolsExpanded(expanded),
          setRegionLimits: (id, limits) => frame?.setRegionLimits(id, limits),
          regionWidth: (id) => frame?.regionWidth(id) ?? null
        });
      } catch (error) {
        if (owner.active) onError?.(error instanceof Error ? error.message : String(error));
      }
    }

    void initializeShellFrame();

    return () => {
      owner.active = false;
      observer?.disconnect();
      cancelAnimationFrame(layoutRequest);
      frameLayoutListener?.dispose();
      frameLayoutListener = null;
      frame?.dispose();
      frame = null;
    };
  });
</script>

<div class="shell-frame" class:ready bind:this={gridHost}></div>

<!-- Parking stage: content lives here until (and unless) dockview claims it, and
     whenever a panel hands it back. Not rendered at all, so nothing parked here
     can be measured — see the note on `.parking-stage` in the styles below. -->
<div class="parking-stage" aria-hidden="true">
  <div class="slot" bind:this={sessionsSlot}>{@render sessions()}</div>
  <div class="slot" bind:this={centerRegionSlot}>{@render center()}</div>
  <div class="slot tools-region" bind:this={toolsSlot}>{@render tools()}</div>
  <div class="slot" bind:this={dockSlot}>{@render dock()}</div>
</div>

<style>
  .shell-frame {
    height: 100%;
    width: 100%;
    overflow: hidden;
    /* Matches the gutter each panel already keeps, so the cards sit inside the
       window rather than running off its edges. */
    padding: 4px;
    background: var(--color-bg);
    visibility: hidden; /* anti-flash: revealed when ready */
  }
  .shell-frame.ready {
    visibility: visible;
  }

  /* `display: none`, NOT a 1px hidden box: parked content must be UNMEASURABLE.
     A 1px stage still has a size, and a terminal parked in one measures its host
     at zero — xterm's fit addon clamps that to 2 columns by 1 row and resizes the
     real PTY to it, reflowing the agent's screen. With `display: none` the
     computed height is `auto`, the fit addon proposes NaN and returns without
     touching the grid. Content renders normally the moment it is moved out. */
  .parking-stage {
    display: none;
  }

  /* Teleported slots and hosts must fill whatever cell they land in. */
  .slot,
  :global(.shell-region-host) {
    height: 100%;
    width: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  .tools-region {
    min-width: 0;
    min-height: 0;
    /* Transparent on purpose: the card behind it paints the surface and its
       gradient, and an opaque fill here would cover both. This also drops a
       stray `--background`, which is a different token from the `--color-*`
       set the shell uses. */
    background: transparent;
  }

  /* ---- dockview overrides (ported from the old shell's audited CSS) ----
     BOTH theme classes must be targeted where dracula reasserts values.   */
  .shell-frame :global(.shell-grid) {
    /* token map: translate dockview vars onto the /next palette */
    --dv-border-radius: 8px;
    /* Transparent, not the surface colour: the card underneath already paints
       the surface and the gradient down its top edge, and an opaque group here
       covered both from the tab strip down. */
    --dv-group-view-background-color: transparent;
    --dv-tabs-and-actions-container-background-color: transparent;
    --dv-activegroup-visiblepanel-tab-background-color: var(--color-surface);
    --dv-activegroup-hiddenpanel-tab-background-color: var(--color-bg);
    --dv-inactivegroup-visiblepanel-tab-background-color: var(--color-tab-unfocused-surface);
    --dv-inactivegroup-hiddenpanel-tab-background-color: var(--color-bg);
    --dv-tab-divider-color: transparent;
    --dv-tabs-and-actions-container-height: 0px;
    --dv-activegroup-visiblepanel-tab-color: var(--color-text);
    --dv-activegroup-hiddenpanel-tab-color: var(--color-text-2);
    --dv-inactivegroup-visiblepanel-tab-color: var(--color-tab-unfocused-text);
    --dv-inactivegroup-hiddenpanel-tab-color: var(--color-text-2);
    --dv-separator-border: color-mix(in srgb, var(--color-border) 64%, transparent);
    --dv-paneview-active-outline-color: transparent;
    /* NEUTRAL wash only — the teal accent here caused the green drag-flash. */
    --dv-drag-over-background-color: rgba(255, 255, 255, 0.05);
    --dv-drag-over-border-color: transparent;
    /* VS Code-style resize feedback: the divider lights up teal on hover/drag.
       Safe to color, unlike the drag-over wash above — this paints only the
       sash line itself, never the panel content. Delay 0 so it appears the
       moment you grab it, not half a second in. */
    --dv-active-sash-color: var(--color-accent);
    /* Panels read as cards laid on the backdrop rather than as regions cut out
       of one sheet: each section is pulled in a few pixels so the background
       shows through as a gutter, and the separator rules are dropped since the
       gap already does that work. */
    --dv-separator-border: transparent;
    --dv-active-sash-transition-delay: 0.1s;
    --dv-active-sash-transition-duration: 0.05s;
  }

  /* The gutter itself. The group is a plain flex column inside the splitview's
     absolutely placed box, so pulling it in on every side and taking the same
     amount off its height leaves the backdrop showing between sections. */
  /* ─── The cards ──────────────────────────────────────────────────────────
     THE one rule that shapes the shell's panels. `.shell-region-host` is the
     box each region's content is mounted into — sessions, centre, dock, tools
     — so rounding and insetting it here is what makes all four read as cards
     laid on the backdrop. Nothing else needs a radius: this clips its content.

     Not the dockview classes: the regions are Gridview cells, not groups. */
  .shell-frame :global(.shell-region-host) {
    /* The inset is a pair of MAXIMUMS, not a width and a height. Dockview
       writes `width: 100%; height: 100%` on this element as an inline style,
       which outranks any rule here, so sizes set the ordinary way were thrown
       away while the margin still moved the box: every card overhung its cell
       by its margin on the right and along the bottom, and the cell's
       `overflow: hidden` shaved those two edges off square — taking two of the
       four rounded corners with them. Nothing inline competes with a maximum,
       so this holds, and the gutter is the same eight pixels on all four sides. */
    max-width: calc(100% - 8px);
    max-height: calc(100% - 8px);
    margin: 4px;
    border-radius: var(--radius-md);
    overflow: hidden;
    /* A card is a lit surface, not a flat fill: each one carries a faint
       purple light down from its top edge (`--panel-fade`), gone within the
       first few hundred pixels. Without it the panels read as holes cut in the
       backdrop. */
    background: var(--panel-fade), var(--color-surface);
    background-repeat: no-repeat;
  }

  /* seam flattening */
  .shell-frame :global(.dv-groupview),
  .shell-frame :global(.dv-tabs-and-actions-container),
  .shell-frame :global(.dv-content-container),
  .shell-frame :global(.dv-tabs-container),
  .shell-frame :global(.dv-tab) {
    border-color: transparent;
    box-shadow: none;
  }

  /* sizing chain — without this, dockview panels collapse in flex/grid parents */
  .shell-frame :global(.dv-dockview),
  .shell-frame :global(.dv-gridview),
  .shell-frame :global(.dv-grid-view),
  .shell-frame :global(.dv-branch-node),
  .shell-frame :global(.dv-view-container),
  .shell-frame :global(.dv-view) {
    min-width: 0;
    min-height: 0;
  }

  /* A grid cell never scrolls; the panel inside it does.
   *
   * dockview leaves `.dv-view` scrollable, and on a Mac set to show scroll bars
   * always, each one drew a horizontal bar along its bottom edge. Three cells
   * side by side read as one band above the status bar, and because the bar is
   * part of the cell, the panel inside — sized at `height: 100%` — stopped
   * seventeen pixels short of the status bar and took its own scrollbar with it.
   * Nothing in a grid cell is meant to be reached by scrolling the cell. */
  .shell-frame :global(.dv-view) {
    overflow: hidden;
  }
</style>
