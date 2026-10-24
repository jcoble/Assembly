<script lang="ts">
	/**
	 * ShellOverlays.svelte — everything that floats above the /next shell rather
	 * than living in a region: the command palette, the settings dialog, the
	 * message strip, the quit warning, and the development-only backend call
	 * counter.
	 *
	 * Its one backend call lets a held close or quit go ahead, and its only
	 * state is the settings handle and that pending exit. The
	 * palette is mounted exactly once (two of them would answer the same keyboard
	 * shortcut and would fight over the seeded actions), which is why it lives
	 * here and not inside a panel.
	 */
	import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
	import { onMount } from "svelte";

	import * as AlertDialog from "$lib/components/ui/alert-dialog/index.js";
	import type { ProblemsLocation } from "$lib/shell/settings/settingsStore.svelte";
	import { cancelAppExit, continueAppExit, listenToAppExitRequests, type AppExitKind } from "$lib/tauriSource";
	import { backgroundWorkSummary, localBackgroundWorkSessions } from "$lib/shell/ownedSessions";
	import { sessionLabel } from "$lib/shell/sessionStrip";
	import { rail } from "$lib/shell/stores/sessionRailStore.svelte";
	import AssistanceHost from "$lib/shell/assistance/AssistanceHost.svelte";
	import ResourceManagerPanel from "$lib/shell/resources/ResourceManagerPanel.svelte";
	import ResourcePopover from "$lib/shell/resources/ResourcePopover.svelte";
	import { resourceManagerState } from "$lib/shell/resources/resourceSampleStore.svelte";
	import UsagePopover from "$lib/shell/usage/UsagePopover.svelte";
	import PalettePanel from "./PalettePanel.svelte";
	import SettingsHost from "../settings/SettingsHost.svelte";
	import { utilityAnchorStyle, type UtilityAnchor, type UtilityId } from "./utilityStrip";

	interface Props {
		/** Put every panel back where it started. */
		onResetLayout: () => void;
		/** One line describing whatever has gone wrong, or null when all is well. */
		message: string | null;
		/** The user moved the Problems list from the settings dialog, which lives
		 * here; the page is what opens or closes the strip along the bottom. */
		onProblemsLocationChange?: (location: ProblemsLocation) => void;
    onRemoteConnected?: (profileId: string) => void;
		/** Restore the hidden Problems and Terminal dock. */
		onShowBottomDock?: () => void;
		/** One of the two bottom-strip surfaces opened or closed. The strip draws
		 * its own button as on or off from this. */
		onUtilityStateChange?: (id: UtilityId, open: boolean) => void;
		/** Runs before a close or quit goes ahead: the window goes away without tearing the page down. */
		onBeforeExit: () => Promise<void>;
	}
	let { onResetLayout, message, onProblemsLocationChange, onRemoteConnected, onShowBottomDock, onUtilityStateChange, onBeforeExit }: Props = $props();

	let settingsHost: { open: (section?: string) => void; close: () => void; isOpen: () => boolean } | null = null;
	let resourcePopoverHost: HTMLDivElement | null = null;
	let usagePopoverHost: HTMLDivElement | null = null;
	let resourceAnchor = $state<UtilityAnchor | null>(null);
	let usageAnchor = $state<UtilityAnchor | null>(null);

	function popoverHost(id: UtilityId): HTMLDivElement | null {
		return id === "resources" ? resourcePopoverHost : usagePopoverHost;
	}

	function popoverTrigger(id: UtilityId): HTMLButtonElement | null {
		return popoverHost(id)?.querySelector<HTMLButtonElement>(".trigger") ?? null;
	}

	function positionPopoverHost(id: UtilityId, anchor: UtilityAnchor): void {
		popoverHost(id)?.setAttribute("style", utilityAnchorStyle(anchor));
	}

	/** Watch one surface's own trigger and report whether it is open, so the
	 * bottom strip's button can read as on without owning that state itself. */
	function observeUtilityState(id: UtilityId): () => void {
		const trigger = popoverTrigger(id);
		if (!trigger) return () => {};
		trigger.tabIndex = -1;
		trigger.setAttribute("aria-hidden", "true");
		const report = (): void => onUtilityStateChange?.(id, trigger.getAttribute("aria-expanded") === "true");
		const observer = new MutationObserver(report);
		observer.observe(trigger, { attributes: true, attributeFilter: ["aria-expanded"] });
		report();
		return () => observer.disconnect();
	}

	/**
	 * A held close or quit, and the sessions on this Mac whose sub-agents or
	 * commands it would stop. With none, the exit goes ahead without asking;
	 * remote sessions keep running on their server, so they never count.
	 */
	let exitRequest = $state<{ kind: AppExitKind; lines: string[] } | null>(null);

	function exitRequested(kind: AppExitKind): void {
		const sessions = localBackgroundWorkSessions(rail.owned);
		if (sessions.length === 0) {
			void exitApp(kind);
			return;
		}
		exitRequest = {
			kind,
			lines: sessions.map((session) => `${sessionLabel(session)} — ${backgroundWorkSummary(session.backgroundWork ?? [])}`),
		};
	}

	async function exitApp(kind: AppExitKind): Promise<void> {
		try {
			await onBeforeExit();
		} catch (error) {
			console.warn("Could not save the workspace before exit", error);
		}
		await continueAppExit(kind);
	}

	onMount(() => {
		const stopObservingResources = observeUtilityState("resources");
		const stopObservingUsage = observeUtilityState("usage");
		let stopExitRequests: (() => void) | null = null;
		const controller = new AbortController();
		async function listenForExitRequests(signal: AbortSignal): Promise<void> {
			try {
				const stop = await listenToAppExitRequests(exitRequested);
				if (signal.aborted) stop();
				else stopExitRequests = stop;
			} catch (error) {
				console.warn('Could not listen for app exit requests', error);
			}
		}
		void listenForExitRequests(controller.signal);
		return () => {
			controller.abort();
			stopExitRequests?.();
			stopObservingResources();
			stopObservingUsage();
		};
	});

	/**
	 * Open one of the two bottom-strip surfaces from outside.
	 *
	 * The strip is inside the layout and these surfaces are mounted out here,
	 * above it, so a card cannot be clipped by the column it was opened from.
	 * The anchor is applied to state and the mounted host before the surface's
	 * own trigger is clicked, or the card opens where the last one was.
	 */
	export function openUtility(id: UtilityId, anchor: UtilityAnchor): void {
		if (id === "resources") resourceAnchor = anchor;
		else usageAnchor = anchor;
		positionPopoverHost(id, anchor);
		popoverTrigger(id)?.click();
	}

	/** Open the settings dialog from outside — the gear on the activity bar is
	 * over in the left column, and the dialog lives here. Same shape as
	 * `SettingsHost`'s own `open()`, one layer out. */
	export function openSettings(section?: string): void {
		settingsHost?.open(section);
	}

	/** Whether the settings screen is up. Reactive: the host's open state is
	 * `$state`, so a caller reading this in a template follows it. */
	export function settingsOpen(): boolean {
		return settingsHost?.isOpen() ?? false;
	}
