<script lang="ts">
	import { ContextMenu as ContextMenuPrimitive } from "bits-ui";
	import type { ComponentProps } from "svelte";

	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";
	import ContextMenuPortal from "./context-menu-portal.svelte";

	let {
		ref = $bindable(null),
		sideOffset = 4,
		align = "start",
		portalProps,
		class: className,
		...restProps
	}: ContextMenuPrimitive.ContentProps & {
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof ContextMenuPortal>>;
	} = $props();
</script>

<ContextMenuPortal {...portalProps}>
	<ContextMenuPrimitive.Content
		bind:ref
		data-slot="context-menu-content"
		{sideOffset}
		{align}
		class={cn(
			"data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 data-closed:zoom-out-95 data-open:zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 bg-[var(--menu-surface)] text-[var(--color-text)] min-w-32 rounded-[4px] p-[4px] shadow-[0_16px_24px_rgba(0,0,0,0.32),0_6px_8px_rgba(0,0,0,0.2)] duration-100 data-[side=inline-start]:slide-in-from-right-2 data-[side=inline-end]:slide-in-from-left-2 z-50 overflow-x-hidden overflow-y-auto outline-none data-closed:overflow-hidden",
			className
		)}
		{...restProps}
	/>
</ContextMenuPortal>
