<script lang="ts">
	import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
	import ChevronRight from "@lucide/svelte/icons/chevron-right";
	import FileUp from "@lucide/svelte/icons/file-up";
	import History from "@lucide/svelte/icons/history";
	import Maximize2 from "@lucide/svelte/icons/maximize-2";
	import RefreshCw from "@lucide/svelte/icons/refresh-cw";
	import { untrack } from "svelte";

	import * as Collapsible from "$lib/components/ui/collapsible/index.js";
	import { IconButton } from "$lib/components/ui/icon-button/index.js";
	import { ScrollArea } from "$lib/components/ui/scroll-area/index.js";
	import {
		createGitPanelState,
		isNotARepositoryError,
	} from "$lib/shell/git/gitPanelStore.svelte";
	import { gitCommitFilesService } from "$lib/shell/git/gitCommitFilesService";
	import { gitCommitFilesView } from "$lib/shell/git/gitCommitFilesStore.svelte";
	import { createGitService } from "$lib/shell/git/gitService";
	import { showCenterTab } from "$lib/shell/workbenchNavigation";
	import { formatLastActivity } from "$lib/shell/relativeTime";

	interface Props {
		visible: boolean;
		root: string;
		relativePath: string;
		fileName: string;
		open?: boolean;
		onOpenLarge?(): void;
		onOpenFile?(): void;
	}

	let {
		visible,
		root,
		relativePath,
		fileName,
		open = $bindable(false),
		onOpenLarge,
		onOpenFile,
	}: Props = $props();

	const panel = $state(createGitPanelState());
	const service = createGitService({ state: panel });
	const commitInteraction = $derived(gitCommitFilesView(gitCommitFilesService.state));
	let viewport = $state<HTMLElement | null>(null);

	$effect(() => {
		const targetRoot = root.trim();
		const targetPath = relativePath.trim();
		const stop = new AbortController();

		if (!visible || !open || !targetRoot || !targetPath) {
			untrack(() => service.releaseHistorySurface());
			return () => stop.abort();
		}

		untrack(() => void loadFileHistory(targetRoot, targetPath, stop.signal));
		return () => {
			stop.abort();
			untrack(() => service.releaseHistorySurface());
		};
	});

	$effect(() => {
		const element = viewport;
		if (!element) return;
		const stop = new AbortController();
		const nearEnd = () => void loadMoreNearEnd(stop.signal);
		element.addEventListener("scroll", nearEnd, { passive: true, signal: stop.signal });
		return () => stop.abort();
	});

	async function loadFileHistory(
		targetRoot: string,
		targetPath: string,
		stopSignal: AbortSignal,
	): Promise<void> {
		service.activate(targetRoot);
		await service.showFileHistory(targetRoot, targetPath);
		if (stopSignal.aborted) return;
		service.ensureHistorySurface();
	}

	async function loadMoreNearEnd(stopSignal: AbortSignal): Promise<void> {
		const element = viewport;
		if (
			stopSignal.aborted ||
			!element ||
			element.scrollHeight - element.scrollTop - element.clientHeight > 48
		) return;
		await service.loadMoreHistory();
	}

	async function openCommitDiff(sha: string): Promise<void> {
		gitCommitFilesService.activate(root);
		showCenterTab("diff");
		await gitCommitFilesService.selectCommitFile(sha, {
			relativePath,
			status: "",
			badge: "",
		});
	}
</script>

