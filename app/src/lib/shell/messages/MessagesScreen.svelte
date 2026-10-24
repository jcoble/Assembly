<!--
  MessagesScreen.svelte — every agent-to-agent message on every machine, over the
  whole window like the Projects screen. Back or Escape closes it, and so does a
  chat actually opening from the reader.
-->
<script lang="ts">
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';

  import { Button } from '$lib/components/ui/button/index.js';
  import AgentMessages from '$lib/shell/messages/AgentMessages.svelte';

  interface Props {
    runId: string | null;
    runLabel: string;
    onBack: () => void;
    onClearRun: () => void;
    onShowRun: (runId: string, label: string) => void;
  }
  let { runId, runLabel, onBack, onClearRun, onShowRun }: Props = $props();

  // Focus starts inside the screen so Escape closes it straight after opening.
  let screen: HTMLDivElement | undefined = $state();
  $effect(() => {
    screen?.focus();
  });
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<div
  bind:this={screen}
  class="messages-screen"
  tabindex="-1"
  role="region"
  aria-label="Messages"
  data-testid="messages-screen"
  onkeydown={(event) => {
    if (event.key === 'Escape') onBack();
  }}
>
  <!-- One centred column, so lines stay readable on a wide window. -->
  <div class="mx-auto flex min-h-0 w-full max-w-[1180px] flex-1 flex-col gap-1">
    <header class="-ml-2 pb-2">
      <Button variant="ghost" size="sm" onclick={onBack}><ArrowLeft />Back</Button>
    </header>
    <h1 class="title">Messages</h1>
    <p class="pb-4 text-(length:--text-quiet) text-[var(--color-text-2)]">What your agents and workflows sent each other, on every machine.</p>
    <div class="min-h-0 flex-1">
      <AgentMessages visible={true} layout="screen" session={null} {runId} {runLabel} {onClearRun} {onShowRun} onOpened={onBack} />
    </div>
  </div>
</div>

<style>
  .messages-screen {
    position: absolute;
    inset: 0;
    z-index: 20;
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    padding: var(--space-3) var(--space-5) var(--space-5);
    background: var(--color-bg);
    outline: none;
  }

  .title {
    margin: 0;
    font-size: var(--text-heading);
    font-weight: var(--text-heading-weight);
  }
</style>
