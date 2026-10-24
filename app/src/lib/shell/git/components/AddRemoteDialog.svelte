<!--
  AddRemoteDialog.svelte — "Add remote…" in the Source Control more-actions menu.

  Asks for a remote name and URL and hands them to `onAdd`. Success closes the
  dialog; a failure keeps it open with the error and the typed values so they
  can be fixed and tried again. Each opening starts from `origin` and an empty URL.
-->
<script lang="ts">
  import { Button } from '$lib/components/ui/button/index.js';
  import * as Dialog from '$lib/components/ui/dialog/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { Label } from '$lib/components/ui/label/index.js';

  interface Props {
    open: boolean;
    /** Adds the remote; resolves to null on success or the sentence saying why it failed. */
    onAdd(name: string, url: string): Promise<string | null>;
  }

  let { open = $bindable(), onAdd }: Props = $props();

  let name = $state('origin');
  let url = $state('');
  let error = $state('');
  let adding = $state(false);

  const canAdd = $derived(name.trim() !== '' && url.trim() !== '' && !adding);

  $effect(() => {
    if (!open) return;
    name = 'origin';
    url = '';
    error = '';
  });

  async function submit(): Promise<void> {
    if (!canAdd) return;
    adding = true;
    error = '';
    try {
      const failure = await onAdd(name.trim(), url.trim());
      if (failure === null) open = false;
      else error = failure;
    } catch (caught) {
      error = caught instanceof Error ? caught.message : String(caught);
    } finally {
      adding = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content
    class="sm:max-w-[440px]"
    showCloseButton={!adding}
    escapeKeydownBehavior={adding ? 'ignore' : 'close'}
    interactOutsideBehavior={adding ? 'ignore' : 'close'}
    data-testid="add-remote-dialog"
  >
    <Dialog.Header>
      <Dialog.Title>Add remote</Dialog.Title>
    </Dialog.Header>
    <form class="flex flex-col gap-3" onsubmit={(event) => { event.preventDefault(); void submit(); }}>
      <div class="flex flex-col gap-1.5">
        <Label for="add-remote-name">Name</Label>
        <Input id="add-remote-name" bind:value={name} disabled={adding} autocomplete="off" spellcheck={false} />
      </div>
      <div class="flex flex-col gap-1.5">
        <Label for="add-remote-url">URL</Label>
        <Input
          id="add-remote-url"
          bind:value={url}
          placeholder="git@github.com:owner/repo.git or a path"
          disabled={adding}
          autocomplete="off"
          spellcheck={false}
        />
      </div>
      {#if error}<p class="text-[13px] text-destructive" data-testid="add-remote-error">{error}</p>{/if}
      <Dialog.Footer>
        <Button type="button" variant="ghost" size="sm" disabled={adding} onclick={() => (open = false)}>Cancel</Button>
        <Button type="submit" size="sm" disabled={!canAdd}>{adding ? 'Adding…' : 'Add'}</Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
