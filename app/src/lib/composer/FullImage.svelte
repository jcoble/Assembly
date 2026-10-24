<script lang="ts">
  // The original image over the page; a click or Escape closes it. The caller owns the URL.
  let { src, name, onclose }: { src: string; name: string; onclose: () => void } = $props();
</script>

<svelte:window onkeydown={(event) => event.key === 'Escape' && onclose()} />

<button class="overlay" onclick={onclose} aria-label={`Close ${name}`}>
  <img {src} alt={name} />
</button>

<style>
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 10;
    display: grid;
    place-items: center;
    padding: 32px;
    border: 0;
    background: rgb(0 0 0 / 0.75);
    cursor: zoom-out;
  }
  img {
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
  }
</style>
