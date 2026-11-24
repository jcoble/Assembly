<script lang="ts">
	import { cn, type WithElementRef } from "$lib/utils.js";
	import type { HTMLAttributes } from "svelte/elements";

	let {
		ref = $bindable(null),
		class: className,
		children,
		hasAction = false,
		hasDescription = false,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
		/** Explicit composition flags replace descendant-dependent utilities. */
		hasAction?: boolean;
		hasDescription?: boolean;
	} = $props();
</script>

<div
	bind:this={ref}
	data-slot="card-header"
	class={cn(
		"gap-1 rounded-t-xl px-(--card-spacing) [.border-b]:pb-(--card-spacing) group/card-header @container/card-header grid auto-rows-min items-start",
		hasAction && "grid-cols-[1fr_auto]",
		hasDescription && "grid-rows-[auto_auto]",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</div>
