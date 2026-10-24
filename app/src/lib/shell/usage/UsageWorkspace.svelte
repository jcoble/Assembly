<script lang="ts">
  import { ChevronDown, RefreshCw, X } from '@lucide/svelte';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { formatMonthDay } from '$lib/shell/dateFormat.ts';
  import {
    buildProviderUsageTrend,
    buildUsageHeatmap,
    usageRangeLabel,
    usageRangeOptions
  } from './usageAnalytics.ts';
  import { USAGE_COST_RATE_VERSION } from './usageCostModel.ts';
  import {
    usageDisplayLabel,
    usageDisplayPercent,
    usageProviderLabel,
    usageQuotaWindowLabel,
    usageResetLabel,
    type UsageDisplayMode
  } from './usageCurrent.ts';
  import { refreshUsageHistory, selectUsageRange, usageState } from './usageStore.svelte.ts';
  import type { UsageProviderSummaryRow } from './usageTypes.ts';

  interface Props {
    onClose?: () => void;
    displayMode?: UsageDisplayMode;
    onToggleDisplayMode?: () => void;
  }
  let {
    onClose = () => undefined,
    displayMode = 'used',
    onToggleDisplayMode = () => undefined
  }: Props = $props();

  type RollupRange = 'today' | 'yesterday' | '30-days';
  // Only Codex/Claude have usage readers: usage_sources.rs::discover_local_usage_sources and usage_current.rs::read_current_provider_usage.
  const providerNames = ['codex', 'claude'] as const;
  const rollupRanges: ReadonlyArray<{ value: RollupRange; label: string }> = [
    { value: 'today', label: 'Today' },
    { value: 'yesterday', label: 'Yesterday' },
    { value: '30-days', label: 'Last 30 Days' }
  ];
  const integerFormat = new Intl.NumberFormat('en-US');
  const compactNumberFormat = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 });
  const compactCurrency = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
  const preciseCurrency = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const donutRadius = 39;
  const donutCircumference = 2 * Math.PI * donutRadius;

  let totalTokens = $derived(usageState.summary?.totalTokens ?? 0);
  let cacheShare = $derived(usageState.summary?.cacheSharePercent ?? 0);
  let heatmapDays = $derived(buildUsageHeatmap(usageState.dailyTotals, usageState.range));
  let estimatedCost = $derived({
    estimateMicros: usageState.providerSummary[0]?.rangeEstimatedCostMicros ?? null,
    unpricedPercent: usageState.providerSummary[0]?.rangeUnpricedPercent ?? 0
  });
  let bestDay = $derived(usageState.dailyTotals[0]?.bestDay ?? null);
  let knownProviderCostTotal = $derived(estimatedCost.estimateMicros ?? 0);

  function visibleTokens(input: number): string {
    return compactNumberFormat.format(input);
  }

  function costLabel(value: number | null): string {
    if (value == null) return 'Unavailable';
    const dollars = value / 1_000_000;
    return dollars >= 100 ? compactCurrency.format(dollars) : preciseCurrency.format(dollars);
  }

  function unpricedLabel(estimate: { estimateMicros: number | null; unpricedPercent: number }): string | null {
    if (estimate.estimateMicros == null || estimate.unpricedPercent <= 0) return null;
    return `excludes ${estimate.unpricedPercent}% unpriced`;
  }

  function formatDay(day: string): string {
    const parsed = new Date(`${day}T00:00:00`);
    return Number.isNaN(parsed.getTime()) ? day : formatMonthDay(parsed);
  }

  function formatLastSeen(value: number | null): string {
    if (value == null) return 'No recent session';
    return `Last seen ${formatMonthDay(new Date(value / 1_000))}`;
  }

  function selectedProviderCard(provider: string): UsageProviderSummaryRow | null {
    return usageState.providerSummary.find((card) => card.provider === provider) ?? null;
  }

  function rollupCard(provider: string, range: RollupRange): UsageProviderSummaryRow | null {
    return usageState.providerRollups[range].find((card) => card.provider === provider) ?? null;
  }

  function selectedProviderEstimate(provider: string): { estimateMicros: number | null; unpricedPercent: number } {
    const card = selectedProviderCard(provider);
    return { estimateMicros: card?.estimatedCostMicros ?? null, unpricedPercent: card?.unpricedPercent ?? 0 };
  }

  function providerEstimate(card: UsageProviderSummaryRow | null): { estimateMicros: number | null; unpricedPercent: number } {
    return { estimateMicros: card?.estimatedCostMicros ?? null, unpricedPercent: card?.unpricedPercent ?? 0 };
  }

  function providerShare(card: UsageProviderSummaryRow): number {
    return card.rangeSharePercent;
  }

  function mixWidth(value: number): number {
    return totalTokens === 0 ? 0 : (value / totalTokens) * 100;
  }

  function trendPoints(provider: string): string {
    return buildProviderUsageTrend(usageState.providerDailyTotals, provider)
      .map((point) => `${point.x},${point.y}`)
      .join(' ');
  }

  function planLabel(provider: string): string {
    return usageState.currentByProvider[provider]?.account ?? 'Local history';
  }

  function quotaUnavailableMessage(snapshot: { unavailableReason: string | null } | null): string {
    return snapshot?.unavailableReason?.trim() || 'Live quota is unavailable.';
  }

  function donutLength(provider: string): number {
    const cost = selectedProviderCard(provider)?.estimatedCostMicros ?? 0;
    return knownProviderCostTotal === 0 ? 0 : (cost / knownProviderCostTotal) * donutCircumference;
  }

  function donutOffset(provider: string): number {
    return provider === 'claude' ? -donutLength('codex') : 0;
  }
