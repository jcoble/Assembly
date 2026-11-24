<script lang="ts">
	import { Slider as SliderPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		value = $bindable(),
		orientation = "horizontal",
		class: className,
		...restProps
	}: WithoutChildrenOrChild<SliderPrimitive.RootProps> = $props();
</script>

<!--
Discriminated Unions + Destructing (required for bindable) do not
get along, so we shut typescript up by casting `value` to `never`.
-->
<SliderPrimitive.Root
	bind:ref
	bind:value={value as never}
	data-slot="slider"
	{orientation}
	class={cn(
		"data-[orientation=vertical]:min-h-40 relative flex w-full touch-none items-center select-none data-disabled:opacity-50 data-[orientation=vertical]:h-full data-[orientation=vertical]:w-auto data-[orientation=vertical]:flex-col",
		className
	)}
	{...restProps}
>
	{#snippet children({ thumbItems })}
		<span
			data-slot="slider-track"
			data-orientation={orientation}
			class={cn(
				"bg-muted rounded-full data-[orientation=horizontal]:h-1 data-[orientation=horizontal]:w-full data-[orientation=vertical]:h-full data-[orientation=vertical]:w-1 bg-muted relative grow overflow-hidden data-[orientation=horizontal]:w-full data-[orientation=vertical]:h-full"
			)}
		>
			<SliderPrimitive.Range
				data-slot="slider-range"
				class={cn(
					"bg-primary absolute select-none data-[orientation=horizontal]:h-full data-[orientation=vertical]:w-full"
				)}
			/>
		</span>
		{#each thumbItems as thumb (thumb.index)}
			<!-- The registry paints the thumb a literal white; inside /next it takes
			     the foreground token so a theme change carries it, and it is 14px
			     with a 28px invisible grab area around it. -->
			<SliderPrimitive.Thumb
				data-slot="slider-thumb"
				index={thumb.index}
				class="border-ring ring-ring/50 bg-foreground relative size-3.5 rounded-full border shadow-[var(--shadow-thumb-sm)] transition-[color,box-shadow] after:absolute after:-inset-[7px] hover:ring-3 focus-visible:ring-3 focus-visible:outline-hidden active:ring-3 block shrink-0 select-none disabled:pointer-events-none disabled:opacity-50"
			/>
		{/each}
	{/snippet}
</SliderPrimitive.Root>
