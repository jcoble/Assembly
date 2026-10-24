<script lang="ts">
  /**
   * GitPanel.svelte — the /next shell's source-control panel.
   *
   * It is a one-line host now: everything on screen is drawn by
   * `SourceControlPanes`, which owns the repository strip, the changed files
   * with the commit box, and the commit history drawn as a graph. This file
   * stays because the tool column parks one panel per view, and this is the one
   * it parks into the Source control view.
   *
   * The changes to a file are NOT drawn here any more. Clicking a file writes it
   * into the state `GitDiffView` reads, and that view is mounted as the Diff tab
   * in the middle of the shell — so this panel only reports that a file was
   * picked, and the page brings that tab to the front.
   *
   * Nothing loads until the shell calls `gitService.activate(root)` with the
   * active session's folder; until then the panes say so and stay quiet, which
   * is what keeps a cold launch free of git calls.
   */
  import SourceControlPanes from './SourceControlPanes.svelte';

  interface Props {
    /** A file's changes were picked, so whatever is showing the diff should come
     * to the front. Optional: without it the diff still updates, it just does
     * not come forward on its own. */
    onShowDiff?: () => void;
  }
  let { onShowDiff }: Props = $props();
</script>

<SourceControlPanes {onShowDiff} />
