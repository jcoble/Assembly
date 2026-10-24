<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import ConversationMessage from '$lib/shell/conversation/components/ConversationMessage.svelte';
  import ArrowLeft from '@lucide/svelte/icons/arrow-left';
  import Check from '@lucide/svelte/icons/check';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import ImagePlus from '@lucide/svelte/icons/image-plus';
  import Pencil from '@lucide/svelte/icons/pencil';
  import Plus from '@lucide/svelte/icons/plus';
  import Square from '@lucide/svelte/icons/square';
  import { onDestroy, onMount } from 'svelte';

  import { Button } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import {
    appendNotionTaskImage,
    appendNotionTaskText,
    readNotionTaskDetail,
    updateNotionBlock,
    updateNotionTaskProperty,
    type NotionBlock,
    type NotionTaskDetail,
    type NotionTaskProperty,
    updateNotionTaskStatus
  } from '$lib/shell/notionTaskDetail.ts';
  import {
    splitTaskTitle,
    taskPriorityTone,
    taskStatusTone,
    type NotionTaskRow
  } from '$lib/shell/notionTasks.ts';
  import { openUrlInBrowser } from '$lib/shell/workbenchNavigation.ts';

  let { task, statuses, priorities, onBack, onOpenExternal, onFileLink, onTaskUpdated }: {
    task: NotionTaskRow;
    statuses: string[];
    priorities: string[];
    onBack(): void;
    onOpenExternal(url: string): void;
    onFileLink(path: string): void;
    /** The task's list row changed (status, priority or title); the list reloads. */
    onTaskUpdated(changes: Partial<NotionTaskRow>): Promise<void>;
  } = $props();

  let detail = $state<NotionTaskDetail | null>(null);
  let loading = $state(true);
  let error = $state('');
  let status = $state('');
  let savingStatus = $state(false);
  const title = $derived(splitTaskTitle(task.title));
  const statusOptions = $derived([...new Set([task.status || 'Unspecified', ...statuses].filter(Boolean))]);
  const priorityOptions = $derived([...new Set([task.priority ?? '', ...priorities].filter(Boolean))]);
  /** Block types whose text can be edited here; the backend keeps the same list. */
  const EDITABLE_BLOCKS = new Set([
    'paragraph', 'heading_1', 'heading_2', 'heading_3', 'bulleted_list_item',
    'numbered_list_item', 'to_do', 'quote', 'callout', 'toggle'
  ]);
  let saving = $state(false);
  let editingBlockId = $state<string | null>(null);
  let adding = $state(false);
  let draft = $state('');
  let imageInput = $state<HTMLInputElement | null>(null);
  const controller = new AbortController();

  async function load(): Promise<void> {
    try {
      detail = await readNotionTaskDetail(task.sourceTaskId, controller.signal);
    } catch (cause) {
      if (!controller.signal.aborted) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (!controller.signal.aborted) loading = false;
    }
  }

  async function changeStatus(nextStatus: string): Promise<void> {
    if (!nextStatus || nextStatus === status || savingStatus) return;
    const previousStatus = status;
    status = nextStatus;
    savingStatus = true;
    error = '';
    try {
      await updateNotionTaskStatus(task.sourceTaskId, nextStatus, controller.signal);
      if (controller.signal.aborted) return;
      await onTaskUpdated({ status: nextStatus });
    } catch (cause) {
      if (!controller.signal.aborted) {
        status = previousStatus;
        error = cause instanceof Error ? cause.message : String(cause);
      }
    } finally {
      if (!controller.signal.aborted) savingStatus = false;
    }
  }

  /** Run one edit, then read the page again so it shows what Notion saved. */
  async function save(edit: () => Promise<void>, changes?: Partial<NotionTaskRow>): Promise<boolean> {
    if (saving) return false;
    saving = true;
    error = '';
    try {
      await edit();
      if (controller.signal.aborted) return false;
      if (changes) await onTaskUpdated(changes);
      if (controller.signal.aborted) return false;
      detail = await readNotionTaskDetail(task.sourceTaskId, controller.signal);
      return true;
    } catch (cause) {
      if (!controller.signal.aborted) error = cause instanceof Error ? cause.message : String(cause);
      return false;
    } finally {
      if (!controller.signal.aborted) saving = false;
    }
  }

  async function saveTitle(node: HTMLElement): Promise<void> {
    const text = (node.textContent ?? '').replace(/\s+/g, ' ').trim();
    if (!text || text === title.text) {
      node.textContent = title.text;
      return;
    }
    const full = title.key ? `[${title.key}] ${text}` : text;
    const saved = await save(() => updateNotionTaskProperty(task.sourceTaskId, 'title', full, controller.signal), { title: full });
    if (!saved && !controller.signal.aborted) node.textContent = title.text;
  }

  function titleKeydown(event: KeyboardEvent & { currentTarget: HTMLElement }): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      event.currentTarget.blur();
    } else if (event.key === 'Escape') {
      event.currentTarget.textContent = title.text;
      event.currentTarget.blur();
    }
  }

  function startEditing(block: NotionBlock): void {
    adding = false;
    editingBlockId = block.id;
    draft = block.markdown;
  }

  async function saveBlock(block: NotionBlock): Promise<void> {
    if (draft === block.markdown) {
      editingBlockId = null;
      return;
    }
    if (await save(() => updateNotionBlock(block, { markdown: draft }, controller.signal))) editingBlockId = null;
  }

  async function addText(): Promise<void> {
    if (!draft.trim()) {
      adding = false;
      return;
    }
    if (await save(() => appendNotionTaskText(task.sourceTaskId, draft, controller.signal))) adding = false;
  }

  function addImage(file: File | undefined): void {
    if (file?.type.startsWith('image/')) void save(() => appendNotionTaskImage(task.sourceTaskId, file, controller.signal));
  }

  /** A screenshot pasted anywhere on the page (outside a text box) is added to the end. */
  function pasteImage(event: ClipboardEvent): void {
    const file = [...(event.clipboardData?.files ?? [])].find((candidate) => candidate.type.startsWith('image/'));
    if (!file) return;
    event.preventDefault();
    addImage(file);
  }

  function focusOnMount(node: HTMLTextAreaElement): void {
    node.focus();
  }

  /** Dates read in the reader's locale; a date with no time is not shifted by time zone. */
  function formatDate(value: string): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return value;
    return value.includes('T')
      ? date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
      : date.toLocaleDateString(undefined, { dateStyle: 'medium', timeZone: 'UTC' });
  }

  /** A numbered item counts up from the first numbered item directly above it. */
  function listNumber(siblings: NotionBlock[], index: number): number {
    let start = index;
    while (start > 0 && siblings[start - 1].kind === 'numbered_list_item') start -= 1;
    return index - start + 1;
  }

  function headingMarkdown(block: NotionBlock): string {
    const level = { heading_1: 1, heading_2: 2, heading_3: 3 }[block.kind];
    return level ? `${'#'.repeat(level)} ${block.markdown}` : block.markdown;
  }

  onMount(() => {
    status = task.status || 'Unspecified';
    void load();
  });
  onDestroy(() => {
    controller.abort();
    detail = null;
  });
