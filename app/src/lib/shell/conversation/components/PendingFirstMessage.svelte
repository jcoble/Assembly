<script lang="ts">
  import WorkingSpinner from '../../components/feedback/WorkingSpinner.svelte';
  let { text, seed }: { text: string; seed?: string } = $props();
</script>
<div class="pending-first-message" role="status" aria-label="Sending your message">
  <span>{text}</span><WorkingSpinner {seed} /><span class="sr-only">Sending…</span>
</div>
<style>
  .pending-first-message { display: flex; align-items: center; gap: 12px; margin: 24px 24px 24px auto; padding: 12px 18px; max-width: min(75%, 760px); border-radius: 20px; background: var(--color-surface-raised); color: var(--color-text); text-align: left; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
