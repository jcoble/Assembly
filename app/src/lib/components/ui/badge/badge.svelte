<script lang="ts">
	import type { HTMLAnchorAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	import { badgeVariants, type BadgeVariant } from "./variants.js";

	let {
		ref = $bindable(null),
		href,
		class: className,
		variant = "default",
		iconPosition,
		children,
		...restProps
	}: WithElementRef<HTMLAnchorAttributes> & {
		variant?: BadgeVariant;
		/** Explicit icon position replaces child-dependent padding selectors. */
		iconPosition?: "start" | "end";
	} = $props();
</script>

<svelte:element
	this={href ? "a" : "span"}
	bind:this={ref}
	data-slot="badge"
	{href}
	class={cn(
		badgeVariants({ variant }),
		iconPosition === "start" && "pl-1.5",
		iconPosition === "end" && "pr-1.5",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</svelte:element>
