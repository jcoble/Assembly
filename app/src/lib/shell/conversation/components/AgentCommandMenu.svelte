<script lang="ts">
  import type { ConversationCommand } from '$lib/shell/conversation/conversationCommandCatalog.ts';
  import { slashName } from '$lib/shell/conversation/conversationCommandCatalog.ts';

  interface Props {
    commands: readonly ConversationCommand[];
    activeIndex: number;
    emptyText?: string;
    onSelect?(command: ConversationCommand): void;
  }
  let { commands, activeIndex, emptyText = '', onSelect }: Props = $props();
  const visible = $derived(commands);
  let rowEls: (HTMLButtonElement | null)[] = $state([]);

  // The keyboard can move the highlight past what's currently scrolled into
  // view (arrowing through a long list); this keeps the highlighted row on
  // screen without disturbing scroll position when it's already visible.
  $effect(() => {
    rowEls[activeIndex]?.scrollIntoView({ block: 'nearest' });
  });

  function sourceLabel(command: ConversationCommand): string {
    if (command.source === 'assembly') return 'Built-in';
    const description = command.description?.toLowerCase() ?? '';
    if (description.includes('(bundled)')) return 'Built-in';
    if (description.includes('(user)')) return 'Custom';
    return command.source === 'skill' ? 'Skill' : 'Provider';
  }
</script>

<div class="command-menu" data-testid="conversation-command-menu" role="listbox" aria-label="Conversation commands" tabindex="-1">
  {#if visible.length}
    {#each visible as command, index (command.id)}
      <button
        bind:this={rowEls[index]}
        class:active={index === activeIndex}
        id={`conversation-command-${command.id}`}
        data-testid="conversation-command-row"
        role="option"
        aria-selected={index === activeIndex}
        aria-label={`${slashName(command)} ${command.description ?? command.label}`}
        type="button"
        onclick={() => onSelect?.(command)}
      >
        <span class="command-glyph" aria-hidden="true">/</span>
        <span class="command-main">
          <strong>{slashName(command)}</strong>
          <span class="command-label">{command.label}</span>
          {#if command.description}<span class="command-description">{command.description}</span>{/if}
        </span>
        <span class:assembly={command.source === 'assembly'} class="command-source">{sourceLabel(command)}</span>
      </button>
    {/each}
  {:else}
    <p class="command-empty" data-testid="conversation-command-empty">{emptyText || 'No commands'}</p>
  {/if}
</div>

<style>
  /* Anchored to the top edge of the input zone rather than a fixed offset from
     its bottom: the composer grows as you type, and a fixed offset walked into
     the line being typed. */
  .command-menu { position: absolute; left: 0; right: 0; bottom: calc(100% + 8px); z-index: 4; display: flex; flex-direction: column; max-height: min(58vh, 520px); overflow: auto; padding: var(--menu-sheet-inset); border: 1px solid var(--color-border); border-radius: var(--menu-sheet-radius); background: var(--menu-sheet-surface); box-shadow: var(--shadow-lg); }
  /* `flex: none` so a long list scrolls instead of squashing the rows into each
     other — the column has a max height, and flex children shrink by default. */
  .command-menu button { display: flex; flex: none; align-items: flex-start; gap: var(--menu-row-gap); width: 100%; min-height: 42px; border: 0; border-radius: var(--menu-row-radius); background: transparent; color: inherit; padding: var(--menu-row-inset); text-align: left; cursor: pointer; }
  .command-menu button:hover { background: var(--menu-row-hover); }
  .command-menu button.active { background: var(--menu-row-active); }
  .command-menu button:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: -1px; }
  .command-glyph { display: grid; place-items: center; flex: none; width: 22px; height: 22px; border: 1px solid var(--color-border); border-radius: var(--radius-sm); color: var(--color-accent); font: 600 13px var(--font-mono); }
  .command-main { display: grid; min-width: 0; flex: 1; gap: var(--menu-row-description-gap); }
  .command-main strong { color: var(--color-text); font: 600 13px var(--font-mono); }
  .command-label, .command-description, .command-source { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .command-label { color: var(--color-text); font-size: 13px; }
  .command-description { color: var(--color-text-2); font-size: 12px; }
  .command-source { flex: 0 0 auto; max-width: 100px; padding-top: 2px; color: var(--color-text-2); font-size: 12px; }
  .command-source.assembly { color: var(--color-accent); }
  .command-empty { margin: 0; padding: var(--menu-row-inset); color: var(--color-text-2); font-size: 13px; }
</style>