</script>

<section class="workspace" data-testid="usage-workspace" aria-label="Stats and Usage" aria-busy={usageState.loading}>
  <header class="workspace-header">
    <div class="title-block">
      <p class="eyebrow">Stats &amp; Usage</p>
      <h2>Local usage, clearly accounted for</h2>
      <p class="muted">Private session history indexed on this Mac.</p>
    </div>
    <div class="header-actions">
      <div class="range-selector" aria-label="Usage date range">
        {#each usageRangeOptions as option (option.value)}
          <button
            type="button"
            class:active={usageState.range === option.value}
            aria-pressed={usageState.range === option.value}
            disabled={usageState.historyLoading}
            onclick={() => void selectUsageRange(option.value)}
          >{option.label}</button>
        {/each}
      </div>
      <button class="refresh" type="button" onclick={() => void refreshUsageHistory()} disabled={usageState.historyLoading || usageState.historyRefreshing}>
        {#if usageState.historyRefreshing || usageState.historyLoading}<WorkingSpinner size={14} />{usageState.historyRefreshing ? 'Refreshing…' : 'Loading…'}{:else}<RefreshCw size={14} aria-hidden="true" />Refresh{/if}
      </button>
      <button class="close" type="button" aria-label="Close Stats and Usage" onclick={onClose}><X size={17} aria-hidden="true" /></button>
    </div>
  </header>

  {#if usageState.summary}
    <div class="overview-grid">
      <section class="cost-panel" aria-labelledby="cost-title">
        <div class="donut-wrap">
          <svg class="donut" viewBox="0 0 100 100" role="img" aria-label={`Estimated cost ${costLabel(estimatedCost.estimateMicros)}${unpricedLabel(estimatedCost) ? `, ${unpricedLabel(estimatedCost)}` : ''}`}>
            <circle class="donut-track" cx="50" cy="50" r={donutRadius}></circle>
            {#each providerNames as provider (provider)}
              <circle
                class={`donut-segment ${provider}`}
                cx="50"
                cy="50"
                r={donutRadius}
                stroke-dasharray={`${donutLength(provider)} ${donutCircumference}`}
                stroke-dashoffset={donutOffset(provider)}
              ></circle>
            {/each}
          </svg>
          <div class="donut-total">
            <span>Estimated</span>
            <strong>{costLabel(estimatedCost.estimateMicros)}</strong>
            {#if unpricedLabel(estimatedCost)}<small class="unpriced-note">{unpricedLabel(estimatedCost)}</small>{/if}
          </div>
        </div>
        <div class="cost-copy">
          <div>
            <h3 id="cost-title">Cost by provider</h3>
            <p class="muted">{usageRangeLabel(usageState.range)} at published token rates.</p>
          </div>
          <div class="cost-legend">
            {#each providerNames as provider (provider)}
              {@const card = selectedProviderCard(provider)}
              {@const estimate = selectedProviderEstimate(provider)}
              <div>
                <i class={provider}></i>
                <span>{usageProviderLabel(provider)}</span>
                <strong>{costLabel(estimate.estimateMicros)}</strong>
                <small>{visibleTokens(card?.totalTokens ?? 0)} tokens{unpricedLabel(estimate) ? ` · ${unpricedLabel(estimate)}` : ''}</small>
              </div>
            {/each}
          </div>
        </div>
      </section>

      <div class="metric-grid" aria-label={`${usageRangeLabel(usageState.range)} summary`}>
        <article><span>Total tokens</span><strong>{visibleTokens(totalTokens)}</strong><small>{integerFormat.format(totalTokens)} indexed</small></article>
        <article><span>Active days</span><strong>{integerFormat.format(usageState.summary.activeDays)}</strong><small>{usageRangeLabel(usageState.range)}</small></article>
        <article><span>Sessions</span><strong>{visibleTokens(usageState.summary.sessionCount)}</strong><small>{integerFormat.format(usageState.summary.turnCount)} turns</small></article>
        <article><span>Cache share</span><strong>{cacheShare}%</strong><small>{visibleTokens(usageState.summary.cacheReadTokens + usageState.summary.cacheWriteTokens)} cached</small></article>
      </div>
    </div>

    <div class="analytics-grid">
      <section class="panel intensity-panel" aria-labelledby="daily-intensity-title">
        <div class="panel-heading">
          <div><h3 id="daily-intensity-title">Six-week activity</h3><p class="muted">A full 7-row × 6-week token map.</p></div>
          {#if bestDay}<span class="quiet-label">Best: {formatDay(bestDay)}</span>{/if}
        </div>
        <div class="heatmap-stage">
          <div class="heatmap" role="img" aria-label="Daily token intensity across six weeks">
            {#each heatmapDays as day (day.day)}
              <span class={`level-${day.intensity}`} title={`${formatDay(day.day)} · ${visibleTokens(day.totalTokens)} tokens`}></span>
            {/each}
          </div>
        </div>
        <div class="heatmap-footer">
          <span>{heatmapDays[0] ? formatDay(heatmapDays[0].day) : 'No data'}</span>
          <span class="heat-scale">Less <i class="level-0"></i><i class="level-1"></i><i class="level-2"></i><i class="level-3"></i><i class="level-4"></i> More</span>
          <span>{heatmapDays.at(-1) ? formatDay(heatmapDays.at(-1)!.day) : ''}</span>
        </div>
      </section>

      <section class="panel token-panel" aria-labelledby="token-mix-title">
        <div class="panel-heading"><div><h3 id="token-mix-title">Token mix</h3><p class="muted">New input, output, and cached context.</p></div></div>
        <div class="mix-total"><strong>{visibleTokens(totalTokens)}</strong><span>tokens</span></div>
        <div class="mix-bar" aria-label="Token type share">
          <i class="input" style={`width: ${mixWidth(usageState.summary.inputTokens)}%`}></i>
          <i class="output" style={`width: ${mixWidth(usageState.summary.outputTokens)}%`}></i>
          <i class="cache" style={`width: ${mixWidth(usageState.summary.cacheReadTokens + usageState.summary.cacheWriteTokens)}%`}></i>
        </div>
        <div class="mix-legend">
          <span><i class="dot input"></i><b>New input</b><strong>{visibleTokens(usageState.summary.inputTokens)}</strong></span>
          <span><i class="dot output"></i><b>Output</b><strong>{visibleTokens(usageState.summary.outputTokens)}</strong></span>
          <span><i class="dot cache"></i><b>Cache</b><strong>{visibleTokens(usageState.summary.cacheReadTokens + usageState.summary.cacheWriteTokens)}</strong></span>
        </div>
      </section>
    </div>

    <section class="providers" aria-labelledby="providers-title">
      <div class="section-heading">
        <div><h3 id="providers-title">Provider detail</h3><p class="muted">Live windows and local history stay separate, but visible together.</p></div>
        <span class="quiet-label">{integerFormat.format(usageState.summary.eventCount)} events</span>
      </div>

      <div class="provider-grid">
        {#each providerNames as provider (provider)}
          {@const card = selectedProviderCard(provider)}
          {@const snapshot = usageState.currentByProvider[provider] ?? null}
          {@const selectedEstimate = selectedProviderEstimate(provider)}
          <article class="provider-card">
            <div class="provider-heading">
              <div><h4>{usageProviderLabel(provider)}</h4><span>{planLabel(provider)} · {card ? formatLastSeen(card.lastSeenAtMicros) : 'No history in this range'}</span></div>
              <strong>{card ? `${providerShare(card)}%` : '0%'}</strong>
            </div>

            {#if snapshot?.state === 'available' && snapshot.windows.length > 0}
              <div class="live-windows" aria-label={`${usageProviderLabel(provider)} live quota`}>
                {#each snapshot.windows as window, index (`${window.label}-${window.windowMinutes ?? index}`)}
                  <div class="live-window">
                    <div><span>{usageQuotaWindowLabel(window)}</span><small>{usageResetLabel(window.resetsAt)}</small></div>
                    <button
                      type="button"
                      class="quota-figure"
                      data-testid="usage-display-toggle"
                      title={displayMode === 'used' ? 'Show how much is left' : 'Show how much has been used'}
                      onclick={onToggleDisplayMode}
                    >
                      {usageDisplayLabel(window, displayMode)}
                    </button>
                    <div class="quota-bar"><i style={`width: ${usageDisplayPercent(window, displayMode)}%`}></i></div>
                  </div>
                {/each}
              </div>
            {:else}
              <div class="quota-empty"><span>Live quota</span><p>{quotaUnavailableMessage(snapshot)}</p></div>
            {/if}

            <div class="provider-metrics">
              <span><small>Tokens</small><strong>{visibleTokens(card?.totalTokens ?? 0)}</strong></span>
              <span>
                <small>Estimated cost</small>
                <strong>{costLabel(selectedEstimate.estimateMicros)}</strong>
                {#if unpricedLabel(selectedEstimate)}<small class="unpriced-note">{unpricedLabel(selectedEstimate)}</small>{/if}
              </span>
              <span><small>Sessions</small><strong>{integerFormat.format(card?.sessionCount ?? 0)}</strong></span>
              <span><small>Events / turns</small><strong>{integerFormat.format(card?.eventCount ?? 0)} / {integerFormat.format(card?.turnCount ?? 0)}</strong></span>
            </div>

            <div class="trend-block">
              <div><span>Daily tokens</span><small>Last 30 days</small></div>
              <svg viewBox="0 0 100 32" preserveAspectRatio="none" role="img" aria-label={`${usageProviderLabel(provider)} daily token trend`}>
                <line x1="0" y1="28" x2="100" y2="28"></line>
                <polyline class={provider} points={trendPoints(provider)}></polyline>
              </svg>
            </div>

            <div class="rollup-list">
              {#each rollupRanges as rollup (rollup.value)}
                {@const rollupData = rollupCard(provider, rollup.value)}
                {@const rollupEstimate = providerEstimate(rollupData)}
                <details>
                  <summary>
                    <span>{rollup.label}</span>
                    <span>
                      {costLabel(rollupEstimate.estimateMicros)}
                      {#if unpricedLabel(rollupEstimate)}<small class="unpriced-note">({unpricedLabel(rollupEstimate)})</small>{/if}
                      · {visibleTokens(rollupData?.totalTokens ?? 0)} tokens <ChevronDown size={14} aria-hidden="true" />
                    </span>
                  </summary>
                  <div class="rollup-detail">
                    <span><small>Sessions</small><strong>{integerFormat.format(rollupData?.sessionCount ?? 0)}</strong></span>
                    <span><small>Turns</small><strong>{integerFormat.format(rollupData?.turnCount ?? 0)}</strong></span>
                    <span><small>Models</small><strong>{rollupData?.modelCount ?? 0}</strong></span>
                  </div>
                </details>
              {/each}
            </div>
          </article>
        {/each}
      </div>
    </section>

    <p class="estimate-note">Costs are estimates, not billing totals · {USAGE_COST_RATE_VERSION.replace('published-api-rates-', 'rates updated ')}</p>
  {:else}
    <div class="empty">
      <strong>{usageState.error ? 'Usage history is unavailable' : 'Index local usage to see analytics'}</strong>
      <p>{usageState.error ?? 'Refresh to build the private SQLite index from local sessions.'}</p>
    </div>
  {/if}
</section>

<style>
  .workspace { display: grid; gap: 14px; max-height: min(90vh, 920px); overflow-y: auto; padding: 20px; color: var(--color-text); background: var(--color-surface); }
  h2, h3, h4, p { margin: 0; }
  h2 { font-size: 1.42rem; line-height: 1.2; letter-spacing: -0.028em; }
  h3 { font-size: 0.88rem; line-height: 1.35; }
  h4 { font-size: 0.94rem; line-height: 1.3; }
  button { border: 0; font: inherit; cursor: pointer; }
  .muted, small { color: var(--color-text-2); }
  .muted { font-size: 0.76rem; line-height: 1.45; }
  .workspace-header { position: sticky; top: -20px; z-index: 5; display: flex; justify-content: space-between; gap: 16px; margin: -20px -20px 0; padding: 18px 20px 14px; background: color-mix(in srgb, var(--color-surface) 94%, transparent); backdrop-filter: blur(10px); }
  .title-block { display: grid; gap: 3px; }
  .eyebrow { color: var(--color-text-3); font-size: 0.75rem; font-weight: 680; letter-spacing: 0.085em; text-transform: uppercase; }
  .header-actions, .panel-heading, .section-heading, .provider-heading { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
  .header-actions { align-items: center; }
  .range-selector { display: flex; gap: 2px; padding: 3px; border-radius: var(--radius-md, 9px); background: color-mix(in srgb, var(--color-bg) 68%, var(--color-surface)); }
  .range-selector button, .refresh, .close { min-height: 30px; border-radius: var(--radius-sm, 7px); padding: 5px 10px; color: var(--color-text-2); background: transparent; }
  .range-selector button:hover:not(:disabled), .refresh:hover:not(:disabled), .close:hover:not(:disabled) { color: var(--color-text); background: var(--color-hover); }
  .range-selector button.active { color: var(--color-text); background: var(--color-elevated); }
  .refresh { display: inline-flex; align-items: center; gap: 6px; background: color-mix(in srgb, var(--color-elevated) 72%, transparent); }
  .close { display: grid; width: 30px; padding: 0; place-items: center; }
  button:focus-visible, summary:focus-visible { outline: none; box-shadow: var(--focus-ring); }
  button:disabled { cursor: not-allowed; opacity: 0.52; }
  .overview-grid { display: grid; grid-template-columns: minmax(420px, 1.15fr) minmax(360px, 1fr); gap: 10px; }
  .cost-panel, .panel, .providers { border: 1px solid color-mix(in srgb, var(--color-border) 36%, transparent); border-radius: var(--radius-lg, 12px); background: color-mix(in srgb, var(--color-elevated) 38%, var(--color-surface)); }
  .cost-panel { display: grid; grid-template-columns: 136px 1fr; gap: 18px; align-items: center; padding: 15px; }
  .donut-wrap { position: relative; width: 126px; height: 126px; }
  .donut { width: 100%; height: 100%; transform: rotate(-90deg); overflow: visible; }
  .donut circle { fill: none; stroke-width: 10; }
  .donut-track { stroke: color-mix(in srgb, var(--color-border) 38%, transparent); }
  .donut-segment { stroke-linecap: butt; transition: stroke-dasharray 180ms ease; }
  .donut-segment.codex { stroke: var(--color-accent); }
  .donut-segment.claude { stroke: var(--color-live); }
  .donut-total { position: absolute; inset: 0; display: grid; max-width: 92px; margin: auto; place-content: center; text-align: center; }
  .donut-total span { color: var(--color-text-3); font-size: 0.75rem; }
  .donut-total strong { font-size: 0.94rem; font-variant-numeric: tabular-nums; }
  .unpriced-note { color: var(--color-text-3); font-size: 0.75rem; font-weight: 500; line-height: 1.25; }
  .cost-copy { display: grid; gap: 13px; }
  .cost-copy > div:first-child { display: grid; gap: 3px; }
  .cost-legend { display: grid; gap: 8px; }
  .cost-legend > div { display: grid; grid-template-columns: 9px 1fr auto; gap: 2px 8px; align-items: center; font-size: 0.76rem; }
  .cost-legend i { grid-row: span 2; width: 8px; height: 8px; border-radius: 2px; }
  .cost-legend i.codex { background: var(--color-accent); }
  .cost-legend i.claude { background: var(--color-live); }
  .cost-legend strong { font-variant-numeric: tabular-nums; }
  .cost-legend small { grid-column: 2 / -1; font-size: 0.75rem; }
  .metric-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
  .metric-grid article { display: grid; align-content: center; gap: 3px; min-width: 0; padding: 13px 14px; border-radius: var(--radius-md, 10px); background: color-mix(in srgb, var(--color-elevated) 56%, var(--color-surface)); }
  .metric-grid span, .metric-grid small { color: var(--color-text-2); font-size: 0.75rem; }
  .metric-grid strong { font-size: clamp(1.25rem, 2.2vw, 1.7rem); font-variant-numeric: tabular-nums; line-height: 1.1; letter-spacing: -0.035em; white-space: nowrap; }
  .metric-grid small { overflow-wrap: anywhere; }
  .analytics-grid { display: grid; grid-template-columns: minmax(420px, 1.35fr) minmax(300px, 0.85fr); gap: 10px; }
  .panel, .providers { display: grid; gap: 13px; padding: 15px; }
  .panel-heading > div, .section-heading > div { display: grid; gap: 3px; }
  .quiet-label { color: var(--color-text-3); font-size: 0.75rem; white-space: nowrap; }
  .heatmap-stage { display: flex; align-items: center; min-height: 120px; padding: 6px 4px; }
  .heatmap { display: grid; grid-template-columns: repeat(6, 14px); grid-template-rows: repeat(7, 14px); grid-auto-flow: column; gap: 4px; flex: 0 0 auto; }
  .heatmap > span, .heat-scale i { display: block; width: 14px; height: 14px; border-radius: 3px; }
  .level-0 { background: color-mix(in srgb, var(--color-border) 26%, transparent); }
  .level-1 { background: color-mix(in srgb, var(--color-accent) 20%, var(--color-elevated)); }
  .level-2 { background: color-mix(in srgb, var(--color-accent) 40%, var(--color-elevated)); }
  .level-3 { background: color-mix(in srgb, var(--color-accent) 68%, var(--color-elevated)); }
  .level-4 { background: var(--color-accent); }
  .heatmap-footer { display: grid; grid-template-columns: 1fr auto 1fr; gap: 10px; align-items: center; color: var(--color-text-3); font-size: 0.75rem; }
  .heatmap-footer > span:last-child { text-align: right; }
  .heat-scale { display: inline-flex; align-items: center; gap: 4px; }
  .heat-scale i { width: 8px; height: 8px; border-radius: 2px; }
  .token-panel { grid-template-rows: auto 1fr auto auto; }
  .mix-total { display: flex; align-items: baseline; gap: 7px; padding-top: 7px; }
  .mix-total strong { font-size: 1.55rem; font-variant-numeric: tabular-nums; letter-spacing: -0.03em; }
  .mix-total span { color: var(--color-text-2); font-size: 0.75rem; }
  .mix-bar, .quota-bar { display: flex; height: 7px; overflow: hidden; border-radius: 999px; background: color-mix(in srgb, var(--color-border) 32%, transparent); }
  .mix-bar i, .quota-bar i { display: block; height: 100%; min-width: 0; }
  .input { background: var(--color-accent); }
  .output { background: var(--color-live); }
  .cache { background: var(--color-attention); }
  .mix-legend { display: grid; gap: 7px; }
  .mix-legend > span { display: grid; grid-template-columns: 9px 1fr auto; gap: 7px; align-items: center; color: var(--color-text-2); font-size: 0.75rem; }
  .mix-legend b { font-weight: 500; }
  .mix-legend strong { color: var(--color-text); font-variant-numeric: tabular-nums; }
  .dot { width: 8px; height: 8px; border-radius: 2px; }
  .provider-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; }
  .provider-card { display: grid; align-content: start; gap: 13px; min-width: 0; padding: 14px; border-radius: var(--radius-md, 10px); background: color-mix(in srgb, var(--color-elevated) 52%, var(--color-surface)); }
  .provider-heading > div { display: grid; gap: 2px; min-width: 0; }
  .provider-heading span { overflow-wrap: anywhere; color: var(--color-text-3); font-size: 0.75rem; }
  .provider-heading > strong { font-size: 1.05rem; font-variant-numeric: tabular-nums; }
  .live-windows { display: grid; gap: 11px; }
  .live-window { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 5px 10px; align-items: end; }
  .live-window > div:first-child { display: grid; gap: 1px; }
  .live-window span, .quota-empty span { font-size: 0.75rem; font-weight: 600; }
  .live-window small { font-size: 0.75rem; }
  .quota-figure { padding: 0; border-radius: 5px; background: transparent; color: var(--color-text); font-size: 0.75rem; font-weight: 650; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .quota-figure:hover { color: var(--color-accent); }
  .live-window .quota-bar { grid-column: 1 / -1; }
  .quota-bar i { background: var(--color-accent); }
  .quota-empty { display: grid; gap: 2px; padding: 9px 10px; border-radius: var(--radius-sm, 7px); background: color-mix(in srgb, var(--color-bg) 48%, transparent); }
  .quota-empty p { color: var(--color-text-2); font-size: 0.75rem; }
  .provider-metrics { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 7px; }
  .provider-metrics > span { display: grid; gap: 2px; min-width: 0; padding: 9px 10px; border-radius: var(--radius-sm, 7px); background: color-mix(in srgb, var(--color-bg) 44%, transparent); }
  .provider-metrics small { font-size: 0.75rem; }
  .provider-metrics strong { font-size: 0.82rem; font-variant-numeric: tabular-nums; line-height: 1.25; overflow-wrap: anywhere; }
  .trend-block { display: grid; grid-template-columns: 88px minmax(0, 1fr); gap: 10px; align-items: center; }
  .trend-block > div { display: grid; gap: 1px; }
  .trend-block span { font-size: 0.75rem; font-weight: 600; }
  .trend-block small { font-size: 0.75rem; }
  .trend-block svg { width: 100%; height: 38px; overflow: visible; }
  .trend-block line { stroke: color-mix(in srgb, var(--color-border) 35%, transparent); stroke-width: 0.6; vector-effect: non-scaling-stroke; }
  .trend-block polyline { fill: none; stroke-width: 1.5; vector-effect: non-scaling-stroke; }
  .trend-block polyline.codex { stroke: var(--color-accent); }
  .trend-block polyline.claude { stroke: var(--color-live); }
  .rollup-list { display: grid; }
  .rollup-list details { border-top: 1px solid color-mix(in srgb, var(--color-border) 28%, transparent); }
  .rollup-list summary { display: flex; justify-content: space-between; gap: 12px; padding: 9px 2px; border-radius: 5px; cursor: pointer; list-style: none; color: var(--color-text-2); font-size: 0.75rem; }
  .rollup-list summary::-webkit-details-marker { display: none; }
  .rollup-list summary > span:last-child { display: inline-flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 4px; color: var(--color-text); font-variant-numeric: tabular-nums; text-align: right; }
  .rollup-list details[open] summary :global(svg) { transform: rotate(180deg); }
  .rollup-detail { display: grid; grid-template-columns: repeat(3, 1fr); gap: 7px; padding: 1px 2px 10px; }
  .rollup-detail > span { display: grid; gap: 1px; }
  .rollup-detail small { font-size: 0.75rem; }
  .rollup-detail strong { font-size: 0.75rem; font-variant-numeric: tabular-nums; }
  .estimate-note { color: var(--color-text-3); font-size: 0.75rem; text-align: right; }
  .empty { display: grid; min-height: 360px; place-content: center; gap: 5px; color: var(--color-text-2); text-align: center; }
  .empty strong { color: var(--color-text); }
  .empty p { font-size: 0.76rem; }
  @media (prefers-reduced-motion: reduce) { .donut-segment { transition: none; } }
  @media (max-width: 900px) {
    .workspace-header { align-items: stretch; flex-direction: column; }
    .header-actions { justify-content: space-between; }
    .overview-grid, .analytics-grid { grid-template-columns: 1fr; }
  }
  @media (max-width: 680px) {
    .workspace { padding: 14px; }
    .workspace-header { top: -14px; margin: -14px -14px 0; padding: 14px; }
    .header-actions { flex-wrap: wrap; }
    .range-selector { flex: 1; }
    .range-selector button { flex: 1; }
    .cost-panel { grid-template-columns: 112px 1fr; }
    .donut-wrap { width: 106px; height: 106px; }
    .provider-grid { grid-template-columns: 1fr; }
  }
</style>
