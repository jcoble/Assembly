<script lang="ts">
	import type { HTMLAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		hasMedia = false,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
		/** The composition site sets this when it renders Media; never infer children relationally. */
		hasMedia?: boolean;
	} = $props();
</script>

<div
	bind:this={ref}
	data-slot="alert-dialog-header"
	class={cn(
		"grid grid-rows-[auto_1fr] place-items-center gap-1.5 text-center sm:group-data-[size=default]/alert-dialog-content:place-items-start sm:group-data-[size=default]/alert-dialog-content:text-left",
		hasMedia && "grid-rows-[auto_auto_1fr] gap-x-4 sm:group-data-[size=default]/alert-dialog-content:grid-rows-[auto_1fr]",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</div>
