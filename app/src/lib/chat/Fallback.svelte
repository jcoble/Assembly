<script lang="ts">
  // A row kind without its own component: show its kind so nothing silently disappears.
  let { type }: { type: string } = $props();
</script>

<div class="fallback">[{type}]</div>

<style>
  .fallback {
    color: var(--muted);
    font-size: 12px;
    margin: 0 0 8px;
  }
</style>