</script>

{#snippet markdown(text: string)}
  <ConversationMessage {text} role="assistant" showImages {onFileLink} />
{/snippet}

{#snippet editor(onSave: () => void, onCancel: () => void)}
  <div class="grid gap-2">
    <textarea
      bind:value={draft}
      use:focusOnMount
      rows={Math.min(12, draft.split('\n').length + 1)}
      class="w-full resize-y rounded-(--radius-md) border border-border bg-background px-2 py-1.5 font-mono text-[13px] text-foreground"
      aria-label="Block text (Markdown)"
      onkeydown={(event) => {
        if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
          event.preventDefault();
          onSave();
        } else if (event.key === 'Escape') onCancel();
      }}
    ></textarea>
    <div class="flex items-center gap-2">
      <Button size="sm" onclick={onSave} disabled={saving}>{#if saving}<WorkingSpinner size={12} />{/if}Save</Button>
      <Button size="sm" variant="ghost" onclick={onCancel} disabled={saving}>Cancel</Button>
      <span class="text-[12px] text-muted-foreground">⌘↵ to save · Esc to cancel</span>
    </div>
  </div>
{/snippet}

{#snippet blockText(block: NotionBlock, text: string)}
  {#if editingBlockId === block.id}
    {@render editor(() => void saveBlock(block), () => (editingBlockId = null))}
  {:else}
    <div class="group/text relative min-w-0">
      {@render markdown(text)}
      {#if EDITABLE_BLOCKS.has(block.kind)}
        <button
          type="button"
          class="absolute top-0 right-0 rounded-(--radius-sm) bg-background p-1 text-muted-foreground opacity-0 group-hover/text:opacity-100 focus-visible:opacity-100 hover:text-foreground"
          aria-label="Edit this block"
          onclick={() => startEditing(block)}
          disabled={saving}
        ><Pencil class="size-3.5" /></button>
      {/if}
    </div>
  {/if}
{/snippet}

{#snippet blockList(blocks: NotionBlock[])}
  {#each blocks as block, index (block.id)}
    {@render blockView(block, blocks, index)}
  {/each}
{/snippet}

{#snippet blockView(block: NotionBlock, siblings: NotionBlock[], index: number)}
  {#if block.kind === 'toggle'}
    <details class="task-block toggle">
      <summary class="flex cursor-pointer list-none items-start gap-1">
        <ChevronRight class="chevron mt-[3px] size-4 shrink-0 text-muted-foreground" />
        <div class="min-w-0 flex-1">{@render blockText(block, block.markdown)}</div>
      </summary>
      {#if block.children.length > 0}<div class="pl-5">{@render blockList(block.children)}</div>{/if}
    </details>
  {:else if block.kind === 'callout'}
    <div class="task-block flex gap-3 rounded-(--radius-md) border border-border bg-card px-4 py-3">
      {#if block.icon}<span class="shrink-0 text-base leading-6" aria-hidden="true">{block.icon}</span>{/if}
      <div class="min-w-0 flex-1">
        {@render blockText(block, block.markdown)}
        {@render blockList(block.children)}
      </div>
    </div>
  {:else if block.kind === 'bulleted_list_item' || block.kind === 'numbered_list_item' || block.kind === 'to_do'}
    <div class="task-block grid grid-cols-[1.5rem_minmax(0,1fr)]">
      {#if block.kind === 'to_do'}
        <button
          type="button"
          class="flex h-6 items-center justify-center text-muted-foreground hover:text-foreground"
          aria-label={block.checked ? 'Mark not done' : 'Mark done'}
          disabled={saving}
          onclick={() => void save(() => updateNotionBlock(block, { checked: !block.checked }, controller.signal))}
        >
          {#if block.checked}<Check class="size-4 text-primary" />{:else}<Square class="size-4" />{/if}
        </button>
      {:else}
        <span class="flex justify-center leading-6 text-muted-foreground" aria-hidden="true">
          {#if block.kind === 'bulleted_list_item'}•{:else}{listNumber(siblings, index)}.{/if}
        </span>
      {/if}
      <div class="min-w-0" class:checked-off={block.kind === 'to_do' && block.checked}>
        {@render blockText(block, block.markdown)}
        {@render blockList(block.children)}
      </div>
    </div>
  {:else if block.kind === 'quote'}
    <blockquote class="task-block border-l-2 border-border pl-4">
      {@render blockText(block, block.markdown)}
      {@render blockList(block.children)}
    </blockquote>
  {:else if block.kind === 'divider'}
    <hr class="task-block my-2 border-border" />
  {:else if !block.markdown.trim()}
    <div class="task-block h-6" aria-hidden="true"></div>
  {:else}
    <div class="task-block" class:pt-3={block.kind.startsWith('heading_')}>
      {@render blockText(block, headingMarkdown(block))}
      {#if block.children.length > 0}<div class="pl-6">{@render blockList(block.children)}</div>{/if}
    </div>
  {/if}
{/snippet}

{#snippet propertyValue(property: NotionTaskProperty)}
  {#if property.name === 'Status' && (property.kind === 'status' || property.kind === 'select')}
    <Select.Root type="single" value={status} onValueChange={(value) => void changeStatus(value)} disabled={savingStatus}>
      <Select.Trigger size="sm" class="h-auto border-0 bg-transparent p-0 hover:bg-transparent dark:bg-transparent" aria-label="Change task status">
        {#if savingStatus}<WorkingSpinner size={12} />Saving…{:else}<Chip tone={taskStatusTone(status)}>{status}</Chip>{/if}
      </Select.Trigger>
      <Select.Content>
        {#each statusOptions as option}<Select.Item value={option} label={option} />{/each}
      </Select.Content>
    </Select.Root>
  {:else if property.name === 'Priority' && property.kind === 'select'}
    <Select.Root type="single" value={task.priority ?? ''} onValueChange={(value) => void save(() => updateNotionTaskProperty(task.sourceTaskId, 'Priority', value, controller.signal), { priority: value })} disabled={saving}>
      <Select.Trigger size="sm" class="h-auto border-0 bg-transparent p-0 hover:bg-transparent dark:bg-transparent" aria-label="Change task priority">
        {#if task.priority}<Chip tone={taskPriorityTone(task.priority)}>{task.priority}</Chip>{:else}<span class="text-muted-foreground">Empty</span>{/if}
      </Select.Trigger>
      <Select.Content>
        {#each priorityOptions as option}<Select.Item value={option} label={option} />{/each}
      </Select.Content>
    </Select.Root>
  {:else if property.values.length === 0}
    <span class="text-muted-foreground">Empty</span>
  {:else if property.kind === 'select' || property.kind === 'multi_select'}
    <span class="flex flex-wrap gap-1">
      {#each property.values as value}
        <Chip tone={property.name === 'Priority' ? taskPriorityTone(value) : 'neutral'}>{value}</Chip>
      {/each}
    </span>
  {:else if property.kind === 'checkbox'}
    {#if property.values[0] === 'true'}<Check class="size-4 text-primary" />{:else}<Square class="size-4 text-muted-foreground" />{/if}
  {:else if property.kind === 'date' || property.kind === 'created_time' || property.kind === 'last_edited_time'}
    <span>{property.values.map(formatDate).join(' → ')}</span>
  {:else}
    <span class="flex flex-wrap gap-x-2 gap-y-1">
      {#each property.values as value, index}
        {@const link = property.links[index]}
        {#if link}
          <button type="button" class="p-0 text-left text-(--color-accent) underline underline-offset-2" onclick={() => void openUrlInBrowser({ url: link })}>{value}</button>
        {:else}
          <span>{value}{index < property.values.length - 1 ? ',' : ''}</span>
        {/if}
      {/each}
    </span>
  {/if}
{/snippet}

<div class="flex h-full min-h-0 min-w-0 flex-col overflow-hidden bg-background">
  <header class="flex items-center gap-(--space-2) border-b border-border px-(--space-2) py-(--space-2)">
    <IconButton label="Back to tasks" onclick={onBack}><ArrowLeft /></IconButton>
    <p class="min-w-0 flex-1 truncate text-(length:--text-quiet) text-muted-foreground">{task.project || 'Task'}</p>
    <IconButton label="Task actions" disabled><Ellipsis /></IconButton>
    <IconButton label="Open in Notion" onclick={() => onOpenExternal(task.sourceUrl)}>
      <ExternalLink />
    </IconButton>
  </header>

  <ScrollArea class="min-h-0 flex-1">
    <article class="selectable task-page mx-auto w-full min-w-0 max-w-[820px] px-(--space-4) pt-(--space-4) pb-(--space-5) outline-none" tabindex="-1" onpaste={pasteImage}>
      {#if title.key}<p class="font-mono text-(length:--text-quiet) text-muted-foreground">{title.key}</p>{/if}
      <h1
        class="mt-0.5 rounded-(--radius-sm) text-[20px] leading-snug font-semibold text-foreground outline-none [overflow-wrap:anywhere] focus:bg-muted/40"
        contenteditable="plaintext-only"
        spellcheck="false"
        aria-label="Task title"
        onkeydown={titleKeydown}
        onblur={(event) => void saveTitle(event.currentTarget)}
      >{title.text}</h1>

      {#if error}
        <p class="mt-4 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</p>
      {/if}

      {#if loading}
        <p class="mt-(--space-5) text-(length:--text-heading) text-muted-foreground"><WorkingSpinner size={14} /> Loading task…</p>
      {:else if detail}
        <dl class="mt-(--space-3) grid grid-cols-[fit-content(9rem)_minmax(0,1fr)] gap-x-(--space-3) gap-y-0.5 text-[13px]">
          {#each detail.properties as property (property.name)}
            <dt class="truncate leading-6 text-muted-foreground">{property.name}</dt>
            <dd class="flex min-h-6 min-w-0 items-center text-foreground [overflow-wrap:anywhere]">{@render propertyValue(property)}</dd>
          {/each}
        </dl>

        <hr class="my-(--space-3) border-border" />

        {#if detail.body.length === 0}
          <p class="text-(length:--text-heading) text-muted-foreground">This task has no additional notes.</p>
        {:else}
          <div class="min-w-0 text-foreground [overflow-wrap:anywhere]">{@render blockList(detail.body)}</div>
        {/if}

        <div class="mt-3">
          {#if adding}
            {@render editor(() => void addText(), () => (adding = false))}
          {:else}
            <div class="flex items-center gap-1">
              <Button variant="ghost" size="sm" disabled={saving} onclick={() => { editingBlockId = null; draft = ''; adding = true; }}><Plus />Add text</Button>
              <Button variant="ghost" size="sm" disabled={saving} onclick={() => imageInput?.click()}><ImagePlus />Add image</Button>
              {#if saving}<span class="text-[12px] text-muted-foreground"><WorkingSpinner size={12} /> Saving…</span>{/if}
            </div>
            <input
              bind:this={imageInput}
              type="file"
              accept="image/png,image/jpeg,image/gif,image/webp"
              class="hidden"
              onchange={(event) => {
                addImage(event.currentTarget.files?.[0]);
                event.currentTarget.value = '';
              }}
            />
          {/if}
        </div>

        {#if detail.comments.length > 0}
          <section class="mt-(--space-6) border-t border-border pt-(--space-4)" aria-label="Comments">
            <h2 class="text-[13px] font-semibold text-muted-foreground">Comments</h2>
            {#each detail.comments as comment, index (index)}
              <div class="mt-3">
                <p class="text-[13px]">
                  <strong class="font-semibold text-foreground">{comment.author}</strong>
                  <span class="ml-2 text-muted-foreground">{formatDate(comment.created)}</span>
                </p>
                <div class="mt-1">{@render markdown(comment.text)}</div>
              </div>
            {/each}
          </section>
        {/if}
      {/if}
    </article>
  </ScrollArea>
</div>

<style>
  .task-page .task-block + .task-block {
    margin-top: 4px;
  }
  .task-page :global(details[open] > summary .chevron) {
    transform: rotate(90deg);
  }
  .task-page summary::-webkit-details-marker {
    display: none;
  }
  .checked-off {
    color: var(--color-text-3);
    text-decoration: line-through;
  }
</style>
