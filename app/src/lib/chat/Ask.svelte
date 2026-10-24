<script lang="ts">
  import { getContext } from 'svelte';
  import type { AskChoice, AskField, AskPayload } from '../rows/store';
  import { ASK_ANSWER } from './tools/tool';
  // A card the agent opened: a permission answered with one of its options, or a question
  // answered with a form built from its flat JSON Schema.
  let { askId, ask }: { askId: string; ask: AskPayload } = $props();

  const post = getContext<(askId: string, answer: Record<string, unknown>) => Promise<unknown>>(ASK_ANSWER);
  const open = $derived(ask.state === 'open');
  const fields = $derived(Object.entries(ask.schema?.properties ?? {}));

  // The form's values by field name: a string, or the checked choices of a multi-select.
  let values = $state<Record<string, string | string[]>>({});
  let sending = $state(false);
  let error = $state('');

  const choices = (field: AskField): AskChoice[] | null =>
    field.oneOf ?? field.enum?.map((value) => ({ const: value })) ?? null;
  const many = (field: AskField): AskChoice[] | null =>
    field.type === 'array' ? (field.items?.anyOf ?? field.items?.enum?.map((value) => ({ const: value })) ?? null) : null;

  async function answer(body: Record<string, unknown>) {
    sending = true;
    error = '';
    try {
      await post(askId, body);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      sending = false;
    }
  }

  function submit() {
    const content: Record<string, unknown> = {};
    for (const [name] of fields) {
      const value = values[name];
      if (Array.isArray(value) ? value.length : value) content[name] = value;
    }
    void answer({ action: 'accept', content });
  }

  function toggle(name: string, value: string, on: boolean) {
    const list = (values[name] as string[] | undefined) ?? [];
    values[name] = on ? [...list, value] : list.filter((v) => v !== value);
  }

  // What a settled card says it was answered with.
  const outcome = $derived.by(() => {
    if (ask.state === 'cancelled') return 'Cancelled';
    const a = ask.answer;
    if (a?.optionId) return ask.options?.find((o) => o.optionId === a.optionId)?.name ?? a.optionId;
    if (a?.action === 'decline') return 'Skipped';
    if (a?.action === 'cancel') return 'Cancelled';
    const picked = Object.entries(a?.content ?? {})
      .filter(([name]) => !name.endsWith('_custom') || a?.content?.[name])
      .map(([, value]) => (Array.isArray(value) ? value.join(', ') : String(value)));
    return picked.length ? picked.join('; ') : 'Answered';
  });
</script>

<div class="ask" class:open>
  <div class="label">{ask.kind === 'permission' ? 'Permission' : 'Question'}</div>
  {#if ask.title}<div class="title">{ask.title}</div>{/if}
  {#if ask.message}<div class="message">{ask.message}</div>{/if}

  {#if !open}
    <div class="outcome">{outcome}</div>
  {:else if ask.kind === 'permission'}
    <div class="buttons">
      {#each ask.options ?? [] as option (option.optionId)}
        <button
          class={option.kind.startsWith('reject') ? 'reject' : option.kind === 'allow_once' ? 'primary' : ''}
          disabled={sending}
          onclick={() => answer({ optionId: option.optionId })}>{option.name}</button>
      {/each}
    </div>
  {:else}
    <form onsubmit={(e) => (e.preventDefault(), submit())}>
      {#each fields as [name, field] (name)}
        <fieldset>
          {#if field.title}<legend>{field.title}</legend>{/if}
          {#if field.description}<div class="hint">{field.description}</div>{/if}
          {#if many(field)}
            {#each many(field) ?? [] as choice (choice.const)}
              <label>
                <input
                  type="checkbox"
                  checked={((values[name] as string[] | undefined) ?? []).includes(choice.const)}
                  onchange={(e) => toggle(name, choice.const, e.currentTarget.checked)} />
                {choice.title ?? choice.const}
                {#if choice.description && choice.description !== (choice.title ?? choice.const)}<span class="hint">{choice.description}</span>{/if}
              </label>
            {/each}
          {:else if choices(field)}
            {#each choices(field) ?? [] as choice (choice.const)}
              <label>
                <input type="radio" name={`${askId}-${name}`} value={choice.const} bind:group={values[name]} />
                {choice.title ?? choice.const}
                {#if choice.description && choice.description !== (choice.title ?? choice.const)}<span class="hint">{choice.description}</span>{/if}
              </label>
            {/each}
          {:else}
            <input type="text" bind:value={values[name]} placeholder={name.endsWith('_custom') ? 'Other' : ''} />
          {/if}
        </fieldset>
      {/each}
      <div class="buttons">
        <button type="submit" class="primary" disabled={sending}>Submit</button>
        <button type="button" disabled={sending} onclick={() => answer({ action: 'decline' })}>Skip</button>
        <button type="button" class="reject" disabled={sending} onclick={() => answer({ action: 'cancel' })}>Cancel</button>
      </div>
    </form>
  {/if}
  {#if error}<div class="error">{error}</div>{/if}
</div>

<style>
  .ask {
    border: 1px solid var(--line);
    border-radius: 8px;
    padding: 12px;
    margin: 0 0 8px;
    font-size: 13px;
  }
  .ask.open {
    border-color: var(--accent);
  }
  .label {
    color: var(--muted);
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-bottom: 4px;
  }
  .title {
    font-weight: 600;
  }
  .message {
    white-space: pre-wrap;
  }
  .outcome {
    margin-top: 6px;
    color: var(--muted);
  }
  .buttons {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin-top: 10px;
  }
  button {
    font: inherit;
    padding: 4px 10px;
    border-radius: 6px;
    border: 1px solid var(--line);
    background: var(--code-bg);
    color: inherit;
    cursor: pointer;
  }
  button.primary {
    border-color: var(--accent);
    color: var(--accent);
  }
  button.reject {
    color: var(--danger);
  }
  button:disabled {
    opacity: 0.5;
    cursor: default;
  }
  fieldset {
    border: 0;
    padding: 0;
    margin: 10px 0 0;
  }
  legend {
    font-weight: 600;
    padding: 0;
    margin-bottom: 4px;
  }
  label {
    display: block;
    margin: 2px 0;
  }
  .hint {
    color: var(--muted);
    margin-left: 4px;
  }
  input[type='text'] {
    font: inherit;
    width: 100%;
    box-sizing: border-box;
    padding: 4px 6px;
    border: 1px solid var(--line);
    border-radius: 6px;
    background: transparent;
    color: inherit;
  }
  .error {
    color: var(--danger);
    margin-top: 6px;
  }
</style>
