<script lang="ts">
	import { cn } from "$lib/utils.js";

	import { buttonVariants, type ButtonProps } from "./variants.js";

	let {
		class: className,
		variant = "default",
		size = "default",
		iconPosition,
		ref = $bindable(null),
		href = undefined,
		type = "button",
		disabled,
		children,
		...restProps
	}: ButtonProps = $props();

	const compactIconPadding = $derived(size === "xs" || size === "sm");
	const iconPaddingClass = $derived(
		iconPosition === "start"
			? compactIconPadding ? "pl-1.5" : "pl-2"
			: iconPosition === "end"
				? compactIconPadding ? "pr-1.5" : "pr-2"
				: undefined
	);
</script>

{#if href}
	<a
		bind:this={ref}
		data-slot="button"
		class={cn(buttonVariants({ variant, size }), iconPaddingClass, className)}
		href={disabled ? undefined : href}
		aria-disabled={disabled}
		role={disabled ? "link" : undefined}
		tabindex={disabled ? -1 : undefined}
		{...restProps}
	>
		{@render children?.()}
	</a>
{:else}
	<button
		bind:this={ref}
		data-slot="button"
		class={cn(buttonVariants({ variant, size }), iconPaddingClass, className)}
		{type}
		{disabled}
		{...restProps}
	>
		{@render children?.()}
	</button>
{/if}
