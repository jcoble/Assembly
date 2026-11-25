<!--
  AddActionDialog.svelte — add or change one run action.

  1:1 port from mac-command-bar:
   - Never starts anything; saving persists via `.NET` preferences and closes.
   - Never touches an already running terminal; edits apply to the next run.
   - Uses `$lib/ui/dialog` with `data-occludes-chat` so native GPUIKit Chat never bleeds through.
-->
<script lang="ts">
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

  import { Button } from '$lib/ui/button/index.js';
  import * as Dialog from '$lib/ui/dialog/index.js';
  import { Input } from '$lib/ui/input/index.js';
  import { Switch } from '$lib/ui/switch/index.js';

  import KeybindingField from './KeybindingField.svelte';
  import {
    commandWithEnv,
    describeRunProblem,
    formatEnvLines,
    readEnvLines
  } from './model';
  import { runStore } from './store.svelte';
  import type { RunDefinition } from './types';

  interface Props {
    /** Whether the dialog is showing. */
    open?: boolean;
    /** Notified with the saved action id so the panel can react. */
    onSaved?: (actionId: string) => void;
  }
  let { open = $bindable(false), onSaved }: Props = $props();

  let editing = $state<RunDefinition | null>(null);
  let draftName = $state('');
  let draftKeybinding = $state('');
  let draftScript = $state('');
  let draftPreviewUrl = $state('');
  let draftFolder = $state('');
  let draftEnv = $state('');
  let draftRunOnWorktreeCreation = $state(false);
  let draftOpenPreviewOnRun = $state(false);
  let refusal = $state<string | null>(null);
  let saving = $state(false);

  const env = $derived(readEnvLines(draftEnv));
  const problem = $derived(
    describeRunProblem({ name: draftName, script: draftScript, cwd: draftFolder })
  );
  const preview = $derived(commandWithEnv(draftScript, env.variables));
  const heading = $derived(editing ? 'Change this action' : 'Add action');

  /**
   * Populate the form and open the dialog. `action` is the configuration being edited,
   * or `null` to create a new one; `defaultFolder` seeds Folder for new actions.
   */
  export function openFor(action: RunDefinition | null, defaultFolder = ''): void {
    editing = action;
    draftName = action?.name ?? '';
    draftKeybinding = action?.keybinding ?? '';
    draftScript = action?.script ?? '';
    draftPreviewUrl = action?.previewUrl ?? '';
    draftFolder = action?.cwd ?? defaultFolder;
    draftEnv = formatEnvLines(action?.env);
    draftRunOnWorktreeCreation = action?.runOnWorktreeCreation === true;
    draftOpenPreviewOnRun = action?.openPreviewOnRun === true;
    refusal = null;
    saving = false;
    open = true;
  }

  async function save(): Promise<void> {
    if (saving) return;
    const draft = {
      name: draftName,
      script: draftScript,
      cwd: draftFolder,
      env: env.variables,
      keybinding: draftKeybinding,
      previewUrl: draftPreviewUrl,
      runOnWorktreeCreation: draftRunOnWorktreeCreation,
      openPreviewOnRun: draftOpenPreviewOnRun
    };
    saving = true;
    try {
      const saved = editing
        ? await runStore.update(editing.id, draft)
        : await runStore.add(draft);
      if (!saved) {
        refusal = describeRunProblem(draft) ?? runStore.notice ?? 'That could not be saved.';
        return;
      }
      open = false;
      onSaved?.(saved.id);
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content
    data-occludes-chat=""
    class="flex w-[min(580px,calc(100vw-3rem))] max-h-[calc(100vh-3rem)] flex-col gap-0
           overflow-hidden rounded-lg bg-background p-0 text-foreground ring-border
           shadow-[var(--shadow-lg)] sm:max-w-none"
  >
    <Dialog.Header class="shrink-0 gap-1 border-b px-5 pt-5 pb-4">
      <Dialog.Title class="text-[14px] leading-[1.4] font-semibold">{heading}</Dialog.Title>
      <Dialog.Description class="text-xs leading-[1.5] text-muted-foreground">
        One saved way to start something. Running it opens a terminal in the folder below and runs
        the command in it.
      </Dialog.Description>
    </Dialog.Header>

    <div class="flex min-h-0 flex-1 flex-col gap-5 overflow-y-auto px-5 py-4">
      <section class="flex flex-col gap-2">
        <label for="run-action-name" class="text-[13px] leading-[1.3] font-medium">Name</label>
        <Input
          id="run-action-name"
          bind:value={draftName}
          placeholder="Dev server"
          autocomplete="off"
          spellcheck={false}
          class="h-8 text-[13px]"
        />
        <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
          What it is called in the run list and on the session it opens.
        </p>
      </section>

      <section class="flex flex-col gap-2">
        <label for="run-action-keybinding" class="text-[13px] leading-[1.3] font-medium">
          Keybinding
        </label>
        <KeybindingField id="run-action-keybinding" bind:value={draftKeybinding} />
        <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
          Optional. The combination runs this action from anywhere in the app, except while you are
          typing in a box.
        </p>
      </section>

      <section class="flex flex-col gap-2">
        <label for="run-action-command" class="text-[13px] leading-[1.3] font-medium">Command</label>
        <Input
          id="run-action-command"
          bind:value={draftScript}
          placeholder="pnpm dev"
          autocomplete="off"
          spellcheck={false}
          class="h-8 font-mono text-[13px]"
        />
        <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
          Exactly what you would type in a terminal.
        </p>
      </section>

      <section class="flex flex-col gap-2">
        <label for="run-action-preview" class="text-[13px] leading-[1.3] font-medium">
          Preview URL
        </label>
        <Input
          id="run-action-preview"
          bind:value={draftPreviewUrl}
          placeholder="http://localhost:5173"
          autocomplete="off"
          spellcheck={false}
          class="h-8 font-mono text-[13px]"
        />
        <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
          Optional. The page this action serves, opened in the Browser panel from the run list.
        </p>
      </section>

      <section class="flex flex-col gap-2">
        <label for="run-action-folder" class="text-[13px] leading-[1.3] font-medium">Folder</label>
        <Input
          id="run-action-folder"
          bind:value={draftFolder}
          placeholder="/Users/you/dev/some-project"
          autocomplete="off"
          spellcheck={false}
          class="h-8 font-mono text-[13px]"
        />
        <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
          The command runs here. This starts as the project folder; point it at a folder inside the
          project when the command belongs to one part of it.
        </p>
      </section>

      <section class="flex flex-col gap-2">
        <label for="run-action-env" class="text-[13px] leading-[1.3] font-medium">
          Environment variables
        </label>
        <textarea
          id="run-action-env"
          bind:value={draftEnv}
          rows="4"
          autocomplete="off"
          spellcheck="false"
          placeholder={'NODE_ENV=development\nPORT=5173'}
          class="w-full resize-y rounded-md border border-input bg-background px-2 py-1.5
                 font-mono text-[13px] leading-[1.5] text-foreground outline-none
                 transition-colors focus-visible:border-(color:--focus-border)"
        ></textarea>
        <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
          One <span class="font-mono">NAME=value</span> per line. Leave it empty if the command
          needs none. Quotes are optional — a value with spaces in it is quoted for you.
        </p>
        {#if env.unreadable.length > 0}
          <p class="flex items-start gap-1.5 text-xs leading-[1.5] text-[var(--color-attention)]">
            <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span>
              {env.unreadable.length === 1 ? 'This line is' : 'These lines are'} not in
              <span class="font-mono">NAME=value</span> form and will not be used:
              <span class="font-mono">{env.unreadable.join(' · ')}</span>
            </span>
          </p>
        {/if}
      </section>

      <section class="flex flex-col gap-3 rounded-xl border px-3 py-3">
        <div class="flex items-start gap-3">
          <div class="flex min-w-0 flex-1 flex-col gap-0.5">
            <label
              for="run-action-on-worktree"
              class="text-[13px] leading-[1.3] font-medium"
            >
              Run automatically on worktree creation
            </label>
            <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
              Saved now, but nothing creates worktrees yet — this takes effect once worktree
              creation lands.
            </p>
          </div>
          <Switch id="run-action-on-worktree" bind:checked={draftRunOnWorktreeCreation} />
        </div>

        <div class="flex items-start gap-3 border-t pt-3">
          <div class="flex min-w-0 flex-1 flex-col gap-0.5">
            <label for="run-action-open-preview" class="text-[13px] leading-[1.3] font-medium">
              Open preview automatically when this action runs
            </label>
            <p class="text-xs leading-[1.5] text-[var(--color-text-3)]">
              Shows the preview URL in the Browser panel as soon as the command starts. Needs a
              preview URL above.
            </p>
          </div>
          <Switch
            id="run-action-open-preview"
            bind:checked={draftOpenPreviewOnRun}
            disabled={draftPreviewUrl.trim() === ''}
          />
        </div>
      </section>

      <section class="flex flex-col gap-1.5">
        <span class="text-[13px] leading-[1.3] font-medium">What will run</span>
        <code
          class="rounded-md bg-secondary px-2 py-1.5 font-mono text-xs leading-[1.5] break-all
                 text-muted-foreground"
        >
          {preview || 'Type a command to see this.'}
        </code>
      </section>

      {#if problem || refusal}
        <p class="flex items-start gap-1.5 text-xs leading-[1.5] text-destructive">
          <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span>{problem ?? refusal}</span>
        </p>
      {/if}
    </div>

    <Dialog.Footer class="m-0 shrink-0 gap-2 border-t px-5 py-3">
      <Button variant="ghost" size="sm" class="text-[13px]" onclick={() => (open = false)}>
        Cancel
      </Button>
      <Button
        size="sm"
        class="text-[13px]"
        disabled={problem !== null || saving}
        onclick={() => void save()}
      >
        {editing ? 'Save changes' : 'Save it'}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
