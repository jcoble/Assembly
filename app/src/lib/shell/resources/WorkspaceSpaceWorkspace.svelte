<script lang="ts">
  import { onDestroy } from 'svelte';
  import { formatClockTime } from '$lib/shell/dateFormat.ts';
  import { releaseWorkspaceSpaceScan, scanWorkspaceSpaceRoots, workspaceSpaceState } from './workspaceSpaceStore.svelte.ts';
  import { diskProtectionLabel, filterWorkspaceEntries, reclaimableBytes } from './workspaceSpaceViewModel.ts';
  import { formatBytes } from './resourceViewModel.ts';
  import { resourceService } from './resourceService.ts';
  import type { DiskProtection, ResourceDiskRoot, WorkspaceDiskEntry, WorkspaceDiskKind } from './resourceTypes.ts';

  interface Props { roots: ResourceDiskRoot[]; }
  let { roots }: Props = $props();
  let additionalRoots = $state<ResourceDiskRoot[]>([]);
  let configuredRoots = $derived([...roots, ...additionalRoots]);
  let pathInput = $state('');
  let repositoryInput = $state('');
  let workspaceInput = $state('');
  let kindInput = $state<WorkspaceDiskKind>('worktree');
  let protectionInput = $state<DiskProtection>('safe-candidate');
  let selectedIds = $state<string[]>([]);
  let workspaceFilter = $state('');
  let deleting = $state(false);
  let lastReceipt = $state<string | null>(null);

  onDestroy(releaseWorkspaceSpaceScan);

  let report = $derived(workspaceSpaceState.report);
  let selectableEntries = $derived(report?.entries.filter((entry) => entry.protection === 'safe-candidate' && reclaimableBytes(entry) > 0) ?? []);
  let selectedEntries = $derived(selectableEntries.filter((entry) => selectedIds.includes(entry.id)));
  let topEntries = $derived((report?.entries ?? []).slice().sort((left, right) => right.bytes - left.bytes).slice(0, 8));
  let filteredEntries = $derived(filterWorkspaceEntries(report?.entries ?? [], workspaceFilter));

  function addExplicitRoot(): void {
    const path = pathInput.trim();
    if (!path) return;
    additionalRoots = [...additionalRoots, { repositoryId: repositoryInput.trim() || 'explicit-root', workspaceId: workspaceInput.trim() || path, path, kind: kindInput, protection: protectionInput }];
    pathInput = '';
  }

  function toggle(id: string): void {
    selectedIds = selectedIds.includes(id) ? selectedIds.filter((value) => value !== id) : [...selectedIds, id];
  }

  function selectAll(): void {
    selectedIds = selectableEntries.map((entry) => entry.id);
  }

  async function deleteSelected(): Promise<void> {
    if (selectedEntries.length === 0 || deleting || !window.confirm(`Delete ${selectedEntries.length} reclaimable workspace item${selectedEntries.length === 1 ? '' : 's'}?`)) return;
    deleting = true;
    lastReceipt = null;
    let reclaimed = 0;
    try {
      for (const entry of selectedEntries) {
        const receipt = await resourceService.cleanup({ repositoryId: entry.repositoryId, workspaceId: entry.workspaceId, path: entry.path, kind: entry.kind, protection: entry.protection, expectedId: entry.id, expectedBytes: entry.bytes, maxDepth: 3, maxEntries: 2000 });
        reclaimed += receipt?.reclaimedBytes ?? 0;
      }
      selectedIds = [];
      lastReceipt = `${formatBytes(reclaimed)} reclaimed from selected safe candidates.`;
      if (configuredRoots.length > 0) await scanWorkspaceSpaceRoots(configuredRoots);
    } finally {
      deleting = false;
    }
  }
</script>

