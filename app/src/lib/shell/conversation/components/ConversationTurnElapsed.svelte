<script lang="ts">
  import { onMount } from 'svelte';
  import { formatWorkedFor } from '$lib/shell/conversation/conversationTimeline.ts';

  let { running, completed, startedAtMs, elapsedMs }: {
    running: boolean;
    completed: boolean;
    startedAtMs: number | null;
    elapsedMs: number | null;
  } = $props();
  let host = $state<HTMLSpanElement>();
  let visible = $state(false);
  let documentVisible = $state(true);
  let now = $state(Date.now());
  const duration = $derived(running && startedAtMs !== null
    ? Math.max(0, now - startedAtMs) : elapsedMs);

  onMount(() => {
    const observer = new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; });
    if (host) observer.observe(host);
    const visibilityChanged = () => { documentVisible = document.visibilityState === 'visible'; };
    visibilityChanged();
    document.addEventListener('visibilitychange', visibilityChanged);
    return () => {
      observer.disconnect();
      document.removeEventListener('visibilitychange', visibilityChanged);
    };
  });
  $effect(() => {
    if (!running || startedAtMs === null || !visible || !documentVisible) return;
    now = Date.now();
    const timer = setInterval(() => { now = Date.now(); }, 1000);
    return () => clearInterval(timer);
  });
</script>

<span bind:this={host}>{running ? 'Working' : completed ? 'Worked' : 'Work'}{duration === null ? '' : ` for ${formatWorkedFor(duration)}`}</span>
