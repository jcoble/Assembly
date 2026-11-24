<script lang="ts">
  /**
   * 1:1 `Provider` | `Status` | `Location` Spotify-style drill-in filter pills bar.
   * Uses the shared `FilterPills` UI component so Rail, Tasks, Workflows, and History
   * all share identical pill interaction and keyboard/auto-close behavior.
   */
  import FilterPills, { type FilterPillGroup } from '$lib/ui/FilterPills.svelte';
  import { providerDisplayName } from './format';
  import type { RailFilters, SessionMarkState } from './types';

  interface Props {
    agents: string[];
    filters: RailFilters;
    onChange: (next: RailFilters) => void;
  }

  let { agents, filters, onChange }: Props = $props();

  const STATUS_OPTIONS = [
    { value: 'working', label: 'Working' },
    { value: 'done', label: 'Done' },
    { value: 'settled', label: 'Settled' }
  ] as const;

  const LOCATION_OPTIONS = [
    { value: 'local', label: 'Local' },
    { value: 'remote', label: 'Remote' }
  ] as const;

  const groups = $derived.by<FilterPillGroup[]>(() => {
    const providerIds = [...new Set([...agents, ...filters.provider])];
    return [
      {
        id: 'provider',
        label: 'Provider',
        options: providerIds.map((value) => ({ value, label: providerDisplayName(value) }))
      },
      {
        id: 'status',
        label: 'Status',
        options: STATUS_OPTIONS
      },
      {
        id: 'location',
        label: 'Location',
        options: LOCATION_OPTIONS
      }
    ];
  });

  const pillValue = $derived<Record<string, string[]>>({
    provider: filters.provider,
    status: filters.status,
    location: filters.location
  });

  function handleChange(next: Record<string, string[]>): void {
    onChange({
      provider: next.provider ?? [],
      status: (next.status ?? []) as SessionMarkState[],
      location: (next.location ?? []) as ('local' | 'remote')[]
    });
  }
</script>

<div class="rail-filter-pills">
  <FilterPills label="Filter sessions" {groups} value={pillValue} onChange={handleChange} />
</div>

<style>
  .rail-filter-pills {
    flex: 0 0 auto;
    padding: 4px 12px 10px;
  }
</style>
