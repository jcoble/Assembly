<script lang="ts">
  import { getContext, onDestroy } from 'svelte';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import Pencil from '@lucide/svelte/icons/pencil';
  import { conversationDisclosureContext, type ConversationDisclosureContext } from '$lib/shell/conversation/conversationChatUI.ts';
  import type { ConversationDisplayItem, ConversationFileEdit } from '$lib/shell/conversation/conversationTimeline.ts';
  import { readAgentConversationToolCallFromTauri } from '$lib/tauriSource.ts';
  import FileChangeItem from './FileChangeItem.svelte';

  let { item, onFileLink }: {
    item: Extract<ConversationDisplayItem, { kind: 'fileEdits' }>;
    onFileLink?(path: string): void;
  } = $props();

  /* A turn that touched nine files is nine lines here, not nine cards. The
     group opens onto the list; a file in that list opens onto its own diff. */
  let open = $state(false);
  let openPaths = $state(new Set<string>());

  const heading = $derived(item.edits.length === 1 ? 'Edited file' : 'Edited files');
  /* The list names the file, not the route to it — the whole path stays on the
     row's title, the same rule the single file-change row already follows. */
  const nameOf = (path: string): string => path.split('/').filter(Boolean).at(-1) ?? path;

  /* An edit from a history page has no diff: an opened file reads it from the backend, and closing
     the file lets it go. */
  const disclosure = getContext<ConversationDisclosureContext>(conversationDisclosureContext);
  const life = new AbortController();
  onDestroy(() => life.abort());
  let diffs = $state<Record<string, string>>({});

  async function readDiff(itemId: string): Promise<void> {
    let diff = '';
    try { diff = (await readAgentConversationToolCallFromTauri(disclosure?.ownedId ?? '', itemId, life.signal))?.diff ?? ''; } catch { /* shown as not available */ }
    if (!life.signal.aborted && openPaths.has(itemId)) diffs = { ...diffs, [itemId]: diff };
  }

  function toggleFile(edit: ConversationFileEdit): void {
    const next = new Set(openPaths);
    if (next.has(edit.itemId)) {
      next.delete(edit.itemId);
      const { [edit.itemId]: _closed, ...kept } = diffs;
      diffs = kept;
    } else {
      next.add(edit.itemId);
    }
    openPaths = next;
  }

  /* An opened file with no inline diff reads it, including one opened while its edit ran that
     lost the inline diff when the edit finished. */
  $effect(() => {
    for (const edit of item.edits) {
      if (openPaths.has(edit.itemId) && edit.diff === undefined && diffs[edit.itemId] === undefined) void readDiff(edit.itemId);
    }
  });

  /** The diff row this group folded away, in the shape that draws it. */
  function fileDisplayItem(edit: ConversationFileEdit, diff: string): Extract<ConversationDisplayItem, { kind: 'file' }> {
    return {
      kind: 'file',
      itemId: edit.itemId,
      text: diff,
      timestampMs: item.timestampMs,
      completed: true,
      metadata: { path: edit.path, diff }
    };
  }
</script>

<section class="file-edits" data-testid="timeline-file-edits">
  <button
    class="group-head"
    class:open
    type="button"
    aria-expanded={open}
    onclick={() => (open = !open)}
  >
    <Pencil size={13} strokeWidth={1.75} aria-hidden="true" />
    <span class="heading">{heading}</span>
    <span class="count">{item.edits.length}</span>
    <ChevronRight class="chevron" size={13} strokeWidth={2} aria-hidden="true" />
  </button>

  {#if open}
    <ul class="files">
      {#each item.edits as edit (edit.itemId)}
        <li>
          <div class="file-row">
            <button
              class="file-open"
              type="button"
              title={edit.path}
              aria-expanded={openPaths.has(edit.itemId)}
              onclick={() => toggleFile(edit)}
            >
              <ChevronRight
                class="chevron"
                size={12}
                strokeWidth={2}
                aria-hidden="true"
              />
              <span class="name">{nameOf(edit.path)}</span>
            </button>
            <button class="jump" type="button" onclick={() => onFileLink?.(edit.path)}>
              open
            </button>
            <span class="counts">
              {#if edit.added}<span class="added">+{edit.added}</span>{/if}
              {#if edit.removed}<span class="removed">−{edit.removed}</span>{/if}
            </span>
          </div>
          {#if openPaths.has(edit.itemId)}
            {@const diff = edit.diff ?? diffs[edit.itemId]}
            <div class="diff">
              {#if diff}<FileChangeItem item={fileDisplayItem(edit, diff)} {onFileLink} />
              {:else if diff === ''}<span>Details are not available</span>{/if}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .file-edits {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .group-head,
  .file-open,
  .jump {
    background: none;
    border: none;
    padding: 0;
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  .group-head {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 32px;
    padding: 4px 8px;
    border-radius: 10px;
    color: var(--color-text-2);
    font-size: 13px;
    line-height: 22px;
  }

  .group-head:hover {
    background: color-mix(in srgb, var(--color-hover) 55%, transparent);
    color: var(--color-text);
  }

  .heading {
    font-weight: 500;
  }

  .count {
    color: var(--color-text-3);
    font-variant-numeric: tabular-nums;
  }

  /* The chevron turns to say the group is open. Transform only, so the turn
     costs the compositor a frame and nothing after it. */
  :global(.file-edits .chevron) {
    transition: transform 140ms ease;
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.file-edits .chevron) {
      transition: none;
    }
  }

  .group-head.open :global(.chevron),
  .file-open[aria-expanded='true'] :global(.chevron) {
    transform: rotate(90deg);
  }

  .files {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin: 4px 0 0;
    padding: 0 0 0 8px;
    list-style: none;
    border-left: 1px solid color-mix(in srgb, var(--color-border) 60%, transparent);
    max-height: 240px;
    overflow-y: auto;
    overflow-x: hidden;
    scrollbar-width: thin;
    scrollbar-color: var(--scrollbar-thumb) transparent;
  }

  .file-row {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 30px;
    padding: 4px 8px;
    font-size: 13px;
    line-height: 20px;
  }

  .file-open {
    display: flex;
    align-items: center;
    gap: 4px;
    min-width: 0;
    color: var(--color-text-2);
  }

  .file-open:hover {
    color: var(--color-text);
  }

  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .jump {
    color: var(--color-text-3);
    opacity: 0;
    transition: opacity 120ms ease;
  }

  .file-row:hover .jump,
  .jump:focus-visible {
    opacity: 1;
  }

  .counts {
    display: flex;
    gap: 6px;
    margin-left: auto;
    font-variant-numeric: tabular-nums;
  }

  .added {
    color: var(--color-good);
  }

  .removed {
    color: var(--color-bad);
  }

  .diff {
    padding: 4px 0 6px 12px;
  }
</style>
