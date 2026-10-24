<script lang="ts">
	/**
	 * The session list: one scroll area, one row height, sticky section headings.
	 *
	 * Working, Done and Settled are sections of the same list rather than three
	 * resizable panes, so nothing has to be dragged to see the sessions further
	 * down and no splitter can hide a section. The heading names and counts come
	 * from the view options, which is also how a person groups by project instead.
	 */
	import ChevronRight from "@lucide/svelte/icons/chevron-right";
	import MessageCircle from "@lucide/svelte/icons/message-circle";
	import Workflow from "@lucide/svelte/icons/workflow";
	import { onMount } from "svelte";

	import { Badge } from "$lib/components/ui/badge";
	import { Chip } from "$lib/components/ui/chip/index.js";
	import { railRowStatuses, type OwnedSession, type RailRowStatus } from "$lib/shell/ownedSessions";
	import { rail } from "$lib/shell/stores/sessionRailStore.svelte";
	import { projectBadge, projectMachineLabel } from "$lib/shell/projects/projects";
	import { deriveOwnedLibraryState } from "$lib/shell/history/library/sessionLibraryModel";
	import {
		buildMyWorkGroups,
		myWorkGroupSessions,
		reorderMyWorkSessions,
		type MyWorkGroup,
		type MyWorkViewOptions,
	} from "./myWorkViewOptions.ts";
	import SessionRowContextMenu from "./SessionRowContextMenu.svelte";
	import { sessionRowJump, type SessionRowSurface } from "./sessionRowJump.ts";
	import { sessionRowMenuItems, type SessionRowMenuAction } from "./sessionRowMenu.ts";
	import { resumeCommand } from "../resumeCommand.ts";
	import WorktreeAgentRow from "./WorktreeAgentRow.svelte";

	interface Props {
		sessions: OwnedSession[];
		options: MyWorkViewOptions;
		/** The saved Custom order, as owned ids. */
		manualOrder?: readonly string[];
		/** A row was dropped; this is the new Custom order to save and show. */
		onReorder?(order: string[]): void;
		activeOwnedId?: string | null;
		onSelectSession?(ownedId: string): void | Promise<void>;
		onComplete?(ownedId: string): void;
		onReopen?(ownedId: string): void;
		onSettle?(ownedId: string): void;
		onUnsettle?(ownedId: string): void;
		onPin?(ownedId: string, pinned: boolean): void;
		onMarkOrchestrator?(ownedId: string, marked: boolean): void;
		onRename?(ownedId: string, title: string): void;
		onConnect?(ownedId: string): void;
		onAskRemove?(ownedId: string): void;
	}

	let {
		sessions,
		options,
		manualOrder = [],
		onReorder,
		activeOwnedId = null,
		onSelectSession,
		onComplete,
		onReopen,
		onSettle,
		onUnsettle,
		onPin,
		onMarkOrchestrator,
		onRename,
		onConnect,
		onAskRemove,
	}: Props = $props();

	const groups = $derived(buildMyWorkGroups(sessions, options, manualOrder));
	// Every row's status icons, built once per change to the whole rail (helpers
	// and workers can be filtered out of `sessions`), so a row costs a lookup.
	const rowStatuses = $derived(railRowStatuses(rail.owned));
	const NO_STATUS: RailRowStatus = { waiting: [], background: 0, helpers: 0, runs: 0 };
	let collapsedGroups = $state<Record<string, boolean>>({});
	let visualActiveOwnedId = $state<string | null>(null);
	// One clock for every row's age label, refreshed coarsely; a hidden window skips the write.
	let nowMs = $state(Date.now());
	onMount(() => {
		const tick = window.setInterval(() => {
			if (!document.hidden) nowMs = Date.now();
		}, 15_000);
		return () => window.clearInterval(tick);
	});
	let contextMenu = $state<{ session: OwnedSession; x: number; y: number } | null>(null);
	let renamingOwnedId = $state<string | null>(null);
	const contextMenuItems = $derived(
		contextMenu
			? sessionRowMenuItems({
					status: deriveOwnedLibraryState(contextMenu.session),
					sessionId: contextMenu.session.nativeSessionId || contextMenu.session.ownedId,
					resumeCommand: resumeCommand(contextMenu.session.agent, contextMenu.session.nativeSessionId, contextMenu.session.cwd),
					worktreePath: contextMenu.session.cwd || null,
					pinned: Boolean(contextMenu.session.pinnedAt),
					orchestrator: Boolean(contextMenu.session.orchestratorMarkedAtMs),
					topLevel: !contextMenu.session.helperOf && !contextMenu.session.runId,
				})
			: [],
	);

	$effect(() => {
		if (activeOwnedId !== null) visualActiveOwnedId = activeOwnedId;
	});

	function sessionNeedsYou(session: OwnedSession): boolean {
		return (
			session.pendingPermission === true ||
			session.pendingInput === true ||
			session.runtimeState === "waiting-approval" ||
			session.runtimeState === "waiting-input"
		);
	}

	/** Settled is the archive, so it starts shut until a person opens it. */
	function isOpen(key: string): boolean {
		return !(collapsedGroups[key] ?? key === "settled");
	}

	function toggleGroup(key: string): void {
		collapsedGroups = { ...collapsedGroups, [key]: isOpen(key) };
	}

	async function selectRow(ownedId: string): Promise<void> {
		visualActiveOwnedId = ownedId;
		await onSelectSession?.(ownedId);
	}

	async function jumpTo(session: OwnedSession, surface: SessionRowSurface): Promise<void> {
		visualActiveOwnedId = session.ownedId;
		if (!(await sessionRowJump(session.ownedId, surface))) await onSelectSession?.(session.ownedId);
	}

	function openContextMenu(event: MouseEvent, session: OwnedSession): void {
		event.preventDefault();
		event.stopPropagation();
		contextMenu = { session, x: event.clientX, y: event.clientY };
	}

	// Dragging a row: one set of listeners on the list, none on the rows. A row
	// can move only within the group it is drawn in.
	let dragging: { ownedId: string; groupKey: string } | null = null;
	let dropTarget = $state<{ ownedId: string; position: "before" | "after" } | null>(null);

	function draggedRow(event: DragEvent): HTMLElement | null {
		return event.target instanceof Element ? event.target.closest<HTMLElement>("[data-owned-id]") : null;
	}

	function startDrag(event: DragEvent): void {
		const row = draggedRow(event);
		const groupKey = row?.parentElement?.dataset.groupKey;
		if (!row?.dataset.ownedId || !groupKey || !event.dataTransfer) return;
		dragging = { ownedId: row.dataset.ownedId, groupKey };
		event.dataTransfer.effectAllowed = "move";
		event.dataTransfer.setData("text/plain", row.dataset.ownedId);
	}

	function dragOver(event: DragEvent): void {
		if (!dragging) return;
		const row = draggedRow(event);
		const ownedId = row?.dataset.ownedId;
		if (!row || !ownedId || ownedId === dragging.ownedId || row.parentElement?.dataset.groupKey !== dragging.groupKey) {
			if (dropTarget) dropTarget = null;
			return;
		}
		event.preventDefault();
		const bounds = row.getBoundingClientRect();
		const position = event.clientY < bounds.top + bounds.height / 2 ? "before" : "after";
		if (dropTarget?.ownedId !== ownedId || dropTarget.position !== position) dropTarget = { ownedId, position };
	}

	function drop(event: DragEvent): void {
		const from = dragging;
		const to = dropTarget;
		endDrag();
		if (!from || !to) return;
		event.preventDefault();
		const lists = groups.flatMap((group) => group.subgroups ?? [group]);
		const group = lists.find((candidate) => candidate.key === from.groupKey);
		if (!group) return;
		// Leaving Recent or Name, the Custom order starts from what is on screen.
		const order =
			options.sortBy === "manual"
				? manualOrder
				: lists.flatMap((list) => list.sessions.map((session) => session.ownedId));
		const groupIds = group.sessions.map((session) => session.ownedId);
		onReorder?.(reorderMyWorkSessions(order, groupIds, from.ownedId, to.ownedId, to.position));
	}

	function endDrag(): void {
		dragging = null;
		dropTarget = null;
	}

	function copyText(value: string | null): void {
		if (!value || !navigator.clipboard?.writeText) return;
		void navigator.clipboard.writeText(value);
	}

	function runContextAction(action: SessionRowMenuAction): void {
		const session = contextMenu?.session;
		contextMenu = null;
		if (!session) return;
		if (action === "rename") renamingOwnedId = session.ownedId;
		else if (action === "mark-done") onComplete?.(session.ownedId);
		else if (action === "reopen") onReopen?.(session.ownedId);
		else if (action === "archive") onSettle?.(session.ownedId);
		else if (action === "unsettle") onUnsettle?.(session.ownedId);
		else if (action === "pin" || action === "unpin") onPin?.(session.ownedId, action === "pin");
		else if (action === "mark-orchestrator" || action === "unmark-orchestrator")
			onMarkOrchestrator?.(session.ownedId, action === "mark-orchestrator");
		else if (action === "copy-deeplink") copyText(`assembly://session/${encodeURIComponent(session.ownedId)}`);
		else if (action === "copy-session-id") copyText(session.nativeSessionId || session.ownedId);
		else if (action === "copy-resume-command")
			copyText(resumeCommand(session.agent, session.nativeSessionId, session.cwd));
		else if (action === "copy-worktree-path") copyText(session.cwd || null);
		else if (action === "open-in-editor") void jumpTo(session, "editor");
		else if (action === "open-source-control") void jumpTo(session, "source-control");
		else if (action === "delete") onAskRemove?.(session.ownedId);
	}
