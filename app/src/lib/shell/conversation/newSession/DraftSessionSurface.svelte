<!--
  DraftSessionSurface.svelte — what "+" opens.

  Not a form and not a dialog: an empty session, in the Session tab, with the
  ordinary composer and the caret already in it. The heading names the project
  ("What should we build in …?") and is the project menu; the agent picker sits
  in the composer footer beside model, effort and approval; the machine sits in
  the row under the composer.

  A project's session runs in its root folder, or in the worktree it was opened
  from; there is no branch or worktree to pick. A bare root has no files to work on,
  so the draft says so and will not send. No Assembly conversation is created
  here, and opening, switching or closing a draft only reads: the project list,
  folder listings and that one folder check. A
  temporary ACP session reads the selected provider's controls; its process
  stops after the read. The surface hands one `ThreadStartPickerState` to
  `onSend` on the first message.
-->
<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { homeDir } from '@tauri-apps/api/path';
  import Check from '@lucide/svelte/icons/check';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import Cloud from '@lucide/svelte/icons/cloud';
  import FolderPlus from '@lucide/svelte/icons/folder-plus';
  import MessageCircle from '@lucide/svelte/icons/message-circle';
  import Monitor from '@lucide/svelte/icons/monitor';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import X from '@lucide/svelte/icons/x';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import PendingFirstMessage from '$lib/shell/conversation/components/PendingFirstMessage.svelte';
  import RemoteConnections from '$lib/shell/connections/RemoteConnections.svelte';
  import AddProjectDialog from '$lib/shell/projects/AddProjectDialog.svelte';
  import { hydrateProjects, projectRegistry } from '$lib/shell/projects/projectRegistry.svelte.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { defaultDraftProjectId, projectBadge, projectMachineLabel, visibleProjects } from '$lib/shell/projects/projects.ts';
  import { checkForProviderUpdates, installProviderUpdates, type ProviderUpdateState } from '$lib/shell/providers/providerUpdateService.svelte';
  import RestartProvidersButton from '$lib/shell/components/RestartProvidersButton.svelte';
  import ConversationComposer from '$lib/shell/conversation/components/ConversationComposer.svelte';
  import { conversationSessions, setConversationDraft } from '$lib/shell/conversation/conversationStore.svelte';
  import { persistConversationSessionDraft, stopStructuredTurn } from '$lib/shell/conversation/conversationService';
  import type { ConversationAttachment } from '$lib/shell/conversation/conversationTypes.ts';
  import type {
    AgentConversationConfigField,
    AgentConversationConfigState
  } from '$lib/shell/conversation/conversationConfig.ts';
  import {
    BARE_ROOT_MESSAGE,
    buildThreadStartRequest,
    defaultThreadStartState,
    displayProvider,
    draftEffortFor,
    projectCwd,
    validateThreadStart,
    type ThreadStartPickerState,
    type ThreadStartProvider,
    type ThreadStartRequest
  } from '$lib/shell/conversation/newSession/threadStartFlow.ts';
  import { projectRootIsBare } from '$lib/shell/conversation/newSession/newSessionBackend.ts';
  import { rememberAgentConfigChoice, rememberedAgentConfigChoice } from '$lib/shell/conversation/agentConfigMemory';
  import {
    listFoldersFromTauri,
    probeAgentProviderConfigFromTauri,
    readRemoteAssemblyEnvironmentFromTauri,
    type ProjectRecord,
    type RemoteAssemblyEnvironment,
    type RemoteAssemblyProfile,
    type ExecutionEnvironment
  } from '$lib/tauriSource.ts';

  interface Props {
    stopSignal: AbortSignal;
    onSend: (request: ThreadStartRequest, images: File[]) => void | Promise<void>;
    onClose: () => void;
    /** The session the first send is starting, until the view switches to it. */
    startingOwnedId?: string | null;
    /** Open in this project, and in `folder` of it when set (a worktree), instead of the default one. */
    projectId?: string | null; folder?: string | null;
  }

  let { stopSignal, onSend, onClose, startingOwnedId = null, projectId = null, folder = null }: Props = $props();
  /* While the first send starts its session, this composer is that session's:
     what is typed lands in its draft, so it is still there after the switch. */
  const starting = $derived(startingOwnedId ? conversationSessions[startingOwnedId] ?? null : null);

  const PROVIDERS: readonly ThreadStartProvider[] = ['codex', 'claude', 'antigravity'];

  // The preset is applied on mount after the registry is read; this is only
  // what the first frame paints.
  let draft = $state<ThreadStartPickerState>(
    { ...defaultThreadStartState({ projectPath: '' }), model: '', effort: '', access: '' }
  );
  let composer = $state<{ focus(): void } | null>(null);
  let folderMessage = $state<string | null>(null);
  let submitting = $state(false);
  let stagedImages = $state<Array<{ file: File; attachment: ConversationAttachment }>>([]);
  let attachmentError = $state('');
  let submitError = $state('');
  let catalogConfig = $state<AgentConversationConfigState | null>(null);
  let catalogKey = $state('');
  let catalogError = $state('');
  let hydrated = $state(false);
  /** Send was pressed before the provider's model choices arrived; it goes when they do. */
  let sendWhenReady = $state(false);
  let loadSequence = 0;
  let remoteAssembly = $state<RemoteAssemblyEnvironment>({ profiles: [], readyProfileIds: [] });
  let remoteSetupOpen = $state(false);
  let addProjectOpen = $state(false);
  /** What to do once the machine being set up connects. */
  let afterRemoteConnect: (() => void) | null = null;
  let remoteProfile = $state<RemoteAssemblyProfile>({
    id: '',
    name: '',
    sshTarget: '',
    sourceRoot: '',
    defaultCwd: ''
  });
  let pickerUpdates = $state<ProviderUpdateState>({ phase: 'idle', generation: 0, installingProvider: null, message: '', status: null });

  function checkSelectedMachine(): void {
    pickerUpdates = { phase: 'idle', generation: 0, installingProvider: null, message: '', status: null };
    void checkForProviderUpdates(stopSignal, pickerUpdates, draft.executionEnvironment === 'remote' ? draft.remoteProfileId ?? undefined : undefined);
  }

  const projects = $derived(visibleProjects(projectRegistry.projects, remoteAssembly.profiles.map((profile) => profile.id)));
  const selectedProject = $derived(projects.find((project) => project.id === draft.projectId) ?? null);
  const draftMachine = $derived(draft.executionEnvironment === 'remote' ? draft.remoteProfileId ?? '' : 'local');
  const selectedCatalogKey = $derived([
    draft.executionEnvironment, draft.remoteProfileId ?? '', draft.provider, draft.cwd
  ].join('\u0000'));
  const currentCatalog = $derived(catalogKey === selectedCatalogKey ? catalogConfig : null);
  const draftEfforts = $derived(currentCatalog ? draftEffortFor(currentCatalog, draft.model, null).efforts : []);
  const problems = $derived(validateThreadStart(draft, stagedImages.length > 0));
  const selectedRemoteProfile = $derived(
    remoteAssembly.profiles.find((profile) => profile.id === draft.remoteProfileId) ?? null
  );

  function emptyRemoteProfile(): RemoteAssemblyProfile {
    return {
      id: crypto.randomUUID(),
      name: '',
      sshTarget: '',
      sourceRoot: '',
      defaultCwd: ''
    };
  }

  /**
   * The draft's own answer to the question the composer's settings menu asks a
   * running session. Same shape, same menu — the difference is that a change
   * lands in this state instead of being written to an agent that does not
   * exist yet.
   */
  const configState = $derived<AgentConversationConfigState>({
    model: currentCatalog ? (draft.model || null) : null,
    availableModels: currentCatalog?.availableModels ?? [],
    modelLabels: currentCatalog?.modelLabels ?? {},
    reasoningEffort: currentCatalog ? (draft.effort || null) : null,
    availableEfforts: draftEfforts,
    approvalPolicy: currentCatalog ? (draft.access || null) : null,
    availableApprovalPolicies: currentCatalog?.availableApprovalPolicies ?? []
  });

  function updateDraft(patch: Partial<ThreadStartPickerState>): void {
    const targetChanged = (patch.executionEnvironment !== undefined && patch.executionEnvironment !== draft.executionEnvironment)
      || (patch.remoteProfileId !== undefined && patch.remoteProfileId !== draft.remoteProfileId)
      || (patch.provider !== undefined && patch.provider !== draft.provider)
      || (patch.cwd !== undefined && patch.cwd !== draft.cwd);
    if (targetChanged) {
      catalogConfig = null;
      catalogKey = '';
    }
    draft = { ...draft, ...(targetChanged ? { model: '', effort: '', access: '' } : {}), ...patch };
    submitError = '';
  }

  function offeredChoice(wanted: string | null | undefined, current: string | null, offered: string[]): string {
    return (wanted && offered.includes(wanted) ? wanted : null)
      ?? (current && offered.includes(current) ? current : null)
      ?? offered[0] ?? '';
  }

  $effect(() => {
    const key = selectedCatalogKey;
    const [machine, profileId, providerId, cwd] = key.split('\u0000');
    const executionEnvironment = machine as ExecutionEnvironment;
    const remoteProfileId = profileId || null;
    const provider = providerId as ThreadStartProvider;
    if (!hydrated || !cwd.startsWith('/') || (executionEnvironment === 'remote' && !remoteProfileId) || stopSignal.aborted) return;
    const controller = new AbortController();
    const cancel = (): void => controller.abort();
    stopSignal.addEventListener('abort', cancel, { once: true });
    catalogConfig = null;
    catalogError = '';
    void loadCatalog(key, { provider, executionEnvironment, remoteProfileId, cwd }, controller.signal);
    return () => {
      controller.abort();
      stopSignal.removeEventListener('abort', cancel);
    };
  });
  /** Asks the draft's machine which models it offers. The effect's signal stops it. */
  async function loadCatalog(
    key: string,
    target: { provider: ThreadStartProvider; executionEnvironment: ExecutionEnvironment; remoteProfileId: string | null; cwd: string },
    signal: AbortSignal
  ): Promise<void> {
    try {
      const config = await probeAgentProviderConfigFromTauri(target, signal);
      if (signal.aborted || stopSignal.aborted || selectedCatalogKey !== key || !config) return;
      if (!config.availableModels.length) throw new Error('The provider did not advertise any models for this machine.');
      const remembered = rememberedAgentConfigChoice(target.provider);
      const model = offeredChoice(remembered?.model, config.model, config.availableModels);
      draft = {
        ...draft,
        model,
        effort: draftEffortFor(config, model, remembered?.reasoningEffort).effort,
        // A new draft starts on the live adapter mode, not a remembered permission.
        access: offeredChoice(null, config.approvalPolicy, config.availableApprovalPolicies)
      };
      catalogConfig = config;
      catalogKey = key;
      submitError = '';
    } catch (error) {
      if (signal.aborted || stopSignal.aborted || selectedCatalogKey !== key) return;
      catalogError = describeError(error);
      submitError = catalogError;
    }
  }

  /** Stop is best effort: a turn that already ended has nothing to stop. */
  async function stopStarting(ownedId: string): Promise<void> {
    try {
      await stopStructuredTurn(ownedId);
    } catch (error) {
      console.warn('Could not stop the starting session', error);
    }
  }

  $effect(() => {
    if (!sendWhenReady || (!currentCatalog && !catalogError)) return;
    sendWhenReady = false;
    submitting = false;
    untrack(() => void send());
  });

  /** Asks the project's own machine whether its folder is a bare repository.
   * Only reads. The draft has no folder until the answer is in. */
  async function checkRoot(machine: string, projectPath: string): Promise<void> {
    const sequence = ++loadSequence;
    if (stopSignal.aborted) return;
    folderMessage = null;
    const answer = await projectRootIsBare(machine, projectPath);
    if (stopSignal.aborted || sequence !== loadSequence) return;
    if (answer.status !== 'ok') {
      folderMessage = answer.message;
      updateDraft({ rootProblem: answer.message });
      return;
    }
    if (answer.value) folderMessage = BARE_ROOT_MESSAGE;
    updateDraft({ rootProblem: answer.value ? BARE_ROOT_MESSAGE : '', cwd: projectCwd(projectPath, answer.value) });
  }

  function clearFolderCheck(): void {
    loadSequence += 1;
    folderMessage = null;
  }

  /** The machine comes from the project; the session starts in `startFolder`, its root unless given. */
  function selectProject(project: ProjectRecord, startFolder = project.rootPath): void {
    const remote = project.machine !== 'local';
    const machineChanged = project.machine !== draftMachine;
    clearFolderCheck();
    updateDraft({
      projectId: project.id,
      executionEnvironment: remote ? 'remote' : 'local',
      remoteProfileId: remote ? project.machine : null,
      projectPath: startFolder,
      // Set once the folder check answers, so nothing runs in a bare root meanwhile.
      cwd: '',
      rootProblem: ''
    });
    if (machineChanged) checkSelectedMachine();
    void checkRoot(project.machine, startFolder);
  }

  /** A plain conversation on `machine`, started in that machine's home folder. */
  async function selectNoProject(machine: string): Promise<void> {
    const sequence = ++loadSequence;
    const remote = machine !== 'local';
    try {
      const cwd = remote ? (await listFoldersFromTauri(machine, '~')).path : await homeDir();
      if (stopSignal.aborted || sequence !== loadSequence) return;
      const machineChanged = machine !== draftMachine;
      clearFolderCheck();
      updateDraft({
        projectId: null,
        executionEnvironment: remote ? 'remote' : 'local',
        remoteProfileId: remote ? machine : null,
        projectPath: '',
        cwd,
        rootProblem: ''
      });
      if (machineChanged) checkSelectedMachine();
    } catch (error) {
      if (sequence === loadSequence) submitError = describeError(error);
    }
  }

  /** Runs `then` now on a ready machine; otherwise opens that machine's setup
   * and runs it once the machine connects. */
  function whenMachineReady(machine: string, then: () => void): void {
    if (machine === 'local' || remoteAssembly.readyProfileIds.includes(machine)) {
      then();
      return;
    }
    const profile = remoteAssembly.profiles.find((candidate) => candidate.id === machine);
    remoteProfile = profile ? { ...profile } : emptyRemoteProfile();
    afterRemoteConnect = then;
    remoteSetupOpen = true;
  }

  function openRemoteSetup(profile: RemoteAssemblyProfile | null): void {
    if (profile) remoteProfile = profile;
    afterRemoteConnect = null;
    remoteSetupOpen = true;
  }

  function remoteConnected(profile: RemoteAssemblyProfile): void {
    const next = afterRemoteConnect ?? (() => void selectNoProject(profile.id));
    afterRemoteConnect = null;
    next();
  }

  function stageImages(files: File[]): void {
    const images = files.filter((file) => file.type.startsWith('image/'));
    if (!images.length) {
      attachmentError = 'That file is not a supported image.';
      return;
    }
    attachmentError = '';
    stagedImages = [...stagedImages, ...images.map((file) => ({
      file,
      attachment: {
        id: crypto.randomUUID(), name: file.name, mimeType: file.type,
        path: '', previewUrl: URL.createObjectURL(file), byteLength: file.size
      }
    }))];
  }

  function removeStagedImage(id: string): void {
    const found = stagedImages.find((item) => item.attachment.id === id);
    if (found) URL.revokeObjectURL(found.attachment.previewUrl);
    stagedImages = stagedImages.filter((item) => item.attachment.id !== id);
  }

  function describeError(error: unknown): string {
    if (error instanceof Error) return error.message;
    if (error && typeof error === 'object' && 'message' in error) {
      const message = (error as { message?: unknown }).message;
      if (typeof message === 'string') return message;
    }
    return String(error);
  }

  function selectProvider(provider: ThreadStartProvider): void {
    if (provider === draft.provider) return;
    updateDraft({ provider });
    if (!pickerUpdates.status && pickerUpdates.phase !== 'checking') checkSelectedMachine();
  }

  function changeConfig(field: AgentConversationConfigField, value: string): void {
    if (field === 'model') {
      // A model offers its own efforts: keep the remembered one if it has it.
      const wanted = rememberedAgentConfigChoice(draft.provider)?.reasoningEffort || draft.effort;
      updateDraft({ model: value, effort: currentCatalog ? draftEffortFor(currentCatalog, value, wanted).effort : draft.effort });
    }
    else if (field === 'reasoningEffort') updateDraft({ effort: value });
    else updateDraft({ access: value });
    // A pick here is the choice the next new session opens on.
    rememberAgentConfigChoice(draft.provider, { [field]: value });
  }

  async function send(): Promise<void> {
    if (submitting || stopSignal.aborted) return;
    const request = buildThreadStartRequest(draft, stagedImages.length > 0);
    if (!request) {
      submitError = problems[0]?.message ?? 'This draft is not ready to send.';
      return;
    }
    if (!currentCatalog && !catalogError) {
      // The choices are still loading: show the message going out and send it when they arrive.
      submitting = true;
      sendWhenReady = true;
      return;
    }
    if (!currentCatalog || !currentCatalog.availableModels.includes(draft.model)
      || (draft.effort && !draftEfforts.includes(draft.effort))
      || (draft.access && !currentCatalog.availableApprovalPolicies.includes(draft.access))) {
      submitError = catalogError || 'Wait for this machine’s model choices before sending.';
      return;
    }
    submitting = true;
    submitError = '';
    try {
      await onSend(request, stagedImages.map((item) => item.file));
    } catch (error) {
      if (!stopSignal.aborted) submitError = describeError(error);
    } finally {
      submitting = false;
    }
  }

  onMount(() => {
    const owner = { active: true };
    const sequence = ++loadSequence;
    void hydrateDraft(owner, sequence);
    return () => {
      owner.active = false;
      loadSequence += 1;
      stagedImages.forEach((item) => URL.revokeObjectURL(item.attachment.previewUrl));
    };
  });

  async function hydrateDraft(owner: { active: boolean }, sequence: number): Promise<void> {
    if (stopSignal.aborted) return;
    await Promise.all([hydrateProjects(), hydrateRemoteAssembly(owner)]);
    if (stopSignal.aborted || !owner.active || sequence !== loadSequence) return;
    if (projectRegistry.error) submitError = projectRegistry.error;
    // The project asked for, else the active session's, else the most recent session's, among the visible ones.
    const presetId = projectId ?? defaultDraftProjectId(projects, rail.owned, rail.activeOwnedId);
    const preset = projects.find((project) => project.id === presetId) ?? null;
    hydrated = true;
    if (preset) selectProject(preset, (preset.id === projectId && folder) || preset.rootPath);
    else void selectNoProject('local');
    if (!preset || preset.machine === 'local') checkSelectedMachine();
    composer?.focus();
  }

  async function hydrateRemoteAssembly(owner: { active: boolean }): Promise<void> {
    try {
      const environment = await readRemoteAssemblyEnvironmentFromTauri();
      if (stopSignal.aborted || !owner.active) return;
      remoteAssembly = environment;
      remoteProfile = emptyRemoteProfile();
    } catch {
      // Remote setup controls stay empty when the desktop backend is unavailable.
    }
  }
