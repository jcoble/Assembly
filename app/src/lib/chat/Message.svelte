<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { ErrorNotice } from './notices';
  // One prompt, or one piece of a reply. Only the first piece of a reply
  // carries the role label; a failed run's error is drawn as a card.
  let {
    role,
    label = true,
    speaker = 'Agent',
    sender = null,
    notice = null,
    children,
  }: {
    role: 'user' | 'assistant';
    label?: boolean;
    speaker?: string;
    // A prompt from another session: its label, and opening its thread.
    sender?: { label: string; open: () => void } | null;
    notice?: ErrorNotice | null;
    children?: Snippet;
  } = $props();
</script>

<article class="message {role}">
  {#if label}
    <div class="role">
      {#if sender}<button class="sender" title="Open its thread" onclick={sender.open}>{sender.label}</button>{:else}{role === 'user' ? 'You' : speaker}{/if}
    </div>
  {/if}
  {#if children}<div class="body">{@render children()}</div>{/if}
  {#if notice}
    <aside class="error">
      <span class="label">{notice.label}</span>
      <p>{notice.message}</p>
      {#if notice.hint}<p class="hint">{notice.hint}</p>{/if}
    </aside>
  {/if}
</article>

<style>
  .message.user {
    margin: 0 0 18px;
  }
  .role {
    font-size: 12px;
    font-weight: 600;
    color: var(--muted);
    margin-bottom: 4px;
  }
  .sender {
    font: inherit;
    color: var(--link, #2563eb);
    background: none;
    border: 0;
    padding: 0;
    cursor: pointer;
  }
  .user .body {
    background: var(--user-bg);
    border-radius: 10px;
    padding: 10px 14px;
  }
  .error {
    margin: 0 0 8px;
    padding: 12px;
    border: 1px solid color-mix(in srgb, var(--danger) 34%, var(--line));
    border-radius: 8px;
    background: color-mix(in srgb, var(--danger) 6%, var(--panel));
  }
  .label {
    display: block;
    color: var(--danger);
    font-size: 12px;
    letter-spacing: 0.06em;
    text-transform: uppercase;
  }
  .error p {
    margin: 8px 0 0;
    color: var(--muted);
    font-size: 13px;
    line-height: 1.5;
    white-space: pre-wrap;
  }
  .error .hint {
    color: var(--text);
    margin-top: 6px;
  }
</style>
