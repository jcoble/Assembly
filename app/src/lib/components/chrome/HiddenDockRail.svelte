<script lang="ts">
  /**
   * HiddenDockRail.svelte — the workbench "restore hidden dock panel" rail.
   *
   * Presentational only: it renders the floating vertical rail of restore
   * buttons (one per currently-hidden dock panel, each with its panel icon) and
   * emits the restore action via a callback. The page owns the panel-id list,
   * the label helper and the restore function; this component holds NO state.
   *
   * Renders inline in the workbench shell (no teleport). The wrapping
   * `{#if sourceDockviewWorkbenchEnabled && hiddenDockPanelIDs().length > 0}`
   * gate stays in the page.
   *
   * The shared base chrome (`.hidden-dock-panel-rail` / `.hidden-dock-panel-button`)
   * lives in `src/app.css` as global rules because it is also used by the
   * editor-topbar's hidden-dock rail (which stays inline in the page); only the
   * workbench-specific `.workbench-hidden-dock-panel-rail` modifier scopes here.
   */
  import { ExternalLink, FolderGit2, Network, Search, Terminal } from '@lucide/svelte';

  interface Props {
    /** Currently-hidden dock panel ids (= page `hiddenDockPanelIDs()`). */
    panelIDs: string[];
    /** Human label for a dock panel id (page helper). */
    dockPanelLabel: (panelID: string) => string;
    /** Restore a hidden dock panel (page function). */
    onRestore: (panelID: string) => void;
  }

  let { panelIDs, dockPanelLabel, onRestore }: Props = $props();
</script>

<div class="hidden-dock-panel-rail workbench-hidden-dock-panel-rail" aria-label="Hidden dock panels">
  {#each panelIDs as panelID (panelID)}
    <button
      class="hidden-dock-panel-button"
      type="button"
      aria-label={`Restore ${dockPanelLabel(panelID)} panel`}
      title={`Restore ${dockPanelLabel(panelID)} panel`}
      onclick={() => onRestore(panelID)}
    >
      {#if panelID === 'activity'}
        <FolderGit2 size={13} strokeWidth={1.9} />
      {:else if panelID === 'context'}
        <Network size={13} strokeWidth={1.9} />
      {:else if panelID === 'insights'}
        <Search size={13} strokeWidth={1.9} />
      {:else if panelID === 'terminal'}
        <Terminal size={13} strokeWidth={1.9} />
      {:else}
        <ExternalLink size={13} strokeWidth={1.9} />
      {/if}
      <span>{dockPanelLabel(panelID)}</span>
    </button>
  {/each}
</div>

<style>
  /*
   * Workbench-only modifier. The base `.hidden-dock-panel-rail` /
   * `.hidden-dock-panel-button` rules are global in `src/app.css` (shared with
   * the editor-topbar rail); this just repositions the rail for the workbench
   * shell. Scoped is fine — it targets this component's own rail element.
   */
  .workbench-hidden-dock-panel-rail {
    top: 40px;
    right: 8px;
    z-index: 35;
  }
</style>
