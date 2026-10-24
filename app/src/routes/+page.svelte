<script lang="ts">
	/**
	 * Primary Assembly shell orchestrator.
	 *
	 * Thin shell component: directly composes the real workbench components
	 * (SessionsColumn, TopTabRow, the tab pane's surfaces, the right drawer,
	 * DockPanel) and uses controllers for state management. The top tab row's
	 * active tab (`topTabs`) decides what the pane shows; one effect below
	 * hands that choice to the editor and browser stores.
	 */
	import { Button } from "$lib/components/ui/button/index.js";
	import { onMount, untrack } from "svelte";

	import { PRODUCT_DOCUMENT_TITLE } from "$lib/productIdentity";

	import "$lib/shell/styles/next.css";
	import "$lib/shell/styles/nextTokens.css";
	import "$lib/shell/styles/themeChrome.css";

	import {
		browser,
		captureBrowserState,
		closeBrowserPageTab,
		createBrowserPageTab,
		queueBrowserPageTab,
		selectBrowserPageTab,
	} from "$lib/shell/browser/browserStore.svelte";
	import PendingFirstMessage from "$lib/shell/conversation/components/PendingFirstMessage.svelte";
	import WorkingSpinner from "$lib/shell/components/feedback/WorkingSpinner.svelte";
	import ConversationSurface from "$lib/shell/conversation/ConversationSurface.svelte";
	import DockPanel from "$lib/shell/components/DockPanel.svelte";
	import EditorPanel from "$lib/shell/files/components/EditorPanel.svelte";
	import GitHistoryView from "$lib/shell/git/components/GitHistoryView.svelte";
	import GitDiffView from "$lib/shell/git/components/GitDiffView.svelte";
	import HomeButton from "$lib/shell/components/HomeButton.svelte";
	import { pullRequestSelection } from "$lib/shell/git/github/pullRequestSelection.svelte";
	import PullRequestWorkspace from "$lib/shell/git/github/PullRequestWorkspace.svelte";
	import RightPanel from "$lib/shell/components/RightPanel.svelte";
	import RightPanelTabs from "$lib/shell/components/RightPanelTabs.svelte";
	import { registerSessionRowJumpTarget } from "$lib/shell/components/sessionRowJump.ts";
	import SessionsColumn from "$lib/shell/components/SessionsColumn.svelte";
	import ShellFrame from "$lib/shell/components/ShellFrame.svelte";
	import ShellOverlays from "$lib/shell/components/ShellOverlays.svelte";
	import TopTabRow, { type NewTabKind, type TopTabView } from "$lib/shell/components/TopTabRow.svelte";
	import type { UtilityId } from "$lib/shell/components/utilityStrip";
	import UtilityStrip from "$lib/shell/components/UtilityStrip.svelte";
	import { sendStructuredMessage } from "$lib/shell/conversation/conversationService";
	import { getConversationSession, setConversationAttachments, setConversationSelectedChild } from "$lib/shell/conversation/conversationStore.svelte";
	import { editorState, pinEditorFile } from "$lib/shell/files/editor/editorStore.svelte";
	import { topTabs } from "$lib/shell/layout/topTabs.svelte";
	import { parseTopTabKey } from "$lib/shell/layout/topTabsOps";
	import BrowserPanel from "$lib/shell/browser/components/BrowserPanel.svelte";
	import { popupCoversPage } from "$lib/shell/browser/components/browserPanelBounds";
	import { parseRemoteWorkspacePath } from "$lib/workspacePaths";

	import { SessionSelectionController } from "$lib/shell/controllers/sessionSelectionController.svelte";
	import { startShell, stopShell } from "$lib/shell/controllers/shellStartup";
	import { WorkbenchController } from "$lib/shell/controllers/workbenchController.svelte";
	import { gitPanel } from "$lib/shell/git/gitPanelStore.svelte";
	import { gitService } from "$lib/shell/git/gitService";
	import { registerSessionHistoryHost } from "$lib/shell/history/sessionHistoryHost";
	import DraftSessionSurface from "$lib/shell/conversation/newSession/DraftSessionSurface.svelte";
	import type { ThreadStartRequest } from "$lib/shell/conversation/newSession/threadStartFlow";
	import { ownedSessionMetaForBackend, type OwnedSession } from "$lib/shell/ownedSessions";
	import ProjectsScreen from "$lib/shell/projects/ProjectsScreen.svelte";
	import WorkflowsScreen from "$lib/shell/workflows/WorkflowsScreen.svelte";
	import { diffPathFor } from "$lib/shell/sessionWorkspaces";
	import { ownedSessionStatusPatch, updateOwnedSession } from "$lib/shell/stores/sessionRailStore.svelte";
	import type { CenterTabId } from "$lib/shell/workbenchNavigation";
	import { clearWorkbenchNavigation, openIncomingLink, registerWorkbenchNavigation } from "$lib/shell/workbenchNavigation";
	import {
		countAgentMessagesFromTauri,
		isTauriRuntime,
		listenAgentMessagesChanged,
		setMainOrchestratorFromTauri,
		updateAgentConversationSessionMetaFromTauri
	} from "$lib/tauriSource";
	import { createUnreadTracker } from "$lib/shell/messages/agentMessagesModel.svelte.ts";
	import MessagesScreen from "$lib/shell/messages/MessagesScreen.svelte";

	const selection = new SessionSelectionController();
	// A remote session never saved on this Mac waits here until enough of its history has arrived.
	const historyDownload = $derived(selection.activeOwnedId ? getConversationSession(selection.activeOwnedId)?.historyDownload ?? null : null);
	const megabytes = (bytes: number) => (bytes / 1_000_000).toFixed(1);
	const workbench = new WorkbenchController();
	// Register before the right-panel children are constructed. History reads
	// this route-owned bridge during its own construction, while the scan itself
	// remains an on-mount async operation with an AbortSignal below.
	const historyHost = registerSessionHistoryHost({
		openOwned: selectSession,
		deleteOwned: (ownedId) => selection.removeSession(ownedId),
	});

	let sessionsColumn = $state<SessionsColumn | null>(null);
	let editorPanel = $state<EditorPanel | null>(null);
	let conversationSurface = $state<ConversationSurface | null>(null);
	// The draft closes the moment its first message is accepted; the typing
	// carries on in the new session's composer unless focus went elsewhere.
	$effect(() => {
		if (selection.newSession.draftOpen || !selection.newSession.pendingFirstMessage) return;
		if (document.activeElement === document.body) untrack(() => conversationSurface?.focusComposer());
	});
	let overlays = $state<ShellOverlays | null>(null);
	let openUtility = $state<UtilityId | null>(null);
	/** The Projects and Workflows screens cover the frame; everything under them stays as it was. */
	let projectsOpen = $state(false);
	let workflowsOpen = $state(false);
	/** The Messages screen covers the frame the same way; a run narrows it to that run. */
	let messagesOpen = $state(false);
	let messagesRun = $state<{ id: string; label: string } | null>(null);
	/** New messages for the selected session since its Messages list was last open. */
	const messagesUnread = createUnreadTracker(countAgentMessagesFromTauri);
	$effect(() => {
		const controller = new AbortController();
		void followMessageChanges(controller.signal);
		return () => {
			controller.abort();
			messagesUnread.dispose();
		};
	});
	$effect(() => {
		const ownedId = selection.activeOwnedId;
		untrack(() => messagesUnread.select(ownedId));
	});
	async function followMessageChanges(signal: AbortSignal): Promise<void> {
		try {
			await listenAgentMessagesChanged(() => messagesUnread.changed(), signal);
		} catch {
			// Without the event the count still updates on each session selection.
		}
	}
	function openMessages(run: { id: string; label: string } | null): void {
		projectsOpen = workflowsOpen = false;
		messagesRun = run;
		messagesOpen = true;
	}
	let sessionsRailWidth = $state(326);
	// Zero until the frame reports the pane's laid-out width: the browser may
	// place its native view only once the pane really has a size.
	let toolsRailWidth = $state(0);
	/** The +/- totals the Changes view last showed; the Changes tab title reads these. */
	let diffTotals = $state({ added: 0, removed: 0 });
	let routeDisposal: Promise<void> | null = null;
	/** The right drawer's card width; its left edge drags it between these. */
	const DRAWER_MIN_WIDTH = 240;
	const DRAWER_MAX_WIDTH = 640;
	let drawerWidth = $state(320);
	let drawerDrag: { x: number; width: number } | null = null;
	/** Bumped to ask the Files drawer to focus its filter. */
	let filesFilterFocus = $state(0);

	/**
	 * Any open menu, list, card or dialog. The browser page is a native view
	 * painted above every DOM layer, so it steps aside (hidden, still live)
	 * while one is drawn over it. Tooltips are left out on purpose.
	 */
	const POPUP_SELECTOR =
		'[role="dialog"], [role="alertdialog"], [role="menu"], [data-select-content], dialog[open], [data-testid="usage-live-quota"]';
	let popupOpen = $state(false);

	async function changeSessionStatus(ownedId: string, status: "working" | "done" | "settled"): Promise<void> {
		const session = selection.railOwned.find((row) => row.ownedId === ownedId);
		if (session) await saveSessionChange(ownedId, ownedSessionStatusPatch(session, status, new Date()));
	}

	/** The last metadata write queued for each row; a row's writes go out one at a time. */
	const sessionSaves = new Map<string, Promise<void>>();

	/**
	 * Saves a person's own change to a session row. The row changes at once; the
	 * write waits for that row's earlier writes and sends the row as it is when
	 * the write starts. A failed write puts back only the fields still showing
	 * its own value, so it never undoes a newer change.
	 */
	async function saveSessionChange(
		ownedId: string,
		patch: Partial<Pick<OwnedSession, "completedAt" | "settledAt" | "pinnedAt" | "title">>,
	): Promise<void> {
		const before = selection.railOwned.find((session) => session.ownedId === ownedId);
		if (!before) return;
		const keys = Object.keys(patch) as (keyof typeof patch)[];
		updateOwnedSession(ownedId, patch);
		const writeAfter = async (previous: Promise<void> | undefined): Promise<void> => {
			try {
				await previous;
			} catch {
				// The earlier caller already saw its error.
			}
			const current = selection.railOwned.find((session) => session.ownedId === ownedId);
			if (!current) return;
			try {
				await updateAgentConversationSessionMetaFromTauri({
					ownedId,
					model: null,
					effort: null,
					meta: ownedSessionMetaForBackend(current),
				});
			} catch (error) {
				const latest = selection.railOwned.find((session) => session.ownedId === ownedId);
				const stillOurs = keys.filter((key) => latest && Object.is(latest[key], patch[key]));
				updateOwnedSession(ownedId, Object.fromEntries(stillOurs.map((key) => [key, before[key]])));
				console.error("Could not save the session change", error);
			}
		};
		const write = writeAfter(sessionSaves.get(ownedId));
		sessionSaves.set(ownedId, write);
		try {
			await write;
		} finally {
			if (sessionSaves.get(ownedId) === write) sessionSaves.delete(ownedId);
		}
	}

	/** The owner's main orchestrator: one session across every machine, or none. */
	async function markOrchestrator(ownedId: string, marked: boolean): Promise<void> {
		const session = selection.railOwned.find((candidate) => candidate.ownedId === ownedId);
		if (!session) return;
		try {
			await setMainOrchestratorFromTauri(marked ? ownedId : null, session.remoteProfileId ?? null);
		} catch (error) {
			console.error("Could not mark the main orchestrator", error);
			return;
		}
		const markedAt = Date.now();
		for (const other of selection.railOwned) {
			const value = marked && other.ownedId === ownedId ? markedAt : null;
			if ((other.orchestratorMarkedAtMs ?? null) !== value) updateOwnedSession(other.ownedId, { orchestratorMarkedAtMs: value });
		}
	}

	if (import.meta.hot) {
		import.meta.hot.dispose(() => {
			void disposeRoute();
		});
	}

	$effect(() => {
		selection.setEditorPanel(editorPanel);
	});

	/** The tab pane is in the frame whenever the top row has a tab. */
	const paneShowing = $derived(topTabs.row.length > 0);
	const browserRoot = $derived(selection.newSession.draftOpen ? "" : selection.durableSessionRoot);

	const topTabViews = $derived.by(() => {
		const browserTabs = captureBrowserState().tabs;
		return topTabs.row.flatMap((key): TopTabView[] => {
			const ref = parseTopTabKey(key);
			if (!ref) return [];
			if (ref.kind === "editor") {
				const file = editorState.openFiles.find((entry) => entry.path === ref.id);
				if (!file) return [];
				const detail = [file.relativePath, file.path, file.dirty ? "Unsaved changes" : ""];
				return [
					{
						key,
						kind: "editor",
						label: file.fileName,
						detail: detail.filter(Boolean).join("\n"),
						path: file.path,
						dirty: Boolean(file.dirty),
						preview: Boolean(file.previewTab),
					},
				];
			}
			if (ref.kind === "browser") {
				const tab = browserTabs.find((entry) => entry.id === ref.id);
				const label = tab?.title || "New browser tab";
				return [{ key, kind: "browser", label, detail: [label, tab?.url ?? ""].filter(Boolean).join("\n") }];
			}
			if (ref.kind === "diff")
				return [
					{
						key,
						kind: "diff",
						label: "Changes",
						detail: gitPanel.selectedPath || "Working tree changes",
						additions: diffTotals.added,
						deletions: diffTotals.removed,
					},
				];
			if (ref.kind === "git-history")
				return [{ key, kind: "git-history", label: "History", detail: gitPanel.historyPath || "Whole repository" }];
			const pullRequest = pullRequestSelection.selected;
			return [
				pullRequest
					? {
							key,
							kind: "pull-requests",
							label: `PR #${pullRequest.number}`,
							detail: `PR #${pullRequest.number} — ${pullRequest.title}\n${pullRequest.headBranch} → ${pullRequest.baseBranch}`,
						}
					: { key, kind: "pull-requests", label: "Pull requests", detail: "Pull requests" },
			];
		});
	});

	// The pane comes and goes with its tabs, but not mid-switch: the next
	// session's tabs arrive in `restoreSessionState`, and removing then re-adding
	// the region in between would flash.
	$effect(() => {
		const present = paneShowing;
		if (workbench.switching) return;
		untrack(() => workbench.setPanePresent(present));
	});

	$effect(() => {
		const key = topTabs.activeKey;
		untrack(() => workbench.paneShown(key));
	});

	// The active top tab is the truth for what is showing. Whatever made it
	// change (a click, a close, a restore), the owning store follows.
	$effect(() => {
		if (workbench.switching) return;
		const ref = topTabs.activeKey ? parseTopTabKey(topTabs.activeKey) : null;
		const panel = editorPanel;
		if (!ref) return;
		untrack(() => {
			if (ref.kind === "editor" && editorState.activePath !== ref.id) panel?.selectFile(ref.id);
			else if (ref.kind === "browser" && captureBrowserState().activeTabId !== ref.id) selectBrowserPageTab(ref.id);
		});
	});

	// Only the native browser needs to step aside for DOM popups, so the page is
	// watched only while a browser tab is on screen: a whole-document query on
	// every DOM change would otherwise run for the life of the app.
	$effect(() => {
		if (!paneShowing || topTabs.activeKind !== "browser" || toolsRailWidth <= 0) {
			popupOpen = false;
			return;
		}
		// Popups mount and unmount as DOM nodes, wherever they are drawn, and a
		// menu is moved into place by a style change on its wrapper after it
		// mounts, so only open popups are watched for style. Only one drawn
		// over the page hides it; one beside the page leaves it showing.
		const report = () => {
			const open = [...document.querySelectorAll(POPUP_SELECTOR)];
			for (const popup of open) {
				const moved = popup.closest("[data-bits-floating-content-wrapper]") ?? popup;
				popups.observe(moved, { attributes: true, attributeFilter: ["style"] });
			}
			const page = open.length ? document.querySelector('[data-testid="browser-page-host"]')?.getBoundingClientRect() : null;
			popupOpen = popupCoversPage(open.map((popup) => popup.getBoundingClientRect()), page ?? null);
		};
		const popups = new MutationObserver(report);
		popups.observe(document.body, { childList: true, subtree: true });
		report();
		return () => popups.disconnect();
	});

	// Follow the active session's project assignment, its registered folder, and its
	// machine connecting after it was opened. A background move must not select that session.
	$effect(() => {
		void selection.durableSessionRoot;
		const reconnected = selection.reconnectedProfileId;
		untrack(() => {
			if (selection.activeOwnedId && selection.treeRootMoved) void openSession(selection.activeOwnedId);
			else if (reconnected) void refreshRemoteConnection(reconnected);
		});
	});

	onMount(() => {
		const historyStop = new AbortController();
		const linkStop = new AbortController();
		const releaseSessionRowJump = registerSessionRowJumpTarget({
			selectSession,
			showCenterPanel: (_ownedId, id) => selectCenterTab(id),
			showSidebarView: (_ownedId, id) => selectRightTab(id),
		});
		registerWorkbenchNavigation({
			selectSession,
			showCenterTab: selectCenterTab,
			showRightTab: selectRightTab,
			openDiff: async (request) => {
				if (!selection.activeRootAvailable) return;
				await gitService.showStoredDiff(request.projectRoot, request.relativePath);
			},
			openPullRequest: (link) => {
				pullRequestSelection.link = link;
			},
			openFileTimeline: async (request) => {
				const ownedId = selection.activeOwnedId;
				const sessionRoot = selection.durableSessionRoot;
				if (!selection.activeRootAvailable || !ownedId || !sessionRoot) return false;
				selectCenterTab("git-history");
				await gitService.showFileHistory(request.projectRoot, request.relativePath);
				if (
					!selection.activeRootAvailable ||
					selection.activeOwnedId !== ownedId ||
					selection.durableSessionRoot !== sessionRoot
				)
					return false;
				return true;
			},
			sendToSession: async (request) => {
				const conversation = getConversationSession(request.ownedId);
				if (!conversation) throw new Error("The selected conversation is not ready");
				if (request.attachments?.length) {
					setConversationAttachments(request.ownedId, [...conversation.attachments, ...request.attachments]);
				}
				await sendStructuredMessage(request.ownedId, request.text);
			},
		});
		void startShellThenListenForLinks(linkStop.signal);
		void historyHost.rescan(historyStop.signal);

		return () => {
			historyStop.abort();
			linkStop.abort();
			historyHost.release();
			releaseSessionRowJump();
			clearWorkbenchNavigation();
			void disposeRoute();
		};
	});

	/** Start the shell, then take links from other apps: the one that launched
	 * the app, and every one after while the page lives. Links wait for the
	 * first session so a launch link is not replaced by the restored session. */
	async function startShellThenListenForLinks(signal: AbortSignal): Promise<void> {
		await startShell({
			onSelectInitial: async (ownedId, stopSignal) => {
				if (stopSignal.aborted) return;
				await selectSession(ownedId);
				if (stopSignal.aborted) return;
			},
		});
		if (signal.aborted || !isTauriRuntime()) return;
		try {
			const { getCurrent, onOpenUrl } = await import("@tauri-apps/plugin-deep-link");
			if (signal.aborted) return;
			const unlisten = await onOpenUrl((urls) => {
				for (const url of urls) void openIncomingLink(url);
			});
			if (signal.aborted) {
				unlisten();
				return;
			}
			signal.addEventListener("abort", unlisten, { once: true });
			const launchUrls = await getCurrent();
			if (signal.aborted) return;
			for (const url of launchUrls ?? []) void openIncomingLink(url);
		} catch (error) {
			console.warn("Links from other apps are unavailable", error);
		}
	}

	async function disposeRoute(): Promise<void> {
		routeDisposal ??= disposeRouteOnce();
		await routeDisposal;
	}

	async function disposeRouteOnce(): Promise<void> {
		selection.rememberWorkspaceState(workbench.captureSessionState());
		const selectionDisposal = selection.dispose();
		stopShell();
		await selectionDisposal;
	}

	function handleAskRemoveSession(ownedId: string): void {
		void selection.removeSession(ownedId);
	}

	async function selectSession(ownedId: string): Promise<void> {
		if (selection.activeOwnedId === ownedId && selection.activeWorkspaceSnapshot !== null) return;
		await openSession(ownedId);
	}

	async function openSession(ownedId: string): Promise<void> {
		if (selection.activeOwnedId !== null) {
			selection.rememberWorkspaceState(workbench.captureSessionState());
		}
		workbench.beginSessionSwitch();
		await selection.selectSession(ownedId);
		if (selection.activeOwnedId === ownedId) {
			await restoreSelectedWorkbench();
		}
	}

	async function connectSelectedRemote(): Promise<void> {
		const profileId = await selection.connectSelectedRemote();
		if (profileId) await refreshRemoteConnection(profileId);
	}

	/** A row's Connect: the same connect as the conversation pane's, for that row's session. */
	async function connectSessionRow(ownedId: string): Promise<void> {
		await selectSession(ownedId);
		// Another row may have been chosen while this one was opening.
		if (selection.activeOwnedId !== ownedId) return;
		await connectSelectedRemote();
	}

	async function refreshRemoteConnection(profileId: string): Promise<void> {
		if (await selection.refreshRemoteConnection(profileId)) await restoreSelectedWorkbench();
	}

	async function restoreSelectedWorkbench(): Promise<void> {
		workbench.restoreSessionState(selection.activeWorkspaceSnapshot);
		const storedDiffPath = diffPathFor(selection.activeWorkspaceSnapshot, selection.durableSessionRoot);
		if (storedDiffPath && selection.activeWorkspaceSnapshot?.topTabs?.order.includes("diff")) {
			await gitService.showStoredDiff(selection.durableSessionRoot, storedDiffPath);
		}
	}

	function persistTabs(): void {
		const ownedId = selection.activeOwnedId;
		if (ownedId) selection.persistWorkspaceState(ownedId, workbench.captureSessionState());
	}

	function selectTopTab(key: string): void {
		topTabs.select(key);
		persistTabs();
	}

	function closeTopTab(key: string): void {
		const ref = parseTopTabKey(key);
		if (!ref) return;
		if (ref.kind === "editor") {
			if (editorState.openFiles.find((file) => file.path === ref.id)?.dirty) {
				// The unsaved-changes dialog must not open over a live browser page,
				// so the file comes to the front first. The row drops its tab once
				// the dialog really closes the file.
				topTabs.select(key);
				editorPanel?.selectFile(ref.id);
				editorPanel?.requestCloseFile(ref.id);
				persistTabs();
				return;
			}
			topTabs.forget(key);
			editorPanel?.requestCloseFile(ref.id);
		} else {
			topTabs.forget(key);
			if (ref.kind === "browser") closeBrowserPageTab(ref.id);
			else if (ref.kind === "diff") gitService.clearSelection();
			else if (ref.kind === "pull-requests") pullRequestSelection.selected = null;
		}
		persistTabs();
	}

	function openNewBrowserTab(): void {
		if (selection.activeOwnedId) browser.workspace.ownedId = selection.activeOwnedId;
		const id = browser.workspace.activated ? createBrowserPageTab() : queueBrowserPageTab();
		if (!id) return;
		topTabs.open(`browser:${id}`);
		persistTabs();
	}

	function selectCenterTab(id: CenterTabId): void {
		if (id === "session") {
			workbench.showChat();
			return;
		}
		if (id === "editor") {
			if (editorState.activePath) topTabs.open(`editor:${editorState.activePath}`);
		} else if (id === "browser") {
			const active = captureBrowserState().activeTabId;
			if (!active) return openNewBrowserTab();
			topTabs.open(`browser:${active}`);
		} else {
			topTabs.open(id);
		}
		persistTabs();
	}

	/** The `+` menu: one of the top-row kinds, or a file picked from the Files drawer. */
	async function openFromMenu(kind: NewTabKind): Promise<void> {
		if (kind === "file") {
			selectRightTab("files");
			filesFilterFocus += 1;
			return;
		}
		if (kind === "git-history") {
			// Clearing the path may re-read History; a session switch meanwhile
			// must not open the tab in the session that arrived.
			const ownedId = selection.activeOwnedId;
			const root = selection.durableSessionRoot;
			await gitService.clearHistoryPath();
			if (selection.activeOwnedId !== ownedId || selection.durableSessionRoot !== root) return;
		} else if (kind === "pull-requests") pullRequestSelection.selected = null;
		if (kind === "browser") openNewBrowserTab(); else selectCenterTab(kind);
		workbench.yieldDrawerTo(topTabs.activeKey);
	}

	function selectRightTab(id: Parameters<WorkbenchController["selectRightTab"]>[0]): void {
		workbench.selectRightTab(id);
		const ownedId = selection.activeOwnedId;
		if (ownedId) selection.persistWorkspaceState(ownedId, workbench.captureSessionState());
	}

	function openNewSession(projectId: string | null = null, folder: string | null = null): void {
		projectsOpen = messagesOpen = workflowsOpen = false;
		selection.newSession.open(projectId, folder);
		selectCenterTab("session");
	}

	async function startNewSession(request: ThreadStartRequest, images: File[]): Promise<void> {
		await selection.newSession.start(
			request,
			images,
			async (ownedId) => {
				await selectSession(ownedId);
			},
			() => {
				selectCenterTab("session");
				workbench.rightTab = "files";
			},
			(ownedId, ids) => selection.persistConversationAttachmentIds(ownedId, ids),
			(ownedId) => selection.removeSession(ownedId),
		);
	}
