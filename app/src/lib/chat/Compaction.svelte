<script lang="ts">
  // The boundary Assembly draws where Claude compacted the context, for a
  // `compaction` row (see test/fixtures/rows-compaction.json).
  import { compactionLabel } from './notices';
  let { value }: { value: { before?: unknown; after?: unknown } } = $props();
</script>

<div class="compaction">
  <span class="rule"></span>
  <span class="label">{compactionLabel(value)}</span>
  <span class="rule"></span>
</div>

<style>
  .compaction {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 0 0 18px;
  }
  .rule {
    flex: 1;
    height: 1px;
    background: var(--line);
  }
  .label {
    color: var(--muted);
    font-size: 12px;
    letter-spacing: 0.04em;
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
  }
</style>
