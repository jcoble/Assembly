<script lang="ts">
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: DropdownMenuPrimitive.SubContentProps = $props();
</script>

<DropdownMenuPrimitive.SubContent
	bind:ref
	data-slot="dropdown-menu-sub-content"
	class={cn(
		// Same sheet as the top-level content, from the same menu tokens.
		// `text-start` is load-bearing: bits-ui reads `align` for positioning but
		// also spreads it onto this div, and a bare `align="end"` is the legacy
		// presentational HTML attribute, which the UA stylesheet turns into
		// `text-align: end`. That inherited down and right-aligned every row's
		// description against its label. The top-level content consumes `align`
		// without re-emitting it, so only the submenu needs the reset.
		"data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 data-closed:zoom-out-95 data-open:zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 origin-(--bits-floating-transform-origin) ring-[var(--color-border)] bg-[var(--menu-sheet-surface)] text-[var(--color-text)] min-w-[96px] rounded-[var(--menu-sheet-radius)] p-[var(--menu-sheet-inset)] shadow-[var(--shadow-lg)] ring-1 duration-100 w-auto text-start",
		className
	)}
	{...restProps}
/>
