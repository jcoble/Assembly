<!--
  Agent-to-agent messages as a list plus a reader. The drawer shows one session's
  messages (to it, from it, or inside a run it started); the full screen shows every
  machine's, with search, sort and filters. The backend does all filtering and paging,
  and a "messages changed" event reloads the list while it is visible.
-->
<script lang="ts">
  import { untrack } from 'svelte';
  import Bot from '@lucide/svelte/icons/bot';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import Search from '@lucide/svelte/icons/search';
  import Workflow from '@lucide/svelte/icons/workflow';
  import X from '@lucide/svelte/icons/x';

  import { Button } from '$lib/components/ui/button/index.js';
  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import type { ChipTone } from '$lib/components/ui/chip/variants.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import { sessionRowJump } from '$lib/shell/components/sessionRowJump.ts';
  import { adoptSession } from '$lib/shell/conversation/conversationService.ts';
  import { messageSession, reloadLimit } from '$lib/shell/messages/agentMessagesModel.svelte.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  import {
    listAgentMessagesFromTauri,
    listenAgentMessagesChanged,
    readRemoteAssemblyEnvironmentFromTauri,
    type AgentMessage,
    type AgentMessageFilter,
    type RemoteAssemblyProfile
  } from '$lib/tauriSource.ts';

  type Sort = NonNullable<AgentMessageFilter['sort']>;
  type Status = AgentMessage['receipt'] | 'all';

  interface Props {
    visible: boolean;
    layout: 'drawer' | 'screen';
    /** The drawer's session; the screen ignores it and shows every session. */
    session: string | null;
    /** The screen's run filter, and its name for the chip. */
    runId?: string | null;
    runLabel?: string;
    onClearRun?: () => void;
    /** A message's run was picked in the reader. */
    onShowRun: (runId: string, label: string) => void;
    /** A visible list finished loading, so its messages count as read. */
    onSeen?: () => void;
    /** A sender's or receiver's chat actually opened. */
    onOpened?: () => void;
  }
  let { visible, layout, session, runId = null, runLabel = '', onClearRun, onShowRun, onSeen, onOpened }: Props = $props();

  const LIMIT = 100;
  const RECEIPT_TONE: Record<AgentMessage['receipt'], ChipTone> = {
    queued: 'live',
    delivered: 'good',
    failed: 'bad',
    expired: 'neutral'
  };
  const SORTS: { value: Sort; label: string }[] = [
    { value: 'time', label: 'Newest first' },
    { value: 'sender', label: 'Sender A–Z' },
    { value: 'receiver', label: 'Receiver A–Z' }
  ];
  const STATUSES: { value: Status; label: string }[] = [
    { value: 'all', label: 'Any status' },
    { value: 'queued', label: 'Queued' },
    { value: 'delivered', label: 'Delivered' },
    { value: 'failed', label: 'Failed' },
    { value: 'expired', label: 'Expired' }
  ];
  const time = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  const clock = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' });
  const stamp = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  const day = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'short', day: 'numeric' });

  let messages = $state<AgentMessage[]>([]);
  let selected = $state<AgentMessage | null>(null);
  let hasOlder = $state(false);
  let loading = $state(false);
  let error = $state('');
  let search = $state('');
  let sort = $state<Sort>('time');
  let machine = $state('all');
  let status = $state<Status>('all');
  let profiles = $state<RemoteAssemblyProfile[]>([]);
  let loader: AbortController | null = null;
  let openLookup: AbortController | null = null;
  /** New criteria whose results have not landed yet; a live reload that cancels them still owes the check. */
  let queryPending = false;

  const waiting = $derived(layout === 'drawer' && !session);

  /** Message ids are per machine, so a row is keyed by both. */
  function keyOf(item: AgentMessage): string {
    return `${item.machine}:${item.id}`;
  }

  function criteria(): Omit<AgentMessageFilter, 'limit' | 'before'> {
    return layout === 'drawer'
      ? { session }
      : { runId, machine: machine === 'all' ? null : machine, status: status === 'all' ? null : status, search: search.trim(), sort };
  }

  // Every change of search, sort or filter starts a new first page and cancels the one before.
  $effect(() => {
    if (!visible || waiting) return;
    const filter = criteria();
    untrack(() => {
      if (layout === 'drawer') {
        messages = [];
        selected = null;
      }
      start('query', filter);
    });
    return () => {
      loader?.abort();
      loader = null;
    };
  });

  // While the list shows, a stored or updated message reloads it.
  $effect(() => {
    if (!visible || waiting) return;
    const controller = new AbortController();
    void follow(controller.signal);
    return () => {
      controller.abort();
      openLookup?.abort();
      openLookup = null;
    };
  });

  $effect(() => {
    if (layout !== 'screen') return;
    const controller = new AbortController();
    void readMachines(controller.signal);
    return () => controller.abort();
  });

  async function follow(signal: AbortSignal): Promise<void> {
    try {
      await listenAgentMessagesChanged(() => start('live', criteria()), signal);
    } catch {
      // Without the event the list still loads on every open and filter change.
    }
  }

  async function readMachines(signal: AbortSignal): Promise<void> {
    try {
      const environment = await readRemoteAssemblyEnvironmentFromTauri();
      if (signal.aborted) return;
      profiles = environment.profiles.filter((profile) => environment.readyProfileIds.includes(profile.id));
    } catch {
      // The machine filter keeps All machines and This Mac.
    }
  }

  function start(mode: 'query' | 'live' | 'older', filter: Omit<AgentMessageFilter, 'limit' | 'before'>): void {
    loader?.abort();
    const controller = new AbortController();
    loader = controller;
    if (mode === 'query') queryPending = true;
    void load(mode, filter, controller.signal);
  }

  async function load(mode: 'query' | 'live' | 'older', filter: Omit<AgentMessageFilter, 'limit' | 'before'>, signal: AbortSignal): Promise<void> {
    loading = true;
    error = '';
    const last = mode === 'older' ? messages.at(-1) : undefined;
    // A live reload refetches the whole loaded window from the top, up to the backend's cap.
    const limit = mode === 'live' ? reloadLimit(messages.length) : LIMIT;
    const key = !last || filter.sort === undefined || filter.sort === 'time' ? undefined
      : filter.sort === 'sender' ? last.senderName : last.receiver;
    try {
      const page = (await listAgentMessagesFromTauri({
        ...filter,
        before: last ? { createdAtMs: last.createdAtMs, id: last.id, key } : null,
        limit
      }, signal)) ?? [];
      if (signal.aborted) return;
      messages = mode === 'older' ? [...messages, ...page] : page;
      hasOlder = page.length === limit;
      const newCriteria = mode !== 'older' && queryPending;
      if (mode !== 'older') queryPending = false;
      if (selected) {
        const current = keyOf(selected);
        const fresh = page.find((item) => keyOf(item) === current);
        // New criteria drop a message they no longer match; a live reload keeps it in the reader.
        if (fresh) selected = fresh;
        else if (newCriteria) selected = null;
      }
      onSeen?.();
    } catch (cause) {
      if (!signal.aborted) error = `Could not load messages: ${cause instanceof Error ? cause.message : String(cause)}`;
    }
    if (!signal.aborted) loading = false;
  }

  /** A sender's label without the trailing `[owned id]` it carries for replies. */
  function senderName(label: string): string {
    return label.replace(/ \[[^\]]*\]$/, '');
  }

  function machineName(id: string): string {
    return id === 'mac' ? 'This Mac' : profiles.find((profile) => profile.id === id)?.name ?? id;
  }

  function runName(id: string): string {
    return `Run ${id.replace(/^workflow-run-/, '').slice(0, 8)}`;
  }

  /** The list reads as sections: one per day, or one per name when sorted by name. */
  const groups = $derived.by(() => {
    const today = new Date().setHours(0, 0, 0, 0);
    const out: { key: string; label: string; items: AgentMessage[] }[] = [];
    for (const item of messages) {
      const label = layout === 'screen' && sort === 'sender' ? senderName(item.senderName)
        : layout === 'screen' && sort === 'receiver' ? item.receiver
        : item.createdAtMs >= today ? 'Today'
        : item.createdAtMs >= today - 86_400_000 ? 'Yesterday'
        : day.format(item.createdAtMs);
      const last = out.at(-1);
      if (last?.label === label) last.items.push(item);
      else out.push({ key: `${out.length}:${label}`, label, items: [item] });
    }
    return out;
  });

  /** A leading `[TSK-12 failed]` reads as a tag beside the text rather than as part of it. */
  function tagged(text: string): { tag: string; text: string } {
    const match = /^\[([^\]]+)\]\s*/.exec(text);
    return match ? { tag: match[1], text: text.slice(match[0].length) } : { tag: '', text };
  }

  async function open(identity: string): Promise<void> {
    const ownedId = identity.slice(identity.indexOf(':') + 1);
    openLookup?.abort();
    const lookup = new AbortController();
    openLookup = lookup;
    try {
      const adopted = await adoptSession(ownedId, lookup.signal);
      if (lookup.signal.aborted) return;
      if (!adopted || !messageSession(identity, rail.owned)) throw new Error('That session is not available on that machine.');
      const opened = await sessionRowJump(ownedId, 'session');
      if (!lookup.signal.aborted && opened) onOpened?.();
    } catch (cause) {
      if (!lookup.signal.aborted) error = `Could not open session: ${cause instanceof Error ? cause.message : String(cause)}`;
    }
  }
