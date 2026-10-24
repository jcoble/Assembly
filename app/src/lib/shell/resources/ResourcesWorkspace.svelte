<script lang="ts">
  import { onDestroy } from 'svelte';
  import { refreshResources, releaseResourceRefresh, resourceState } from './resourceStore.svelte.ts';
  import { countInactiveResourceWorkspaces, formatBytes, groupResourceProcesses, resourceCanStop, resourceOwnerLabel } from './resourceViewModel.ts';
  import { resourceService } from './resourceService.ts';
  import type { ResourceDiskRoot } from './resourceTypes.ts';

  interface Props { spaceRoots?: ResourceDiskRoot[]; }
  let { spaceRoots = [] }: Props = $props();

  let stoppingPid = $state<number | null>(null);
  let groups = $derived(resourceState.snapshot ? groupResourceProcesses(resourceState.snapshot.processes) : []);
  let inactiveWorkspaceCount = $derived(countInactiveResourceWorkspaces(resourceState.snapshot?.processes ?? [], spaceRoots.map((root) => root.workspaceId)));

  onDestroy(releaseResourceRefresh);

  async function stop(pid: number): Promise<void> {
    const process = resourceState.snapshot?.processes.find((item) => item.pid === pid);
    if (!process || !resourceCanStop(process) || !window.confirm(`Stop ${resourceOwnerLabel(process)}?`)) return;
    stoppingPid = pid;
    try {
      await resourceService.stop({ pid, expectedPgid: process.pgid, expectedGeneration: process.registryGeneration, ownerId: process.ownerId, expectedRoot: process.root });
      await refreshResources();
    } finally {
      stoppingPid = null;
    }
  }
</script>

<section class="workspace" data-testid="resources-workspace" aria-label="Resource Manager">
  <header class="workspace-header"><div><p class="eyebrow">Owned process inventory</p><h2>Resource Manager</h2><p class="muted">Only terminal and agent trees rooted by this app are shown.</p></div><button type="button" onclick={() => void refreshResources()} disabled={resourceState.loading}>{resourceState.loading ? 'Refreshing…' : 'Refresh'}</button></header>
  {#if resourceState.snapshot}
    <div class="summary"><div><strong>{resourceState.snapshot.processes.length}</strong><span>processes</span></div><div><strong>{resourceState.snapshot.totalCpuPercent.toFixed(1)}%</strong><span>CPU</span></div><div><strong>{formatBytes(resourceState.snapshot.totalRssBytes)}</strong><span>Memory</span></div></div>
    {#if groups.length === 0}<p class="empty">No owned sessions are active.</p>{/if}
    <div class="table" role="table">
      <div class="row heading" role="row"><span>Project / workspace / session</span><span>CPU</span><span>Memory</span><span>Action</span></div>
      {#each groups as project (project.id)}
        <div class="row group project" role="row"><strong>{project.label}</strong><span>{project.totalCpuPercent.toFixed(1)}%</span><span>{formatBytes(project.totalRssBytes)}</span><span>{project.workspaces.length} workspaces</span></div>
        {#each project.workspaces as workspace (workspace.id)}
          <div class="row group workspace-row" role="row"><span>↳ {workspace.label}</span><span>{workspace.totalCpuPercent.toFixed(1)}%</span><span>{formatBytes(workspace.totalRssBytes)}</span><span>{workspace.sessions.length} sessions</span></div>
          {#each workspace.sessions as session (session.id)}
            <div class="row group session-row" role="row"><span>↳ {session.label}</span><span>{session.totalCpuPercent.toFixed(1)}%</span><span>{formatBytes(session.totalRssBytes)}</span><span>{session.processes.length} processes</span></div>
            {#each session.processes as process (process.pid)}
              <div class="row process" role="row"><span><strong>{process.command || 'Unnamed process'}</strong><small>PID {process.pid} · {process.user} · {process.elapsedSeconds}s</small></span><span>{process.cpuPercent.toFixed(1)}%</span><span>{formatBytes(process.rssBytes)}</span><span><button type="button" disabled={!resourceCanStop(process) || stoppingPid === process.pid} onclick={() => void stop(process.pid)}>{stoppingPid === process.pid ? 'Stopping…' : 'Stop'}</button></span></div>
            {/each}
          {/each}
        {/each}
      {/each}
    </div>
    <button type="button" class="inactive-row" onclick={() => void refreshResources()}><span>Review inactive workspaces ({inactiveWorkspaceCount})</span><span aria-hidden="true">›</span></button>
  {:else}<p class="empty">{resourceState.error ?? 'Refresh to inspect resources.'}</p>{/if}
</section>

<style>
  .workspace { display: grid; gap: 1rem; padding: 1rem; color: var(--color-text); background: var(--color-surface); } .workspace-header { display: flex; align-items: start; justify-content: space-between; gap: 1rem; } h2, p { margin: 0; } h2 { font-size: 1.35rem; } .eyebrow { margin-bottom: 0.2rem; text-transform: uppercase; letter-spacing: 0.09em; color: var(--color-text-2); font-size: 0.75rem; } .muted, small, .empty { color: var(--color-text-2); }
  button { border: 0; background: var(--color-elevated); color: var(--color-text); border-radius: 0.45rem; padding: 0.45rem 0.7rem; font: inherit; cursor: pointer; } button:hover:not(:disabled) { background: var(--color-hover); } button:focus-visible { outline: none; box-shadow: var(--focus-ring); } button:disabled { cursor: not-allowed; opacity: 0.55; }
  .summary { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); border: 1px solid var(--color-border); border-radius: 0.55rem; overflow: hidden; } .summary div { display: grid; gap: 0.15rem; padding: 0.8rem; border-right: 1px solid var(--color-border); } .summary div:last-child { border-right: 0; } .summary strong { font-size: 1.2rem; } .summary span { color: var(--color-text-2); font-size: 0.78rem; }
  .table { overflow: auto; border: 1px solid var(--color-border); border-radius: 0.55rem; } .row { display: grid; grid-template-columns: minmax(16rem, 1fr) 5rem 6rem 8rem; gap: 0.8rem; align-items: center; min-width: 39rem; padding: 0.65rem 0.8rem; border-top: 1px solid var(--color-border); } .row:first-child { border-top: 0; } .heading { color: var(--color-text-2); font-size: 0.75rem; } .row > span:not(:first-child) { text-align: right; color: var(--color-text-2); font-variant-numeric: tabular-nums; } .group { background: var(--color-elevated); } .group strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .workspace-row { padding-left: 1.45rem; } .session-row { padding-left: 2.1rem; } .process { padding-left: 3rem; } .process span:first-child { display: grid; gap: 0.15rem; min-width: 0; } .process strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .process button { padding: 0.28rem 0.5rem; font-size: 0.75rem; background: transparent; color: var(--color-text-2); } .process button:hover:not(:disabled) { background: var(--color-hover); color: var(--color-text); } .inactive-row { display: flex; align-items: center; justify-content: space-between; width: 100%; padding: 0.7rem 0.8rem; border: 0; border-radius: 0.55rem; background: var(--color-surface); color: var(--color-text); text-align: left; cursor: pointer; } .inactive-row:hover:not(:disabled) { background: var(--color-hover); } .inactive-row span:last-child { color: var(--color-text-2); font-size: 1.1rem; }
</style>
