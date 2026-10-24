<script lang="ts">
  import type { Snippet } from 'svelte';
  let { parts }: { parts: Snippet } = $props();
</script>

{@render parts()}
