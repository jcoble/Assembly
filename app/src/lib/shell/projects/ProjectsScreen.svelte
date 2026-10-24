<!--
  ProjectsScreen.svelte — every project on the left, every machine on the right.

  Opened from the home button, over the whole frame; the shell under it stays
  as it was, so Back returns to it. Choosing a machine narrows the list to that
  machine and makes it the one New project and Add folder start on. Clicking a
  project selects it; its ▶ button opens a new session there. In a narrow
  window the two panes become tabs. Each project's ⋯ menu renames it, changes its folder on the same
  machine, pins it to the top, or removes it (its files always stay). Each
  machine's ⋯ menu connects or disconnects it, makes a new project or adds a folder on it, or opens Manage.
-->
<script lang="ts">
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import Cloud from '@lucide/svelte/icons/cloud';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import FolderPlus from '@lucide/svelte/icons/folder-plus';
  import Laptop from '@lucide/svelte/icons/laptop';
  import Pin from '@lucide/svelte/icons/pin';
  import Play from '@lucide/svelte/icons/play';
  import Plus from '@lucide/svelte/icons/plus';
  import X from '@lucide/svelte/icons/x';
  import { onMount } from 'svelte';

  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import * as Dialog from '$lib/components/ui/dialog/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import * as Tabs from '$lib/components/ui/tabs/index.js';
  import RemoteConnections from '$lib/shell/connections/RemoteConnections.svelte';
  import AddProjectDialog from '$lib/shell/projects/AddProjectDialog.svelte';
  import NewProjectDialog from '$lib/shell/projects/NewProjectDialog.svelte';
  import { hydrateProjects, projectRegistry, refreshRailProjects } from '$lib/shell/projects/projectRegistry.svelte.ts';
  import { ownedSessionFromBackend } from '$lib/shell/ownedSessions';
  import { hydrateOwned, rail, removeOwnedSession } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import {
    filterProjects,
    projectCountsByMachine,
    projectLastUsedLabel,
    projectMachineLabel,
    shortProjectPath,
    visibleProjects
  } from '$lib/shell/projects/projects.ts';
  import {
    connectRemoteAssemblyFromTauri,
    disconnectRemoteAssemblyFromTauri,
    readRemoteAssemblyEnvironmentFromTauri,
    removeProjectFromTauri,
    renameProjectFromTauri,
    setProjectPinnedFromTauri,
    setProjectRootFromTauri,
    type ProjectRecord,
    type RemoteAssemblyEnvironment
  } from '$lib/tauriSource.ts';

  interface Props {
    onBack: () => void;
    onOpenProject: (projectId: string) => void;
    /** The rail's own delete, so an open session that goes away is closed the same way. */
    removeSession: (ownedId: string) => Promise<void>;
  }

  let { onBack, onOpenProject, removeSession }: Props = $props();

  let environment = $state<RemoteAssemblyEnvironment>({ profiles: [], readyProfileIds: [] });
  let machine = $state<string | null>(null);
  let selectedProjectId = $state<string | null>(null);
  let query = $state('');
  let addProjectOpen = $state(false);
  let newProjectOpen = $state(false);
  let machinesOpen = $state(false);
  let width = $state(0);
  let machinesError = $state('');
  let searchField = $state<HTMLInputElement | null>(null);
  let actionError = $state('');
  let renaming = $state<ProjectRecord | null>(null);
  let newName = $state('');
  let moving = $state<ProjectRecord | null>(null);
  let removing = $state<ProjectRecord | null>(null);
  /** Last used is read against the time the screen opened; it is open briefly. */
  const now = new Date();
  /** Ends a read or a connect still running when the screen closes. */
  const stop = new AbortController();

  const projects = $derived(visibleProjects(projectRegistry.projects, environment.profiles.map((profile) => profile.id)));
  const rows = $derived(filterProjects(projects, machine, query));
  const counts = $derived(projectCountsByMachine(projects));
  const machines = $derived([
    { id: 'local', name: 'This Mac', connected: true },
    ...environment.profiles.map((profile) => ({
      id: profile.id,
      name: profile.name,
      connected: environment.readyProfileIds.includes(profile.id)
    }))
  ]);
  /** The window can't be narrower than 900px, so this catches the narrowest ones. */
  const narrow = $derived(width > 0 && width < 960);

  onMount(() => {
    searchField?.focus();
    void hydrateProjects();
    void readMachines(stop.signal);
    return () => stop.abort();
  });

  async function readMachines(signal: AbortSignal): Promise<void> {
    try {
      const next = await readRemoteAssemblyEnvironmentFromTauri();
      if (!signal.aborted) environment = next;
    } catch (error) {
      if (!signal.aborted) machinesError = error instanceof Error ? error.message : String(error);
    }
  }

  /** Connects or disconnects a saved machine; a connect brings its sessions into the rail. */
  async function toggleConnection(id: string, connected: boolean): Promise<void> {
    const profile = environment.profiles.find((saved) => saved.id === id);
    if (!profile) return;
    machinesError = '';
    try {
      if (connected) {
        environment = await disconnectRemoteAssemblyFromTauri(id);
        return;
      }
      const result = await connectRemoteAssemblyFromTauri(profile, stop.signal, () => {});
      if (stop.signal.aborted) return;
      const replaced = new Set([result.profile.id, result.replacedProfileId]);
      hydrateOwned([...rail.owned.filter((session) => !replaced.has(session.remoteProfileId ?? '')),
        ...result.sessions.map(ownedSessionFromBackend)]);
      await readMachines(stop.signal);
    } catch (error) {
      if (!stop.signal.aborted) machinesError = error instanceof Error ? error.message : String(error);
    }
  }

  /** Waits for one project change, then reads the list again; a refusal is shown on the screen. */
  async function applyProjectChange(change: Promise<void>): Promise<boolean> {
    actionError = '';
    try {
      await change;
    } catch (error) {
      actionError = error instanceof Error ? error.message : String(error);
      return false;
    }
    await hydrateProjects();
    return true;
  }

  async function rename(): Promise<void> {
    const project = renaming;
    if (!project || !await applyProjectChange(renameProjectFromTauri(project.id, newName))) return;
    renaming = null;
    await refreshRailProjects();
  }

  /** A refusal belongs to the rename it answered, so it goes when the dialog does. */
  function closeRename(): void {
    renaming = null;
    actionError = '';
  }

  /** The folder picker shows a refusal itself, so this one lets it through. */
  async function changeFolder(path: string): Promise<void> {
    if (!moving) return;
    await setProjectRootFromTauri(moving.id, path);
    await hydrateProjects();
  }

  async function remove(deleteSessions: boolean): Promise<void> {
    const project = removing;
    removing = null;
    if (!project) return;
    const sessionIds = rail.owned.filter((session) => session.projectId === project.id).map((session) => session.ownedId);
    const openId = rail.activeOwnedId;
    if (!await applyProjectChange(removeProjectFromTauri(project.id, deleteSessions))) return;
    if (!deleteSessions) return refreshRailProjects();
    for (const ownedId of sessionIds) if (ownedId !== openId) removeOwnedSession(ownedId);
    if (openId && sessionIds.includes(openId)) {
      try {
        await removeSession(openId);
      } catch (error) {
        actionError = error instanceof Error ? error.message : String(error);
      }
    }
  }