<section class="workspace" data-testid="workspace-space-workspace" aria-label="Space">
  <header class="workspace-header"><div><p class="eyebrow">Space <span>Beta</span></p><h2>Workspace disk usage</h2><p class="muted">Explicit roots only. Protected worktrees remain visible and cannot be removed.</p></div><button type="button" onclick={() => void scanWorkspaceSpaceRoots(configuredRoots)} disabled={workspaceSpaceState.scanning || configuredRoots.length === 0}>{workspaceSpaceState.scanning ? 'Scanning…' : 'Scan space'}</button></header>
  <div class="summary">
    <div><span>Scanned</span><strong>{report ? formatBytes(report.scannedBytes) : '—'}</strong></div>
    <div><span>Reclaimable</span><strong>{report ? formatBytes(report.reclaimableBytes) : '—'}</strong></div>
    <div><span>Workspaces</span><strong>{report?.entries.length ?? '—'}</strong></div>
    <div><span>Updated</span><strong>{report ? formatClockTime(new Date(report.capturedAtMs)) : 'not scanned'}</strong></div>
  </div>
  {#if report}
    <div class="space-layout">
      <div class="workspace-map" aria-label="Workspace size map">
        {#each topEntries as entry (entry.id)}<div class="map-item" style={`--size:${Math.max(7, Math.round((entry.bytes / Math.max(report.scannedBytes, 1)) * 100))}%`}><strong>{entry.workspaceId}</strong><span>{formatBytes(entry.bytes)}</span></div>{/each}
      </div>
      <aside class="largest"><h3>Largest workspace items</h3>{#each topEntries.slice(0, 6) as entry (entry.id)}<div class="bar-row"><div><strong>{entry.path}</strong><span>{formatBytes(entry.bytes)}</span></div><div class="bar"><i style={`width:${Math.min(100, Math.round((entry.bytes / Math.max(topEntries[0]?.bytes ?? 1, 1)) * 100))}%`}></i></div></div>{/each}</aside>
    </div>
    <div class="selection-bar"><span><strong>{selectedEntries.length} selected</strong> · {formatBytes(selectedEntries.reduce((sum, entry) => sum + reclaimableBytes(entry), 0))} reclaimable</span><button type="button" onclick={selectAll} disabled={selectableEntries.length === 0}>Select all safe</button><button type="button" onclick={() => (selectedIds = [])} disabled={selectedIds.length === 0}>Clear</button><button type="button" class="danger" onclick={() => void deleteSelected()} disabled={selectedEntries.length === 0 || deleting}>{deleting ? 'Deleting…' : 'Delete selected'}</button></div>
    <div class="entry-tools"><label class="entry-filter"><span class="sr-only">Filter workspaces</span><input type="search" bind:value={workspaceFilter} placeholder="Filter workspaces" aria-label="Filter workspaces" /></label><span class="muted">{filteredEntries.length} of {report.entries.length} workspaces</span></div>
    <div class="entry-list"><div class="entry-heading"><span>Workspace</span><span>Repository</span><span>Size</span><span>State</span></div>{#each filteredEntries as entry (entry.id)}<label class:protected={entry.protection !== 'safe-candidate'} class="entry"><input type="checkbox" checked={selectedIds.includes(entry.id)} disabled={entry.protection !== 'safe-candidate' || reclaimableBytes(entry) === 0} onchange={() => toggle(entry.id)} /><span><strong>{entry.workspaceId}</strong><small>{entry.path}</small></span><span>{entry.repositoryId}</span><span><strong>{formatBytes(entry.bytes)}</strong><small>{formatBytes(reclaimableBytes(entry))} reclaimable</small></span><span>{diskProtectionLabel(entry.protection)}</span></label>{/each}</div>
    {#if lastReceipt}<p class="receipt">{lastReceipt}</p>{/if}
  {:else}<p class="empty">{workspaceSpaceState.error ?? 'Start a visible scan to review explicit workspace roots.'}</p>{/if}
  <details class="roots"><summary>Explicit scan roots ({configuredRoots.length})</summary><form onsubmit={(event) => { event.preventDefault(); addExplicitRoot(); }}><label>Path<input bind:value={pathInput} placeholder="/path/to/worktree-or-cache" /></label><label>Repository<input bind:value={repositoryInput} placeholder="repository" /></label><label>Workspace<input bind:value={workspaceInput} placeholder="workspace" /></label><label>Kind<select bind:value={kindInput}><option value="worktree">Worktree</option><option value="build-output">Build output</option><option value="dependency-cache">Dependency cache</option><option value="agent-data">Agent data</option><option value="other">Other</option></select></label><label>Protection<select bind:value={protectionInput}><option value="active">Active</option><option value="dirty">Dirty</option><option value="unmerged">Unmerged</option><option value="locked">Locked</option><option value="user-data">User data</option><option value="safe-candidate">Safe candidate</option><option value="unknown">Unknown</option></select></label><button type="submit" disabled={!pathInput.trim()}>Add explicit root</button></form>{#if configuredRoots.length}<ul>{#each configuredRoots as root (root.path)}<li>{root.path} · {root.kind} · {root.protection}</li>{/each}</ul>{/if}</details>
</section>

<style>
  .workspace { display: grid; gap: 1rem; padding: 1rem; color: var(--color-text); background: var(--color-surface); } .workspace-header { display: flex; justify-content: space-between; gap: 1rem; align-items: start; } h2, h3, p { margin: 0; } h2 { font-size: 1.35rem; } h3 { font-size: 0.9rem; } .eyebrow { margin-bottom: 0.2rem; color: var(--color-text-2); text-transform: uppercase; letter-spacing: 0.09em; font-size: 0.75rem; } .eyebrow span { color: var(--color-accent); } .muted, small, .empty { color: var(--color-text-2); }
  button { border: 0; background: var(--color-elevated); color: var(--color-text); border-radius: 0.45rem; padding: 0.45rem 0.65rem; font: inherit; cursor: pointer; } button:hover:not(:disabled) { background: var(--color-hover); } button:focus-visible { outline: none; box-shadow: var(--focus-ring); } button:disabled { cursor: not-allowed; opacity: 0.55; } .danger { background: var(--color-bad-bg); color: var(--color-bad); } .danger:hover:not(:disabled) { background: var(--color-bad-bg-strong); }
  input, select { border: 1px solid var(--color-border); background: var(--color-elevated); color: var(--color-text); border-radius: 0.45rem; padding: 0.45rem 0.65rem; font: inherit; }
  .summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); border: 1px solid var(--color-border); border-radius: 0.55rem; overflow: hidden; } .summary div { display: grid; gap: 0.15rem; padding: 0.8rem; border-right: 1px solid var(--color-border); } .summary div:last-child { border-right: 0; } .summary span { color: var(--color-text-2); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.06em; } .summary strong { font-size: 1.2rem; }
  .space-layout { display: grid; grid-template-columns: minmax(0, 1.5fr) minmax(16rem, 1fr); gap: 1rem; } .workspace-map { display: grid; grid-template-columns: repeat(4, 1fr); grid-auto-rows: minmax(4.5rem, auto); gap: 0.2rem; min-height: 13rem; } .map-item { grid-column: span 2; display: grid; align-content: center; gap: 0.2rem; padding: 0.75rem; min-width: 0; border: 1px solid var(--color-border); background: color-mix(in srgb, var(--color-selected) calc(var(--size) * 0.7), var(--color-elevated)); } .map-item:nth-child(3n) { grid-column: span 1; } .map-item strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .map-item span { color: var(--color-text-2); font-size: 0.78rem; }
  .largest { display: grid; gap: 0.75rem; padding: 0.9rem; border: 1px solid var(--color-border); border-radius: 0.55rem; } .bar-row { display: grid; gap: 0.3rem; min-width: 0; } .bar-row > div:first-child { display: flex; justify-content: space-between; gap: 0.5rem; color: var(--color-text-2); font-size: 0.76rem; } .bar-row strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .bar { height: 0.35rem; overflow: hidden; border-radius: 9rem; background: var(--color-border); } .bar i { display: block; height: 100%; background: var(--color-accent); }
  .selection-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; padding: 0.7rem; border: 1px solid var(--color-border); border-radius: 0.55rem; } .selection-bar > span { flex: 1 1 auto; color: var(--color-text-2); font-size: 0.8rem; } .entry-tools { display: flex; align-items: center; gap: 0.7rem; } .entry-filter { flex: 1 1 auto; } .entry-filter input { width: 100%; } .entry-tools > span { flex: 0 0 auto; font-size: 0.78rem; }
  .entry-list { overflow: auto; border: 1px solid var(--color-border); border-radius: 0.55rem; } .entry-heading, .entry { display: grid; grid-template-columns: 1.4rem minmax(16rem, 2fr) minmax(8rem, 1fr) 7rem 9rem; gap: 0.7rem; align-items: center; min-width: 46rem; padding: 0.65rem 0.75rem; } .entry-heading { color: var(--color-text-2); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.06em; } .entry { border-top: 1px solid var(--color-border); cursor: pointer; } .entry:hover { background: var(--color-hover); } .entry.protected { cursor: default; opacity: 0.7; } .entry > span { display: grid; gap: 0.15rem; min-width: 0; color: var(--color-text-2); font-size: 0.78rem; } .entry strong { color: var(--color-text); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .sr-only { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0; }
  .receipt { color: var(--color-good); } .roots { border-top: 1px solid var(--color-border); padding-top: 0.7rem; color: var(--color-text-2); } .roots summary { cursor: pointer; } .roots form { display: grid; grid-template-columns: 2fr 1fr 1fr 1fr 1fr auto; gap: 0.5rem; align-items: end; padding-top: 0.8rem; } .roots label { display: grid; gap: 0.25rem; font-size: 0.75rem; } .roots input, .roots select { min-width: 0; } .roots ul { margin: 0.75rem 0 0; padding-left: 1.1rem; font-size: 0.75rem; }
  @media (max-width: 48rem) { .summary { grid-template-columns: repeat(2, 1fr); } .summary div:nth-child(2) { border-right: 0; } .space-layout { grid-template-columns: 1fr; } .roots form { grid-template-columns: 1fr 1fr; } }
</style>
