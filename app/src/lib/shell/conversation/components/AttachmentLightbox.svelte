<script lang="ts">
  import { convertFileSrc, invoke, isTauri } from '@tauri-apps/api/core';
  import { onDestroy } from 'svelte';
  import * as Dialog from '$lib/components/ui/dialog/index.js';
  import { createTrackedObjectUrl, revokeTrackedObjectUrl } from '$lib/shell/resourceDiagnostics.svelte';
  import WorkingSpinner from '../../components/feedback/WorkingSpinner.svelte';

  let {
    src,
    fullPath,
    fullSrc,
    remoteOwnedId,
    attachmentId,
    mimeType,
    originalByteLength,
    previewLoading = false,
    name,
    variant
  }: {
    src: string;
    fullPath?: string;
    fullSrc?: string;
    remoteOwnedId?: string;
    attachmentId?: string;
    mimeType?: string;
    originalByteLength?: number;
    previewLoading?: boolean;
    name: string;
    variant: 'composer' | 'timeline';
  } = $props();

  let open = $state(false);
  let fullObjectUrl = $state('');
  let loadError = $state('');
  let loadingFull = $state(false);
  let thumbnailLoaded = $state(false);
  let loadGeneration = 0;
  let activeRead: AbortController | null = null;
  let originalTransfer = '';

  $effect(() => {
    src;
    thumbnailLoaded = false;
  });

  async function discardOriginal(transferId: string): Promise<void> {
    if (!remoteOwnedId || !attachmentId || !mimeType) return;
    try {
      await invoke('discard_agent_conversation_original', {
        ownedId: remoteOwnedId, attachmentId, mimeType, transferId
      });
    } catch {
      // Best effort: a failed discard leaves nothing for the lightbox to show.
    }
  }

  function releaseFullObjectUrl(): void {
    loadGeneration += 1;
    activeRead?.abort();
    activeRead = null;
    if (originalTransfer) void discardOriginal(originalTransfer);
    originalTransfer = '';
    if (fullObjectUrl.startsWith('blob:')) revokeTrackedObjectUrl(fullObjectUrl);
    fullObjectUrl = '';
    loadError = '';
    loadingFull = false;
  }

  async function openChanged(next: boolean): Promise<void> {
    open = next;
    if (!next) {
      releaseFullObjectUrl();
      return;
    }
    const generation = ++loadGeneration;
    loadingFull = true;
    if (remoteOwnedId) {
      if (!attachmentId || !mimeType || !originalByteLength
        || !Number.isSafeInteger(originalByteLength) || originalByteLength > 20 * 1024 * 1024) {
        loadError = 'Image metadata is missing or outside the 20 MB limit';
        loadingFull = false;
        return;
      }
      const controller = new AbortController();
      activeRead = controller;
      const transferId = crypto.randomUUID();
      originalTransfer = transferId;
      try {
        const path = await invoke<string>('read_agent_conversation_attachment_file', {
          ownedId: remoteOwnedId, attachmentId, thumbnail: false,
          byteLength: originalByteLength, mimeType, transferId
        });
        if (controller.signal.aborted || generation !== loadGeneration) {
          void discardOriginal(transferId);
          return;
        }
        fullObjectUrl = convertFileSrc(path);
      } catch (error) {
        if (generation === loadGeneration && !controller.signal.aborted) {
          const message = error && typeof error === 'object' && 'message' in error ? String(error.message) : String(error);
          const reason = message.match(/connection closed|not connected|did not answer within \d+ seconds|request queue is unavailable|attachment row was not found|attachment read is outside the 20 MB limit|wrong attachment bytes|incomplete image chunk/i)?.[0];
          loadError = reason ? `Image read failed: ${reason}` : 'Image read failed';
        }
      } finally {
        if (activeRead === controller) activeRead = null;
        if (generation === loadGeneration && !fullObjectUrl) loadingFull = false;
      }
      return;
    }
    const source = fullPath
      ? (isTauri() ? convertFileSrc(fullPath) : fullPath)
      : fullSrc;
    if (!source) { loadingFull = false; return; }
    try {
      const response = await fetch(source);
      if (!response.ok || generation !== loadGeneration) return;
      const blob = await response.blob();
      if (generation !== loadGeneration) return;
      if (fullObjectUrl) revokeTrackedObjectUrl(fullObjectUrl);
      fullObjectUrl = createTrackedObjectUrl(blob, 'attachment');
    } catch {
      if (generation === loadGeneration) fullObjectUrl = '';
    } finally {
      if (generation === loadGeneration && !fullObjectUrl) loadingFull = false;
    }
  }

  onDestroy(releaseFullObjectUrl);
