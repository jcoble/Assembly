<script lang="ts">
  /**
   * LanguageServerStatusChip.svelte — a few words in the editor header saying
   * what the background language server is doing.
   *
   * The language server is the program that knows what the code MEANS: where a
   * name is defined, what is wrong with a file, what the inline hints say. On a
   * large solution it can spend the first half-minute reading the project, and
   * anything asked of it before then comes back empty. Without this chip that
   * looks like a broken editor rather than a busy one.
   *
   * IT SAYS NOTHING RATHER THAN GUESSING. The chip disappears entirely when
   * there is no truthful answer to show — a browser tab (there is no language
   * server behind a browser at all) or a desktop build older than the status
   * fields. `describeLanguageServer` returns null in both cases and nothing is
   * rendered, so those builds look exactly as they did before.
   */
  import {
    describeLanguageServer,
    type LanguageServerChip
  } from '../files/components/editor/languageServerStatus.ts';

  interface Props {
    /** The open file's language, e.g. `csharp`. */
    language: string | null;
    /**
     * The last thing the desktop app said about that language's server: either
     * the answer to `read_source_lsp_status` or a pushed
     * `source-lsp-status-changed` message. Both carry the same two fields this
     * chip reads, so either one can be handed straight in.
     */
    status: unknown;
  }
  let { language, status }: Props = $props();

  const chip = $derived<LanguageServerChip | null>(
    language ? describeLanguageServer(language, status) : null
  );
</script>

{#if chip}
  <span class="server-chip" data-tone={chip.tone} title={chip.tooltip} aria-live="polite">
    <span class="server-dot" aria-hidden="true"></span>
    {chip.label}
  </span>
{/if}

<style>
  .server-chip {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    flex: 0 0 auto;
    border: 0;
    border-radius: 5px;
    color: var(--color-text-2);
    background: var(--color-elevated);
    font-size: 12px;
    line-height: 1;
    padding: 3px 7px;
    white-space: nowrap;
    user-select: none;
  }

  .server-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--color-idle);
    flex: 0 0 auto;
  }

  /* Ready: the server can answer questions now. */
  .server-chip[data-tone='ready'] {
    color: var(--color-good);
    background: var(--color-good-bg);
    border-color: transparent;
  }

  .server-chip[data-tone='ready'] .server-dot {
    background: var(--color-good);
  }

  /* Starting up or reading the project: answers are on their way. */
  .server-chip[data-tone='working'] {
    color: var(--color-attention);
    background: var(--color-attention-bg);
    border-color: transparent;
  }

  .server-chip[data-tone='working'] .server-dot {
    background: var(--color-attention);
  }

  /* Switched off, or never started for this project. */
  .server-chip[data-tone='off'] {
    color: var(--color-text-3);
  }
</style>
