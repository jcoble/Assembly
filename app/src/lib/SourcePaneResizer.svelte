<script lang="ts">
	type SourcePaneResizerOrientation = "vertical" | "horizontal";

	type Props = {
		className: string;
		ariaLabel: string;
		title: string;
		orientation: SourcePaneResizerOrientation;
		onpointerdown: (event: PointerEvent) => void;
		onkeydown?: (event: KeyboardEvent) => void;
		ondblclick?: (event: MouseEvent) => void;
	};

	let { className, ariaLabel, title, orientation, onpointerdown, onkeydown, ondblclick }: Props = $props();
</script>

<button
	class={className}
	type="button"
	aria-label={ariaLabel}
	{title}
	data-pane-resizer={orientation}
	{onpointerdown}
	{onkeydown}
	{ondblclick}
></button>
