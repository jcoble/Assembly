<script lang="ts">
	import { AlertDialog as AlertDialogPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: AlertDialogPrimitive.OverlayProps = $props();
</script>

<AlertDialogPrimitive.Overlay
	bind:ref
	data-slot="alert-dialog-overlay"
	class={cn("next-shell data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 bg-[var(--color-scrim)] duration-100 supports-backdrop-filter:backdrop-blur-[2px] fixed inset-0 z-[300]", className)}
	{...restProps}
/>
