<!--
  HomeButton.svelte — the round green button in the top-left window chrome.

  Clicking it opens a menu for app-level items (Settings). Hovering it, or
  moving keyboard focus into it, slides joined circles out to its right:
  Projects, Messages, and Update while an app update is waiting. The waiting update also
  puts a dot on the green button, and while the circles are out a still arc
  marks the Update circle. Only the slide moves, once per hover or focus.
-->
<script lang="ts">
  import Download from '@lucide/svelte/icons/download';
  import FolderKanban from '@lucide/svelte/icons/folder-kanban';
  import House from '@lucide/svelte/icons/house';
  import MessagesSquare from '@lucide/svelte/icons/messages-square';
  import Settings from '@lucide/svelte/icons/settings';

  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { appUpdateState } from '$lib/shell/appUpdateService.svelte';

  interface Props {
    onOpenProjects: () => void;
    onOpenMessages: () => void;
    onOpenSettings: () => void;
    onOpenUpdate: () => void;
  }

  let { onOpenProjects, onOpenMessages, onOpenSettings, onOpenUpdate }: Props = $props();

  let hovered = $state(false);
  /** Focus arrived by keyboard; a click's focus does not keep the circles out. */
  let keyboardFocus = $state(false);
  const updateWaiting = $derived(appUpdateState.phase === 'available');
  const out = $derived(hovered || keyboardFocus);
</script>

<div
  class="home-cluster"
  class:out
  data-tauri-drag-region
  role="group"
  aria-label="Home"
  onpointerenter={() => (hovered = true)}
  onpointerleave={() => (hovered = false)}
  onfocusin={(event) => (keyboardFocus = (event.target as Element).matches(':focus-visible'))}
  onfocusout={(event) => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) keyboardFocus = false;
  }}
>
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <button {...props} type="button" class="home-button" aria-label="Home" data-testid="home-button">
          <House aria-hidden="true" />
          {#if updateWaiting}<span class="home-dot" aria-hidden="true"></span>{/if}
        </button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content align="start" sideOffset={6} class="min-w-44">
      <DropdownMenu.Item onSelect={onOpenSettings}><Settings />Settings</DropdownMenu.Item>
    </DropdownMenu.Content>
  </DropdownMenu.Root>
  <div class="home-slide" aria-hidden={!out}>
    <IconButton label="Projects" side="bottom" class="home-circle" data-testid="home-projects" onclick={onOpenProjects}>
      <FolderKanban />
    </IconButton>
    <IconButton label="Messages" side="bottom" class="home-circle" data-testid="home-messages" onclick={onOpenMessages}>
      <MessagesSquare />
    </IconButton>
    {#if updateWaiting}
      <span class="home-update">
        <IconButton
          label={`Update to Assembly ${appUpdateState.version}`}
          side="bottom"
          class="home-circle"
          data-testid="home-update"
          onclick={onOpenUpdate}
        >
          <Download />
        </IconButton>
        <!-- One still arc. The glow is two wider, fainter strokes under the
             bright one, not a blur filter. -->
        <svg class="home-arc" viewBox="0 0 32 32" aria-hidden="true">
          <circle cx="16" cy="16" r="14" pathLength="3" stroke-width="5" opacity="0.15" />
          <circle cx="16" cy="16" r="14" pathLength="3" stroke-width="3" opacity="0.35" />
          <circle cx="16" cy="16" r="14" pathLength="3" stroke-width="1.5" />
        </svg>
      </span>
    {/if}
  </div>
</div>

<style>
  .home-cluster {
    display: flex;
    align-items: center;
    height: 100%;
    /* Clear of the macOS window buttons. */
    padding-left: 80px;
  }

  .home-button {
    position: relative;
    z-index: 1;
    display: grid;
    place-items: center;
    width: 26px;
    height: 26px;
    border-radius: 50%;
    background: var(--color-accent);
    color: var(--color-on-accent);
    transition: filter 150ms ease;
  }

  .home-button:hover,
  .home-button[data-state='open'] {
    filter: brightness(1.12);
  }

  .home-button:focus-visible {
    outline: 2px solid var(--focus-border);
    outline-offset: 2px;
  }

  .home-button :global(svg) {
    width: 15px;
    height: 15px;
  }

  .home-dot {
    position: absolute;
    top: -1px;
    right: -1px;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--color-attention);
    box-shadow: 0 0 0 2px var(--color-bg);
  }

  /* The circles sit on one pill tucked under the green button, so they read
     as joined to it. Hidden, they slide back under it. */
  .home-slide {
    display: flex;
    align-items: center;
    gap: 2px;
    margin-left: -13px;
    padding: 1px 3px 1px 15px;
    border-radius: 999px;
    background: var(--secondary);
    opacity: 0;
    transform: translateX(-12px);
    pointer-events: none;
    transition:
      opacity 160ms ease,
      transform 200ms ease;
  }

  .out .home-slide {
    opacity: 1;
    transform: none;
    pointer-events: auto;
  }

  .home-slide :global(.home-circle) {
    width: 26px;
    height: 26px;
    min-width: 26px;
    min-height: 26px;
    padding: 0;
    border-radius: 50%;
  }

  .home-slide :global(.home-circle svg) {
    width: 15px;
    height: 15px;
  }

  .home-update {
    position: relative;
    display: grid;
  }

  .home-arc {
    position: absolute;
    inset: -3px;
    width: calc(100% + 6px);
    height: calc(100% + 6px);
    fill: none;
    stroke: var(--color-accent);
    stroke-linecap: round;
    /* A third of the circle, out of a path length of 3. */
    stroke-dasharray: 1 2;
    opacity: 0;
    pointer-events: none;
  }

  .out .home-arc {
    opacity: 1;
  }

  @media (prefers-reduced-motion: reduce) {
    .home-slide {
      transition: none;
    }
  }
</style>
