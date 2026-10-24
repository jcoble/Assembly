<!--
  NewProjectDialog.svelte — "New project" on the Projects screen.

  Makes a new folder on a machine and adds it as a project: empty, empty with
  `git init`, or a `git clone` of a URL or path. The name goes in the "Put it
  in" folder, which starts as the parent of the machine's most recently used
  project and is changed with the Add folder picker. While the folder is being
  made only Cancel closes the dialog; Cancel stops waiting, but git on the
  machine runs on until it finishes or times out.
-->
<script lang="ts">
  import { onMount } from 'svelte';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Dialog from '$lib/components/ui/dialog/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { Label } from '$lib/components/ui/label/index.js';
  import { SegmentedControl } from '$lib/components/ui/segmented-control/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import AddProjectDialog from '$lib/shell/projects/AddProjectDialog.svelte';
  import { addProject, projectRegistry } from '$lib/shell/projects/projectRegistry.svelte.ts';
  import { projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import type { ProjectRecord, ProjectStart, RemoteAssemblyProfile } from '$lib/tauriSource.ts';

  interface Props {
    profiles: RemoteAssemblyProfile[];
    /** The machine the dialog starts on; This Mac when none. */
    machine?: string | null;
    onCreated: (project: ProjectRecord) => void;
    onClose: () => void;
  }

  let { profiles, machine: startMachine = null, onCreated, onClose }: Props = $props();

  const START_OPTIONS = [
    { value: 'empty', label: 'Empty folder' },
    { value: 'git', label: 'Empty folder with git' },
    { value: 'clone', label: 'Copy of a repository' }
  ];

  let open = $state(true);
  // The starting machine is read once: the dialog is mounted per use.
  // svelte-ignore state_referenced_locally
  let machine = $state(startMachine ?? 'local');
  let name = $state('');
  // svelte-ignore state_referenced_locally
  let parent = $state(recentParent(machine));
  let startWith = $state('git');
  let source = $state('');
  let message = $state('');
  let creating = $state(false);
  let choosing = $state(false);
  /** Ends the wait for a create when the dialog is cancelled or goes away. */
  const stop = new AbortController();

  const nameError = $derived(name.includes('/') ? 'A name cannot contain /.' : '');
  const canCreate = $derived(
    Boolean(name.trim()) && !nameError && Boolean(parent) && (startWith !== 'clone' || Boolean(source.trim())) && !creating
  );

  onMount(() => () => stop.abort());

  /** The folder holding the machine's most recently used project, or '' when it has none. */
  function recentParent(id: string): string {
    const recent = projectRegistry.projects
      .filter((project) => project.machine === id && project.lastUsedMs !== null)
      .sort((a, b) => (b.lastUsedMs ?? 0) - (a.lastUsedMs ?? 0))[0];
    if (!recent) return '';
    return recent.rootPath.slice(0, recent.rootPath.lastIndexOf('/')) || '/';
  }

  function chooseMachine(id: string): void {
    machine = id;
    parent = recentParent(id);
    message = '';
  }

  async function chooseParent(path: string): Promise<void> {
    parent = path.replace(/(.)\/+$/, '$1');
  }

  function close(): void {
    stop.abort();
    open = false;
    onClose();
  }

  async function create(): Promise<void> {
    if (!canCreate) return;
    creating = true;
    message = '';
    const start: ProjectStart | undefined =
      startWith === 'git' ? 'git' : startWith === 'clone' ? { clone: source.trim() } : undefined;
    try {
      const project = await addProject(machine, `${parent.replace(/\/+$/, '')}/${name.trim()}`, true, start);
      if (stop.signal.aborted) return;
      open = false;
      onCreated(project);
    } catch (error) {
      if (!stop.signal.aborted) message = error instanceof Error ? error.message : String(error);
    } finally {
      if (!stop.signal.aborted) creating = false;
    }
  }
</script>

<Dialog.Root bind:open onOpenChange={(next) => { if (!next) close(); }}>
  <Dialog.Content
    class="sm:max-w-[520px]"
    showCloseButton={!creating}
    escapeKeydownBehavior={creating ? 'ignore' : 'close'}
    interactOutsideBehavior={creating ? 'ignore' : 'close'}
  >
    <Dialog.Header>
      <Dialog.Title>New project</Dialog.Title>
      <Dialog.Description>Makes a new folder and adds it as a project.</Dialog.Description>
    </Dialog.Header>
    <form class="flex flex-col gap-3" onsubmit={(event) => { event.preventDefault(); void create(); }}>
      <div class="flex flex-col gap-1.5">
        <Label>Machine</Label>
        <Select.Root type="single" value={machine} onValueChange={chooseMachine} disabled={creating}>
          <Select.Trigger size="sm" class="w-full" aria-label="Machine">{projectMachineLabel(machine, profiles)}</Select.Trigger>
          <Select.Content>
            <Select.Item value="local" label="This Mac" />
            {#each profiles as profile (profile.id)}
              <Select.Item value={profile.id} label={profile.name} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="flex flex-col gap-1.5">
        <Label for="new-project-name">Name</Label>
        <Input id="new-project-name" bind:value={name} disabled={creating} autocomplete="off" />
        {#if nameError}<p class="text-[13px] text-destructive">{nameError}</p>{/if}
      </div>
      <div class="flex flex-col gap-1.5">
        <Label>Put it in</Label>
        <div class="flex items-center gap-2">
          <span class="min-w-0 flex-1 truncate text-[13px] {parent ? '' : 'text-muted-foreground'}" title={parent}>
            {parent || 'Choose a folder'}
          </span>
          <Button variant="ghost" size="sm" disabled={creating} onclick={() => (choosing = true)}>Choose…</Button>
        </div>
      </div>
      <div class="flex flex-col gap-1.5">
        <Label>Start with</Label>
        <SegmentedControl items={START_OPTIONS} bind:value={startWith} size="sm" disabled={creating} aria-label="Start with" class="w-full" />
        {#if startWith === 'clone'}
          <Input aria-label="Repository URL or path" placeholder="Repository URL or path" bind:value={source} disabled={creating} autocomplete="off" />
        {/if}
      </div>
      {#if message}<p class="text-[13px] text-destructive" data-testid="new-project-error">{message}</p>{/if}
      <Dialog.Footer>
        <Button type="button" variant="ghost" size="sm" onclick={close}>Cancel</Button>
        <Button type="submit" size="sm" disabled={!canCreate}>
          {creating ? (startWith === 'clone' ? 'Copying…' : 'Creating…') : 'Create'}
        </Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>

{#if choosing}
  <AddProjectDialog
    {profiles}
    {machine}
    chooseFolder={chooseParent}
    onAdded={() => (choosing = false)}
    onAddRemote={() => {}}
    onManageRemotes={() => {}}
    onClose={() => (choosing = false)}
  />
{/if}