</script>

<PalettePanel {onResetLayout} {onShowBottomDock} onOpenSettings={() => settingsHost?.open()} />
<SettingsHost bind:this={settingsHost} {onProblemsLocationChange} {onRemoteConnected} />
<!-- The session's own browser, over the whole window including the sessions
     column. It reads the active session itself and takes no props. -->
<AssistanceHost />
<div
	bind:this={resourcePopoverHost}
	class="rail-popover-host"
	style={resourceAnchor ? utilityAnchorStyle(resourceAnchor) : undefined}
>
	<ResourcePopover />
</div>
<!-- The Resource Manager is a window-anchored sheet rather than a card beside
     the button, so it is mounted plainly rather than in a popover host. -->
{#if resourceManagerState.open}
	<ResourceManagerPanel />
{/if}
<div
	bind:this={usagePopoverHost}
	class="rail-popover-host"
	style={usageAnchor ? utilityAnchorStyle(usageAnchor) : undefined}
>
	<UsagePopover />
</div>
<AlertDialog.Root
	open={exitRequest !== null}
	onOpenChange={(open) => {
		if (open || !exitRequest) return;
		exitRequest = null;
		void cancelAppExit();
	}}
>
	<AlertDialog.Content
		class="rounded-lg bg-background text-foreground ring-[var(--color-border)] shadow-[var(--shadow-lg)]"
		data-testid="quit-warning"
	>
		<AlertDialog.Header>
			<AlertDialog.Title class="text-[14px] leading-[1.4] font-semibold">Quit while work is still running?</AlertDialog.Title>
			<AlertDialog.Description class="text-[13px] leading-[1.5] text-[var(--color-text-2)]">
				These sessions on this Mac have sub-agents or commands running. Quitting stops them.
			</AlertDialog.Description>
		</AlertDialog.Header>
		<ul class="m-0 flex list-none flex-col gap-1 p-0">
			{#each exitRequest?.lines ?? [] as line, index (index)}
				<li class="flex gap-1.5 text-[13px] leading-[1.5] text-[var(--color-text)]">
					<span class="text-[var(--color-text-3)]" aria-hidden="true">•</span>
					<span class="min-w-0 break-words">{line}</span>
				</li>
			{/each}
		</ul>
		<AlertDialog.Footer class="bg-transparent">
			<AlertDialog.Cancel size="sm" class="text-[13px]">Cancel</AlertDialog.Cancel>
			<AlertDialog.Action
				size="sm"
				variant="destructive"
				class="text-[13px]"
				onclick={() => {
					const kind = exitRequest?.kind;
					exitRequest = null;
					if (kind) void exitApp(kind);
				}}>Quit Anyway</AlertDialog.Action
			>
		</AlertDialog.Footer>
	</AlertDialog.Content>
</AlertDialog.Root>
{#if message}
	<!-- Something went wrong, said once, along the bottom edge. Announced to
       screen readers, and see-through to the mouse so it can never swallow a
       click meant for the shell underneath.

       Fixed and above the dock, not absolute: the panes are their own stacking
       contexts, so a strip that only said `absolute` painted behind whichever
       pane sat over it and the message was never read. Being above content it
       also carries its own surface — the old ten-percent wash was legible over
       the shell background and over nothing else. -->
	<footer
		role="alert"
		class="pointer-events-none fixed bottom-2 left-1/2 z-[70] flex -translate-x-1/2 items-center
           gap-2 rounded-lg border border-[color-mix(in_srgb,var(--color-bad)_32%,transparent)]
           bg-[color-mix(in_srgb,var(--color-bad)_14%,var(--color-elevated))] px-3 py-2 shadow-[var(--shadow-md)]
           text-[13px] leading-[1.4] text-destructive"
	>
		<TriangleAlert class="size-3.5 shrink-0" aria-hidden="true" />
		<span>{message}</span>
	</footer>
{/if}

{#if import.meta.env.DEV}
	<!-- <footer class="invoke-counter">invokes: {invokeCounts.total} (+{invokeCounts.input} input)</footer> -->
{/if}

<style>
	/* Resource and quota state stay with their existing components, but the
     mounts live above Dockview so inward-opening content cannot be clipped by
     the right column's Gridview cell. The bottom strip supplies the exact
     button rectangle before each toggle. */
	.rail-popover-host {
		position: fixed;
		z-index: 70;
		width: 32px;
		height: 32px;
		pointer-events: none;
	}

	.rail-popover-host :global(aside) {
		position: absolute;
		inset: 0;
		width: 32px;
		height: 32px;
		pointer-events: none;
	}

	.rail-popover-host :global(.trigger) {
		position: absolute;
		width: 1px;
		height: 1px;
		overflow: hidden;
		padding: 0;
		opacity: 0;
		pointer-events: none;
	}

	.rail-popover-host :global(.usage-popover .card),
	.rail-popover-host :global(.modal-backdrop),
	.rail-popover-host :global(.modal-surface) {
		pointer-events: auto;
	}

	.rail-popover-host :global(.usage-popover .card) {
		top: auto;
		right: calc(100% + 12px);
		bottom: 0;
	}

</style>