</script>

<!-- One session row; a helper row is the same row with no drop target. -->
{#snippet row(session: OwnedSession, dropPosition: "before" | "after" | null)}
	<WorktreeAgentRow
		{session}
		status={rowStatuses.get(session.ownedId) ?? NO_STATUS}
		{nowMs}
		active={session.ownedId === visualActiveOwnedId}
		onSelect={() => void selectRow(session.ownedId)}
		onContextMenu={(event) => openContextMenu(event, session)}
		renaming={session.ownedId === renamingOwnedId}
		onRename={(title) => {
			renamingOwnedId = null;
			if (title) onRename?.(session.ownedId, title);
		}}
		onConnect={() => onConnect?.(session.ownedId)}
		{dropPosition}
	/>
{/snippet}

<!-- A section is a heading and its rows, or, with both groupings on, a status
     heading and the project sections inside it. -->
{#snippet section(group: MyWorkGroup, nested: boolean)}
	{@const allSessions = myWorkGroupSessions(group)}
	{@const needsYouCount = allSessions.filter(sessionNeedsYou).length}
	<!-- Project headings sit at the top level, or inside each status when both
	     groupings are on. The Pinned section is not a project. -->
	{@const runHeading = group.key.startsWith("run:")}
	{@const projectHeading =
		options.groupByProject && (nested || !options.groupByStatus) && group.key !== "pinned" && group.key !== "orchestrator" && !runHeading}
	<section data-testid="session-rail-section" data-group-key={group.key} class:collapsed={!isOpen(group.key)}>
		{#if group.label}
			<button
				data-testid="session-rail-section-toggle"
				type="button"
				class="section-heading"
				class:nested
				class:project={projectHeading || runHeading}
				aria-expanded={isOpen(group.key)}
				onclick={() => toggleGroup(group.key)}
			>
				<!-- One chevron that turns, rather than two that swap: the quarter
           turn is what tells a person the section answered them. -->
				<ChevronRight class="chevron" aria-hidden="true" />
				<!-- Inside a status the key reads "<status>::none". -->
				{#if runHeading}
					<Workflow class="group-icon" aria-hidden="true" />
				{:else if projectHeading && (group.key === "none" || group.key.endsWith("::none"))}
					<MessageCircle class="group-icon" aria-hidden="true" />
				{:else if projectHeading}
					<Badge variant="outline" class="h-4 rounded-sm px-1 font-mono text-[10px]" aria-hidden="true">
						{projectBadge(group.label)}
					</Badge>
				{/if}
				<span class="name">{group.label}</span>
				<!-- The machines its sessions are on, as on the Projects screen. A repository's
				     copies on two machines share one heading, so it can name more than one. -->
				{#if projectHeading && group.key !== "none" && !group.key.endsWith("::none")}
					{#each [...new Set(allSessions.map((session) => projectMachineLabel(session.executionEnvironment === "remote" ? (session.remoteProfileId ?? "") : "local", rail.remoteProfiles)))].sort() as machine (machine)}
						<Chip class="text-xs">{machine}</Chip>
					{/each}
				{/if}
				<span data-testid="session-rail-section-count" class="count">{allSessions.length}</span>
				{#if needsYouCount > 0}
					<span
						data-testid="session-rail-needs-you-count"
						class="needs-count"
						aria-label={`${needsYouCount} ${needsYouCount === 1 ? "session needs" : "sessions need"} you`}
					>
						<span aria-hidden="true"></span>{needsYouCount}
					</span>
				{/if}
			</button>
		{/if}

		{#if isOpen(group.key)}
			{#if group.subgroups}
				{#each group.subgroups as subgroup (subgroup.key)}
					{@render section(subgroup, true)}
				{/each}
			{:else}
				<ul class="rows" data-group-key={group.key}>
					{#each group.sessions as session (session.ownedId)}
						{@render row(session, dropTarget?.ownedId === session.ownedId ? dropTarget.position : null)}
						<!-- The session's helpers, one step in under it. Their list carries no
						     group key, so they are never a drag source or drop target. -->
						{#if group.helpers?.[session.ownedId]}
							<li class="helpers">
								<ul class="helper-rows">
									{#each group.helpers[session.ownedId] as helper (helper.ownedId)}
										{@render row(helper, null)}
									{/each}
								</ul>
							</li>
						{/if}
					{/each}
				</ul>
			{/if}
		{/if}
	</section>
{/snippet}

<div
	data-testid="session-rail"
	class="session-scroll"
	role="presentation"
	ondragstart={startDrag}
	ondragover={dragOver}
	ondrop={drop}
	ondragend={endDrag}
>
	{#each groups as group (group.key)}
		{@render section(group, false)}
	{/each}

	{#if groups.every((group) => group.sessions.length === 0)}
		<p class="empty-state">No work matches these filters</p>
	{/if}

	<div class="scroll-spacer" aria-hidden="true"></div>
</div>

{#if contextMenu}
	<SessionRowContextMenu
		x={contextMenu.x}
		y={contextMenu.y}
		items={contextMenuItems}
		session={contextMenu.session}
		onSelect={runContextAction}
		onClose={() => {
			contextMenu = null;
		}}
	/>
{/if}

<style>
	/* One scroll area for the whole list, with the scrollbar in its own gutter so
     it never sits on top of a row or a heading. */
	.session-scroll {
		min-height: 0;
		height: 100%;
		overflow-y: scroll;
		scrollbar-gutter: stable;
		scrollbar-width: thin;
		scrollbar-color: var(--color-text-3) var(--color-surface);
		overscroll-behavior: contain;
	}

	.session-scroll::-webkit-scrollbar {
		width: 9px;
	}
	.session-scroll::-webkit-scrollbar-track {
		background: var(--color-surface);
	}
	.session-scroll::-webkit-scrollbar-thumb {
		min-height: 48px;
		border: 2px solid var(--color-surface);
		border-radius: var(--radius-sm);
		background: var(--color-text-3);
	}

	.section-heading {
		position: sticky;
		top: 0;
		z-index: 3;
		display: flex;
		width: 100%;
		min-height: 28px;
		align-items: center;
		gap: 6px;
		padding: 8px 16px 4px;
		line-height: 16px;
		border: 0;
		background: var(--card);
		color: var(--muted-foreground);
		font-size: 12px;
		letter-spacing: 0.06em;
		text-transform: uppercase;
		text-align: left;
		cursor: pointer;
	}

	/* A project heading reads as the project's own name, in its own case. */
	.section-heading.project {
		color: var(--foreground);
		font-size: 12px;
		letter-spacing: normal;
		text-transform: none;
	}
	.section-heading :global(.group-icon) {
		width: 14px;
		height: 14px;
		flex: 0 0 auto;
	}

	/* A project heading inside a status: indented under it, and not sticky, so
     it scrolls away under the status heading rather than stacking on it. */
	.section-heading.nested {
		position: static;
		padding-left: 32px;
	}

	.section-heading:focus-visible {
		outline: 2px solid var(--color-focus);
		outline-offset: -2px;
	}
	.section-heading :global(.chevron) {
		width: 12px;
		height: 12px;
		flex: 0 0 auto;
	}
	.section-heading[aria-expanded="true"] :global(.chevron) {
		transform: rotate(90deg);
	}
	.section-heading .name {
		min-width: 0;
		overflow: hidden;
		font-weight: 600;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.section-heading .count {
		margin-left: auto;
		color: var(--color-text-3);
		font-variant-numeric: tabular-nums;
	}

	.needs-count {
		min-width: 24px;
		height: 20px;
		display: inline-flex;
		flex: 0 0 auto;
		align-items: center;
		justify-content: center;
		gap: 4px;
		padding: 0 6px;
		border: 1px solid color-mix(in srgb, var(--color-attention) 34%, transparent);
		border-radius: var(--radius-sm);
		background: var(--color-attention-bg);
		color: var(--color-attention);
		font-size: 12px;
		font-weight: 650;
		font-variant-numeric: tabular-nums;
	}

	.needs-count > span {
		width: 6px;
		height: 6px;
		flex: 0 0 auto;
		border-radius: 50%;
		background: var(--color-attention);
	}

	/* A shut section has nothing to stick to, so its heading joins the list. */
	.collapsed .section-heading {
		position: relative;
		top: auto;
		border-top: 1px solid var(--color-border);
	}

	/* Rows sit 2px apart, so two highlighted rows never touch. */
	.rows {
		display: flex;
		min-width: 0;
		flex-direction: column;
		gap: 2px;
		margin: 0;
		padding: 0;
		list-style: none;
	}

	/* Helper rows sit one step in, on a thin guide line from their parent. */
	.helpers {
		list-style: none;
	}
	.helper-rows {
		display: flex;
		min-width: 0;
		flex-direction: column;
		gap: 2px;
		margin: 0 0 0 var(--space-5);
		padding: 0 0 0 var(--space-1);
		border-left: 1px solid var(--color-border);
		list-style: none;
	}

	/* Opening a section is a short settle, not a height animation: the rows keep
     their real height from the first frame, so nothing above or below them
     moves twice. Both of these end; neither loops. */
	@media (prefers-reduced-motion: no-preference) {
		.section-heading :global(.chevron) {
			transition: transform 160ms cubic-bezier(0.2, 0, 0, 1);
		}
		.section-heading {
			transition: color 120ms ease;
		}
		.rows {
			animation: section-in 160ms ease-out;
		}
	}

	@keyframes section-in {
		from {
			opacity: 0;
			transform: translateY(-4px);
		}
		to {
			opacity: 1;
			transform: none;
		}
	}
	.empty-state {
		margin: 0;
		padding: 10px 12px;
		color: var(--color-text-3);
		font-size: 13px;
	}
	.scroll-spacer {
		height: 64px;
	}
</style>
