<script lang="ts">
  import { Kept } from '../rows/kept.svelte';
  let { text, kept = new Kept() }: { text: string; kept?: Kept } = $props();
</script>

<details bind:open={kept.open}>
  <summary>Thinking</summary>
  <p>{text}</p>
</details>

<style>
  details {
    color: var(--muted);
    margin: 0 0 8px;
  }
  p {
    white-space: pre-wrap;
    margin: 4px 0 0;
  }
</style>
