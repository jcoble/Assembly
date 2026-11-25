<script lang="ts">
  import type { Snippet } from 'svelte';
  import {
    Bot,
    Ellipsis,
    FileText,
    Gauge,
    GitBranch,
    GitFork,
    History,
    ListTodo,
    PlayCircle,
    Terminal,
    X
  } from '@lucide/svelte';
  import {
    RIGHT_CORE_TABS,
    RIGHT_OVERFLOW_TABS,
    workspaceStore,
    type RightCoreTabId,
    type RightOverflowTabId,
    type RightPanelTabId
  } from '$lib/application/workspace.svelte';

  interface Props {
    panel?: Snippet<[RightPanelTabId]>;
  }
  let { panel }: Props = $props();

  let overflowOpen = $state(false);

  const coreMeta: Record<RightCoreTabId, { label: string; icon: typeof FileText }> = {
    files: { label: 'Files', icon: FileText },
    git: { label: 'Git', icon: GitBranch },
    agents: { label: 'Agents', icon: Bot },
    tasks: { label: 'Tasks', icon: ListTodo }
  };

  const overflowMeta: Record<RightOverflowTabId, { label: string; icon: typeof GitFork }> = {
    worktrees: { label: 'Worktrees', icon: GitFork },
    terminal: { label: 'Terminal', icon: Terminal },
    run: { label: 'Run', icon: PlayCircle },
    context: { label: 'Context', icon: Gauge },
    history: { label: 'History', icon: History }
  };

  const isOverflowActive = $derived(
    (RIGHT_OVERFLOW_TABS as readonly string[]).includes(workspaceStore.activeRightTab)
  );
</script>

<aside class="right-panel card-panel">
  <!-- 4 hanging icon tabs (Files, Git, Agents, Tasks) + `...` overflow + `×` close (matches old app 1:1) -->
  <div class="right-header">
    <div class="core-tabs">
      {#each RIGHT_CORE_TABS as tabId (tabId)}
        {@const meta = coreMeta[tabId]}
        {@const Icon = meta.icon}
        <button
          type="button"
          class="tab-icon"
          class:active={workspaceStore.activeRightTab === tabId}
          title={meta.label}
          onclick={() => {
            overflowOpen = false;
            workspaceStore.selectRightTab(tabId);
          }}
        >
          <Icon size={14} />
        </button>
      {/each}
    </div>

    <div class="right-actions">
      <div class="menu-anchor">
        <button
          type="button"
          class="action-icon"
          class:active={overflowOpen || isOverflowActive}
          title="More panels"
          onclick={() => (overflowOpen = !overflowOpen)}
        >
          <Ellipsis size={15} />
        </button>

        {#if overflowOpen}
          <div class="dropdown glass-popover" role="menu" data-occludes-chat>
            {#each RIGHT_OVERFLOW_TABS as tabId (tabId)}
              {@const meta = overflowMeta[tabId]}
              {@const Icon = meta.icon}
              <button
                type="button"
                class="menu-item"
                class:selected={workspaceStore.activeRightTab === tabId}
                onclick={() => {
                  overflowOpen = false;
                  workspaceStore.selectRightTab(tabId);
                }}
              >
                <Icon size={14} />
                <span>{meta.label}</span>
              </button>
            {/each}
          </div>
        {/if}
      </div>

      <button
        type="button"
        class="action-icon"
        title="Close right panel"
        onclick={() => workspaceStore.closeRightPanel()}
      >
        <X size={14} />
      </button>
    </div>
  </div>

  {#if panel}
    <div class="right-body-slot">
      {@render panel(workspaceStore.activeRightTab)}
    </div>
  {:else}
    <div class="right-body">
      <div class="empty-hint">
        <strong>{workspaceStore.activeRightTab}</strong> — Wave 1 lane mounts here (1:1 port of old app).
      </div>
    </div>
  {/if}
</aside>

<style>
  .right-panel {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100%;
    background: var(--color-surface);
    border-radius: var(--radius-md);
    overflow: hidden;
    min-height: 0;
  }

  .right-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: 38px;
    padding: 0 8px;
    border-bottom: 1px solid var(--color-border);
    flex-shrink: 0;
  }

  .core-tabs {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    padding: 2px;
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.03);
  }

  .tab-icon,
  .action-icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    border: none;
    border-radius: 6px;
    background: transparent;
    color: var(--color-text-2);
    cursor: pointer;
  }

  .tab-icon:hover,
  .action-icon:hover {
    background: var(--color-hover);
    color: var(--color-text);
  }

  .tab-icon.active,
  .action-icon.active {
    background: var(--color-selected);
    color: var(--color-text);
  }

  .right-actions {
    display: flex;
    align-items: center;
    gap: 2px;
  }

  .menu-anchor {
    position: relative;
  }

  .dropdown {
    position: absolute;
    top: calc(100% + 6px);
    right: 0;
    min-width: 170px;
    padding: 5px;
    border-radius: var(--radius-md);
    z-index: 60;
    display: flex;
    flex-direction: column;
    gap: 2px;
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

  .menu-item:hover,
  .menu-item.selected {
    background: var(--color-selected);
  }

  .right-body-slot {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
  }

  .right-body {
    flex: 1;
    padding: 12px;
    overflow-y: auto;
  }

  .empty-hint {
    color: var(--color-text-3);
    font-size: 12px;
    text-transform: capitalize;
  }
</style>
