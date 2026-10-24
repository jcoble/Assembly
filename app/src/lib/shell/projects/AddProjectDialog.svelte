<!--
  AddProjectDialog.svelte — "New project" from the draft's project menu.

  Two steps in one command dialog: choose the machine, then browse its folders
  and add one. The folder is checked on the machine that owns it; a refusal
  (git's own folder, a folder inside a repository, a missing path) is shown
  under the path field, and Add checks again when pressed again. A path that
  names a new folder in the listed one is created there first (no `git init`);
  otherwise nothing here writes to the folder. With `chooseFolder` it picks a
  new folder for an existing project on the same machine instead of adding one.
-->
<script lang="ts">
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import CircleAlert from '@lucide/svelte/icons/circle-alert';
  import Cloud from '@lucide/svelte/icons/cloud';
  import CornerLeftUp from '@lucide/svelte/icons/corner-left-up';
  import Folder from '@lucide/svelte/icons/folder';
  import Laptop from '@lucide/svelte/icons/laptop';
  import Plus from '@lucide/svelte/icons/plus';
  import Settings2 from '@lucide/svelte/icons/settings-2';
  import { onMount } from 'svelte';
  import { remoteWorkspacePath } from '$lib/workspacePaths.ts';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Command from '$lib/components/ui/command/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { pickProjectFolder } from '$lib/shell/conversation/newSession/newSessionBackend.ts';
  import { addProject } from '$lib/shell/projects/projectRegistry.svelte.ts';
  import { isNewFolderPath, projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import { listFoldersFromTauri, validateProjectRootFromTauri, type FolderListing, type ProjectRecord, type RemoteAssemblyProfile } from '$lib/tauriSource.ts';

  interface Props {
    profiles: RemoteAssemblyProfile[];
    /** Start on this machine's folders instead of asking for a machine. */
    machine?: string | null;
    onAdded: (project: ProjectRecord) => void;
    onAddRemote: () => void;
    onManageRemotes: () => void;
    onClose: () => void;
    /** Hands the chosen folder here instead of adding a project; the dialog stays on `machine`. */
    chooseFolder?: (path: string) => Promise<void>;
  }

  let { profiles, machine: startMachine = null, onAdded, onAddRemote, onManageRemotes, onClose, chooseFolder }: Props = $props();

  const isMac = typeof navigator !== 'undefined' && /Mac/i.test(navigator.platform);

  let open = $state(true);
  // The starting machine is read once: the dialog is mounted per use.
  // svelte-ignore state_referenced_locally
  let step = $state<'machine' | 'folder'>(startMachine ? 'folder' : 'machine');
  let search = $state('');
  // svelte-ignore state_referenced_locally
  let machine = $state(startMachine ?? 'local');
  let path = $state('');
  let listing = $state<FolderListing | null>(null);
  let message = $state('');
  let adding = $state(false);
  let pathField = $state<HTMLInputElement | null>(null);
  let listSequence = 0;
  let homePath = $state('');
  let checkedFolder = $state<{ path: string; machine: string; exists: boolean } | null>(null);

  const machineLabel = $derived(projectMachineLabel(machine, profiles));
  const pathChecked = $derived(checkedFolder?.path === path.trim() && checkedFolder?.machine === machine);
  const canAdd = $derived(Boolean(path.trim()) && !adding && (Boolean(chooseFolder) || pathChecked));
  /** The path names a folder that is not there yet: Enter and the button create it. Choosing a folder creates none. */
  const newFolder = $derived(!chooseFolder && pathChecked && isNewFolderPath(path, listing, checkedFolder!.exists));

  $effect(() => {
    const requested = path.trim();
    const owner = machine;
    const home = homePath;
    const stop = new AbortController();
    checkedFolder = null;
    if (requested && !chooseFolder && (!requested.startsWith('~') || home)) {
      const resolved = requested === '~' ? home : requested.startsWith('~/') ? `${home}/${requested.slice(2)}` : requested;
      void checkFolder(owner, requested, resolved, stop.signal);
    }
    return () => stop.abort();
  });

  async function checkFolder(owner: string, requested: string, resolved: string, signal: AbortSignal): Promise<void> {
    try {
      const result = await validateProjectRootFromTauri(owner === 'local' ? resolved : remoteWorkspacePath(owner, resolved), signal);
      if (result && !signal.aborted) checkedFolder = { path: requested, machine: owner, exists: result.exists };
    } catch (error) {
      if (!signal.aborted) message = describe(error);
    }
  }

  onMount(() => {
    if (step === 'folder') void startOnFolder();
  });

  /** Opened on a machine: list its home, then take focus back from the dialog's own first-focus. */
  async function startOnFolder(): Promise<void> {
    await list('~');
    pathField?.focus();
  }

  // The folder step opens with the path field focused so typing goes there.
  $effect(() => {
    if (step === 'folder') pathField?.focus();
  });

  function close(): void {
    open = false;
    onClose();
  }

  function describe(error: unknown): string {
    if (error instanceof Error) return error.message;
    if (error && typeof error === 'object' && 'message' in error) return String((error as { message: unknown }).message);
    return String(error);
  }

  function withSlash(folder: string): string {
    return folder.endsWith('/') ? folder : `${folder}/`;
  }

  function parentOf(folder: string): string {
    const trimmed = folder.replace(/\/+$/, '');
    const parent = trimmed.slice(0, trimmed.lastIndexOf('/'));
    return withSlash(parent || '/');
  }

  async function list(folder: string): Promise<void> {
    const sequence = ++listSequence;
    try {
      const next = await listFoldersFromTauri(machine, folder);
      if (sequence !== listSequence) return;
      listing = next;
      if (folder === '~') homePath = next.path;
      if (folder === '~' && !path) path = withSlash(next.path);
      message = '';
    } catch (error) {
      if (sequence !== listSequence) return;
      listing = null;
      message = describe(error);
    }
  }

  function chooseMachine(id: string): void {
    machine = id;
    step = 'folder';
    search = '';
    path = '';
    listing = null;
    homePath = '';
    message = '';
    void list('~');
  }

  function openFolder(folder: string): void {
    path = withSlash(folder);
    void list(path);
  }

  async function add(): Promise<void> {
    if (!canAdd) return;
    adding = true;
    message = '';
    const requested = path;
    try {
      if (chooseFolder) {
        await chooseFolder(requested.trim());
        close();
        return;
      }
      const project = await addProject(machine, requested.trim(), newFolder);
      open = false;
      onAdded(project);
    } catch (error) {
      message = describe(error);
    } finally {
      adding = false;
    }
  }

  async function openInFinder(): Promise<void> {
    const answer = await pickProjectFolder();
    if (answer.status !== 'ok') {
      message = answer.message;
      return;
    }
    if (answer.value) openFolder(answer.value);
  }

  function machineKeydown(event: KeyboardEvent): void {
    if (event.key === 'Backspace' && !search) {
      event.preventDefault();
      close();
    }
  }

  function pathKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.metaKey || event.ctrlKey || newFolder)) {
      event.preventDefault();
      event.stopPropagation();
      void add();
    } else if (event.key === 'Backspace' && path.endsWith('/') && path !== '/') {
      event.preventDefault();
      openFolder(parentOf(path));
    }
  }

  function pathInput(): void {
    if (path.endsWith('/') && !newFolder) void list(path);
  }
