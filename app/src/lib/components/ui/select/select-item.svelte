<script lang="ts">
	/**
	 * The recipe below keeps the app's 13px body-text floor and uses the shell's
	 * explicit hover and selected-row tokens. The registry's accent surface is
	 * too close to the surrounding popover to communicate either state here.
	 */
	import { Select as SelectPrimitive } from "bits-ui";
	import { cn, type WithoutChild } from "$lib/utils.js";
	import CheckIcon from '@lucide/svelte/icons/check';

	let {
		ref = $bindable(null),
		class: className,
		value,
		label,
		children: childrenProp,
		...restProps
	}: WithoutChild<SelectPrimitive.ItemProps> = $props();
</script>

<SelectPrimitive.Item
	bind:ref
	{value}
	data-slot="select-item"
	class={cn(
		"focus:bg-[var(--color-hover)] focus:text-[var(--color-text)] data-highlighted:bg-[var(--color-hover)] data-highlighted:text-[var(--color-text)] data-selected:bg-[var(--color-selected)] data-selected:text-[var(--color-text)] data-selected:[&_svg]:text-[var(--color-accent)] min-h-6 gap-1.5 rounded-md py-1 pr-8 pl-2 text-[13px] [&_svg:not([class*='size-'])]:size-4 *:[span]:last:flex *:[span]:last:items-center *:[span]:last:gap-2 relative flex w-full cursor-default items-center outline-hidden select-none data-[disabled]:pointer-events-none data-[disabled]:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0",
		className
	)}
	{...restProps}
>
	{#snippet children({ selected, highlighted })}
		<span class="absolute end-2 flex size-3.5 items-center justify-center">
			{#if selected}
				<CheckIcon class="cn-select-item-indicator-icon" />
			{/if}
		</span>
		<span class="flex flex-1 gap-2 shrink-0 whitespace-nowrap">
			{#if childrenProp}
				{@render childrenProp({ selected, highlighted })}
			{:else}
				{label || value}
			{/if}
		</span>
	{/snippet}
</SelectPrimitive.Item>
