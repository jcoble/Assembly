<script lang="ts">
	import type { HTMLAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		size = "default",
		hasFooter = false,
		hasLeadingImage = false,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
		size?: "default" | "sm";
		/** Set from the composition site; descendant-dependent styling stays explicit. */
		hasFooter?: boolean;
		hasLeadingImage?: boolean;
	} = $props();
</script>

<div
	bind:this={ref}
	data-slot="card"
	data-size={size}
	class={cn(
		"bg-card text-card-foreground gap-(--card-spacing) overflow-hidden rounded-xl py-(--card-spacing) text-(length:--text-quiet) [--card-spacing:var(--space-4)] data-[size=sm]:[--card-spacing:var(--space-3)] *:[img:first-child]:rounded-t-xl *:[img:last-child]:rounded-b-xl group/card flex flex-col",
		hasFooter && "pb-0",
		hasLeadingImage && "pt-0",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</div>
