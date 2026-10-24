<script lang="ts">
  import { onMount } from 'svelte';
  import { Input } from '$lib/components/ui/input/index.js';
  import { readAssemblySettingFromTauri, writeAssemblySettingFromTauri } from '$lib/tauriSource';

  type HelperSetting = 'helper_auto_done_minutes' | 'helpers_per_session';
  let { setting }: { setting: HelperSetting } = $props();

  const spec = $derived({
    helper_auto_done_minutes: { label: 'Mark finished helpers Done after', min: 0, max: undefined, fallback: 60 },
    helpers_per_session: { label: 'Helpers per session', min: 1, max: 10, fallback: 5 }
  }[setting]);

  let value = $state<number | null>(null);
  let loaded = $state(false);
  let note = $state<string | null>(null);

  // Read on each mount; the row mounts when the settings screen shows it.
  onMount(async () => {
    try {
      value = (await readAssemblySettingFromTauri(setting)) as number | null;
      loaded = true;
    } catch (error) { note = String(error); }
  });

  async function save(event: Event): Promise<void> {
    const input = event.currentTarget as HTMLInputElement;
    if (!input.checkValidity()) { input.reportValidity(); return; }
    try {
      await writeAssemblySettingFromTauri(setting, input.valueAsNumber);
      value = input.valueAsNumber;
      note = null;
    } catch (error) { note = String(error); }
  }
</script>

<div class="flex w-full flex-col gap-1.5">
  <Input type="number" min={spec.min} max={spec.max} step={1} required
    aria-label={spec.label} value={value ?? spec.fallback} disabled={!loaded} onchange={save} />
  {#if note}<p role="alert">{note}</p>{/if}
</div>
