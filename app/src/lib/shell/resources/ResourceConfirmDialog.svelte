<!--
  ResourceConfirmDialog.svelte — the question in front of every destructive
  action in the Resource Manager.

  Both things this panel can do are irreversible: signalling a process ends
  running work, and removing a folder removes it. So both come through one
  dialog, and what it says is decided by a plain function — `describeStopQuestion`
  or `describeReclaimQuestion` — which is what lets the exact wording, including
  the process ids and the folder path, be read in a test rather than clicked
  through in an app.

  No `window.confirm` anywhere: that dialog does not exist in the desktop
  webview, where it answers "no" without asking anybody.
-->
<script lang="ts">
  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';

  interface Props {
    /** What is being asked, or `null` when the dialog is shut. */
    question: {
      title: string;
      intro: string;
      lines: string[];
      confirmLabel: string;
      cancelLabel: string;
    } | null;
    open: boolean;
    busy?: boolean;
    onCancel(): void;
    onConfirm(): void;
  }

  let { question, open, busy = false, onCancel, onConfirm }: Props = $props();

  function change(next: boolean): void {
    if (!next) onCancel();
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
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-[var(--color-text-2)]">
        {question?.intro ?? ''}
      </AlertDialog.Description>
    </AlertDialog.Header>

    <ul class="m-0 flex list-none flex-col gap-1 p-0">
      {#each question?.lines ?? [] as line, index (index)}
        <li class="flex gap-1.5 text-[13px] leading-[1.5] break-all text-[var(--color-text)]">
          <span class="text-[var(--color-bad)]" aria-hidden="true">•</span>
          <span>{line}</span>
        </li>
      {/each}
    </ul>

    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">
        {question?.cancelLabel ?? 'Cancel'}
      </AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant="destructive"
        class="text-[13px]"
        disabled={busy}
        onclick={onConfirm}
      >
        {busy ? 'Working…' : (question?.confirmLabel ?? 'Go ahead')}
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