<Collapsible.Root bind:open class="file-history-pane">
	<div class="file-history-heading">
		<Collapsible.Trigger class="file-history-trigger">
			<ChevronRight class={open ? "open" : ""} aria-hidden="true" />
			<History aria-hidden="true" />
			<strong>File history</strong>
			{#if fileName}<span title={relativePath}>{fileName}</span>{/if}
		</Collapsible.Trigger>
		{#if open && relativePath}
			<div class="file-history-actions">
				<IconButton label="Go to File" onclick={onOpenFile}>
					<FileUp />
				</IconButton>
				<IconButton label="Refresh file history" onclick={() => void service.refreshHistory()}>
					<RefreshCw />
				</IconButton>
				<IconButton label="Open file history in the large view" onclick={onOpenLarge}>
					<Maximize2 />
				</IconButton>
			</div>
		{/if}
	</div>

	{#if open}
		<Collapsible.Content>
			<div class="file-history-body">
			{#if !relativePath}
				<p>Select a file to see its history.</p>
			{:else if panel.historyLoading}
				<p><WorkingSpinner size={12} /> Reading the file history…</p>
			{:else if panel.historyError && isNotARepositoryError(panel.historyError)}
				<p>This folder’s Git history is unavailable.</p>
			{:else if panel.historyError}
				<p class="error">{panel.historyError}</p>
			{:else if panel.history.length === 0}
				<p>No commits have touched this file.</p>
			{:else}
				<ScrollArea bind:viewportRef={viewport} class="file-history-list">
					<ol>
						{#each panel.history as commit (commit.sha)}
							<li title={`${commit.subject}\n${commit.author} · ${commit.shortSha}`}>
								<button
									type="button"
									class:selected={commitInteraction.selectedCommitSha === commit.sha &&
										commitInteraction.selectedRelativePath === relativePath}
									onclick={() => void openCommitDiff(commit.sha)}
								>
									<span class="commit-mark" aria-hidden="true"></span>
									<span class="commit-subject">{commit.subject}</span>
									<time datetime={commit.committedAt}>
										{formatLastActivity(commit.committedAt, new Date())}
									</time>
								</button>
							</li>
						{/each}
						{#if panel.historyLoadingMore}
							<li class="loading-more"><WorkingSpinner size={12} /> Reading older commits…</li>
						{/if}
					</ol>
				</ScrollArea>
			{/if}
			</div>
		</Collapsible.Content>
	{/if}
</Collapsible.Root>

<style>
	:global(.file-history-pane) {
		display: flex;
		min-height: 0;
		flex-direction: column;
		/* A quiet card of its own under the tree, like Spotify's "Credits". */
		margin: var(--space-2) var(--space-3) var(--space-3);
		overflow: hidden;
		border-radius: var(--radius-md);
		background: var(--muted);
	}

	.file-history-heading {
		display: flex;
		min-height: 30px;
		align-items: center;
	}

	:global(.file-history-trigger) {
		display: flex;
		min-width: 0;
		flex: 1;
		align-items: center;
		gap: 8px;
		padding: 8px 12px;
		color: var(--color-text);
		font-size: 13px;
		text-align: left;
		transition: background-color 0.15s ease;
	}

	:global(.file-history-trigger:hover) {
		background: color-mix(in srgb, var(--color-text) 5%, transparent);
	}

	:global(.file-history-trigger svg) {
		width: 14px;
		height: 14px;
		flex: none;
		color: var(--color-text-2);
	}

	:global(.file-history-trigger svg:first-child) {
		width: 12px;
		height: 12px;
		color: var(--color-text-3);
		transition: transform 120ms ease-out;
	}

	:global(.file-history-trigger svg:first-child.open) {
		transform: rotate(90deg);
	}

	:global(.file-history-trigger strong) {
		flex: none;
		white-space: nowrap;
		font-size: 13px;
		font-weight: 600;
	}

	:global(.file-history-trigger span) {
		min-width: 0;
		overflow: hidden;
		color: var(--color-text-3);
		font-size: 12px;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	.file-history-actions {
		display: flex;
		padding-right: 4px;
	}

	.file-history-body {
		height: 220px;
		min-height: 0;
		border-top: 1px solid color-mix(in srgb, var(--color-border) 70%, transparent);
	}

	.file-history-body > p {
		margin: 0;
		padding: 10px 12px;
		color: var(--color-text-2);
		font-size: 12px;
		line-height: 1.4;
	}

	.file-history-body > p.error {
		color: var(--color-bad);
	}

	:global(.file-history-list) {
		height: 100%;
	}

	ol {
		min-width: 0;
		margin: 0;
		padding: 4px;
		list-style: none;
	}

	li {
		content-visibility: auto;
		contain-intrinsic-size: auto 28px;
	}

	li > button {
		display: grid;
		width: 100%;
		min-width: 0;
		min-height: 28px;
		grid-template-columns: 14px minmax(0, 1fr) auto;
		align-items: center;
		gap: 5px;
		padding: 3px 6px;
		border: 0;
		border-radius: var(--radius-sm);
		background: transparent;
		color: inherit;
		text-align: left;
		cursor: pointer;
	}

	li > button:hover,
	li > button:focus-visible {
		background: var(--color-hover);
	}

	li > button.selected,
	li > button.selected:hover,
	li > button.selected:focus-visible {
		background: var(--color-selected);
	}

	li > button:focus-visible {
		outline: var(--focus-ring);
	}

	.commit-mark {
		position: relative;
		width: 8px;
		height: 8px;
		border: 2px solid var(--color-text-2);
		border-radius: 999px;
	}

	.commit-mark::before,
	.commit-mark::after {
		position: absolute;
		left: 1px;
		width: 2px;
		height: 11px;
		background: var(--color-border-strong);
		content: "";
	}

	.commit-mark::before { bottom: 5px; }
	.commit-mark::after { top: 5px; }
	li:first-child .commit-mark::before { display: none; }

	.commit-subject {
		min-width: 0;
		overflow: hidden;
		font-size: 12px;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	time {
		color: var(--color-text-3);
		font-size: 12px;
	}

	li.loading-more {
		display: block;
		padding-left: 25px;
		color: var(--color-text-2);
		font-size: 12px;
	}

	@media (prefers-reduced-motion: reduce) {
		:global(.file-history-trigger svg:first-child) { transition: none; }
	}
</style>
