<!--
  AddActionDialog.svelte — add or change one run action.

  A run action is one saved way to start something: a name, a command, the
  folder it runs in, an optional shortcut, an optional page it serves, any
  environment variables it needs, and two choices about what should happen
  around it. This dialog is the only place any of that can be typed.

  WHAT IT DOES NOT DO
   - It never starts anything. Saving writes to storage and closes; running is
     the panel's job.
   - It never touches the terminal an action is already running in. Editing
     changes what the NEXT run does; the run going on right now keeps the
     command it was started with, because that is the command whose output is
     on screen.

  NO `$effect`: the draft is filled in by `openFor()`, which the panel calls
  when it opens the dialog, and nothing else writes to it afterwards.

  Usage:
    <AddActionDialog bind:this={dialog} bind:open={dialogOpen} />
    dialog.openFor(null, folder)          // add a new one
    dialog.openFor(row.definition, '')    // change an existing one
-->
<script lang="ts">
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Dialog from '$lib/components/ui/dialog/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { Switch } from '$lib/components/ui/switch/index.js';
  import {
    addStack,
    commandWithEnv,
    describeStackProblem,
    formatEnvLines,
    readEnvLines,
    updateStack,
    type StackDefinition
  } from '$lib/shell/stacks/stackStore.svelte';

  import KeybindingField from './KeybindingField.svelte';

  interface Props {
    /** Whether the dialog is showing. */
    open?: boolean;
    /** Told the id of whatever was just saved, so the panel can react. */
    onSaved?: (stackId: string) => void;
  }
  let { open = $bindable(false), onSaved }: Props = $props();

  /** The action being changed, or `null` while adding a new one. */
  let editing = $state<StackDefinition | null>(null);
  let draftName = $state('');
  let draftKeybinding = $state('');
  let draftScript = $state('');
  let draftPreviewUrl = $state('');
  let draftFolder = $state('');
  let draftEnv = $state('');
  let draftRunOnWorktreeCreation = $state(false);
  let draftOpenPreviewOnRun = $state(false);
  /** Set when a save was refused, so the reason sits under the fields. */
  let refusal = $state<string | null>(null);

  const env = $derived(readEnvLines(draftEnv));
  const problem = $derived(
    describeStackProblem({ name: draftName, script: draftScript, cwd: draftFolder })
  );
  const preview = $derived(commandWithEnv(draftScript, env.variables));
  const heading = $derived(editing ? 'Change this action' : 'Add action');

  /**
   * Fill the dialog in and show it. `action` is the one being changed, or
   * `null` to add a new one; `defaultFolder` is what a new one starts with.
   */
  export function openFor(action: StackDefinition | null, defaultFolder: string = ''): void {
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
    open = true;
  }

  async function save(): Promise<void> {
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
    const saved = editing ? await updateStack(editing.id, draft) : await addStack(draft);
    if (!saved) {
      // `addStack` and `updateStack` both say why they refused. Show it here
      // rather than leaving the user with a button that did nothing.
      refusal = describeStackProblem(draft) ?? 'That could not be saved.';
      return;
    }
    open = false;
    onSaved?.(saved.id);
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content
    class="flex w-[min(580px,calc(100vw-3rem))] max-h-[calc(100vh-3rem)] flex-col gap-0
           overflow-hidden rounded-lg bg-background p-0 text-foreground ring-border
           shadow-[var(--shadow-lg)] sm:max-w-none"
  >
    <Dialog.Header class="shrink-0 gap-1 border-b px-5 pt-5 pb-4">
      <Dialog.Title class="text-[14px] leading-[1.4] font-semibold">{heading}</Dialog.Title>
      <Dialog.Description class="text-sm leading-[1.5] text-muted-foreground">
        One saved way to start something. Running it opens a terminal in the folder below and runs
        the command in it.
      </Dialog.Description>
    </Dialog.Header>

    <!-- Only the fields scroll: the heading stays readable and Save stays
         reachable however short the window is. -->
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
        <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
          What it is called in the run list and on the session it opens.
        </p>
      </section>

      <section class="flex flex-col gap-2">
        <label for="run-action-keybinding" class="text-[13px] leading-[1.3] font-medium">
          Keybinding
        </label>
        <KeybindingField id="run-action-keybinding" bind:value={draftKeybinding} />
        <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
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
        <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
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
        <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
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
        <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
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
        <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
          One <span class="font-mono">NAME=value</span> per line. Leave it empty if the command
          needs none. Quotes are optional — a value with spaces in it is quoted for you.
        </p>
        {#if env.unreadable.length > 0}
          <p class="flex items-start gap-1.5 text-sm leading-[1.5] text-[var(--color-attention)]">
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
            <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
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
            <p class="text-sm leading-[1.5] text-[var(--color-text-3)]">
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
          class="rounded-md bg-secondary px-2 py-1.5 font-mono text-sm leading-[1.5] break-all
                 text-muted-foreground"
        >
          {preview || 'Type a command to see this.'}
        </code>
      </section>

      {#if problem || refusal}
        <p class="flex items-start gap-1.5 text-sm leading-[1.5] text-destructive">
          <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span>{problem ?? refusal}</span>
        </p>
      {/if}
    </div>

    <!-- `m-0` undoes the kit footer's negative margins, which assume the dialog
         keeps its own padding; this one sets `p-0` and lays the footer out itself. -->
    <Dialog.Footer class="m-0 shrink-0 gap-2 border-t px-5 py-3">
      <Button variant="ghost" size="sm" class="text-[13px]" onclick={() => (open = false)}>
        Cancel
      </Button>
      <Button size="sm" class="text-[13px]" disabled={problem !== null} onclick={save}>
        {editing ? 'Save changes' : 'Save it'}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
