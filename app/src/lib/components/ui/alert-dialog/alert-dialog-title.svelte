<script lang="ts">
	import { AlertDialog as AlertDialogPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		hasMedia = false,
		...restProps
	}: AlertDialogPrimitive.TitleProps & {
		/** Mirror Header.hasMedia at the composition site; avoids a group relational selector. */
		hasMedia?: boolean;
	} = $props();
</script>

<AlertDialogPrimitive.Title
	bind:ref
	data-slot="alert-dialog-title"
	class={cn(
		"text-base font-medium",
		hasMedia && "sm:group-data-[size=default]/alert-dialog-content:col-start-2",
		className
	)}
	{...restProps}
/>
