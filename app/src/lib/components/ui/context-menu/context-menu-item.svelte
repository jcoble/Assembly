<script lang="ts">
	import { ContextMenu as ContextMenuPrimitive } from "bits-ui";

	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		inset,
		variant = "default",
		...restProps
	}: ContextMenuPrimitive.ItemProps & {
		inset?: boolean;
		variant?: "default" | "destructive";
	} = $props();
</script>

<ContextMenuPrimitive.Item
	bind:ref
	data-slot="context-menu-item"
	data-inset={inset}
	data-variant={variant}
	class={cn(
		"text-[var(--color-text)] focus:bg-[color-mix(in_srgb,var(--color-text)_10%,transparent)] data-highlighted:bg-[color-mix(in_srgb,var(--color-text)_10%,transparent)] data-[variant=destructive]:text-destructive data-[variant=destructive]:*:[svg]:text-destructive min-h-[32px] gap-1.5 rounded-[2px] px-[12px] py-1 text-[13px] font-normal data-inset:pl-7 [&_svg:not([class*='size-'])]:size-4 group/context-menu-item relative flex cursor-default items-center outline-hidden select-none data-disabled:pointer-events-none data-disabled:opacity-50 data-[inset]:pl-8 [&_svg]:pointer-events-none [&_svg]:shrink-0",
		className
	)}
	{...restProps}
/>
