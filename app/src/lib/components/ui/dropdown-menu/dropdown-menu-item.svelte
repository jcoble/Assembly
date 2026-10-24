<script lang="ts">
	import { cn } from "$lib/utils.js";
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";

	let {
		ref = $bindable(null),
		class: className,
		inset,
		variant = "default",
		...restProps
	}: DropdownMenuPrimitive.ItemProps & {
		inset?: boolean;
		variant?: "default" | "destructive";
	} = $props();
</script>

<DropdownMenuPrimitive.Item
	bind:ref
	data-slot="dropdown-menu-item"
	data-inset={inset}
	data-variant={variant}
	class={cn(
		// Sizing diverges from the registry on two points, both from DESIGN.md:
		// a 13px body-text floor (the registry's `text-sm` is this app's 12px),
		// and a row tall enough to be a real hit target. Height, corner, padding
		// and the hover fill all come from the shared menu tokens so every menu
		// in the shell reads as the same sheet.
		// `data-highlighted` is added alongside `focus:` because keyboard
		// movement through a bits-ui menu marks the row rather than focusing it.
		"focus:bg-[var(--menu-row-hover)] focus:text-accent-foreground data-highlighted:bg-[var(--menu-row-hover)] data-highlighted:text-accent-foreground data-[variant=destructive]:text-destructive data-[variant=destructive]:focus:bg-destructive/10 dark:data-[variant=destructive]:focus:bg-destructive/20 data-[variant=destructive]:focus:text-destructive data-[variant=destructive]:*:[svg]:text-destructive not-data-[variant=destructive]:focus:**:text-accent-foreground min-h-8 gap-[var(--menu-row-gap)] rounded-[var(--menu-row-radius)] p-[var(--menu-row-inset)] text-[13px] data-inset:pl-7 [&_svg:not([class*='size-'])]:size-4 group/dropdown-menu-item relative flex cursor-default items-center outline-hidden select-none data-disabled:pointer-events-none data-disabled:opacity-50 data-[inset]:pl-8 [&_svg]:pointer-events-none [&_svg]:shrink-0",
		className
	)}
	{...restProps}
/>
