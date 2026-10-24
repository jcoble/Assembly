<script lang="ts">
  import Check from '@lucide/svelte/icons/check';
  import type { AgentConfigValue, AgentUserInputAction, AgentUserInputField, AgentUserInputRequest } from '$lib/shell/conversation/conversationTypes.ts';

  interface Props {
    requests: readonly AgentUserInputRequest[];
    responding?: boolean;
    disabled?: boolean;
    onSubmit?(requestId: string, action: AgentUserInputAction, content: Record<string, AgentConfigValue>): void | Promise<void>;
  }

  let { requests, responding = false, disabled = false, onSubmit }: Props = $props();
  let values = $state<Record<string, AgentConfigValue>>({});
  let activeRequestId = $state('');

  const activeRequest = $derived(requests[0] ?? null);

  $effect(() => {
    const requestId = activeRequest?.requestId ?? '';
    if (requestId === activeRequestId) return;
    activeRequestId = requestId;
    values = Object.fromEntries((activeRequest?.fields ?? []).filter((field) => !(activeRequest?.canDecline && field.kind === 'select' && !field.required)).map((field) => [
      field.id,
      field.kind === 'boolean'
        ? false
        : field.kind === 'multi-select'
          ? []
          : field.kind === 'select' && (!activeRequest?.canDecline || field.required)
            ? field.choices?.[0]?.value ?? ''
            : ''
    ]));
  });

  function valueFor(field: AgentUserInputField): AgentConfigValue {
    return values[field.id] ?? '';
  }

  function setValue(field: AgentUserInputField, value: AgentConfigValue): void {
    values[field.id] = value;
  }

  function toggleChoice(field: AgentUserInputField, value: AgentConfigValue): void {
    if (field.kind === 'multi-select') {
      const selected = Array.isArray(values[field.id]) ? values[field.id] as AgentConfigValue[] : [];
      values[field.id] = selected.includes(value) ? selected.filter((entry) => entry !== value) : [...selected, value];
    } else if (activeRequest?.canDecline && !field.required && values[field.id] === value) {
      delete values[field.id];
    } else {
      setValue(field, value);
    }
  }

  function submit(action: AgentUserInputAction = 'accept'): void {
    if (!activeRequest) return;
    if (responding || disabled) return;
    onSubmit?.(activeRequest.requestId, action, action === 'accept' ? { ...values } : {});
  }
</script>