</script>

{#snippet avatar(item: AgentMessage, size: string)}
  <span class={['flex shrink-0 items-center justify-center rounded-full bg-foreground/10 text-[var(--color-text-2)]', size]} aria-hidden="true">
    {#if item.fromOwnedId.startsWith('workflow:')}<Workflow />{:else}<Bot />{/if}
  </span>
{/snippet}

{#snippet tag(text: string)}
  <span class="inline-flex h-5 shrink-0 items-center rounded-md bg-foreground/10 px-1.5 text-sm leading-none font-medium text-[var(--color-text-2)]">{text}</span>
{/snippet}

<!-- One line under the names: a status that needs a look, then the tag, then the text. -->
{#snippet preview(item: AgentMessage)}
  {@const body = tagged(item.body)}
  <span class="flex min-w-0 items-center gap-1.5">
    {#if item.receipt !== 'delivered'}<Chip tone={RECEIPT_TONE[item.receipt]}>{item.receipt}</Chip>{/if}
    {#if body.tag}{@render tag(body.tag)}{/if}
    <span class="min-w-0 truncate text-[13px] text-[var(--color-text-2)]">{body.text}</span>
  </span>
{/snippet}

{#snippet detail(label: string, value: string, action?: { text: string; run: () => void })}
  <div class="flex min-h-9 min-w-0 items-center gap-3 rounded-lg px-2 py-1">
    <span class={['shrink-0 text-sm text-[var(--color-text-2)]', layout === 'screen' ? 'w-16' : 'w-10']}>{label}</span>
    <span class="line-clamp-2 min-w-0 flex-1 text-[13px] [overflow-wrap:anywhere]" title={value}>{value}</span>
    {#if action}<Button size="sm" variant="ghost" onclick={action.run}>{action.text}</Button>{/if}
  </div>
{/snippet}

{#snippet reader(item: AgentMessage)}
  {@const body = tagged(item.body)}
  {@const surface = layout === 'screen' ? 'bg-card' : 'bg-foreground/8'}
  <div class={['flex min-w-0 flex-col', layout === 'screen' ? 'gap-4' : 'gap-2 px-2 pb-2']} data-testid="agent-message-reader">
    <Card.Root size={layout === 'screen' ? 'default' : 'sm'} class={surface}>
      <Card.Header>
        <div class="flex min-w-0 items-start gap-3">
          {@render avatar(item, layout === 'screen' ? 'size-10 [&>svg]:size-5' : 'size-8 [&>svg]:size-4')}
          <div class="flex min-w-0 flex-1 flex-col gap-0.5">
            <h3 class={['leading-snug font-semibold [overflow-wrap:anywhere]', layout === 'screen' ? 'text-(length:--text-heading)' : 'text-(length:--text-body)']} title={item.senderName}>{senderName(item.senderName)}</h3>
            <p class="line-clamp-2 text-(length:--text-quiet) text-[var(--color-text-2)] [overflow-wrap:anywhere]">to {item.receiver}</p>
          </div>
        </div>
      </Card.Header>
      <Card.Content class="flex min-w-0 flex-col gap-3">
        <div class="flex min-w-0 flex-wrap items-center gap-1.5">
          <Chip tone={RECEIPT_TONE[item.receipt]}>{item.receipt}</Chip>
          {#if body.tag}{@render tag(body.tag)}{/if}
        </div>
        {#if body.text}
          <p class="text-(length:--text-body) leading-relaxed [overflow-wrap:anywhere] whitespace-pre-wrap">{body.text}</p>
        {/if}
      </Card.Content>
    </Card.Root>

    <!-- Who and where, each with the action that leads there. -->
    <Card.Root size="sm" class={['gap-1', surface]}>
      <Card.Header><h4 class="text-[13px] leading-tight font-semibold">Details</h4></Card.Header>
      <Card.Content class="flex flex-col px-1">
        {@render detail('From', senderName(item.senderName), item.fromOwnedId.startsWith('workflow:') ? undefined
          : { text: 'Open chat', run: () => void open(item.fromOwnedId) })}
        {@render detail('To', item.receiver, { text: 'Open chat', run: () => void open(item.toOwnedId) })}
        {#if item.runId}
          {@const run = item.runId}
          {@render detail('Run', runName(run), { text: 'Show run', run: () => onShowRun(run, runName(run)) })}
        {/if}
        {@render detail('Sent', `${time.format(item.createdAtMs)} · ${machineName(item.machine)}`)}
      </Card.Content>
    </Card.Root>
  </div>
{/snippet}

<div class="flex h-full min-h-0 flex-col" data-testid="agent-messages">
  {#if layout === 'screen'}
    <div class="flex min-w-0 flex-wrap items-center gap-2 pb-4">
      <div class="relative">
        <Search class="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-[var(--color-text-3)]" aria-hidden="true" />
        <Input class="w-80 pl-8" placeholder="Search sender, receiver or text" aria-label="Search messages" bind:value={search} />
      </div>
      <Select.Root type="single" value={sort} onValueChange={(value) => (sort = value as Sort)}>
        <Select.Trigger size="sm" aria-label="Sort">{SORTS.find((item) => item.value === sort)?.label}</Select.Trigger>
        <Select.Content>
          <Select.Group>
            {#each SORTS as item (item.value)}<Select.Item value={item.value} label={item.label} />{/each}
          </Select.Group>
        </Select.Content>
      </Select.Root>
      <Select.Root type="single" bind:value={machine}>
        <Select.Trigger size="sm" aria-label="Machine">{machine === 'all' ? 'All machines' : machineName(machine)}</Select.Trigger>
        <Select.Content>
          <Select.Group>
            <Select.Item value="all" label="All machines" />
            <Select.Item value="mac" label="This Mac" />
            {#each profiles as profile (profile.id)}<Select.Item value={profile.id} label={profile.name} />{/each}
          </Select.Group>
        </Select.Content>
      </Select.Root>
      <Select.Root type="single" value={status} onValueChange={(value) => (status = value as Status)}>
        <Select.Trigger size="sm" aria-label="Status">{STATUSES.find((item) => item.value === status)?.label}</Select.Trigger>
        <Select.Content>
          <Select.Group>
            {#each STATUSES as item (item.value)}<Select.Item value={item.value} label={item.label} />{/each}
          </Select.Group>
        </Select.Content>
      </Select.Root>
      {#if runId}
        <span class="flex min-w-0 items-center gap-0.5 rounded-full bg-secondary py-0.5 pr-0.5 pl-2.5 text-sm">
          <span class="truncate">{runLabel}</span>
          <IconButton label="Show messages from every run" size="xs" onclick={() => onClearRun?.()}><X /></IconButton>
        </span>
      {/if}
      <span class="ml-auto text-sm text-[var(--color-text-2)] tabular-nums">
        {messages.length}{hasOlder ? '+' : ''} {messages.length === 1 ? 'message' : 'messages'}
      </span>
    </div>
  {/if}
  {#if error}<p role="alert" class="px-3 py-2 text-sm text-destructive">{error}</p>{/if}

  {#if waiting}
    <EmptyState title="Select a session" body="Its messages, and those in runs it started, appear here." />
  {:else}
    <div class={layout === 'screen'
      ? 'grid min-h-0 flex-1 grid-cols-[minmax(0,412px)_minmax(0,1fr)] gap-3'
      : 'flex min-h-0 flex-1 flex-col'}>
      <div class="flex min-h-0 flex-1 flex-col">
        {#if messages.length === 0}
          <EmptyState title={loading ? 'Loading messages' : 'No messages yet.'}>
            {#snippet icon()}<MessageSquare />{/snippet}
          </EmptyState>
        {:else}
          <ScrollArea class="min-h-0 flex-1">
            <div class={['flex min-w-0 flex-col', layout === 'screen' ? 'gap-4 pr-3 pb-1' : 'gap-3 px-2 pb-2']}>
              {#each groups as group (group.key)}
                <Card.Root size="sm" class={['gap-1 pb-0.5', layout === 'screen' ? 'bg-card' : 'bg-foreground/8']}>
                  <Card.Header>
                    <div class="flex min-w-0 items-center gap-2">
                      <h3 class="min-w-0 flex-1 truncate text-[13px] leading-tight font-semibold">{group.label}</h3>
                      <Chip tone="count">{group.items.length}</Chip>
                    </div>
                  </Card.Header>
                  <ul class="flex flex-col gap-0.5 px-1">
                    {#each group.items as item (keyOf(item))}
                      <!-- Rows off screen skip layout and paint, so many loaded pages stay cheap. -->
                      <li class="[contain-intrinsic-size:auto_68px] [content-visibility:auto]">
                        <button
                          type="button"
                          class={['flex w-full min-w-0 items-start gap-3 rounded-lg px-2 py-2 text-left outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50',
                            selected && keyOf(selected) === keyOf(item) ? 'bg-secondary' : 'hover:bg-accent/60']}
                          aria-pressed={selected !== null && keyOf(selected) === keyOf(item)}
                          onclick={() => (selected = item)}
                        >
                          {@render avatar(item, 'mt-0.5 size-8 [&>svg]:size-4')}
                          <span class="flex min-w-0 flex-1 flex-col gap-0.5">
                            <span class="flex min-w-0 items-baseline gap-2">
                              <span class="min-w-0 flex-1 truncate text-[13px] font-semibold" title={item.senderName}>{senderName(item.senderName)}</span>
                              <span class="shrink-0 text-sm text-[var(--color-text-2)] tabular-nums">
                                {(layout === 'drawer' || sort === 'time' ? clock : stamp).format(item.createdAtMs)}
                              </span>
                            </span>
                            <span class="truncate text-sm text-[var(--color-text-2)]">
                              to {item.receiver}{layout === 'screen' || item.machine !== 'mac' ? ` · ${machineName(item.machine)}` : ''}
                            </span>
                            <span class="pt-1">{@render preview(item)}</span>
                          </span>
                        </button>
                      </li>
                    {/each}
                  </ul>
                </Card.Root>
              {/each}
              {#if hasOlder}
                <Button class="w-full" size="sm" variant="ghost" disabled={loading} onclick={() => start('older', criteria())}>Load older</Button>
              {/if}
            </div>
          </ScrollArea>
        {/if}
      </div>
      {#if layout === 'screen'}
        <div class="min-h-0">
          {#if selected}
            <ScrollArea class="h-full"><div class="pb-1">{@render reader(selected)}</div></ScrollArea>
          {:else}
            <div class="flex h-full items-center justify-center rounded-xl bg-card">
              <EmptyState title="Select a message" body="Its full text, who sent it, and where it went appear here.">
                {#snippet icon()}<MessageSquare />{/snippet}
              </EmptyState>
            </div>
          {/if}
        </div>
      {:else if selected}
        <!-- A fixed height: the scroll viewport fills its box, so the box needs a size. -->
        <ScrollArea class="h-72 flex-none border-t border-border pt-2">{@render reader(selected)}</ScrollArea>
      {/if}
    </div>
  {/if}
</div>
