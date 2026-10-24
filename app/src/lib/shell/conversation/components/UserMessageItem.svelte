<script lang="ts">
  import ConversationMessage from './ConversationMessage.svelte';
  import AttachmentLightbox from './AttachmentLightbox.svelte';
  import TurnMetadata from './TurnMetadata.svelte';
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  let { item, onFileLink }: { item: Extract<ConversationDisplayItem, { kind: 'user' }>; onFileLink?(path: string): void } = $props();
</script>

<div class="message-wrap" data-testid="timeline-user-message">
  {#if item.attachments?.length}
    <div class="sent-images" data-testid="timeline-user-attachments">
      {#each item.attachments as attachment (attachment.id)}
        <figure>
          <AttachmentLightbox
            src={attachment.previewUrl}
            previewLoading={attachment.previewLoading}
            fullPath={attachment.path}
            remoteOwnedId={attachment.remoteOwnedId}
            attachmentId={attachment.id}
            mimeType={attachment.mimeType}
            originalByteLength={attachment.byteLength}
            name={attachment.name}
            variant="timeline"
          />
          <figcaption>{attachment.name}</figcaption>
        </figure>
      {/each}
    </div>
  {/if}
  <ConversationMessage text={item.text} role="user" itemId={item.itemId} completed={item.completed} {onFileLink} />
  <TurnMetadata text={item.text} timestampMs={item.timestampMs} align="end" />
</div>

<style>
  .message-wrap{position:relative}
  .sent-images { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; margin-bottom: 6px; }
  .sent-images figure { display: grid; gap: 4px; max-width: 220px; margin: 0; }
  .sent-images figcaption { overflow: hidden; color: var(--color-text-2); font-size: 12px; text-align: right; text-overflow: ellipsis; white-space: nowrap; }
</style>