</script>

{#snippet projectMenu()}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <button {...props} type="button" data-testid="draft-session-project" class="draft-project-trigger">
          {selectedProject?.title ?? 'No project'}
          <ChevronDown aria-hidden="true" class="size-3.5 opacity-70" />
        </button>
      {/snippet}
    </DropdownMenu.Trigger><DropdownMenu.Content align="start" sideOffset={8} avoidCollisions collisionPadding={12} class="w-[300px]">
      {#each projects as project (project.id)}
        <DropdownMenu.Item
          data-testid={`draft-session-project-${project.id}`}
          title={`${project.rootPath} on ${projectMachineLabel(project.machine, remoteAssembly.profiles)}`}
          onSelect={() => whenMachineReady(project.machine, () => selectProject(project))}
        >
          <span class="draft-project-badge" aria-hidden="true">{projectBadge(project.title)}</span>
          <span class="draft-machine-name">{project.title}</span>
          <Chip>{projectMachineLabel(project.machine, remoteAssembly.profiles)}</Chip>
          <span class="draft-check">{#if draft.projectId === project.id}<Check aria-hidden="true" class="size-3.5" />{/if}</span>
        </DropdownMenu.Item>
      {/each}
      {#if projects.length > 0}<DropdownMenu.Separator />{/if}
      <DropdownMenu.Item data-testid="draft-session-no-project" onSelect={() => void selectNoProject(draftMachine || 'local')}>
        <MessageCircle aria-hidden="true" class="size-4" />
        <span class="draft-machine-name">No project</span>
        <span class="draft-check">{#if !draft.projectId}<Check aria-hidden="true" class="size-3.5" />{/if}</span>
      </DropdownMenu.Item>
      <DropdownMenu.Item data-testid="draft-session-new-project" onSelect={() => (addProjectOpen = true)}>
        <FolderPlus aria-hidden="true" class="size-4" />
        <span class="draft-machine-name">New project</span>
      </DropdownMenu.Item>
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

{#snippet providerMenu()}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button {...props} data-testid="draft-session-provider" variant="ghost" size="xs" class="draft-control">
          {displayProvider(draft.provider)}
          <ChevronDown aria-hidden="true" class="size-3 opacity-70" />
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content side="top" align="start" sideOffset={8} avoidCollisions collisionPadding={12} class="min-w-[230px]">
      <DropdownMenu.Label>Which agent runs this</DropdownMenu.Label>
      {#each PROVIDERS as provider (provider)}
        <DropdownMenu.Item
          data-testid={`draft-session-provider-${provider}`}
          onSelect={() => selectProvider(provider)}
        >
          <span class="draft-check">
            {#if draft.provider === provider}<Check aria-hidden="true" class="size-3.5" />{/if}
          </span>
          {provider === 'antigravity' ? 'Antigravity' : provider}
          {#if pickerUpdates.status?.providers.find((item) => item.provider === provider)?.currentVersion === null}
            <span class="text-[var(--color-text-2)]"> · Install available</span>
          {/if}
        </DropdownMenu.Item>
      {/each}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

{#snippet subBar()}
  {#if selectedProject && selectedProject.machine !== 'local'}
    <span class="draft-machine-label" data-testid="draft-session-machine">
      <Cloud aria-hidden="true" class="size-3.5" />
      {projectMachineLabel(selectedProject.machine, remoteAssembly.profiles)}
    </span>
  {:else if !selectedProject}
    <DropdownMenu.Root>
      <DropdownMenu.Trigger>
        {#snippet child({ props })}
          <Button {...props} data-testid="draft-session-environment" variant="ghost" size="xs" class="draft-control">
            {#if draft.executionEnvironment === 'remote'}<Cloud aria-hidden="true" class="size-3.5" />{:else}<Monitor aria-hidden="true" class="size-3.5" />{/if}
            {draft.executionEnvironment === 'remote' ? selectedRemoteProfile?.name ?? 'Remote machine' : 'This Mac'}
            <ChevronDown aria-hidden="true" class="size-3 opacity-70" />
          </Button>
        {/snippet}
      </DropdownMenu.Trigger>
      <DropdownMenu.Content class="draft-machine-menu" side="top" align="start" sideOffset={8} avoidCollisions collisionPadding={12}>
        <DropdownMenu.Label>Work in</DropdownMenu.Label>
        <DropdownMenu.Item onSelect={() => void selectNoProject('local')}>
          <Monitor aria-hidden="true" class="size-4" />
          <span class="draft-machine-name">This Mac</span>
          {#if draft.executionEnvironment === 'local'}<Check aria-hidden="true" class="draft-machine-selected size-4" />{/if}
        </DropdownMenu.Item>
        {#each remoteAssembly.profiles as profile (profile.id)}
          <DropdownMenu.Item title={profile.sshTarget} onSelect={() => whenMachineReady(profile.id, () => void selectNoProject(profile.id))}>
            <Cloud aria-hidden="true" class="size-4" />
            <span class="draft-machine-name">{profile.name}</span>
            {#if draft.remoteProfileId === profile.id}
              <Check aria-hidden="true" class="draft-machine-selected size-4" />
            {/if}
          </DropdownMenu.Item>
        {/each}
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  {/if}
{/snippet}

<section class="draft-surface" class:centred={!submitting && !remoteSetupOpen} data-testid="draft-session-surface" aria-label="New session">
  <div class="draft-topline">
    <span data-testid="draft-session-note">Your conversation starts when you send.</span>
    <Button
      data-testid="draft-session-close"
      variant="ghost"
      size="icon-sm"
      aria-label="Discard this draft"
      onclick={onClose}
    >
      <X aria-hidden="true" class="size-4" />
    </Button>
  </div>

  <div class="draft-transcript" class:pending-first-send={submitting} data-testid="draft-session-transcript">
    {#if submitting}<PendingFirstMessage text={draft.prompt} />
    {:else if remoteSetupOpen}
      <div class="remote-setup" data-testid="draft-session-remote-setup">
        <RemoteConnections initialProfile={remoteProfile} onChange={(next) => remoteAssembly = next}
          onConnected={remoteConnected} onClose={() => remoteSetupOpen = false} />
      </div>
    {:else}
      <h2 class="draft-heading" data-testid="draft-session-heading">
        {#if selectedProject}What should we build in {@render projectMenu()}?{:else}What should we build? {@render projectMenu()}{/if}
      </h2>
    {/if}
    {#if folderMessage}
      <p class="draft-warning" data-testid="draft-session-folder-note">
        <span>
          {folderMessage}
        </span>
        <button
          class="draft-warning-dismiss"
          type="button"
          aria-label="Dismiss"
          onclick={() => (folderMessage = null)}
        >
          <X aria-hidden="true" class="size-3" />
        </button>
      </p>
    {/if}
  </div>

  {#if pickerUpdates.status?.providers.find((item) => item.provider === draft.provider)?.currentVersion === null}
    <div class="draft-adapter-action" data-testid="draft-provider-install-offer">
      {#if pickerUpdates.phase === 'installing'}<span class="inline-flex" role="status" aria-label="Downloading adapter"><WorkingSpinner /></span>{/if}
      <span>{displayProvider(draft.provider)} is not installed on {draft.executionEnvironment === 'remote' ? 'the remote machine' : 'this machine'}.</span>
      <Button variant="secondary" size="sm" disabled={pickerUpdates.phase === 'installing'} onclick={() => void installProviderUpdates(draft.provider, stopSignal, pickerUpdates, draft.executionEnvironment === 'remote' ? draft.remoteProfileId ?? undefined : undefined)}>
        {pickerUpdates.phase === 'installing' ? 'Installing…' : 'Install'}
      </Button>
    </div>
  {/if}
  {#if pickerUpdates.phase === 'error'}
    <p class="draft-adapter-message">{pickerUpdates.message}</p>
  {/if}
  {#if pickerUpdates.status?.restartRequired || pickerUpdates.phase === 'reconnecting'}
    <div class="draft-adapter-action">
      <span>{pickerUpdates.message}</span>
      <RestartProvidersButton updateState={pickerUpdates} profileId={draft.executionEnvironment === 'remote' ? draft.remoteProfileId ?? undefined : undefined} />
    </div>
  {/if}
  <ConversationComposer
    bind:this={composer}
    provider={draft.provider}
    draft={starting ? starting.draft : draft.prompt}
    attachments={stagedImages.map((item) => item.attachment)}
    {attachmentError}
    sending={submitting}
    supportsSteering={starting?.capabilities?.session.steering === true}
    {configState}
    pendingConfig={{}}
    commands={[]}
    sendError={submitError} sendErrorIsProbe={Boolean(catalogError) && submitError === catalogError}
    footerControls={providerMenu}
    {subBar}
    onDraftChange={(value) => {
      if (!starting) return updateDraft({ prompt: value });
      setConversationDraft(starting.ownedId, value);
      persistConversationSessionDraft(starting.ownedId, value);
    }}
    onSend={send}
    onStop={() => { if (starting) void stopStarting(starting.ownedId); }}
    onPaste={(event) => {
      const files = [...(event.clipboardData?.files ?? [])];
      if (!files.length) files.push(...[...(event.clipboardData?.items ?? [])]
        .filter((item) => item.kind === 'file')
        .map((item) => item.getAsFile())
        .filter((file): file is File => file !== null));
      if (files.length) { event.preventDefault(); stageImages(files); }
    }}
    onDropFiles={stageImages}
    onRemoveAttachment={removeStagedImage}
    onConfigChange={changeConfig}
  />
</section>

{#if addProjectOpen}
  <AddProjectDialog
    profiles={remoteAssembly.profiles}
    onAdded={(project) => { addProjectOpen = false; whenMachineReady(project.machine, () => selectProject(project)); }}
    onAddRemote={() => openRemoteSetup(emptyRemoteProfile())}
    onManageRemotes={() => openRemoteSetup(null)}
    onClose={() => (addProjectOpen = false)}
  />
{/if}

<style>
  .draft-surface {
    position: absolute;
    inset: 0;
    z-index: 3;
    display: flex;
    flex-direction: column;
    min-height: 0;
    /* The card's own surface colour, not the backdrop, so the draft reads as
       part of the panel. It stays opaque: this layer sits over a conversation
       that keeps its size and its terminals while the draft is open, and it
       has to hide them. */
    background: var(--color-surface);
    color: var(--color-text);
    font: 13px ui-sans-serif, system-ui;
  }

  .draft-adapter-action { position: absolute; z-index: 3; bottom: 240px; left: 46px; right: 46px; display: flex; align-items: center; gap: 8px; padding: 8px 12px; border: 1px solid var(--color-border); border-radius: 10px; background: var(--color-surface-raised); font-size: 12px; }
  .draft-adapter-message { position: absolute; z-index: 3; bottom: 290px; left: 46px; right: 46px; color: var(--color-text-2); font-size: 12px; }

  .draft-topline {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 6px 10px 6px 14px;
    color: var(--color-text-3);
    font-size: 12px;
  }

  .draft-transcript {
    display: flex;
    min-height: 0;
    flex: 1;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 6px;
    padding: 0 24px;
    color: var(--color-text-3);
    text-align: center;
  }

  .draft-transcript.pending-first-send { justify-content: flex-start; align-items: stretch; }

  /* An empty draft centres the heading and the composer together, as one
     block, in the middle of the panel. Once the first message is on its way
     the composer goes back to the bottom like any running session. */
  .draft-surface.centred .draft-transcript { justify-content: flex-end; padding-bottom: var(--space-4); }
  .draft-surface.centred::after { content: ''; flex: 1; }
  .draft-surface.centred :global(.composer-area) { position: relative; padding-top: 0; background: none; }

  .draft-heading {
    margin: 0;
    color: var(--color-text);
    font-size: var(--text-heading);
    font-weight: var(--text-heading-weight);
  }

  .draft-project-trigger {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 1px 6px;
    border: 0;
    border-radius: 6px;
    background: color-mix(in srgb, var(--color-text) 9%, transparent);
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  .draft-project-trigger:hover { background: color-mix(in srgb, var(--color-text) 14%, transparent); }
  .draft-project-trigger:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 1px; }

  /* The two-letter project badge. Plain grey on purpose. */
  .draft-project-badge {
    display: inline-grid;
    flex: none;
    place-items: center;
    width: 22px;
    height: 18px;
    border-radius: 5px;
    background: color-mix(in srgb, var(--color-text) 11%, transparent);
    color: var(--color-text-2);
    font: 600 12px/1 ui-monospace, SFMono-Regular, Menlo, monospace;
    letter-spacing: 0.02em;
  }

  .draft-machine-label {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 0 6px;
    color: var(--color-text-2);
    font-size: 13px;
  }
  .draft-transcript p { margin: 0; font-size: 13px; }
  .remote-setup {
    display: flex;
    width: min(520px, 100%);
    flex-direction: column;
    gap: 12px;
    padding: 0;
    border: 1px solid var(--color-border);
    border-radius: 12px;
    background: var(--color-surface-raised);
    color: var(--color-text);
    text-align: left;
  }

  .draft-warning {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    color: var(--color-attention);
  }

  /* Every notice the reader is shown has to be closable, or it sits on the
     surface for the rest of the draft with nothing to do about it. */
  .draft-warning-dismiss {
    flex: none;
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    margin-left: auto;
    padding: 0;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: inherit;
    opacity: 0.7;
    cursor: pointer;
  }

  .draft-warning-dismiss:hover {
    opacity: 1;
    background: color-mix(in srgb, var(--color-hover) 70%, transparent);
  }

  .draft-warning-dismiss:focus-visible {
    outline: 2px solid var(--color-focus-solid);
    outline-offset: 1px;
  }

  :global(.draft-control) {
    max-width: 200px;
    gap: 4px;
    padding: 0 6px;
    color: var(--color-text-2);
    font-size: 13px;
    font-weight: 400;
  }

  :global(.draft-control:hover) { color: var(--color-text); }

  :global(.draft-machine-menu) {
    width: 280px;
    max-width: calc(100vw - 24px);
    padding: 6px;
    border-radius: 6px;
    background: color-mix(in srgb, var(--color-text) 9%, var(--color-surface));
    box-shadow: 0 8px 24px rgb(0 0 0 / 25%);
    animation: none;
    --menu-row-radius: 3px;
    --menu-row-inset: 10px 12px;
    --menu-row-gap: 12px;
  }

  :global(.draft-machine-menu [data-slot="dropdown-menu-label"]) {
    padding: 8px 12px;
    color: var(--color-text-3);
    font-size: 12px;
  }

  :global(.draft-machine-menu [data-slot="dropdown-menu-separator"]) {
    margin: 5px 6px;
  }

  :global(.draft-machine-menu [data-slot="dropdown-menu-item"] > svg) {
    color: var(--color-text-2);
  }

  :global(.draft-machine-menu [data-slot="dropdown-menu-item"] > .draft-machine-selected) {
    color: var(--color-accent);
  }

  .draft-machine-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .draft-check {
    display: inline-flex;
    width: 14px;
    min-width: 14px;
    justify-content: center;
    color: var(--color-accent);
  }

</style>