</script>

<Dialog.Root {open} onOpenChange={(next) => void openChanged(next)}>
  <Dialog.Trigger class={`attachment-lightbox-thumbnail ${variant}`} aria-label={`Enlarge ${name}`}>
    {#if src}
      <img {src} alt={name} loading="lazy" decoding="async" onload={() => thumbnailLoaded = true} onerror={() => thumbnailLoaded = true} style:opacity={thumbnailLoaded ? 1 : 0} />
    {/if}
    {#if (!src && previewLoading) || (src && !thumbnailLoaded)}
      <span class="attachment-loading" role="status" aria-label={`Loading ${name}`}><WorkingSpinner size={18} /></span>
    {:else if !src}
      <span>{name}</span>
    {/if}
  </Dialog.Trigger>
  <Dialog.Content
    class="w-auto max-w-[calc(100vw-48px)] bg-transparent p-0 shadow-none ring-0 sm:max-w-[calc(100vw-48px)]"
    showCloseButton={!fullObjectUrl}
  >
    <Dialog.Title class="sr-only">{name}</Dialog.Title>
    {#if open && loadError}<p class="text-destructive text-[13px]" role="alert">{loadError}</p>{/if}
    {#if open && loadingFull}<span class="attachment-loading-full" role="status" aria-label={`Loading enlarged ${name}`}><WorkingSpinner size={24} /></span>{/if}
    <Dialog.Close class="attachment-lightbox-full-image" aria-label={`Close enlarged ${name}`}>
      {#if open && fullObjectUrl}
        <img src={fullObjectUrl} alt={name} onload={() => loadingFull = false} onerror={() => { loadingFull = false; loadError = 'Image display failed'; }} style:opacity={loadingFull ? 0 : 1} />
      {/if}
    </Dialog.Close>
  </Dialog.Content>
</Dialog.Root>

<style>
  :global(.attachment-lightbox-thumbnail){display:block;overflow:hidden;padding:0;border:0;background:transparent;cursor:zoom-in}
  :global(.attachment-lightbox-thumbnail img){display:block;width:100%;height:100%;object-fit:cover}
  :global(.attachment-lightbox-thumbnail){position:relative}
  :global(.attachment-lightbox-thumbnail .attachment-loading){position:absolute;inset:0;display:grid;place-items:center}
  :global(.attachment-lightbox-thumbnail.composer){width:60px;height:52px;border-radius:7px}
  /* A fixed box, held whether or not the image has landed, so a transcript row
     never changes height when a screenshot finishes loading. The box itself
     paints nothing — the frame belongs to the picture, so a small or narrow
     screenshot is framed at its own size rather than sitting inside an empty
     rectangle. */
  :global(.attachment-lightbox-thumbnail.timeline){display:grid;place-items:center;width:220px;height:160px}
  :global(.attachment-lightbox-thumbnail.timeline img){width:auto;max-width:100%;height:auto;max-height:100%;border:1px solid color-mix(in srgb,var(--color-border) 76%,transparent);border-radius:10px}
  :global(.attachment-lightbox-thumbnail:focus-visible){outline:2px solid var(--color-focus-solid);outline-offset:2px}
  :global(.attachment-lightbox-full-image){display:block;padding:0;border:0;background:transparent;cursor:zoom-out}
  :global(.attachment-lightbox-full-image img){display:block;width:auto;height:auto;max-width:calc(100vw - 48px);max-height:calc(100vh - 48px);object-fit:contain}
  :global(.attachment-loading-full){display:flex;justify-content:center;margin:28px auto}
</style>
