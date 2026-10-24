<!--
  SessionContextPanel.svelte — the Context tab of the right column.

  Everything a person wants to know about the selected session that is not the
  conversation itself: what it is running with, how much of its context window
  it has spent, which files it has opened or changed, and what is attached to
  it. It reads and shows; nothing in here changes anything, and the only thing
  it can do to the rest of the app is open a file in the centre Editor tab.

  WHERE THE NUMBERS COME FROM, AND WHERE THEY DO NOT. Providers report wildly
  different amounts of housekeeping. One sends a used-token count and a window
  size, another sends neither, a third sends the count alone. Rather than paper
  over that with a plausible-looking default, every unreported field names the
  provider that did not send it — "not reported by Codex" — so the gap reads as
  a fact about that provider rather than as a hole in the panel, and a
  percentage appears only when both halves of the pair are real.
  `sessionContextModel.ts` holds that rule and the script test pins it.

  Files touched is derived, not stored: the panel reads the session's event
  pages each time it comes on screen and folds them down. It deliberately does
  not subscribe to the live stream — the conversation store is already doing
  that work, and a second subscriber would double it for a list nobody is
  staring at while it changes.
-->
<script lang="ts">
  import { FileText, Gauge, Paperclip, SlidersHorizontal } from '@lucide/svelte';

  import * as Card from '$lib/components/ui/card/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import { PanelHeader } from '$lib/components/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { agentDisplayName } from '$lib/shell/agentIcons.ts';
  import AttachmentLightbox from '$lib/shell/conversation/components/AttachmentLightbox.svelte';
  import { cleanupConversationAttachmentPreview, restoreAttachmentList, type SavedAttachment } from '$lib/shell/conversation/conversationService';
  import { getConversationSession } from '$lib/shell/conversation/conversationStore.svelte';
  import { openFileInEditor } from '$lib/shell/workbenchNavigation';
  import { listAgentConversationEventsBeforeFromTauri } from '$lib/tauriSource';
  import { invoke } from '@tauri-apps/api/core';
  import type { ConversationAttachment } from '$lib/shell/conversation/conversationTypes.ts';

  import {
    fileNameOf,
    formatTokenCount,
    metadataWithConfigFallback,
    sessionAttachments,
    sessionContextFacts,
    sessionContextUsage,
    sessionFilesTouched,
    type SessionFileTouch
  } from './sessionContextModel.ts';

  interface Props {
    visible: boolean;
    root: string;
    ownedId: string | null;
  }

  let { visible, root, ownedId }: Props = $props();

  /**
   * One look for all four cards: a sheet lifted off the panel's own surface by
   * a little light rather than by an outline, the way the shell's menus and
   * popovers are, and a tighter gap between the heading and the rows than a
   * full-page card wants.
   */
  const cardClass = 'gap-2 bg-foreground/8';

  const session = $derived(ownedId ? getConversationSession(ownedId) : null);
  const facts = $derived(
    sessionContextFacts(metadataWithConfigFallback(session?.metadata ?? null, session?.agentConfig))
  );
  const usage = $derived(sessionContextUsage(session?.metadata ?? null, session?.usage));
  let savedAttachments = $state<ConversationAttachment[]>([]);
  const attachments = $derived(sessionAttachments(
    [...savedAttachments, ...(session?.attachments ?? [])],
    session?.unclaimedSentAttachments ?? [],
    session?.sentAttachments ?? {}
  ));

  /** Whoever did not send a figure gets named for it, so "not reported" reads
   * as a fact about the provider rather than as a hole in the panel. */
  const providerName = $derived(session ? agentDisplayName(session.provider) : '');
  const notReported = $derived(`not reported by ${providerName}`);

  let filesTouched = $state<SessionFileTouch[]>([]);
  let filesLoaded = $state(false);
  let filesError = $state(false);

  /**
   * Re-read the session's events whenever the panel comes on screen or the
   * selected session changes. The guard on the way back matters: a tab switch
   * or a session switch can land while the read is still in flight, and the
   * answer to a question nobody is asking any more must not overwrite the
   * answer to the one they are.
   */
  $effect(() => {
    const forSession = ownedId;
    const forRoot = root.trim();
    if (!visible || !forSession) {
      filesLoaded = false;
      filesError = false;
      filesTouched = [];
      savedAttachments = [];
      return;
    }

    const owner = { active: true };
    filesLoaded = false;
    filesError = false;
    filesTouched = [];
    savedAttachments = [];
    void loadTouchedFiles(owner, forSession, forRoot);
    void loadAttachments(owner, forSession);

    return () => {
      owner.active = false;
      savedAttachments.forEach(cleanupConversationAttachmentPreview);
      savedAttachments = [];
    };
  });

  async function loadAttachments(owner: { active: boolean }, forSession: string): Promise<void> {
    try {
      const records = await invoke<SavedAttachment[]>('read_agent_conversation_attachments', { ownedId: forSession });
      if (!owner.active) return;
      const restored = await restoreAttachmentList(forSession, records);
      if (!owner.active || ownedId !== forSession) {
        restored.forEach(cleanupConversationAttachmentPreview);
        return;
      }
      savedAttachments = restored;
    } catch {
      // Existing draft and loaded-message attachments remain visible.
    }
  }

  async function loadTouchedFiles(
    owner: { active: boolean },
    forSession: string,
    forRoot: string
  ): Promise<void> {
    try {
      const touches = new Map<string, SessionFileTouch>();
      let before = Number.MAX_SAFE_INTEGER;
      while (owner.active) {
        const page = await listAgentConversationEventsBeforeFromTauri(forSession, before, 512 * 1024);
        if (!page) throw new Error('No event page returned');
        for (const touch of sessionFilesTouched(page.events, forRoot)) {
          const existing = touches.get(touch.path);
          if (existing) existing.count += touch.count;
          else touches.set(touch.path, touch);
        }
        const oldest = page.events[0]?.sequence;
        if (!page.hasMore || touches.size >= 50 || oldest === undefined || oldest >= before) break;
        before = oldest;
      }
      if (!owner.active || ownedId !== forSession || root.trim() !== forRoot) return;
      filesTouched = [...touches.values()]
        .sort((a, b) => b.lastTouchedMs - a.lastTouchedMs || a.path.localeCompare(b.path))
        .slice(0, 50);
      filesLoaded = true;
    } catch {
      if (!owner.active || ownedId !== forSession || root.trim() !== forRoot) return;
      filesTouched = [];
      filesLoaded = true;
      filesError = true;
    }
  }

  function openTouchedFile(touch: SessionFileTouch): void {
    openFileInEditor({ path: touch.path, projectRoot: root.trim() || undefined });
  }

  /** The folder a path sits in, shown under the file name. Empty at the top level. */
  function folderOf(path: string): string {
    const cut = path.lastIndexOf('/');
    return cut > 0 ? path.slice(0, cut) : '';
  }
