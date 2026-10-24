<script lang="ts">
	import type { Snippet } from "svelte";

	interface Props {
		active?: boolean;
		onSelect?(): void;
		children: Snippet;
	}

	let { active = false, onSelect, children }: Props = $props();
</script>

<button
	data-testid="worktree-agent-select"
	type="button"
	class="session-row"
	class:active
	aria-current={active ? "true" : undefined}
	onclick={onSelect}
>
	{@render children()}
</button>

<style>
	/* A 32px provider tile, then the two text lines; the age sits in line one's
	   right corner. 6px corners, the rows' step in the shell's radius scale. */
	.session-row {
		position: relative;
		display: grid;
		grid-template-columns: 32px minmax(0, 1fr);
		column-gap: 12px;
		align-items: center;
		width: 100%;
		min-height: 48px;
		padding: 6px 8px;
		border: 0;
		border-radius: 6px;
		overflow: hidden;
		background: transparent;
		color: inherit;
		font: inherit;
		text-align: left;
		cursor: pointer;
		outline: none;
	}

	.session-row:hover {
		background: var(--accent);
	}

	/* The selected row: a fill plus a short primary bar on its left edge. */
	.session-row.active {
		background: var(--secondary);
	}

	.session-row.active::before {
		content: "";
		position: absolute;
		left: 0;
		top: 12px;
		bottom: 12px;
		width: 2px;
		border-radius: 2px;
		background: var(--primary);
	}
</style>
