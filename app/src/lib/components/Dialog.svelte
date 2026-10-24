<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Dialog } from 'bits-ui';
  import { X } from '@lucide/svelte';

  interface Props {
    open?: boolean;
    title?: string;
    description?: string;
    trigger?: Snippet;
    children?: Snippet;
    footer?: Snippet;
  }

  let {
    open = $bindable(false),
    title,
    description,
    trigger,
    children,
    footer,
  }: Props = $props();
</script>

<Dialog.Root bind:open>
  {#if trigger}
    <Dialog.Trigger class="mcb-dialog-trigger-reset">
      {@render trigger()}
    </Dialog.Trigger>
  {/if}

  <Dialog.Portal>
    <Dialog.Overlay class="mcb-dialog-overlay" />

    <Dialog.Content class="mcb-dialog-content">
      <!-- Header -->
      {#if title || description}
        <div class="mcb-dialog-header">
          <div class="mcb-dialog-header-text">
            {#if title}
              <Dialog.Title class="mcb-dialog-title">{title}</Dialog.Title>
            {/if}
            {#if description}
              <Dialog.Description class="mcb-dialog-description">
                {description}
              </Dialog.Description>
            {/if}
          </div>

          <Dialog.Close class="mcb-dialog-close" aria-label="Close dialog">
            <X size={16} aria-hidden="true" />
          </Dialog.Close>
        </div>
      {:else}
        <!-- No title/description — still render close button -->
        <div class="mcb-dialog-close-row">
          <Dialog.Close class="mcb-dialog-close" aria-label="Close dialog">
            <X size={16} aria-hidden="true" />
          </Dialog.Close>
        </div>
      {/if}

      <!-- Body -->
      {#if children}
        <div class="mcb-dialog-body">
          {@render children()}
        </div>
      {/if}

      <!-- Footer -->
      {#if footer}
        <div class="mcb-dialog-footer">
          {@render footer()}
        </div>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  /* ── Trigger reset — let caller control all styling ─── */
  :global(.mcb-dialog-trigger-reset) {
    all: unset;
    cursor: pointer;
  }

  /* ── Overlay ──────────────────────────────────────────── */
  :global(.mcb-dialog-overlay) {
    position: fixed;
    inset: 0;
    z-index: 100;
    background-color: var(--color-scrim);
    backdrop-filter: blur(2px);
    animation: dialog-overlay-in 160ms ease forwards;
  }

  :global(.mcb-dialog-overlay[data-state='closed']) {
    animation: dialog-overlay-out 130ms ease forwards;
  }

  @keyframes dialog-overlay-in {
    from { opacity: 0; }
    to   { opacity: 1; }
  }

  @keyframes dialog-overlay-out {
    from { opacity: 1; }
    to   { opacity: 0; }
  }

  /* ── Content panel ────────────────────────────────────── */
  :global(.mcb-dialog-content) {
    position: fixed;
    top: 50%;
    left: 50%;
    z-index: 101;
    transform: translate(-50%, -50%);

    width: min(560px, calc(100vw - var(--space-6) * 2));
    max-height: calc(100vh - var(--space-6) * 2);
    overflow-y: auto;

    background-color: var(--color-bg);
    /* thin hairline border + elevation */
    box-shadow:
      0 0 0 1px var(--color-border),
      var(--shadow-lg);
    border-radius: var(--radius-lg);
    outline: none;

    display: flex;
    flex-direction: column;

    animation: dialog-content-in 180ms cubic-bezier(0.16, 1, 0.3, 1) forwards;
  }

  :global(.mcb-dialog-content[data-state='closed']) {
    animation: dialog-content-out 140ms ease forwards;
  }

  @keyframes dialog-content-in {
    from {
      opacity: 0;
      transform: translate(-50%, -50%) scale(0.97);
    }
    to {
      opacity: 1;
      transform: translate(-50%, -50%) scale(1);
    }
  }

  @keyframes dialog-content-out {
    from {
      opacity: 1;
      transform: translate(-50%, -50%) scale(1);
    }
    to {
      opacity: 0;
      transform: translate(-50%, -50%) scale(0.97);
    }
  }

  /* ── Header ───────────────────────────────────────────── */
  .mcb-dialog-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: var(--space-4);
    padding: var(--space-5) var(--space-5) var(--space-4);
    border-bottom: 1px solid var(--color-border);
  }

  .mcb-dialog-header-text {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    min-width: 0;
  }

  :global(.mcb-dialog-title) {
    font-size: var(--text-md);
    font-weight: var(--weight-semibold);
    color: var(--color-text);
    line-height: 1.4;
    margin: 0;
  }

  :global(.mcb-dialog-description) {
    font-size: var(--text-sm);
    color: var(--color-text-2);
    line-height: 1.5;
    margin: 0;
  }

  .mcb-dialog-close-row {
    display: flex;
    justify-content: flex-end;
    padding: var(--space-3) var(--space-3) 0;
  }

  :global(.mcb-dialog-close) {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
    width: 28px;
    height: 28px;
    background: transparent;
    border: none;
    border-radius: var(--radius-sm);
    color: var(--color-text-3);
    cursor: pointer;
    outline: none;
    transition: background 130ms ease, color 130ms ease;
  }

  :global(.mcb-dialog-close:hover) {
    background: var(--color-elevated);
    color: var(--color-text);
  }

  :global(.mcb-dialog-close:focus-visible) {
    box-shadow: var(--focus-ring);
  }

  /* ── Body ─────────────────────────────────────────────── */
  .mcb-dialog-body {
    padding: var(--space-5);
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    color: var(--color-text);
    font-size: var(--text-md);
    line-height: 1.6;
  }

  /* ── Footer ───────────────────────────────────────────── */
  .mcb-dialog-footer {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: var(--space-3);
    padding: var(--space-4) var(--space-5);
    border-top: 1px solid var(--color-border);
  }
</style>