{#if activeRequest}
  <section class="input-panel" data-testid="conversation-pending-user-input" aria-labelledby="pending-input-title">
    <div class="input-heading">
      <span class="input-kicker">Input requested</span>
      {#if requests.length > 1}<span class="input-count">1/{requests.length}</span>{/if}
    </div>
    <h3 id="pending-input-title">{activeRequest.title}</h3>
    {#if activeRequest.description}<p class="input-description">{activeRequest.description}</p>{/if}
    <form onsubmit={(event) => { event.preventDefault(); submit(); }}>
      {#each activeRequest.fields as field (field.id)}
        <fieldset class="input-field">
          <legend>{field.label}{field.required ? '' : ' (optional)'}</legend>
          {#if field.description}<p class="field-description">{field.description}</p>{/if}
          {#if field.kind === 'select'}
            <div class="choice-list" role="radiogroup" aria-label={field.label}>
              {#each field.choices ?? [] as choice (JSON.stringify(choice.value))}
                {@const selected = JSON.stringify(valueFor(field)) === JSON.stringify(choice.value)}
                <button
                  class:selected
                  type="button"
                  role="radio"
                  aria-checked={selected}
                  disabled={responding || disabled}
                  onclick={() => toggleChoice(field, choice.value)}
                >
                  <span class="choice-copy"><strong>{choice.label}</strong>{#if choice.description}<small>{choice.description}</small>{/if}</span>
                  {#if selected}<Check size={14} aria-hidden="true" />{/if}
                </button>
              {/each}
            </div>
          {:else if field.kind === 'multi-select'}
            <div class="choice-list">
              {#each field.choices ?? [] as choice (JSON.stringify(choice.value))}
                {@const selected = Array.isArray(valueFor(field)) && (valueFor(field) as AgentConfigValue[]).includes(choice.value)}
                <label class="multiple-choice" class:selected>
                  <input type="checkbox" checked={selected} disabled={responding || disabled} onchange={() => toggleChoice(field, choice.value)} />
                  <span class="choice-copy"><strong>{choice.label}</strong>{#if choice.description}<small>{choice.description}</small>{/if}</span>
                </label>
              {/each}
            </div>
          {:else if field.kind === 'boolean'}
            <label class="boolean-field"><input type="checkbox" checked={valueFor(field) === true} disabled={responding || disabled} onchange={(event) => setValue(field, event.currentTarget.checked)} /><span>Yes</span></label>
          {:else}
            <input class="text-field" required={field.required} type={field.kind === 'password' ? 'password' : 'text'} value={String(valueFor(field))} disabled={responding || disabled} oninput={(event) => setValue(field, event.currentTarget.value)} />
          {/if}
        </fieldset>
      {/each}
      <div class="input-actions">
        <button class="cancel" type="button" disabled={responding || disabled} onclick={() => submit('cancel')}>Cancel</button>
        {#if activeRequest.canDecline}<button class="cancel" type="button" disabled={responding || disabled} onclick={() => submit('decline')}>Skip</button>{/if}
        <button class="continue" type="submit" disabled={responding || disabled}>{responding ? 'Submitting…' : 'Continue'}</button>
      </div>
    </form>
  </section>
{/if}

<style>
  .input-panel { padding: 16px 18px 14px; border-bottom: 1px solid color-mix(in srgb, var(--color-border) 68%, transparent); background: color-mix(in srgb, var(--color-surface) 84%, var(--color-accent) 6%); }
  .input-heading { display: flex; align-items: center; gap: 8px; color: var(--color-text-2); }
  .input-kicker { font-size: 12px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; }
  .input-count { padding: 2px 6px; border-radius: 6px; background: color-mix(in srgb, var(--color-surface) 80%, transparent); font-size: 12px; font-variant-numeric: tabular-nums; }
  h3 { margin: 9px 0 0; font-size: 14px; font-weight: 650; line-height: 1.35; }
  .input-description { margin: 5px 0 0; color: var(--color-text-2); font-size: 13px; line-height: 1.5; }
  form { display: grid; gap: 10px; margin-top: 13px; }
  .input-field { min-width: 0; margin: 0; padding: 0; border: 0; }
  legend { color: var(--color-text); font-size: 13px; font-weight: 600; }
  .field-description { margin: 3px 0 6px; color: var(--color-text-2); font-size: 12px; }
  .choice-list { display: grid; gap: 5px; margin-top: 6px; }
  .choice-list button, .multiple-choice { display: flex; align-items: center; gap: 10px; width: 100%; min-height: 32px; padding: 6px 9px; border: 1px solid transparent; border-radius: 9px; background: color-mix(in srgb, var(--color-surface) 55%, transparent); color: var(--color-text); text-align: left; cursor: pointer; }
  .choice-list button:hover:not(:disabled) { border-color: color-mix(in srgb, var(--color-border) 65%, transparent); background: var(--color-hover); }
  .choice-list button.selected, .multiple-choice.selected { border-color: color-mix(in srgb, var(--color-accent) 36%, var(--color-border)); background: color-mix(in srgb, var(--color-accent) 10%, var(--color-surface)); }
  .choice-list button:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 2px; }
  .multiple-choice input { accent-color: var(--color-accent); }
  .choice-copy { display: grid; min-width: 0; gap: 2px; }
  .choice-copy strong { font-size: 13px; font-weight: 600; }
  .choice-copy small { color: var(--color-text-2); font-size: 12px; }
  .text-field { width: 100%; min-height: 30px; margin-top: 6px; padding: 5px 8px; border: 1px solid var(--color-border); border-radius: 8px; background: var(--color-bg); color: var(--color-text); font: 13px/1.35 inherit; }
  .text-field:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 1px; }
  .boolean-field { display: inline-flex; align-items: center; gap: 7px; margin-top: 7px; color: var(--color-text-2); font-size: 13px; }
  .boolean-field input { accent-color: var(--color-accent); }
  .input-actions { display: flex; justify-content: flex-end; gap: 7px; margin-top: 2px; }
  .input-actions button { min-height: 28px; padding: 5px 11px; border-radius: 999px; font: 600 12px/1.2 inherit; cursor: pointer; }
  .input-actions .cancel { border: 1px solid var(--color-border); background: var(--color-elevated); color: var(--color-text-2); }
  .input-actions .continue { border: 1px solid var(--color-accent); background: var(--color-accent); color: var(--color-on-accent); }
  .input-actions button:hover:not(:disabled) { filter: brightness(1.05); }
  .input-actions button:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 2px; }
  .input-actions button:disabled { cursor: wait; opacity: .5; }
</style>
