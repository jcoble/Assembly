<script lang="ts">
  import {
    FileCode2,
    FolderOpen,
    GitBranch,
    GitCompare,
    GitPullRequest,
    Globe,
    Home,
    Maximize2,
    MessageSquare,
    MessagesSquare,
    Minimize2,
    PanelRight,
    Plus,
    Settings,
    X
  } from '@lucide/svelte';
  import { engineStore } from '$lib/application/engine.svelte';
  import { sessionsStore } from '$lib/application/sessions.svelte';
  import { workspaceStore, type SecondaryTabKind } from '$lib/application/workspace.svelte';

  let addMenuOpen = $state(false);
  let homeHovered = $state(false);

  const addMenuItems: { kind: SecondaryTabKind; label: string; tabTitle: string; icon: typeof Globe }[] = [
    { kind: 'browser', label: 'New browser tab', tabTitle: 'New browser tab', icon: Globe },
    { kind: 'changes', label: 'Changes', tabTitle: 'Changes', icon: GitCompare },
    { kind: 'source_history', label: 'Source control history', tabTitle: 'History', icon: GitBranch },
    { kind: 'prs', label: 'Pull requests', tabTitle: 'Pull requests', icon: GitPullRequest },
    { kind: 'editor', label: 'Open file...', tabTitle: 'Editor', icon: FileCode2 }
  ];

  function tabIcon(kind: SecondaryTabKind) {
    switch (kind) {
      case 'browser':
        return Globe;
      case 'changes':
        return GitCompare;
      case 'source_history':
        return GitBranch;
      case 'prs':
        return GitPullRequest;
      case 'editor':
        return FileCode2;
    }
  }
  const leftSpacerWidth = $derived(
    workspaceStore.railOpen ? Math.max(12, workspaceStore.railWidth - 116) : 12
  );
</script>

