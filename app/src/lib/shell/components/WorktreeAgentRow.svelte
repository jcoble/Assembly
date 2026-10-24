<script lang="ts">
	/**
	 * One session in the rail: status, title, the machine it runs on and its branch.
	 *
	 * The row currently owns one visual-only click. It changes the rail's local
	 * active paint and calls no session, panel, persistence, or native service.
	 *
	 * The rail passes one shared timestamp into every row. This row mounts no
	 * timer or clock subscription. Its seeded activity glyph exists only during
	 * real work and pauses itself when the row is offscreen. A dot on the
	 * provider tile says when the session is waiting on you or failed, and a
	 * cloud in its corner marks a session on a remote machine.
	 */
	import Bot from "@lucide/svelte/icons/bot";
	import Cloud from "@lucide/svelte/icons/cloud";
	import Crown from "@lucide/svelte/icons/crown";
	import Hand from "@lucide/svelte/icons/hand";
	import MessageSquareDot from "@lucide/svelte/icons/message-square-dot";
	import SquareTerminal from "@lucide/svelte/icons/square-terminal";
	import Workflow from "@lucide/svelte/icons/workflow";
	import type { Component } from "svelte";
	import { Button } from "$lib/components/ui/button/index.js";
	import { Chip } from "$lib/components/ui/chip/index.js";
	import type { ChipTone } from "$lib/components/ui/chip/variants.js";
	import * as Tooltip from "$lib/components/ui/tooltip/index.js";
	import { AGENT_ICONS, agentDisplayName } from "$lib/shell/agentIcons.ts";
	import {
		deriveSessionPresence,
		EMPTY_SESSION_PRESENCE_HISTORY,
		sessionPresenceHistory,
	} from "$lib/shell/conversation/sessionPresence.ts";
	import { presentAgentError } from "$lib/shell/errorPresentation";
	import { sessionWaitsOnYou, type OwnedSession, type RailRowStatus } from "$lib/shell/ownedSessions";
	import { projectMachineLabel } from "$lib/shell/projects/projects";
	import { deriveOwnedLibraryState } from "$lib/shell/history/library/sessionLibraryModel";
	import { sessionLabel } from "$lib/shell/sessionStrip";
	import { rail } from "$lib/shell/stores/sessionRailStore.svelte";
	import { formatRailElapsed } from "./railElapsedTicker.ts";
	import SessionRowVisual from "./SessionRowVisual.svelte";

	interface Props {
		session: OwnedSession;
		/** What the status icons count: this session, its helpers and the workers of runs it started. */
		status: RailRowStatus;
		nowMs: number;
		active?: boolean;
		onSelect?(): void;
		onContextMenu?(event: MouseEvent): void;
		/** The row shows a name field over its title while this is on. */
		renaming?: boolean;
		/** The new name, or `null` when the person backed out or left it unchanged. */
		onRename?(title: string | null): void;
		onConnect?(): void;
		/** Where a dragged row would land against this one, while it is over it. */
		dropPosition?: "before" | "after" | null;
	}

	type RowPresence = "working" | "attention" | "idle" | "done" | "failed";
	let {
		session,
		status,
		nowMs,
		active = false,
		onSelect,
		onContextMenu,
		renaming = false,
		onRename,
		onConnect,
		dropPosition = null,
	}: Props = $props();

	const label = $derived(sessionLabel(session));
	const shelf = $derived(deriveOwnedLibraryState(session));
	const presentedError = $derived(session.lastError ? presentAgentError(session.lastError) : null);

	const presenceHistory = $derived($sessionPresenceHistory[session.ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY);
	// Presence is rail-record truth plus events received live. Loading a stored
	// transcript may populate `conversation`, but it must not repaint this row.
	const needsYou = $derived(sessionWaitsOnYou(session));
	/** The one status tooltip open on this row; its content mounts only while open. */
	let openTip = $state<string | null>(null);
	// Moving between two icons opens the next before the last one closes.
	function setTip(kind: string, open: boolean): void {
		if (open) openTip = kind;
		else if (openTip === kind) openTip = null;
	}
	const pendingApprovalCount = $derived(needsYou ? 1 : 0);
	const runtimeState = $derived(session.runtimeState);
	const activeTurnId = $derived(session.activeTurnId ?? presenceHistory.activeTurnId);
	const suspended = $derived(session.runtimeState === "suspended");
	const backgroundActive = $derived(
		(session.backgroundWork?.length ?? 0) > 0
			&& (session.executionEnvironment !== "remote" || rail.remoteConnections[session.remoteProfileId ?? ""] === "connected"),
	);
	const presenceSignals = $derived(
		deriveSessionPresence(
			{
				terminalState: session.state,
				suspended,
				activeTurnId,
				sending: false,
				pendingApprovalCount,
				runtimeState,
			},
			presenceHistory,
			0,
		).state,
	);

	const presence = $derived<RowPresence>(
		backgroundActive
			? "working"
			: shelf === "done"
			? "done"
			: session.lastError || runtimeState === "failed"
				? "failed"
				: suspended
					? "idle"
					: needsYou || presenceSignals === "needs-attention"
						? "attention"
						: presenceSignals === "working"
							? "working"
							: "idle",
	);
	const finishedUnread = $derived(!needsYou && presenceSignals === "needs-attention");
	const presenceLabel = $derived(
		{
			working: "Working",
			attention: "Waiting on you",
			idle: "Idle",
			done: "Finished",
			failed: "Error",
		}[presence],
	);
	const ProviderIcon = $derived(AGENT_ICONS[session.agent]);
	const providerName = $derived(agentDisplayName(session.agent, session.viaCmux));
	/** Whose machine runs this session. The saved record answers it. */
	const remote = $derived(session.executionEnvironment === "remote");
	/**
	 * What that machine's connection is doing, from the backend's own lifecycle
	 * event and nothing else. A saved row starts disconnected until the event
	 * says otherwise; presence, a loading transcript, or the row simply existing
	 * never implies it. No read, no timer, no animation.
	 */
	const remoteState = $derived(
		remote ? (rail.remoteConnections[session.remoteProfileId ?? ""] ?? "disconnected") : null,
	);
	/** "This Mac" or the saved profile's name; the group heading already names the project. */
	const machine = $derived(projectMachineLabel(remote ? (session.remoteProfileId ?? "") : "local", rail.remoteProfiles));

	// ── The age, and the working indicator in the rail ─────────────────────────
	/**
	 * How old this session is, counted from when it started rather than from the
	 * current turn — the number stays put when the agent stops working, which is
	 * what makes it comparable between rows.
	 */
	const startedAtMs = $derived(
		session.startedAtMs ??
			(session.lastActivity && Number.isFinite(Date.parse(session.lastActivity))
				? Date.parse(session.lastActivity)
				: null),
	);
	const ageMs = $derived(startedAtMs === null ? null : Math.max(0, nowMs - startedAtMs));
	const ageText = $derived(ageMs === null ? null : formatRailElapsed(ageMs));

	// Enter, Escape and the blur that follows the field leaving can each end one
	// rename; only the first one counts.
	let renameEnded = false;
	// The field takes the name once, when it opens, so a title arriving while a
	// person types cannot replace what they typed.
	function startRename(input: HTMLInputElement): void {
		renameEnded = false;
		input.value = label;
		input.focus();
		input.select();
	}
	function endRename(input: HTMLInputElement, keep: boolean): void {
		if (renameEnded) return;
		renameEnded = true;
		const next = input.value.trim();
		onRename?.(keep && next && next !== label ? next : null);
	}
</script>

<!-- One status icon: a small chip with a count when there is more than one, and a tooltip that says it in words. -->
{#snippet statusIcon(kind: string, testId: string, tone: ChipTone, Icon: Component, count: number, tip: string)}
	<Tooltip.Root open={openTip === kind} onOpenChange={(open) => setTip(kind, open)}>
		<Tooltip.Trigger>
			{#snippet child({ props })}
				<span {...props} data-testid={testId} class="status-icon" role="img" aria-label={tip}>
					<Chip {tone} class="h-4 gap-0.5 px-1 tabular-nums"><Icon aria-hidden="true" />{#if count > 1}{count}{/if}</Chip>
				</span>
			{/snippet}
		</Tooltip.Trigger>
		{#if openTip === kind}
			<Tooltip.Content side="top" sideOffset={6}>{tip}</Tooltip.Content>
		{/if}
	</Tooltip.Root>
{/snippet}

<li
	data-testid="worktree-agent-row"
	data-presence={presence}
	data-shelf={shelf}
	data-owned-id={session.ownedId}
	draggable={!renaming}
	class:drop-before={dropPosition === "before"}
	class:drop-after={dropPosition === "after"}
	class:needs-you-row={needsYou}
	class:selected={active}
	class="row"
	oncontextmenu={onContextMenu}
>
	<SessionRowVisual {active} {onSelect}>
		<!-- The 32px provider tile. It carries the provider and, in its corner
               dot, whether this session is working, waiting or failed, and
               nothing else: no action is ever drawn on top of it. -->
		<span
			data-testid="worktree-agent-provider"
			class="thumb"
			data-agent={session.agent}
			role="img"
			aria-label={remote ? `${providerName}, on a remote machine, ${remoteState}` : providerName}
			title={remote ? `This session runs on a remote machine — ${remoteState}` : undefined}
		>
			<ProviderIcon class="thumb-mark" aria-hidden="true" />
			{#if remote}
				<span class="cloud-mark" data-state={remoteState} aria-hidden="true"><Cloud /></span>
			{/if}
			{#if presence === "attention" || presence === "failed"}
				<!-- The cloud holds the bottom corner of a remote tile, so the dot moves up. -->
				<span class="status-dot" class:top={remote} data-presence={presence} role="img" aria-label={presenceLabel}></span>
			{/if}
		</span>

		<span class="lines">
			<!-- Line one is the title and a corner that always holds exactly one
                 thing: the last activity time at rest, the quick-jump buttons
                 while the pointer is on the row. The corner is in the same place
                 either way, so the buttons never land on the title — the title
                 simply clips a little earlier to make room for them. -->
			<span class="line line-title">
				{#if session.orchestratorMarkedAtMs}
					<Tooltip.Root open={openTip === "crown"} onOpenChange={(open) => setTip("crown", open)}>
						<Tooltip.Trigger>
							{#snippet child({ props })}
								<span {...props} data-testid="worktree-agent-crown" class="crown" role="img" aria-label="Main orchestrator">
									<Crown aria-hidden="true" />
								</span>
							{/snippet}
						</Tooltip.Trigger>
						{#if openTip === "crown"}
							<Tooltip.Content side="top" sideOffset={6}>Main orchestrator</Tooltip.Content>
						{/if}
					</Tooltip.Root>
				{/if}
				<span data-testid="worktree-agent-title" class="session-title">{label}</span>
				<span data-testid="worktree-agent-age" class="age">
					<span class="age-text">{ageText ?? ""}</span>
					{#if presence === "working"}
						<span class="working-mark" role="img" aria-label="Working">
							<span class="size-[7px] rounded-full bg-primary"></span>
						</span>
					{/if}
				</span>
			</span>

			<span class="line line-meta">
				<span data-testid="worktree-agent-meta" class="machine">{machine}</span>
				{#if session.handle}
					<span class="sep" aria-hidden="true">•</span>
					<span class="handle" title="Other sessions send messages to this one as {session.handle}">{session.handle}</span>
				{/if}
				{#if session.branch}
					<span class="sep" aria-hidden="true">•</span>
					<span class="branch">{session.branch}</span>
				{/if}

				<!-- Small icons for what is under this session, needs you first. The
				     working state is the mark's job; a failure still needs its words. -->
				<span class="status-icons">
					{#if status.waiting.length}
						{@render statusIcon("needs-you", "worktree-agent-needs-you", "attention", Hand, status.waiting.length,
							`Needs you: ${status.waiting.length} (${status.waiting.join("; ")})`)}
					{/if}
					{#if backgroundActive && status.background}
						{@render statusIcon("background", "worktree-agent-background", "live", SquareTerminal, status.background,
							`${status.background} background ${status.background === 1 ? "task" : "tasks"} running`)}
					{/if}
					{#if status.helpers}
						{@render statusIcon("helpers", "worktree-agent-helpers", "live", Bot, status.helpers,
							`${status.helpers} ${status.helpers === 1 ? "helper session or sub-agent" : "helper sessions and sub-agents"} running`)}
					{/if}
					{#if status.runs}
						{@render statusIcon("runs", "worktree-agent-runs", "live", Workflow, status.runs,
							`${status.runs} workflow ${status.runs === 1 ? "run" : "runs"} working`)}
					{/if}
					{#if finishedUnread}
						{@render statusIcon("unread", "worktree-agent-unread", "neutral", MessageSquareDot, 1,
							"Unread: the last turn finished and you have not read it")}
					{/if}
				</span>
				{#if presence === "failed"}
					<span data-testid="worktree-agent-status" class="failed">{presentedError?.summary ?? presenceLabel}</span>
				{/if}
			</span>
		</span>
	</SessionRowVisual>

	{#if renaming}
		<input
			data-testid="worktree-agent-rename"
			class="rename"
			aria-label="Session name"
			use:startRename
			onkeydown={(event) => {
				if (event.key === "Enter") endRename(event.currentTarget, true);
				else if (event.key === "Escape") endRename(event.currentTarget, false);
			}}
			onblur={(event) => endRename(event.currentTarget, true)}
		/>
	{/if}

	<!-- A remote session whose machine is not connected offers the connection
	     right on its row. It sits beside the row button, never inside it. -->
	{#if remote && remoteState !== "connected" && onConnect}
		<span class="connect">
			<Button
				data-testid="worktree-agent-connect"
				size="xs"
				variant="outline"
				disabled={remoteState === "reconnecting"}
				onclick={onConnect}>{remoteState === "reconnecting" ? "Connecting…" : "Connect"}</Button
			>
		</span>
	{/if}
</li>

<style>
	/* The name field covers the title line: past the 32px tile and its gap. */
	.rename {
		position: absolute;
		top: 6px;
		left: 60px;
		right: 16px;
		z-index: 2;
		height: 22px;
		padding: 0 6px;
		border: 1px solid var(--focus-border);
		border-radius: var(--radius-sm);
		background: var(--card);
		color: var(--foreground);
		font: inherit;
		outline: none;
	}

	/* Where a dragged row will land. Drawn inside the row, since the row's
	   content-visibility clips anything outside it. */
	.row.drop-before::before,
	.row.drop-after::after {
		content: "";
		position: absolute;
		left: 8px;
		right: 8px;
		z-index: 3;
		height: 2px;
		border-radius: 1px;
		background: var(--color-accent);
	}
	.row.drop-before::before {
		top: 0;
	}
	.row.drop-after::after {
		bottom: 0;
	}

	.connect {
		position: absolute;
		right: 16px;
		bottom: 6px;
		z-index: 2;
	}

	.row {
		position: relative;
		display: block;
		box-sizing: border-box;
		min-width: 0;
		content-visibility: auto;
		contain-intrinsic-size: auto 48px;
		/* The highlight block is inset from the rail's edges rather than bled to
       them, so a hovered row reads as a card in the list. */
		padding: 0 8px;
		list-style: none;
		color: var(--foreground);
		font-size: 13px;
		line-height: 1.4;
	}

	/* The 32px provider tile, on the raised fill. Nothing is ever drawn over
     this square: it says who is running the session and, in its corner dot,
     what state the session is in. */
	.thumb {
		position: relative;
		display: flex;
		width: 32px;
		height: 32px;
		align-items: center;
		justify-content: center;
		border-radius: 8px;
		background: var(--muted);
		color: var(--muted-foreground);
	}

	.row.selected .thumb {
		background: var(--secondary);
	}

	:global(.thumb-mark) {
		width: 15px;
		height: 15px;
		flex: 0 0 auto;
	}

	/* The status dot in the tile's bottom corner, ringed in the row's own fill
     so it reads as cut out of the tile. Static: it never animates. */
	.status-dot {
		position: absolute;
		right: -2px;
		bottom: -2px;
		width: 10px;
		height: 10px;
		box-sizing: border-box;
		border: 2px solid var(--card);
		border-radius: 50%;
		background: var(--color-good);
	}

	.status-dot[data-presence="attention"] {
		background: var(--color-attention);
	}

	.status-dot[data-presence="failed"] {
		background: var(--color-bad);
	}

	.row:hover .status-dot {
		border-color: var(--accent);
	}

	.row.selected .status-dot {
		border-color: var(--secondary);
	}

	.status-dot.top {
		top: -2px;
		bottom: auto;
	}

	/* The cloud in a remote tile's bottom corner, cut out of the tile like the
     dot. Static: its colour changes only when the machine's state does, and it
     never animates. */
	.cloud-mark {
		position: absolute;
		right: -3px;
		bottom: -3px;
		display: flex;
		width: 15px;
		height: 15px;
		align-items: center;
		justify-content: center;
		border-radius: 50%;
		background: var(--card);
		color: var(--color-idle);
	}

	.cloud-mark :global(svg) {
		width: 10px;
		height: 10px;
	}

	.row:hover .cloud-mark {
		background: var(--accent);
	}

	.row.selected .cloud-mark {
		background: var(--secondary);
	}

	/* One state, told in colour as well as in the label and tooltip above. */
	.cloud-mark[data-state="connected"] {
		color: var(--color-good);
	}

	.cloud-mark[data-state="reconnecting"] {
		color: var(--color-attention);
	}

	/* The two marks their vendors publish in a colour wear it here. The rest
     stay the theme's own, because inventing a brand colour for them would be
     a worse lie than a neutral glyph. */
	.thumb[data-agent="claude"] {
		color: var(--agent-mark-claude);
	}
	.thumb[data-agent="codex"] {
		color: var(--agent-mark-codex);
	}

	/* TWO fixed line boxes, not three. The heights are declared rather than left
     to the font so the row is the same height on every machine and the mark can
     be sized against it. */
	.lines {
		display: flex;
		min-width: 0;
		flex-direction: column;
		justify-content: center;
		gap: 0;
	}

	.line {
		position: relative;
		display: flex;
		min-width: 0;
		align-items: center;
		gap: 5px;
	}

	.line-title {
		height: 20px;
		line-height: 20px;
	}

	.line-meta {
		height: 16px;
		line-height: 16px;
	}

	.machine {
		min-width: 0;
		flex: 0 1 auto;
		overflow: hidden;
		color: var(--muted-foreground);
		font-size: 12px;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	/* The branch, at the machine's size. Not monospaced: this
     line is read, not compared character by character, and a mono face at this
     size is both wider and harder to read in a rail this narrow. The machine
     gives way first, because it repeats down the whole list and the branch is
     what tells one row from the next. */
	.sep {
		flex: 0 0 auto;
		color: var(--color-text-3);
		font-size: 12px;
	}

	.handle,
	.branch {
		min-width: 0;
		flex: 0 1 auto;
		overflow: hidden;
		color: var(--muted-foreground);
		font-size: 12px;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	/* The status icons sit at the meta line's right end and never shrink; the
     machine, the branch and a failure's words give way to them. */
	.status-icons {
		display: inline-flex;
		flex: 0 0 auto;
		align-items: center;
		gap: 4px;
		margin-left: auto;
	}

	.status-icon {
		display: inline-flex;
		flex: 0 0 auto;
	}

	/* Same 12px glyph as the status chips, beside the title. */
	.crown {
		display: inline-flex;
		flex: 0 0 auto;
		color: var(--primary);
	}

	.crown :global(svg) {
		width: 12px;
		height: 12px;
	}

	/* Last activity stays in line one's right corner while the row is hovered. */
	.age {
		display: inline-flex;
		flex: 0 0 auto;
		align-items: center;
		justify-content: flex-end;
		gap: 6px;
		padding-left: 8px;
		color: var(--color-text-3);
		font-size: 12px;
		font-variant-numeric: tabular-nums;
		white-space: nowrap;
	}

	/* Keep elapsed values right-aligned down the list. */
	.age-text {
		min-width: 28px;
		text-align: right;
	}

	.working-mark {
		display: inline-grid;
		width: 14px;
		height: 14px;
		flex: 0 0 auto;
		place-items: center;
		color: var(--primary);
	}

	.failed {
		min-width: 0;
		flex: 0 1 auto;
		overflow: hidden;
		color: var(--color-bad);
		font-size: 12px;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	.session-title {
		min-width: 0;
		flex: 1 1 auto;
		overflow: hidden;
		color: var(--foreground);
		font-size: 14px;
		font-weight: 500;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	/* The running session's title takes the primary colour. */
	.row[data-presence="working"] .session-title {
		color: var(--primary);
	}

	/* Presence changes may fade their own small labels; pointer movement owns no
     transition or animation in a session row. */
	@media (prefers-reduced-motion: no-preference) {
		.age-text,
		.thumb {
			transition: opacity 120ms ease;
		}
	}
</style>
