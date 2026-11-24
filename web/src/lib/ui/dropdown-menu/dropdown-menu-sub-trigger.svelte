<script lang="ts">
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import ChevronRightIcon from '@lucide/svelte/icons/chevron-right';
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		inset,
		children,
		...restProps
	}: DropdownMenuPrimitive.SubTriggerProps & {
		inset?: boolean;
	} = $props();
</script>

<DropdownMenuPrimitive.SubTrigger
	bind:ref
	data-slot="dropdown-menu-sub-trigger"
	data-inset={inset}
	class={cn(
		// Matches dropdown-menu-item, from the same shared menu tokens. The
		// chevron below is what says this row opens a menu to the side.
		"focus:bg-[var(--menu-row-hover)] focus:text-accent-foreground data-highlighted:bg-[var(--menu-row-hover)] data-highlighted:text-accent-foreground data-open:bg-[var(--menu-row-hover)] data-open:text-accent-foreground not-data-[variant=destructive]:focus:**:text-accent-foreground min-h-8 gap-[var(--menu-row-gap)] rounded-[var(--menu-row-radius)] p-[var(--menu-row-inset)] text-[13px] data-inset:pl-7 [&_svg:not([class*='size-'])]:size-4 flex cursor-default items-center outline-hidden select-none data-[inset]:pl-8 [&_svg]:pointer-events-none [&_svg]:shrink-0",
		className
	)}
	{...restProps}
>
	{@render children?.()}
	<ChevronRightIcon class="ml-auto" />
</DropdownMenuPrimitive.SubTrigger>