</script>

<div class="flex h-full min-h-0 flex-col">
  <PanelHeader title="Context" data-testid="session-context-header" />

  {#if !ownedId}
    <EmptyState
      title="No session selected"
      body="Pick a session on the left and this panel shows what it is running with, how much of its context it has used, and the files it has touched."
    >
      {#snippet icon()}<SlidersHorizontal />{/snippet}
    </EmptyState>
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <div class="flex flex-col gap-2 px-3 py-3">
        <!-- Session -->
        <section aria-labelledby="session-context-facts">
          <Card.Root size="sm" class={cardClass}>
            <Card.Header>
              {@render sectionHeading('session-context-facts', 'Session', SlidersHorizontal)}
            </Card.Header>
            <Card.Content>
              <dl class="flex flex-col">
                {#each facts as item (item.label)}
                  <div class="flex min-h-7 items-center gap-3 border-b py-1.5 last:border-b-0">
                    <dt class="w-24 shrink-0 text-sm text-muted-foreground">{item.label}</dt>
                    <dd
                      class="min-w-0 flex-1 truncate text-right text-[13px] leading-tight"
                      class:text-muted-foreground={item.missing}
                    >
                      {item.missing ? notReported : item.value}
                    </dd>
                  </div>
                {/each}
              </dl>
            </Card.Content>
          </Card.Root>
        </section>

        <!-- Context usage -->
        <section aria-labelledby="session-context-usage">
          <Card.Root size="sm" class={cardClass}>
            <Card.Header>
              {@render sectionHeading('session-context-usage', 'Context usage', Gauge)}
            </Card.Header>
            <Card.Content class="flex flex-col gap-2">
              <div class="flex items-baseline gap-2">
                <span class="text-[13px] leading-tight">
                  {usage.usedTokens === null
                    ? `Current context ${notReported}`
                    : `Current context: ${formatTokenCount(usage.usedTokens)}`}
                </span>
                <span class="min-w-0 flex-1 truncate text-right text-sm text-muted-foreground">
                  {usage.contextWindow === null
                    ? `window size ${notReported}`
                    : `of ${formatTokenCount(usage.contextWindow)}`}
                </span>
              </div>

              {#if usage.percentUsed !== null}
                <div class="flex items-center gap-2">
                  <div
                    class="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-secondary"
                    role="img"
                    aria-label={`${usage.percentUsed} percent of the context window used`}
                  >
                    <div
                      class="h-full rounded-full bg-primary"
                      style:width={`${usage.percentUsed}%`}
                    ></div>
                  </div>
                  <span class="shrink-0 text-sm text-muted-foreground">{usage.percentUsed}%</span>
                </div>
              {/if}

              <div class="flex items-center gap-2 text-sm text-muted-foreground">
                <span>Session total</span>
                <span class="min-w-0 flex-1 truncate text-right">
                  {usage.totalTokens === null
                    ? notReported
                    : `${formatTokenCount(usage.totalTokens)} tokens`}
                </span>
              </div>

              <div class="flex items-center gap-2 text-sm text-muted-foreground">
                <span class="min-w-0 flex-1 truncate">
                  In: {usage.inputTokens === null
                    ? notReported
                    : formatTokenCount(usage.inputTokens)}
                </span>
                <span class="min-w-0 flex-1 truncate text-right">
                  Out: {usage.outputTokens === null
                    ? notReported
                    : formatTokenCount(usage.outputTokens)}
                </span>
              </div>
            </Card.Content>
          </Card.Root>
        </section>

        <!-- Files touched -->
        <section aria-labelledby="session-context-files">
          <Card.Root size="sm" class={cardClass}>
            <Card.Header>
              {@render sectionHeading(
                'session-context-files',
                'Files touched',
                FileText,
                filesTouched.length
              )}
            </Card.Header>
            <Card.Content class="px-1">
              {#if filesTouched.length === 0}
                <EmptyState
                  class="px-2 py-2"
                  title={filesError ? 'Could not read files' : filesLoaded ? 'No files yet' : 'Reading this session'}
                  body={filesError
                    ? 'The session history could not be read. Reopen Context to try again.'
                    : filesLoaded
                    ? 'Files this session reads or edits are listed here, most recent first.'
                    : 'Working out which files this session has touched.'}
                />
              {:else}
                <div class="flex flex-col">
                  {#each filesTouched as touch (touch.path)}
                    <ListRow onclick={() => openTouchedFile(touch)}>
                      <span class="min-w-0 flex-1 truncate">{fileNameOf(touch.path)}</span>
                      {#if folderOf(touch.path)}
                        <span class="min-w-0 max-w-[45%] truncate text-sm text-muted-foreground">
                          {folderOf(touch.path)}
                        </span>
                      {/if}
                      {#if touch.count > 1}
                        <Chip tone="count">{touch.count}</Chip>
                      {/if}
                    </ListRow>
                  {/each}
                </div>
              {/if}
            </Card.Content>
          </Card.Root>
        </section>

        <!-- Attachments -->
        <section aria-labelledby="session-context-attachments">
          <Card.Root size="sm" class={cardClass}>
            <Card.Header>
              {@render sectionHeading(
                'session-context-attachments',
                'Attachments',
                Paperclip,
                attachments.length
              )}
            </Card.Header>
            <Card.Content class="px-1">
              {#if attachments.length === 0}
                <EmptyState
                  class="px-2 py-2"
                  title="Nothing attached"
                  body="Files and images added to this session's message box are listed here."
                />
              {:else}
                <div class="flex flex-col">
                  {#each attachments as attachment (attachment.id)}
                    <div class="flex min-h-9 items-center gap-2 px-2 py-1">
                      {#if attachment.mimeType.startsWith('image/')}
                        <AttachmentLightbox
                          src={attachment.previewUrl}
                          fullPath={attachment.path}
                          remoteOwnedId={attachment.remoteOwnedId}
                          attachmentId={attachment.id}
                          mimeType={attachment.mimeType}
                          originalByteLength={attachment.byteLength}
                          name={attachment.name}
                          variant="composer"
                        />
                      {/if}
                      <span class="min-w-0 flex-1 truncate">{attachment.name}</span>
                      <Chip tone="neutral">{attachment.mimeType || 'type not reported'}</Chip>
                    </div>
                  {/each}
                </div>
              {/if}
            </Card.Content>
          </Card.Root>
        </section>
      </div>
    </ScrollArea>
  {/if}
</div>

{#snippet sectionHeading(
  id: string,
  label: string,
  Icon: typeof FileText,
  count: number | null = null
)}
  <div class="flex items-center gap-2">
    <span class="heading-icon flex text-muted-foreground" aria-hidden="true"><Icon /></span>
    <h3 {id} class="min-w-0 flex-1 truncate text-[13px] leading-tight font-semibold text-foreground">
      {label}
    </h3>
    {#if count !== null && count > 0}
      <Chip tone="count">{count}</Chip>
    {/if}
  </div>
{/snippet}

<style>
  /* One size for every section's icon, so the four headings line up. */
  .heading-icon :global(svg) {
    width: 14px;
    height: 14px;
  }
</style>