<header class="topbar">
  <!-- Left: macOS traffic-light clearance (88px) + green circular Home button with hover-expand pill -->
  <div class="topbar-left">
    <div
      class="home-pill"
      class:expanded={homeHovered}
      role="group"
      aria-label="Home navigation"
      onmouseenter={() => (homeHovered = true)}
      onmouseleave={() => (homeHovered = false)}
    >
      <button
        type="button"
        class="home-circle"
        title="Home / Settings"
        onclick={() =>
          workspaceStore.openHomeOverlay(
            workspaceStore.homeOverlay === 'settings' ? 'none' : 'settings'
          )}
      >
        <Home size={14} />
      </button>

      {#if homeHovered}
        <button
          type="button"
          class="home-extra"
          title="Projects"
          onclick={() => workspaceStore.openHomeOverlay('projects')}
        >
          <FolderOpen size={14} />
        </button>
        <button
          type="button"
          class="home-extra"
          title="Build Board & Messages"
          onclick={() => workspaceStore.openHomeOverlay('board')}
        >
          <MessagesSquare size={14} />
        </button>
        <button
          type="button"
          class="home-extra"
          title="Settings"
          onclick={() => workspaceStore.openHomeOverlay('settings')}
        >
          <Settings size={14} />
        </button>
      {/if}
    </div>
  </div>

  <!-- Draggable empty top-bar spacer aligned with left rail width -->
  <div
    class="drag-spacer drag-spacer-left"
    style:width={`${leftSpacerWidth}px`}
    data-window-drag-region
    aria-hidden="true"
  ></div>

  <!-- Center-left: Active Chat session tab ONLY (matches old app 1:1) -->
  <div class="chat-tab-group">
    <button
      type="button"
      class="session-tab"
      class:active={workspaceStore.homeOverlay === 'none'}
      onclick={() => workspaceStore.openHomeOverlay('none')}
    >
      <MessageSquare size={13} />
      <span class="session-tab-label">
        {sessionsStore.selected?.title ?? sessionsStore.selectedId ?? 'Assembly Chat'}
      </span>
    </button>
  </div>

  <!-- Center-right: Dynamic secondary chrome tabs (empty until user clicks `+` or opens a view) -->
  <div class="secondary-strip">
    {#each workspaceStore.secondaryTabs as tab (tab.id)}
      {@const Icon = tabIcon(tab.kind)}
      <div
        class="chrome-tab"
        class:active={workspaceStore.activeSecondaryTabId === tab.id &&
          workspaceStore.homeOverlay === 'none'}
      >
        <button
          type="button"
          class="chrome-tab-main"
          onclick={() => workspaceStore.selectSecondaryTab(tab.id)}
        >
          <Icon size={13} />
          <span>{tab.title}</span>
        </button>
        <button
          type="button"
          class="chrome-tab-close"
          title="Close tab"
          onclick={() => workspaceStore.closeSecondaryTab(tab.id)}
        >
          <X size={11} />
        </button>
      </div>
    {/each}

    {#if workspaceStore.hasSecondaryPane}
      <div class="menu-anchor">
        <button
          type="button"
          class="chrome-icon-btn"
          title="Open tab"
          onclick={() => (addMenuOpen = !addMenuOpen)}
        >
          <Plus size={14} />
        </button>
        {#if addMenuOpen}
          <div class="dropdown glass-popover" role="menu" data-occludes-chat>
            {#each addMenuItems as item (item.kind)}
              {@const Icon = item.icon}
              <button
                type="button"
                class="menu-item"
                onclick={() => {
                  addMenuOpen = false;
                  workspaceStore.openSecondaryTab(item.kind, item.tabTitle);
                }}
              >
                <Icon size={14} />
                <span>{item.label}</span>
              </button>
            {/each}
          </div>
        {/if}
      </div>
    {/if}

    <!-- Native macOS window drag spacer occupying remaining empty titlebar width -->
    <div class="drag-spacer drag-spacer-flex" data-window-drag-region aria-hidden="true"></div>
  </div>

  <!-- Right controls: Needs badge (when active), `+` (when no split tabs open), expand, right-panel toggle -->
  <div class="topbar-right">
    {#if engineStore.needCount > 0}
      <button
        type="button"
        class="needs-pill"
        onclick={() => workspaceStore.openHomeOverlay('board')}
      >
        {engineStore.needCount} need you
      </button>
    {/if}

    {#if !workspaceStore.hasSecondaryPane}
      <div class="menu-anchor">
        <button
          type="button"
          class="chrome-icon-btn"
          title="Open tab"
          onclick={() => (addMenuOpen = !addMenuOpen)}
        >
          <Plus size={14} />
        </button>
        {#if addMenuOpen}
          <div class="dropdown dropdown-right glass-popover" role="menu" data-occludes-chat>
            {#each addMenuItems as item (item.kind)}
              {@const Icon = item.icon}
              <button
                type="button"
                class="menu-item"
                onclick={() => {
                  addMenuOpen = false;
                  workspaceStore.openSecondaryTab(item.kind, item.tabTitle);
                }}
              >
                <Icon size={14} />
                <span>{item.label}</span>
              </button>
            {/each}
          </div>
        {/if}
      </div>
    {:else}
      <button
        type="button"
        class="chrome-icon-btn"
        title={workspaceStore.secondaryExpanded ? 'Restore split view' : 'Expand secondary pane'}
        onclick={() => workspaceStore.toggleSecondaryExpanded()}
      >
        {#if workspaceStore.secondaryExpanded}
          <Minimize2 size={14} />
        {:else}
          <Maximize2 size={14} />
        {/if}
      </button>
    {/if}

    <button
      type="button"
      class="chrome-icon-btn"
      class:active={workspaceStore.rightPanelOpen}
      title="Toggle right panel"
      onclick={() => workspaceStore.toggleRightPanel()}
    >
      <PanelRight size={15} />
    </button>
  </div>
</header>

<style>
  .topbar {
    display: flex;
    align-items: center;
    height: var(--top-bar-height);
    padding: 0 10px 0 88px;
    background: var(--color-canvas);
    gap: 8px;
    flex-shrink: 0;
    user-select: none;
  }

  .drag-spacer {
    height: 100%;
    min-width: 12px;
  }

  .drag-spacer-left {
    flex-shrink: 0;
  }

  .drag-spacer-flex {
    flex: 1;
  }

  .topbar-left {
    display: flex;
    align-items: center;
    flex-shrink: 0;
  }

  .home-pill {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    height: 28px;
    padding: 0;
    border-radius: 999px;
    transition: background 140ms ease, padding 140ms ease;
  }

  .home-pill.expanded {
    background: var(--color-elevated);
    padding-right: 4px;
  }

  .home-circle {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    border-radius: 999px;
    border: none;
    background: var(--color-accent);
    color: var(--color-on-accent);
    cursor: pointer;
    flex-shrink: 0;
  }

  .home-extra {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    border: none;
    border-radius: 999px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
  }

  .home-extra:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .chat-tab-group {
    display: flex;
    align-items: center;
    flex-shrink: 0;
  }

  .session-tab {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    height: 28px;
    max-width: 240px;
    padding: 0 12px;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    background: var(--color-elevated);
    color: var(--color-text);
    font-size: 12.5px;
    font-weight: 500;
    cursor: pointer;
  }

  .session-tab-label {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .secondary-strip {
    display: flex;
    align-items: center;
    height: 100%;
    gap: 4px;
    flex: 1;
    min-width: 0;
  }

  .chrome-tab {
    display: inline-flex;
    align-items: center;
    height: 28px;
    border-radius: 8px;
    border: 1px solid transparent;
    background: transparent;
    color: var(--color-text-2);
    padding: 0 4px 0 8px;
    gap: 4px;
  }

  .chrome-tab.active {
    background: var(--color-elevated);
    border-color: var(--color-border);
    color: var(--color-text);
  }

  .chrome-tab-main {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    border: none;
    background: transparent;
    color: inherit;
    font-size: 12px;
    cursor: pointer;
    padding: 0;
  }

  .chrome-tab-close {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 18px;
    height: 18px;
    border: none;
    border-radius: 4px;
    background: transparent;
    color: var(--color-text-3);
    cursor: pointer;
  }

  .chrome-tab-close:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .topbar-right {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-shrink: 0;
  }

  .needs-pill {
    height: 24px;
    padding: 0 9px;
    border-radius: 999px;
    border: 1px solid rgba(245, 185, 91, 0.35);
    background: var(--color-attention-bg);
    color: var(--color-attention);
    font-size: 11.5px;
    font-weight: 500;
    cursor: pointer;
  }

  .chrome-icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    border: 1px solid transparent;
    border-radius: 999px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
  }

  .chrome-icon-btn:hover,
  .chrome-icon-btn.active {
    background: var(--color-elevated);
    color: var(--color-text);
  }

  .menu-anchor {
    position: relative;
  }

  .dropdown {
    position: absolute;
    top: calc(100% + 6px);
    left: 0;
    min-width: 205px;
    padding: 5px;
    border-radius: var(--radius-md);
    z-index: 60;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .dropdown-right {
    left: auto;
    right: 0;
  }

  .menu-item {
    display: flex;
    align-items: center;
    gap: 9px;
    width: 100%;
    padding: 7px 10px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text);
    font-size: 12.5px;
    cursor: pointer;
    text-align: left;
  }

  .menu-item:hover {
    background: var(--color-selected);
  }
</style>
