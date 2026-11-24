<script lang="ts">
  /**
   * Shared provider glyph rendered strictly from the backend's `icon` path (`ProviderGlyph.cs`),
   * keeping the frontend 100% provider-agnostic while rendering crisp brand marks + remote badge.
   */
  import { Bot, Cloud, Sparkles } from '@lucide/svelte';
  import AntigravityMark from './AntigravityMark.svelte';
  import ClaudeMark from './ClaudeMark.svelte';
  import CodexMark from './CodexMark.svelte';

  interface Props {
    /** Backend-supplied glyph path from `ProviderGlyph.Of(agent)` (e.g. `icons/asterisk.svg`). */
    icon?: string | null;
    remote?: boolean;
    size?: number;
  }

  let { icon = null, remote = false, size = 15 }: Props = $props();

  const glyphKey = $derived((icon ?? '').trim().toLowerCase());
</script>

<span
  class="provider-icon"
  class:warm={glyphKey.endsWith('asterisk.svg')}
  class:cool={glyphKey.endsWith('square-terminal.svg')}
  class:mint={glyphKey.endsWith('rocket.svg')}
  class:violet={glyphKey.endsWith('sparkles.svg')}
  style:--icon-size={`${size}px`}
>
  {#if glyphKey.endsWith('asterisk.svg')}
    <ClaudeMark class="glyph" aria-hidden="true" />
  {:else if glyphKey.endsWith('square-terminal.svg')}
    <CodexMark class="glyph" aria-hidden="true" />
  {:else if glyphKey.endsWith('rocket.svg')}
    <AntigravityMark class="glyph" aria-hidden="true" />
  {:else if glyphKey.endsWith('sparkles.svg')}
    <Sparkles size={size} aria-hidden="true" />
  {:else}
    <Bot size={size} aria-hidden="true" />
  {/if}

  {#if remote}
    <span class="remote-cloud" title="Remote session" aria-hidden="true">
      <Cloud size={9} strokeWidth={2.3} />
    </span>
  {/if}
</span>

<style>
  .provider-icon {
    position: relative;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: calc(var(--icon-size) + 6px);
    height: calc(var(--icon-size) + 6px);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.04);
    color: var(--color-text-2);
    flex-shrink: 0;
  }

  .provider-icon.warm {
    color: #d97757;
  }

  .provider-icon.cool {
    color: #93c5fd;
  }

  .provider-icon.mint {
    color: #6ee7b7;
  }

  .provider-icon.violet {
    color: #c4b5fd;
  }

  .provider-icon :global(.glyph) {
    width: var(--icon-size);
    height: var(--icon-size);
  }

  .remote-cloud {
    position: absolute;
    right: -3px;
    bottom: -3px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 12px;
    height: 12px;
    border-radius: 999px;
    background: var(--color-rail);
    color: var(--color-accent);
  }
</style>