</script>

{#snippet projectsPane()}
  <section class="pane" aria-label="Projects">
    <div class="flex items-center gap-2">
      <h2 class="pane-title">Projects</h2>
      <Input class="ml-auto h-8 max-w-64" placeholder="Search projects…" aria-label="Search projects" bind:value={query} bind:ref={searchField} />
    </div>
    <div class="flex items-center gap-2">
      {#if machine}
        <Chip data-testid="projects-machine-filter">
          Showing: {projectMachineLabel(machine, environment.profiles)}
          <button type="button" class="chip-clear" aria-label="Show every machine" onclick={() => (machine = null)}><X /></button>
        </Chip>
      {/if}
      <Button class="ml-auto" size="sm" onclick={() => (newProjectOpen = true)}><Plus />New project</Button>
      <Button variant="ghost" size="sm" onclick={() => (addProjectOpen = true)}><FolderPlus />Add folder</Button>
    </div>
    {#if actionError || projectRegistry.error || machinesError}
      <p class="text-[13px] text-destructive">{actionError || projectRegistry.error || machinesError}</p>
    {/if}
    <div class="pane-list">
      {#if projects.length === 0}
        <EmptyState title="No projects yet." body="A project is a folder Assembly works in.">
          {#snippet actions()}
            <Button size="sm" onclick={() => (newProjectOpen = true)}>New project</Button>
            <Button variant="ghost" size="sm" onclick={() => (addProjectOpen = true)}>Add existing folder</Button>
          {/snippet}
        </EmptyState>
      {:else if rows.length === 0}
        <p class="px-2 py-4 text-[13px] text-muted-foreground">No projects match.</p>
      {:else}
        {#each rows as project (project.id)}
          <ListRow
            data-testid="projects-row"
            actionsLabel="Project actions"
            selected={selectedProjectId === project.id}
            onclick={() => (selectedProjectId = project.id)}
          >
            {#if project.pinnedAtMs !== null}<Pin class="size-3.5 shrink-0 text-muted-foreground" aria-label="Pinned" />{/if}
            <span class="truncate font-medium">{project.title}</span>
            <Chip>{projectMachineLabel(project.machine, environment.profiles)}</Chip>
            <span class="ml-auto truncate text-sm text-muted-foreground" title={project.rootPath}>{shortProjectPath(project.rootPath)}</span>
            <!-- The right margin keeps the ▶ and ⋯ buttons clear of the label. -->
            <span class="mr-16 w-8 shrink-0 text-right text-sm text-muted-foreground">{projectLastUsedLabel(project.lastUsedMs, now)}</span>
            {#snippet actions()}
              <Button variant="ghost" size="icon-sm" aria-label={`New session in ${project.title}`} onclick={() => onOpenProject(project.id)}><Play /></Button>
              <DropdownMenu.Root>
                <DropdownMenu.Trigger>
                  {#snippet child({ props })}
                    <Button {...props} variant="ghost" size="icon-sm" aria-label={`Actions for ${project.title}`}><Ellipsis /></Button>
                  {/snippet}
                </DropdownMenu.Trigger>
                <DropdownMenu.Content class="w-44" align="end">
                  <DropdownMenu.Item onSelect={() => { actionError = ''; newName = project.title; renaming = project; }}>Rename…</DropdownMenu.Item>
                  <DropdownMenu.Item onSelect={() => (moving = project)}>Change folder…</DropdownMenu.Item>
                  <DropdownMenu.Item onSelect={() => void applyProjectChange(setProjectPinnedFromTauri(project.id, project.pinnedAtMs === null))}>
                    {project.pinnedAtMs === null ? 'Pin' : 'Unpin'}
                  </DropdownMenu.Item>
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item variant="destructive" onSelect={() => (removing = project)}>Remove…</DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu.Root>
            {/snippet}
          </ListRow>
        {/each}
      {/if}
    </div>
  </section>
{/snippet}

{#snippet machinesPane()}
  <section class="pane" aria-label="Machines">
    <h2 class="pane-title">Machines</h2>
    <div class="pane-list">
      {#each machines as entry (entry.id)}
        <ListRow
          data-testid="projects-machine"
          actionsLabel="Machine actions"
          selected={machine === entry.id}
          onclick={() => (machine = machine === entry.id ? null : entry.id)}
        >
          <span class="dot" class:connected={entry.connected} aria-label={entry.connected ? 'Connected' : 'Disconnected'}></span>
          {#if entry.id === 'local'}<Laptop class="size-4 shrink-0" />{:else}<Cloud class="size-4 shrink-0" />{/if}
          <span class="truncate">{entry.name}</span>
          <Chip tone="count" class="ml-auto mr-8">{counts[entry.id] ?? 0}</Chip>
          {#snippet actions()}
            <DropdownMenu.Root>
              <DropdownMenu.Trigger>
                {#snippet child({ props })}
                  <Button {...props} variant="ghost" size="icon-sm" aria-label={`Actions for ${entry.name}`}><Ellipsis /></Button>
                {/snippet}
              </DropdownMenu.Trigger>
              <DropdownMenu.Content class="w-44" align="end">
                {#if entry.id !== 'local'}
                  <DropdownMenu.Item onSelect={() => void toggleConnection(entry.id, entry.connected)}>
                    {entry.connected ? 'Disconnect' : 'Connect'}
                  </DropdownMenu.Item>
                {/if}
                <DropdownMenu.Item onSelect={() => { machine = entry.id; newProjectOpen = true; }}>New project here…</DropdownMenu.Item>
                <DropdownMenu.Item onSelect={() => { machine = entry.id; addProjectOpen = true; }}>Add folder here…</DropdownMenu.Item>
                {#if entry.id !== 'local'}
                  <DropdownMenu.Item onSelect={() => (machinesOpen = true)}>Manage…</DropdownMenu.Item>
                {/if}
              </DropdownMenu.Content>
            </DropdownMenu.Root>
          {/snippet}
        </ListRow>
      {/each}
    </div>
    <Button variant="ghost" size="sm" class="self-start" onclick={() => (machinesOpen = true)}><Plus />Add machine</Button>
  </section>
{/snippet}

<!-- Escape anywhere inside closes the screen, like Back. Dialogs portal out
     of this element, so their Escape never reaches it. -->
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<div
  class="projects-screen"
  role="region"
  aria-label="Projects"
  bind:clientWidth={width}
  data-testid="projects-screen"
  onkeydown={(event) => {
    if (event.key === 'Escape') onBack();
  }}
>
  <header class="flex items-center gap-2">
    <Button variant="ghost" size="sm" onclick={onBack}><ArrowLeft />Back</Button>
  </header>
  {#if narrow}
    <Tabs.Root value="projects" class="min-h-0 flex-1">
      <Tabs.List>
        <Tabs.Trigger value="projects">Projects</Tabs.Trigger>
        <Tabs.Trigger value="machines">Machines</Tabs.Trigger>
      </Tabs.List>
      <Tabs.Content value="projects" class="min-h-0 data-[state=active]:flex">{@render projectsPane()}</Tabs.Content>
      <Tabs.Content value="machines" class="min-h-0 data-[state=active]:flex">{@render machinesPane()}</Tabs.Content>
    </Tabs.Root>
  {:else}
    <div class="panes">
      {@render projectsPane()}
      {@render machinesPane()}
    </div>
  {/if}
</div>

{#if addProjectOpen}
  <AddProjectDialog
    profiles={environment.profiles}
    {machine}
    onAdded={() => (addProjectOpen = false)}
    onAddRemote={() => (machinesOpen = true)}
    onManageRemotes={() => (machinesOpen = true)}
    onClose={() => (addProjectOpen = false)}
  />
{/if}

{#if newProjectOpen}
  <NewProjectDialog profiles={environment.profiles} {machine} onCreated={() => (newProjectOpen = false)} onClose={() => (newProjectOpen = false)} />
{/if}

{#if moving}
  <AddProjectDialog
    profiles={environment.profiles}
    machine={moving.machine}
    chooseFolder={changeFolder}
    onAdded={() => (moving = null)}
    onAddRemote={() => (machinesOpen = true)}
    onManageRemotes={() => (machinesOpen = true)}
    onClose={() => (moving = null)}
  />
{/if}

<Dialog.Root open={renaming !== null} onOpenChange={(next) => { if (!next) closeRename(); }}>
  <Dialog.Content class="sm:max-w-[420px]">
    <Dialog.Header>
      <Dialog.Title>Rename project</Dialog.Title>
      <Dialog.Description>Only the name changes. The folder stays where it is.</Dialog.Description>
    </Dialog.Header>
    <form class="flex flex-col gap-3" onsubmit={(event) => { event.preventDefault(); void rename(); }}>
      <Input aria-label="Project name" bind:value={newName} />
      {#if actionError}<p class="text-[13px] text-destructive">{actionError}</p>{/if}
      <Dialog.Footer>
        <Button type="button" variant="ghost" size="sm" onclick={closeRename}>Cancel</Button>
        <Button type="submit" size="sm">Rename</Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>

<AlertDialog.Root open={removing !== null} onOpenChange={(next) => { if (!next) removing = null; }}>
  <AlertDialog.Content class="data-[size=default]:sm:max-w-md">
    <AlertDialog.Header>
      <AlertDialog.Title>Remove {removing?.title}?</AlertDialog.Title>
      <AlertDialog.Description>
        It leaves the project list. Its files stay where they are. Choose what happens to its sessions.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm">Cancel</AlertDialog.Cancel>
      <AlertDialog.Action size="sm" variant="destructive" onclick={() => void remove(true)}>Delete its sessions too</AlertDialog.Action>
      <AlertDialog.Action size="sm" onclick={() => void remove(false)}>Keep its sessions</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<Dialog.Root bind:open={machinesOpen}>
  <Dialog.Content class="max-h-[85vh] overflow-y-auto p-0 sm:max-w-[640px]">
    <Dialog.Title class="sr-only">Machines</Dialog.Title>
    {#if machinesOpen}
      <RemoteConnections onChange={(next) => (environment = next)} onClose={() => (machinesOpen = false)} />
    {/if}
  </Dialog.Content>
</Dialog.Root>

<style>
  .projects-screen {
    position: absolute;
    inset: 0;
    z-index: 20;
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    padding: var(--space-3) var(--space-5) var(--space-5);
    background: var(--color-bg);
  }

  .panes {
    display: grid;
    flex: 1 1 auto;
    grid-template-columns: minmax(0, 1fr) minmax(260px, 320px);
    gap: var(--space-4);
    min-height: 0;
  }

  .pane {
    display: flex;
    flex: 1 1 auto;
    flex-direction: column;
    gap: var(--space-3);
    min-height: 0;
    padding: var(--space-4);
    border-radius: 16px;
    background: var(--color-surface);
  }

  .pane-title {
    margin: 0;
    font-size: var(--text-heading);
    font-weight: var(--text-heading-weight);
  }

  .pane-list {
    display: flex;
    flex: 1 1 auto;
    flex-direction: column;
    gap: 2px;
    min-height: 0;
    overflow-y: auto;
  }

  .chip-clear {
    display: grid;
    place-items: center;
    margin-right: -4px;
    border-radius: 50%;
  }

  .chip-clear :global(svg) {
    width: 12px;
    height: 12px;
  }

  .chip-clear:hover {
    color: var(--color-text);
  }

  .dot {
    width: 7px;
    height: 7px;
    flex: none;
    border-radius: 50%;
    background: var(--color-text-3);
  }

  .dot.connected {
    background: var(--color-accent);
  }
</style>
