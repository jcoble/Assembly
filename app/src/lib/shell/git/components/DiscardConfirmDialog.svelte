<script lang="ts">
  /**
   * DiscardConfirmDialog.svelte — the question asked before any change is
   * thrown away, and the only route to `gitService.discardPaths` /
   * `gitService.discardAll` anywhere in the shell.
   *
   * What it says is not decided here. `describeDiscardQuestion` in
   * `discardConfirm.ts` works out the title, the sentences and the button
   * words, which is what lets the exact wording be read in a test rather than
   * clicked through in an app.
   *
   * No `window.confirm`: that dialog does not exist in the desktop webview,
   * where it answers "no" without asking anybody.
   */
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import type { DiscardQuestion } from './discardConfirm';

  interface Props {
    /** What is being asked, or `null` when the dialog is shut. */
    question: DiscardQuestion | null;
    open: boolean;
    onOpenChange(open: boolean): void;
    /** Go through with it. */
    onConfirm(): void;
  }
  let { question, open, onOpenChange, onConfirm }: Props = $props();
</script>

<AlertDialog.Root {open} onOpenChange={onOpenChange}>
  <AlertDialog.Content
    class="rounded-lg bg-background text-foreground ring-[var(--color-border)]
           shadow-[var(--shadow-lg)]"
    data-testid="discard-confirm"
  >
    <AlertDialog.Header>
      <AlertDialog.Title
        class="flex items-center gap-1.5 text-[14px] leading-[1.4] font-semibold"
      >
        <TriangleAlert class="size-4 shrink-0 text-[var(--color-bad)]" aria-hidden="true" />
        {question?.title ?? ''}
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-[var(--color-text-2)]">
        {question?.intro ?? ''}
      </AlertDialog.Description>
    </AlertDialog.Header>

    <ul class="m-0 flex list-none flex-col gap-1 p-0">
      {#each question?.lines ?? [] as line, index (index)}
        <li class="flex gap-1.5 text-[13px] leading-[1.5] text-[var(--color-text)]">
          <span class="text-[var(--color-bad)]" aria-hidden="true">•</span>
          <span class="min-w-0 break-words">{line}</span>
        </li>
      {/each}
    </ul>

    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">
        {question?.cancelLabel ?? 'Keep my changes'}
      </AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant="destructive"
        class="text-[13px]"
        onclick={onConfirm}
        data-testid="discard-confirm-go"
      >
        {question?.confirmLabel ?? 'Discard changes'}
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
