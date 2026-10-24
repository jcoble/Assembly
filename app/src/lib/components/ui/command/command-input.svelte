<script lang="ts">
	import { Command as CommandPrimitive } from "bits-ui";
	import SearchIcon from '@lucide/svelte/icons/search';
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		value = $bindable(""),
		...restProps
	}: CommandPrimitive.InputProps = $props();
</script>

<!-- The registry's input-group wrapper styles itself with has-[…] selectors, which this
     app does not use; the search icon and input sit in a plain row instead. This app
     loads no Tailwind reset, so the input clears its own browser border. -->
<div data-slot="command-input-wrapper" class="flex h-9 items-center gap-2 border-b px-3">
	<SearchIcon class="size-4 shrink-0 opacity-50" />
	<CommandPrimitive.Input
		data-slot="command-input"
		class={cn(
			"placeholder:text-muted-foreground flex h-10 w-full min-w-0 rounded-md border-0 bg-transparent py-3 text-sm outline-hidden disabled:cursor-not-allowed disabled:opacity-50",
			className
		)}
		bind:ref
		{...restProps}
		bind:value
	/>
</div>