</script>

<Command.Dialog
  bind:open
  onOpenChange={(next) => { if (!next) onClose(); }}
  shouldFilter={step === 'machine'}
  title={chooseFolder ? 'Change folder' : 'Add a project'}
  description="Choose a machine, then a folder on it."
  class="top-[17%] sm:max-w-[576px]"
>
  {#if step === 'machine'}
    <div class="flex items-center gap-1 border-b pl-2">
      <!-- Out of the tab order so the dialog opens with focus in the search field. -->
      <Button variant="ghost" size="icon-sm" aria-label="Close" tabindex={-1} onclick={close}><ArrowLeft /></Button>
      <div class="min-w-0 flex-1 [&_[data-slot=command-input-wrapper]]:border-0">
        <Command.Input placeholder="Search machines…" bind:value={search} onkeydown={machineKeydown} />
      </div>
    </div>
    <Command.List class="max-h-[360px]">
      <Command.Empty>No machine matches.</Command.Empty>
      <Command.Group heading="Add a project on">
        <Command.Item value="This Mac" onSelect={() => chooseMachine('local')}>
          <Laptop />
          <span class="text-[13px]">This Mac</span>
        </Command.Item>
        {#each profiles as profile (profile.id)}
          <Command.Item value={`${profile.name} ${profile.sshTarget}`} onSelect={() => chooseMachine(profile.id)}>
            <Cloud />
            <span class="flex min-w-0 flex-col">
              <span class="truncate text-[13px]">{profile.name}</span>
              <span class="truncate text-sm text-muted-foreground">{profile.sshTarget}</span>
            </span>
          </Command.Item>
        {/each}
      </Command.Group>
      <Command.Separator />
      <Command.Group>
        <Command.Item value="Add remote machine" onSelect={() => { close(); onAddRemote(); }}>
          <Plus />
          <span class="text-[13px]">Add remote machine…</span>
        </Command.Item>
        {#if profiles.length > 0}
          <Command.Item value="Manage remote machines" onSelect={() => { close(); onManageRemotes(); }}>
            <Settings2 />
            <span class="text-[13px]">Manage remote machines…</span>
          </Command.Item>
        {/if}
      </Command.Group>
    </Command.List>
  {:else}
    <div class="flex items-center gap-1 border-b py-1 pr-2 pl-2">
      {#if !chooseFolder}
        <Button variant="ghost" size="icon-sm" aria-label="Back to machines" onclick={() => { step = 'machine'; message = ''; }}><ArrowLeft /></Button>
      {/if}
      <Input
        aria-label="Folder path"
        bind:ref={pathField}
        class="h-8 flex-1 border-0 bg-transparent text-[13px] shadow-none"
        bind:value={path}
        oninput={pathInput}
        onkeydown={pathKeydown}
      />
      {#if machine === 'local'}
        <Button variant="ghost" size="sm" onclick={() => void openInFinder()}>Open in Finder</Button>
      {/if}
      <Button size="sm" disabled={!canAdd} onclick={() => void add()}>
        {chooseFolder ? 'Choose' : newFolder ? 'Create' : 'Add'} <span class="opacity-70">{isMac ? '⌘' : 'Ctrl'} Enter</span>
      </Button>
    </div>
    {#if message}
      <p class="flex items-center gap-2 border-b px-3 py-2 text-[13px] text-destructive" data-testid="add-project-error">
        <CircleAlert class="size-4 shrink-0" />{message}
      </p>
    {/if}
    <Command.List class="max-h-[360px]">
      <Command.Group heading={`Folders on ${machineLabel}`}>
        {#if listing}
          <Command.Item value=".." onSelect={() => openFolder(parentOf(listing!.path))}>
            <CornerLeftUp />
            <span class="text-[13px]">..</span>
          </Command.Item>
          {#each listing.directories as name (name)}
            <Command.Item value={name} onSelect={() => openFolder(`${withSlash(listing!.path)}${name}`)}>
              <Folder />
              <span class="truncate text-[13px]">{name}</span>
            </Command.Item>
          {/each}
        {/if}
      </Command.Group>
    </Command.List>
  {/if}
  <div class="flex items-center gap-3 border-t px-3 py-2 text-sm text-muted-foreground">
    <span><kbd class="add-project-key">↑</kbd> <kbd class="add-project-key">↓</kbd> Navigate</span>
    <span><kbd class="add-project-key">Enter</kbd> {step === 'machine' ? 'Select' : newFolder ? 'Create folder and add' : 'Open'}</span>
    <span><kbd class="add-project-key">Backspace</kbd> Back</span>
    <span><kbd class="add-project-key">Esc</kbd> Close</span>
  </div>
</Command.Dialog>

<style>
  .add-project-key {
    padding: 1px 5px;
    border-radius: 4px;
    background: var(--color-surface-raised);
    color: var(--color-text-2);
    font: inherit;
  }
</style>
