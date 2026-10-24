<!-- The owner's notes on a card or a live row: one quiet line each, and a field to add one. -->
<script lang="ts">
  import Pencil from '@lucide/svelte/icons/pencil';

  import { Input } from '$lib/components/ui/input/index.js';
  import { addBoardNote, type BoardNote } from './boardSource.ts';

  interface Props {
    notes: BoardNote[];
    /** The machine that owns the item; the note is stored there. */
    machine: string;
    /** `card:<key>`, `session:<owned id>`, `run:<run id>` or `step:<run id>/<step id>`. */
    target: string;
    /** Aborted when the board closes. */
    signal: AbortSignal;
    /** Shows the field. */
    adding: boolean;
    onError(text: string): void;
    /** Called after a save or Escape, so a live row can hide its field again. */
    onDone?(): void;
  }
  let { notes, machine, target, signal, adding, onError, onDone }: Props = $props();

  let body = $state('');
  let saving = $state(false);

  const time = (ms: number): string => new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

  async function save(): Promise<void> {
    const text = body.trim();
    if (!text) return;
    saving = true;
    onError('');
    try {
      // A successful note fires the change signal, which reloads the board with it.
      await addBoardNote(machine, target, text, Date.now(), signal);
      if (signal.aborted) return;
      body = '';
      onDone?.();
    } catch (cause) {
      if (!signal.aborted) onError(`Could not add the note: ${cause instanceof Error ? cause.message : String(cause)}`);
    }
    if (!signal.aborted) saving = false;
  }

  function keydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') void save();
    if (event.key === 'Escape' && onDone) {
      // Escape hides the field instead of closing the whole board.
      event.stopPropagation();
      body = '';
      onDone();
    }
  }
</script>

{#each notes as note (note.id)}
  <p class="flex min-w-0 gap-1.5 px-0.5 text-xs leading-[1.45] text-(--color-text-2)">
    <Pencil class="mt-0.5 size-3 flex-none text-(--color-text-3)" aria-hidden="true" />
    <span class="min-w-0"><span class="text-(--color-text-3)">You · {time(note.created_at_ms)}</span> <span class="text-(--color-text)">{note.body}</span></span>
  </p>
{/each}
{#if adding}
  <!-- svelte-ignore a11y_autofocus -->
  <Input
    bind:value={body}
    maxlength={280}
    placeholder="Add a note…"
    aria-label="Add a note"
    disabled={saving}
    autofocus={Boolean(onDone)}
    onkeydown={keydown}
    class="h-[30px] rounded-(--radius-sm) border-0 bg-[color-mix(in_srgb,var(--color-bg)_44%,transparent)] text-[13px] dark:bg-[color-mix(in_srgb,var(--color-bg)_44%,transparent)]"
  />
{/if}
