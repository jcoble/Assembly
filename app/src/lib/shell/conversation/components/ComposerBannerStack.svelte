<script lang="ts">
  import X from '@lucide/svelte/icons/x';

  export type ComposerBannerVariant = 'default' | 'error' | 'info' | 'success' | 'warning';
  export interface ComposerBannerItem {
    id: string;
    variant: ComposerBannerVariant;
    title: string;
    description?: string;
    dismissLabel?: string;
    onDismiss?(): void;
  }

  interface Props { items: readonly ComposerBannerItem[]; }
  let { items }: Props = $props();
  let expanded = $state(false);
  const front = $derived(items[0] ?? null);
  const rest = $derived(items.slice(1));
</script>

{#if front}
  <div class="banner-stack" class:has-stack={rest.length > 0} role="region" aria-label="Composer notices" onmouseenter={() => (expanded = true)} onmouseleave={() => (expanded = false)} onfocusin={() => (expanded = true)} onfocusout={(event) => { if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) expanded = false; }}>
    {#if rest.length && !expanded}<div class={`stack-cap ${rest[0]?.variant ?? 'default'}`} aria-hidden="true"></div>{/if}
    <article class={`banner ${front.variant}`} data-testid="conversation-composer-banner">
      <div class="banner-copy"><strong>{front.title}</strong>{#if front.description}<span>{front.description}</span>{/if}</div>
      {#if front.onDismiss}<button class="dismiss" type="button" aria-label={front.dismissLabel ?? 'Dismiss notice'} onclick={() => front.onDismiss?.()}><X size={14} /></button>{/if}
    </article>
    {#if expanded && rest.length}<div class="stacked-items">{#each rest as item (item.id)}<article class={`banner ${item.variant}`}><div class="banner-copy"><strong>{item.title}</strong>{#if item.description}<span>{item.description}</span>{/if}</div>{#if item.onDismiss}<button class="dismiss" type="button" aria-label={item.dismissLabel ?? 'Dismiss notice'} onclick={() => item.onDismiss?.()}><X size={14} /></button>{/if}</article>{/each}</div>{/if}
  </div>
{/if}

<style>
  .banner-stack { position: relative; width: min(820px, calc(100% - 44px)); margin: 0 auto 8px; }
  .stack-cap { position: absolute; inset: -5px 2% auto; height: 8px; border: 1px solid var(--color-border); border-bottom: 0; border-radius: 14px 14px 0 0; background: var(--color-surface); }
  .stack-cap.error { border-color: color-mix(in srgb, var(--color-bad) 35%, var(--color-border)); }
  .stack-cap.warning { border-color: color-mix(in srgb, var(--color-attention) 35%, var(--color-border)); }
  .stack-cap.success { border-color: color-mix(in srgb, var(--color-good) 35%, var(--color-border)); }
  .stack-cap.info { border-color: color-mix(in srgb, var(--color-accent) 35%, var(--color-border)); }
  .banner { position: relative; display: flex; align-items: flex-start; gap: 10px; min-height: 38px; padding: 9px 11px; border: 1px solid var(--color-border); border-radius: 14px; background: color-mix(in srgb, var(--color-surface) 94%, transparent); box-shadow: var(--shadow-sm); }
  .banner.error { border-color: color-mix(in srgb, var(--color-bad) 38%, var(--color-border)); }
  .banner.warning { border-color: color-mix(in srgb, var(--color-attention) 38%, var(--color-border)); }
  .banner.success { border-color: color-mix(in srgb, var(--color-good) 34%, var(--color-border)); }
  .banner.info { border-color: color-mix(in srgb, var(--color-accent) 34%, var(--color-border)); }
  .banner-copy { display: grid; min-width: 0; gap: 2px; }
  .banner-copy strong { font-size: 13px; font-weight: 650; }
  .banner-copy span { color: var(--color-text-2); font-size: 12px; line-height: 1.4; }
  .dismiss { display: inline-grid; place-items: center; flex: none; min-width: 24px; min-height: 24px; margin-left: auto; border: 0; border-radius: 6px; background: transparent; color: var(--color-text-2); cursor: pointer; }
  .dismiss:hover { background: var(--color-hover); color: var(--color-text); }
  .dismiss:focus-visible { outline: 2px solid var(--color-focus-solid); outline-offset: 1px; }
  .stacked-items { display: grid; gap: 7px; margin-top: 7px; }
</style>
