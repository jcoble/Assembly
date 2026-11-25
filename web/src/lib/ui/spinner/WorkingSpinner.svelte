<script lang="ts">
  import { prefersReducedMotion } from 'svelte/motion';
  import { isPageHidden, observeSpinnerElement } from './spinnerLifecycle.svelte';
  import { pickSpinner } from './workingSpinners';

  // Each design is a static 32-frame horizontal WebP sprite strip (8 fps across a 4 s loop),
  // decoded ONCE into a single GPU texture and stepped via compositor-only `transform: translate3d`
  // (`steps(32, end)`). No per-frame CPU WebP decoding, no texture re-uploads, no JS timers.
  // Frame 0 is the still frame, used automatically whenever the window is hidden, the spinner is
  // scrolled off-screen, or prefers-reduced-motion is enabled.
  const STRIP_FRAMES = 32;
  const strips = import.meta.glob<string>('./spinners/*.webp', {
    eager: true,
    import: 'default',
    query: '?url'
  });

  let { size = 16, seed }: { size?: number; seed?: string } = $props();
  // A session's design follows its seed; an unseeded spinner picks once and keeps it while mounted.
  const randomDesign = pickSpinner();
  const id = $derived(seed === undefined ? randomDesign : pickSpinner(seed));
  const src = $derived(strips[`./spinners/${id}.webp`]);

  let hostEl = $state<HTMLSpanElement | null>(null);
  let inViewport = $state(true);

  $effect(() => {
    const el = hostEl;
    if (!el) return;
    return observeSpinnerElement(el, (visible) => {
      inViewport = visible;
    });
  });

  const running = $derived(!prefersReducedMotion.current && !isPageHidden() && inViewport);
  const framePx = $derived((size * 20) / 14);
  const bleedPx = $derived((-size * 3) / 14);
</script>

<!-- Each frame holds a 14 px drawing plus 3 px of glow per side, so the box is 20/14 of size and pulled back by
     that glow: the drawing fills size and the glow spills past the box, matching the original neon spinner geometry. -->
<span
  bind:this={hostEl}
  class="spinner-viewport"
  style:width={`${framePx}px`}
  style:height={`${framePx}px`}
  style:margin={`${bleedPx}px`}
  data-testid="working-spinner"
  data-spinner={id}
  data-running={running ? 'true' : 'false'}
  aria-hidden="true"
>
  <img
    class="spinner-strip"
    class:running
    {src}
    alt=""
    draggable="false"
    decoding="async"
    style:width={`${STRIP_FRAMES * 100}%`}
  />
</span>

<style>
  .spinner-viewport {
    position: relative;
    display: inline-block;
    flex-shrink: 0;
    vertical-align: middle;
    overflow: hidden;
    contain: strict;
  }

  .spinner-strip {
    display: block;
    height: 100%;
    max-width: none;
    transform: translate3d(0, 0, 0);
  }

  .spinner-strip.running {
    animation: assembly-spinner-strip 4s steps(32, end) infinite;
    will-change: transform;
  }

  @keyframes assembly-spinner-strip {
    from {
      transform: translate3d(0, 0, 0);
    }
    to {
      transform: translate3d(-100%, 0, 0);
    }
  }
</style>
