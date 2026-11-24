<script lang="ts">
  import { prefersReducedMotion } from 'svelte/motion';
  import { pickSpinner } from './workingSpinners';

  // Each design is a pre-rendered 12 fps loop (scripts/renderSpinnerFrames.ts) plus a still frame. WebKit
  // stops drawing an image that is off screen or in a hidden window, so nothing here needs to pause it.
  const images = import.meta.glob<string>('./spinners/*.webp', { eager: true, import: 'default', query: '?url' });

  let { size = 16, seed }: { size?: number; seed?: string } = $props();
  // A session's design follows its seed; an unseeded spinner picks once and keeps it while mounted.
  const randomDesign = pickSpinner();
  const id = $derived(seed === undefined ? randomDesign : pickSpinner(seed));
  const src = $derived(images[`./spinners/${id}${prefersReducedMotion.current ? '-still' : ''}.webp`]);
</script>

<!-- Each frame holds a 14 px drawing plus 3 px of glow per side, so the image is 20/14 of size and pulled back by
     that glow: the drawing fills size and the glow spills past the box, as the CSS spinner's did. -->
<img class="spinner" {src} alt="" draggable="false" style:width={`${(size * 20) / 14}px`} style:height={`${(size * 20) / 14}px`} style:margin={`${(-size * 3) / 14}px`} data-testid="working-spinner" data-spinner={id} aria-hidden="true" />

<style>
  .spinner{display:inline-block;flex-shrink:0;vertical-align:middle}
</style>
