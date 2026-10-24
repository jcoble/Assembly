<script lang="ts">
  /**
   * ConfirmWorktreeDialog.svelte — the question asked before anything about a
   * worktree changes.
   *
   * Two kinds of question come through here and they are deliberately the same
   * dialog: clearing git's record of a folder that is already gone, and starting
   * a session that can remove a folder that is not. The wording differs; the
   * amount of asking does not. There used to be a path that acted on the first
   * click, and it turned out to clear rows nobody had been told about.
   *
   * What the dialog says is never decided here. It arrives already written — by
   * `describeRemovalQuestion` in `worktreeManagerRows.ts` or by
   * `describeWorktreeAgentQuestion` in `worktreeAgentPrompts.ts` — which is what
   * lets the exact wording be read in a test instead of clicked through in an
   * app. The prop is structural rather than one of those two named types, so
   * both fit without either module knowing about the other.
   *
   * No `window.confirm` anywhere: that dialog does not exist in the desktop
   * webview, where it answers "no" without asking anybody.
   */
  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import { Input } from '$lib/components/ui/input/index.js';

  /** Everything this dialog draws. Both question builders satisfy it. */
  interface WorktreeQuestion {
    title: string;
    intro: string;
    lines: string[];
    confirmLabel: string;
    cancelLabel: string;
    /** The folder's own name has to be typed before the button works. */
    requiresTypedName: boolean;
    /** Drawn in the danger colour. */
    destructive: boolean;
  }

  interface Props {
    /** What is being asked, or `null` when the dialog is shut. */
    question: WorktreeQuestion | null;
    /** The folder's own name — the word that has to be typed, when one does. */
    folderName: string;
    open: boolean;
    onOpenChange(open: boolean): void;
    /** Go through with it. */
    onConfirm(): void;
  }
  let { question, folderName, open, onOpenChange, onConfirm }: Props = $props();

  /** What has been typed into the confirmation box. */
  let typed = $state('');

  const needsName = $derived(question?.requiresTypedName === true);
  /**
   * The folder name is the one thing a person cannot type by accident, so a
   * question that asks for it waits. Every other one is ready as it opens.
   */
  const ready = $derived(!needsName || (typed.trim() === folderName && folderName !== ''));

  function change(next: boolean): void {
    // Every opening starts from an empty box: a name left over from the last
    // worktree would let the next one through without being read.
    if (!next) typed = '';
    onOpenChange(next);
  }

  function confirm(): void {
    if (!ready) return;
    typed = '';
    onConfirm();
  }
</script>

<AlertDialog.Root {open} onOpenChange={change}>
  <AlertDialog.Content
    class="rounded-lg bg-background text-foreground ring-[var(--color-border)]
           shadow-[var(--shadow-lg)]"
  >
    <AlertDialog.Header>
      <AlertDialog.Title class="text-[14px] leading-[1.4] font-semibold">
        {question?.title ?? ''}
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-muted-foreground">
        {question?.intro ?? ''}
      </AlertDialog.Description>
    </AlertDialog.Header>

    <ul class="m-0 flex list-none flex-col gap-1 p-0">
      {#each question?.lines ?? [] as line, index (index)}
        <li class="flex gap-1.5 text-[13px] leading-[1.5] text-foreground">
          <span
            class={question?.destructive ? 'text-[var(--color-bad)]' : 'text-muted-foreground'}
            aria-hidden="true">•</span
          >
          <span>{line}</span>
        </li>
      {/each}
    </ul>

    {#if needsName}
      <label class="flex flex-col gap-1 text-[13px] leading-[1.5] text-muted-foreground">
        Type <span class="font-medium text-foreground">{folderName}</span> to confirm.
        <Input
          class="h-8 text-[13px]"
          autocomplete="off"
          spellcheck="false"
          placeholder={folderName}
          bind:value={typed}
        />
      </label>
    {/if}

    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">
        {question?.cancelLabel ?? 'Cancel'}
      </AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant={question?.destructive ? 'destructive' : 'default'}
        class="text-[13px]"
        disabled={!ready}
        onclick={confirm}
      >
        {question?.confirmLabel ?? 'Go ahead'}
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
