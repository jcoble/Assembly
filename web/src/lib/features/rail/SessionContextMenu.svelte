<script lang="ts">
  /**
   * Right-click glass context menu for a session row (`data-occludes-chat`).
   * Lifecycle transitions (`sessions/mark`) + clipboard copy actions.
   */
  import { Copy, FolderGit2, Link2, Terminal } from '@lucide/svelte';
  import { onMount } from 'svelte';
  import { markMovesFor, sessionDeepLink } from './format';
  import type { RailEntry, SessionMarkState } from './types';

  interface Props {
    entry: RailEntry;
    x: number;
    y: number;
    onMark: (sessionId: string, state: SessionMarkState) => void;
    onClose: () => void;
  }

  let { entry, x, y, onMark, onClose }: Props = $props();

  let menuEl = $state<HTMLDivElement | null>(null);
  let left = $state(0);
  let top = $state(0);

  const moves = $derived(markMovesFor(entry));

  onMount(() => {
    left = x;
    top = y;
    const rect = menuEl?.getBoundingClientRect();
    if (rect) {
      left = Math.max(8, Math.min(x, window.innerWidth - rect.width - 8));
      top = Math.max(8, Math.min(y, window.innerHeight - rect.height - 8));
    }
    menuEl?.focus();
  });

  function copyText(text: string): void {
    void navigator.clipboard?.writeText(text).catch(() => {});
    onClose();
  }
</script>

<svelte:window
  onpointerdown={(event) => {
    if (menuEl && event.target instanceof Node && !menuEl.contains(event.target)) {
      onClose();
    }
  }}
  onkeydown={(event) => {
    if (event.key === 'Escape') onClose();
  }}
/>

<div
  bind:this={menuEl}
  class="ctx-menu glass-popover"
  role="menu"
  tabindex="-1"
  data-occludes-chat
  style:left={`${left}px`}
  style:top={`${top}px`}
  oncontextmenu={(event) => event.preventDefault()}
>
  {#each moves as move (move.to)}
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      onclick={() => {
        onMark(entry.sessionId, move.to);
        onClose();
      }}
    >
      <span>{move.label}</span>
    </button>
  {/each}

  <div class="ctx-sep" role="separator"></div>

  <button
    type="button"
    class="ctx-item"
    role="menuitem"
    onclick={() => copyText(sessionDeepLink(entry.sessionId, entry.machine))}
  >
    <Link2 size={13} />
    <span>Copy deeplink</span>
  </button>

  <button
    type="button"
    class="ctx-item"
    role="menuitem"
    onclick={() => copyText(entry.sessionId)}
  >
    <Copy size={13} />
    <span>Copy session ID</span>
  </button>

  {#if entry.resumeCommand}
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      onclick={() => copyText(entry.resumeCommand ?? '')}
    >
      <Terminal size={13} />
      <span>Copy resume command</span>
    </button>
  {/if}

  {#if entry.worktree}
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      onclick={() => copyText(entry.worktree ?? '')}
    >
      <FolderGit2 size={13} />
      <span>Copy worktree</span>
    </button>
  {/if}
</div>

<style>
  .ctx-menu {
    position: fixed;
    z-index: 1000;
    width: 212px;
    padding: 5px;
    border-radius: var(--radius-md);
    display: flex;
    flex-direction: column;
    gap: 2px;
    outline: none;
  }

  .ctx-item {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    min-height: 29px;
    padding: 4px 10px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text);
    font-size: 12.5px;
    text-align: left;
    cursor: pointer;
  }

  .ctx-item:hover,
  .ctx-item:focus-visible {
    background: var(--color-selected);
    outline: none;
  }

  .ctx-sep {
    height: 1px;
    margin: 4px 6px;
    background: var(--color-border);
  }
</style>
