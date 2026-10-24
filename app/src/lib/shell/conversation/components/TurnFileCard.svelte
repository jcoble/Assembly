<script lang="ts">
  import FilePenLine from '@lucide/svelte/icons/file-pen-line';
  import Undo2 from '@lucide/svelte/icons/undo-2';

  interface Props {
    path: string;
    added?: number;
    removed?: number;
    onReview?(path: string): void;
    onUndo?(path: string): void;
  }

  let { path, added = 0, removed = 0, onReview, onUndo }: Props = $props();

  const fileName = $derived(path.split(/[/\\]/).pop() || path);
</script>

<div class="turn-file-card" data-testid="turn-file-card">
  <div class="card-left">
    <div class="file-icon-badge">
      <FilePenLine size={16} />
    </div>
    <div class="file-info">
      <span class="file-label">Edited {fileName}</span>
      <div class="diff-counts">
        {#if added > 0}<span class="added">+{added}</span>{/if}
        {#if removed > 0}<span class="removed">-{removed}</span>{/if}
      </div>
    </div>
  </div>

  <div class="card-actions">
    {#if onUndo}
      <button
        class="card-btn undo-btn"
        type="button"
        onclick={() => onUndo?.(path)}
        title="Undo file changes"
      >
        <span>Undo</span>
        <Undo2 size={13} />
      </button>
    {/if}
    {#if onReview}
      <button
        class="card-btn review-btn"
        type="button"
        onclick={() => onReview?.(path)}
        title="Review diff in editor"
      >
        Review
      </button>
    {/if}
  </div>
</div>

<style>
  .turn-file-card {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: 10px 14px;
    margin-top: 10px;
    border: 1px solid color-mix(in srgb, var(--color-border) 75%, transparent);
    border-radius: 12px;
    background: color-mix(in srgb, var(--color-surface) 45%, var(--color-bg) 55%);
  }

  .card-left {
    display: flex;
    align-items: center;
    gap: 12px;
    min-width: 0;
  }

  .file-icon-badge {
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    border-radius: 8px;
    background: color-mix(in srgb, var(--color-surface) 75%, transparent);
    border: 1px solid color-mix(in srgb, var(--color-border) 60%, transparent);
    color: var(--color-text-2);
    flex: none;
  }

  .file-info {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .file-label {
    font-weight: 500;
    font-size: 13px;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .diff-counts {
    display: flex;
    gap: 6px;
    font-size: 12px;
    font-family: var(--font-mono);
  }

  .added {
    color: var(--color-good);
  }

  .removed {
    color: var(--color-bad);
  }

  .card-actions {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: none;
  }

  .card-btn {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    height: 28px;
    padding: 0 12px;
    border: 1px solid color-mix(in srgb, var(--color-border) 70%, transparent);
    border-radius: 8px;
    background: color-mix(in srgb, var(--color-surface) 60%, transparent);
    color: var(--color-text);
    font-size: 12.5px;
    font-weight: 500;
    cursor: pointer;
    transition: background 0.12s ease, border-color 0.12s ease;
  }

  .card-btn:hover {
    background: var(--color-hover);
    border-color: color-mix(in srgb, var(--color-border) 90%, transparent);
  }

  .card-btn:focus-visible {
    outline: none;
    box-shadow: var(--focus-ring);
  }

  .review-btn {
    background: color-mix(in srgb, var(--color-elevated) 85%, var(--color-surface));
  }
</style>