</script>

<svelte:head>
	<title>{PRODUCT_DOCUMENT_TITLE}</title>
</svelte:head>

{#snippet sessionsArea()}
	<div class="sessions-region">
		<div class="sessions-list">
			<SessionsColumn
				bind:this={sessionsColumn}
				owned={selection.railOwned}
				activeOwnedId={selection.activeOwnedId}
				collapsed={workbench.sessionsCollapsed}
				onCollapse={(collapsed) => workbench.collapseSessions(collapsed)}
				onNewSession={() => openNewSession()}
				onSelectSession={(ownedId) => {
					void selectSession(ownedId);
				}}
				onAskRemove={handleAskRemoveSession}
				onComplete={(ownedId) => void changeSessionStatus(ownedId, "done")}
				onReopen={(ownedId) => void changeSessionStatus(ownedId, "working")}
				onSettle={(ownedId) => void changeSessionStatus(ownedId, "settled")}
				onUnsettle={(ownedId) => void changeSessionStatus(ownedId, "done")}
				onPin={(ownedId, pinned) =>
					void saveSessionChange(ownedId, { pinnedAt: pinned ? new Date().toISOString() : null })}
				onMarkOrchestrator={(ownedId, marked) => void markOrchestrator(ownedId, marked)}
				onRename={(ownedId, title) => void saveSessionChange(ownedId, { title })}
				onConnect={(ownedId) => void connectSessionRow(ownedId)}
			/>
		</div>
	</div>
{/snippet}

{#snippet drawerArea()}
	<RightPanel
		visible={workbench.rightPanelOpen && !messagesOpen && !projectsOpen && !workflowsOpen}
		activeId={workbench.rightTab}
		root={selection.newSession.draftOpen ? "" : selection.durableSessionRoot}
		rootAvailable={selection.activeRootAvailable} sourceControlRoot={selection.newSession.draftOpen ? "" : selection.sessionCheckoutRoot}
		ownedId={selection.activeOwnedId}
		messagesUnread={messagesUnread.unread}
		onMessagesSeen={() => messagesUnread.seen()}
		onShowRunMessages={(id, label) => openMessages({ id, label })}
		onOpenWorkflows={() => { projectsOpen = messagesOpen = false; workflowsOpen = true; }}
		filesRoot={selection.filesProjectionRoot}
		filesOwnedId={selection.filesProjectionOwnedId}
		filesFilterFocusRequest={filesFilterFocus}
		expandedPathsByRoot={selection.expandedPathsByRoot}
		onExpandedPathsChange={(ownedId, root, paths) => selection.rememberExpandedPaths(ownedId, root, paths)}
		filesInspectionRoot={selection.activeWorkspaceSnapshot?.filesInspectionRoot ?? null}
		sourceControlInspectionRoot={selection.activeWorkspaceSnapshot?.sourceControlInspectionRoot ?? null}
		onFilesInspectionRootChange={(root) => selection.rememberWorkspaceState({ filesInspectionRoot: root })}
		onSourceControlInspectionRootChange={(root) => selection.rememberWorkspaceState({ sourceControlInspectionRoot: root })}
		sourceControlWorkspace={selection.activeWorkspaceSnapshot?.sourceControl}
		onSourceControlWorkspaceChange={(ownedId, sourceControl) => { if (selection.activeOwnedId === ownedId) selection.rememberWorkspaceState({ sourceControl }); }}
		viewedRoot={selection.activeWorkspaceSnapshot?.viewedProjectRoot ?? null}
		onViewedRootChange={(root) => { if (selection.activeOwnedId) selection.persistWorkspaceState(selection.activeOwnedId, { viewedProjectRoot: root }); }}
		onNewSessionHere={openNewSession}
		checkoutDiscoveryRoots={selection.filesProjectionRoot ? [selection.filesProjectionRoot] : []}
		onUseSessionCheckout={selection.controlledSession?.canChangeCheckout && selection.controlledSession.origin === "app"
			? async (root) => {
					await selection.useSessionCheckout(root);
				}
			: undefined}
	/>
{/snippet}

{#snippet dockArea()}
	<DockPanel
		root={selection.activeRootAvailable ? selection.durableSessionRoot : ""}
		onReset={() => workbench.resetLayout()}
		onProblemsLocationChange={(location) => workbench.applyProblemsLocation(location)}
	/>
{/snippet}

{#snippet sessionArea()}
	<div class="session-area">
		{#if selection.hasChatProjection && selection.chatOwnedId}
			<div
				class="conversation-surface-shell"
				hidden={selection.chatOwnedId !== selection.activeOwnedId}
				aria-hidden={selection.chatOwnedId !== selection.activeOwnedId ? "true" : undefined}
				inert={selection.chatOwnedId !== selection.activeOwnedId}
			>
				<ConversationSurface
					bind:this={conversationSurface}
					owned={selection.railOwned}
					activeOwnedId={selection.chatOwnedId}
					pendingFirstMessage={selection.newSession.pendingFirstMessage?.ownedId === selection.chatOwnedId
						? selection.newSession.pendingFirstMessage.text
						: null}
					rootAvailable={selection.activeRootAvailable}
					onPersistAttachmentIds={(ownedId, ids) => selection.persistConversationAttachmentIds(ownedId, ids)}
					onOpenChild={(ownedId, childId) => {
						setConversationSelectedChild(ownedId, childId);
						selectRightTab("agents");
					}}
				/>
			</div>
		{/if}
		{#if selection.activeOwnedId && (!selection.hasChatProjection || selection.chatOwnedId !== selection.activeOwnedId)}
			<div
				class="conversation-data-isolation"
				class:pending-first-send={selection.newSession.pendingFirstMessage?.ownedId === selection.activeOwnedId}
				aria-label={selection.selectionError ? "Conversation unavailable" : "Loading conversation"}
			>
				{#if selection.newSession.pendingFirstMessage?.ownedId === selection.activeOwnedId}
					<PendingFirstMessage text={selection.newSession.pendingFirstMessage.text} seed={selection.newSession.pendingFirstMessage.ownedId} />
				{:else if selection.selectionError}
					<p>{selection.selectionError}</p>
					{#if selection.disconnectedRemoteProfileId || selection.connectingRemote}
						<Button disabled={selection.connectingRemote} onclick={() => void connectSelectedRemote()}
							>{#if selection.connectingRemote}<WorkingSpinner size={14} />Connecting…{:else}Connect{/if}</Button
						>
					{/if}
				{:else if historyDownload && !historyDownload.ready && (historyDownload.phase === 'downloading' || historyDownload.phase === 'paused')}
					<div class="history-waiting" role="status">
						<p>{historyDownload.phase === 'paused' ? 'History download paused · reconnecting'
							: `Downloading history${historyDownload.totalBytes ? ` · ${megabytes(historyDownload.coveredBytes)} of ${megabytes(historyDownload.totalBytes)} MB · ${Math.floor(historyDownload.coveredBytes * 100 / historyDownload.totalBytes)}%` : ''}`}</p>
						<progress value={historyDownload.coveredBytes} max={historyDownload.totalBytes || 1}></progress>
					</div>
				{:else}<p><WorkingSpinner size={14} /> Loading conversation…</p>{/if}
			</div>
		{:else if !selection.activeOwnedId}
			<div class="conversation-data-isolation" aria-label="No session selected">
				<p>Select a session to view conversation.</p>
			</div>
		{/if}
		{#if selection.newSession.draftOpen}
			{#key selection.newSession.stopSignal}
			<DraftSessionSurface
				stopSignal={selection.newSession.stopSignal}
				projectId={selection.newSession.projectId} folder={selection.newSession.folder}
				startingOwnedId={selection.newSession.pendingFirstMessage?.ownedId ?? null}
				onSend={startNewSession}
				onClose={() => selection.newSession.close()}
			/>
			{/key}
		{/if}
	</div>
{/snippet}

{#snippet toolsArea()}
	<!-- The tab pane: one surface per kind, only the active one displayed.
	     Editor and browser stay mounted (their lifecycles are per session);
	     History and Pull requests mount while their tab exists; the diff only
	     while it is in front. -->
	<div class="pane-stack">
		<div class="pane-body" class:showing={paneShowing && topTabs.activeKind === "editor"}>
			<EditorPanel
				bind:this={editorPanel}
				ownedId={selection.activeOwnedId} sessionRoot={selection.durableSessionRoot}
				showing={paneShowing && topTabs.activeKind === "editor"}
				rootAvailable={selection.controlledEditorRootAvailable}
				onCloseAllEditors={() => selection.editorSessions.clearActiveEditors()}
				onFileOpened={() => selectCenterTab("editor")}
			/>
		</div>
		<div class="pane-body" class:showing={paneShowing && topTabs.activeKind === "browser"}>
			<!-- The page is a native view painted above every DOM layer, so it
			     steps aside (hidden, still live) while Settings or a popup is up. -->
			<BrowserPanel
				visible={paneShowing &&
					topTabs.activeKind === "browser" &&
					toolsRailWidth > 0 &&
					!overlays?.settingsOpen() &&
					!popupOpen &&
					!projectsOpen && !workflowsOpen &&
					!messagesOpen}
				panelOpen={paneShowing}
				root={browserRoot}
				ownedId={selection.activeOwnedId}
				onWorkspaceChange={(ownedId, browser) => {
					selection.persistWorkspaceState(ownedId, { browser });
				}}
			/>
		</div>
		{#if topTabs.row.includes("git-history")}
			<div class="pane-body" class:showing={topTabs.activeKind === "git-history"}>
				<GitHistoryView
					root={selection.activeRootAvailable ? selection.durableSessionRoot : ""}
					rootAvailable={selection.activeRootAvailable}
					historyPath={gitPanel.historyPath}
					showing={topTabs.activeKind === "git-history"}
				/>
			</div>
		{/if}
		{#if topTabs.row.includes("pull-requests")}
			<div class="pane-body" class:showing={topTabs.activeKind === "pull-requests"}>
				<PullRequestWorkspace showing={topTabs.activeKind === "pull-requests"} />
			</div>
		{/if}
		{#if topTabs.activeKind === "diff"}
			<div class="pane-body showing">
				<GitDiffView
					rootAvailable={selection.activeRootAvailable}
					sessionRoot={selection.durableSessionRoot}
					mode={workbench.diffMode}
					onModeChange={(mode) => workbench.setDiffMode(mode)}
					onTotals={(totals) => (diffTotals = totals)}
				/>
			</div>
		{/if}
	</div>
{/snippet}

<main
	class="next-shell"
	style:--sessions-rail-width={`${sessionsRailWidth}px`}
	style:--tools-rail-width={`${toolsRailWidth}px`}
	style:--drawer-width={workbench.rightPanelOpen ? `${drawerWidth + 8}px` : "0px"}
	oncontextmenu={(event) => {
		const target = event.target instanceof Element ? event.target : null;
		if (target?.closest('input, textarea, [contenteditable="true"]')) return;
		event.preventDefault();
	}}
>
	<div class="window-chrome" data-tauri-drag-region>
		<div class="window-sessions-cap" data-tauri-drag-region>
			<HomeButton
				onOpenProjects={() => { messagesOpen = workflowsOpen = false; projectsOpen = true; }}
				onOpenMessages={() => openMessages(null)}
				onOpenSettings={() => overlays?.openSettings()}
				onOpenUpdate={() => overlays?.openSettings("updates")}
			/>
		</div>
		<TopTabRow
			tabs={topTabViews}
			activeKey={topTabs.activeKey}
			expanded={workbench.expanded}
			paneOpen={paneShowing && toolsRailWidth > 0}
			chatTitle={selection.railOwned.find((s) => s.ownedId === selection.activeOwnedId)?.title || "Chat"}
			drawerOpen={workbench.rightPanelOpen}
			canOpenBrowser={Boolean(browserRoot)}
			onSelect={selectTopTab}
			onClose={closeTopTab}
			onSelectChat={() => selectCenterTab("session")}
			onOpen={(kind) => void openFromMenu(kind)}
			onToggleExpanded={() => workbench.setExpanded(!workbench.expanded)}
			onToggleDrawer={() => workbench.toggleRightPanel()}
			editorActions={{
				pin: pinEditorFile,
				closeOthers: (path) => editorPanel?.closeOtherFiles(path),
				closeSaved: () => editorPanel?.closeSavedFiles(),
				timeline: (path) => editorPanel?.openTimeline(path),
			}}
		/>
	</div>
	<!-- The drawer pushes rather than floats: the frame narrows beside it, so
	     nothing is ever drawn over the native browser page. -->
	<div class="frame-area">
		<div class="frame-main">
			<ShellFrame
				sessions={sessionsArea}
				center={sessionArea}
				tools={toolsArea}
				dock={dockArea}
				onSessionsWidthChange={(width) => (sessionsRailWidth = width)}
				onToolsWidthChange={(width) => (toolsRailWidth = width)}
				onReady={(controls) => workbench.onFrameReady(controls)}
			/>
		</div>
		<!-- Closing hides the drawer rather than unmounting it (measured: unmounting
		     saved no memory and made reopening slower). Hidden, it takes no width. -->
		<aside
			class="right-drawer"
			class:open={workbench.rightPanelOpen}
			style:flex-basis={`${drawerWidth}px`}
			aria-label="Tools"
			aria-hidden={!workbench.rightPanelOpen}
		>
			<div
				class="right-drawer-resize"
				role="separator"
				aria-orientation="vertical"
				aria-label="Resize the drawer"
				onpointerdown={(event) => {
					event.currentTarget.setPointerCapture(event.pointerId);
					drawerDrag = { x: event.clientX, width: drawerWidth };
				}}
				onpointermove={(event) => {
					if (drawerDrag)
						drawerWidth = Math.min(
							DRAWER_MAX_WIDTH,
							Math.max(DRAWER_MIN_WIDTH, drawerDrag.width + drawerDrag.x - event.clientX),
						);
				}}
				onpointerup={() => (drawerDrag = null)}
				onpointercancel={() => (drawerDrag = null)}
			></div>
			<RightPanelTabs
				activeId={workbench.rightTab}
				extraId={workbench.rightExtraTab}
				onSelect={selectRightTab}
				onClose={() => workbench.setRightPanelOpen(false)}
				agentsUnread={messagesUnread.unread}
			/>
			<div class="right-drawer-body">
				{@render drawerArea()}
			</div>
		</aside>
		{#if projectsOpen}
			<ProjectsScreen onBack={() => (projectsOpen = false)} onOpenProject={openNewSession} removeSession={(ownedId) => selection.removeSession(ownedId)} />
		{/if}
		{#if workflowsOpen}<WorkflowsScreen root={selection.durableSessionRoot} onBack={() => (workflowsOpen = false)} />{/if}
		{#if messagesOpen}
			<MessagesScreen
				runId={messagesRun?.id ?? null}
				runLabel={messagesRun?.label ?? ""}
				onBack={() => (messagesOpen = false)}
				onClearRun={() => (messagesRun = null)}
				onShowRun={(id, label) => (messagesRun = { id, label })}
			/>
		{/if}
	</div>

	<UtilityStrip
		{openUtility}
		onOpenUtility={(id, anchor) => overlays?.openUtility(id, anchor)}
		onOpenSettings={() => overlays?.openSettings()}
		location={browserRoot ? (parseRemoteWorkspacePath(browserRoot) ? "Remote" : "Local Mac") : null}
	/>

	<ShellOverlays
		bind:this={overlays}
		onRemoteConnected={(profileId) => void refreshRemoteConnection(profileId)}
		onResetLayout={() => workbench.resetLayout()}
		onBeforeExit={disposeRoute}
		message={null}
		onProblemsLocationChange={(location) => workbench.applyProblemsLocation(location)}
		onShowBottomDock={() => workbench.showBottomDock()}
		onUtilityStateChange={(id, open) => {
			openUtility = open ? id : null;
		}}
	/>
</main>

<style>
	.next-shell {
		display: flex;
		flex-direction: column;
		height: 100vh;
		width: 100vw;
		overflow: hidden;
		background: var(--color-bg);
		color: var(--color-text);
	}

	.frame-area {
		display: flex;
		flex: 1 1 auto;
		min-height: 0;
		position: relative;
	}

	.frame-main {
		flex: 1 1 auto;
		min-width: 0;
		height: 100%;
	}

	/* The drawer is a card in the same gutter as the frame's own cards; its
	   width (320px until its left edge is dragged) plus the 8px gutter is the
	   --drawer-width the chrome row adds. The
	   column takes its full width at once (an animated width would lay the
	   whole frame out again every frame); only the card slides in, and the
	   animation ends. Closed, it is display:none and releases its width. */
	.right-drawer {
		display: none;
		flex: 0 0 320px;
		flex-direction: column;
		min-height: 0;
		position: relative;
		margin: 8px 8px 8px 0;
		border-radius: var(--radius-md);
		overflow: hidden;
		background: var(--panel-fade), var(--color-surface);
		background-repeat: no-repeat;
	}

	.right-drawer.open {
		display: flex;
		animation: right-drawer-in 160ms ease-out;
	}

	@keyframes right-drawer-in {
		from {
			opacity: 0;
			transform: translateX(16px);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.right-drawer.open {
			animation: none;
		}
	}

	.right-drawer-resize {
		position: absolute;
		inset: 0 auto 0 0;
		z-index: 1;
		width: 6px;
		cursor: col-resize;
		touch-action: none;
	}

	.right-drawer-body {
		flex: 1 1 auto;
		min-height: 0;
	}

	.pane-stack {
		position: relative;
		height: 100%;
		width: 100%;
		min-width: 0;
		min-height: 0;
		overflow: hidden;
	}

	.pane-body {
		position: absolute;
		inset: 0;
		display: none;
		overflow: hidden;
	}

	.pane-body.showing {
		display: block;
	}

	.window-chrome {
		display: grid;
		/* Never narrower than the home button with its circles out, so a folded
		   sessions column cannot put the first tab over them. */
		grid-template-columns: max(var(--sessions-rail-width), 172px) minmax(0, 1fr);
		flex: 0 0 var(--center-head-row-height);
		align-items: center;
		min-height: 0;
		background: var(--color-bg);
	}

	.window-sessions-cap {
		height: 100%;
	}

	.sessions-region {
		display: flex;
		flex-direction: column;
		height: 100%;
		min-width: 0;
		overflow: hidden;
	}

	.sessions-list {
		flex: 1;
		min-height: 0;
		overflow: hidden;
	}

	.session-area {
		display: flex;
		flex-direction: column;
		height: 100%;
		width: 100%;
		min-height: 0;
		overflow: hidden;
		position: relative;
	}

	.conversation-surface-shell {
		display: flex;
		flex: 1;
		height: 100%;
		min-height: 0;
	}

	.conversation-surface-shell[hidden] {
		display: none;
	}

	.conversation-data-isolation {
		display: flex;
		flex: 1;
		align-items: center;
		justify-content: center;
		height: 100%;
		color: var(--color-text-2);
		font-size: 13px;
		gap: 10px;
		inset: 0;
		position: absolute;
		z-index: 1;
		/* Covers the card it sits in, so it repaints the card's own face. */
		background: var(--panel-fade), var(--color-surface);
		background-repeat: no-repeat;
	}

	.history-waiting p {
		margin: 0 0 8px;
		text-align: center;
	}

	.history-waiting progress {
		display: block;
		width: 320px;
		height: 6px;
		accent-color: var(--color-accent);
	}

	.conversation-data-isolation.pending-first-send {
		align-items: flex-start;
		justify-content: flex-end;
	}
</style>
