import { sessionCheckoutRoot, sessionWorkspaceRoot, parseRemoteWorkspacePath } from '../../workspacePaths.ts';
import { projectRegistry } from '../projects/projectRegistry.svelte.ts';
/**
 * Controller for session selection, filesystem projections, and workspace expansion.
 */
import {
	ACTIVE_OWNED_SESSION_SETTING_KEY,
	rail,
	hydrateOwned,
	setActiveOwned
} from '../stores/sessionRailStore.svelte';
import { SessionSelectionLayers, type SessionSelectionOwner } from '../sessionSelectionLayers.svelte';
import { canonicalPath } from '../files/explorer/explorerStore.svelte';
import { countInvoke } from '../devInvokeCounter.svelte';
import {
	readAgentConversationWorkspaceExpandedPathsFromTauri,
	writeAgentConversationWorkspaceExpandedPathsFromTauri,
	writeAssemblySettingFromTauri,
	deleteAgentConversationSessionFromTauri,
	validateProjectRootFromTauri,
	readRemoteAssemblyEnvironmentFromTauri,
	connectRemoteAssemblyFromTauri,
} from '../../tauriSource';
import { ownedSessionFromBackend, type OwnedSession } from '../ownedSessions';
import { removeOwnedSession } from '../stores/sessionRailStore.svelte';
import { shellPanels } from '../shellPanels';
import { refresh as refreshExplorer } from '../files/explorer/explorerService.ts';
import { NewSessionController } from './newSessionController.svelte';
import { EditorSessionController, type EditorPanelLifecycle } from './editorSessionController.svelte';
import {
	changeStructuredConversationCheckout,
	flushConversationSessionDraft,
} from '../conversation/conversationService';
import { captureConversationWorkspace, getConversationSession, restoreConversationAttachmentIds } from '../conversation/conversationStore.svelte';
import type { SessionWorkspaceSnapshot } from '../sessionWorkspaces';

export class SessionSelectionController {
	controlledSelectionOwnedId = $state<string | null>(null);
	expandedPathsByRoot = $state.raw<Readonly<Record<string, readonly string[]>>>({});
	sessionSelectionLayers = new SessionSelectionLayers();
	newSession = new NewSessionController();
	editorSessions = new EditorSessionController();
	activeRootAvailable = $state(true);
	activeRootRemote = $state(false);
	activeWorkspaceSnapshot = $state.raw<SessionWorkspaceSnapshot | null>(null);
	selectionError = $state<string | null>(null);
	connectingRemote = $state(false);
	private remoteConnectAbort: AbortController | null = null;
	/** The machine the selected session was opened without, because it was not connected. */
	private openedWithoutMachine = $state<string | null>(null);

	private expansionOwnedId = $state<string | null>(null);
	private workspaceWriteQueue: Promise<void> | null = null;
	private selectionGeneration = 0;
	private selectionAbort: AbortController | null = null;
	private pendingSelectionOwnedId: string | null = null;
	private selectionWork: Promise<void> | null = null;

	get activeOwnedId(): string | null {
		return this.controlledSelectionOwnedId ?? rail.activeOwnedId;
	}

	get railOwned(): OwnedSession[] {
		return rail.owned;
	}

	get hasTreeProjection(): boolean {
		return this.sessionSelectionLayers.hasTreeProjection;
	}

	get hasChatProjection(): boolean {
		return this.sessionSelectionLayers.hasChatProjection;
	}

	get filesProjectionRoot(): string {
		return this.sessionSelectionLayers.hasTreeProjection && this.expansionOwnedId === this.controlledSelectionOwnedId
			? this.sessionSelectionLayers.treeRoot
			: "";
	}

	get filesProjectionOwnedId(): string | null {
		return this.sessionSelectionLayers.hasTreeProjection && this.expansionOwnedId === this.controlledSelectionOwnedId
			? this.sessionSelectionLayers.treeOwnedId
			: null;
	}

	get controlledSession(): OwnedSession | null {
		if (!this.controlledSelectionOwnedId) return null;
		return rail.owned.find((s) => s.ownedId === this.controlledSelectionOwnedId) ?? null;
	}

	get durableSessionRoot(): string {
		const session = this.controlledSession ?? rail.owned.find((s) => s.ownedId === rail.activeOwnedId);
		if (!session) return "";
		return canonicalPath(sessionWorkspaceRoot(session, projectRegistry.projects));
	}

	/** Where the session's agent works now: a checkout change moves it, while a project keeps `durableSessionRoot` put. */
	get sessionCheckoutRoot(): string {
		const session = this.controlledSession ?? rail.owned.find((s) => s.ownedId === rail.activeOwnedId);
		return session ? canonicalPath(sessionCheckoutRoot(session)) : "";
	}

	/** The tree was filled for another root than the session's, as when its project's folder changed. */
	get treeRootMoved(): boolean {
		const layers = this.sessionSelectionLayers;
		const root = this.durableSessionRoot;
		return layers.hasTreeProjection && layers.treeOwnedId === this.activeOwnedId
			&& Boolean(root) && canonicalPath(layers.treeRequestedRoot) !== root;
	}

	get controlledEditorRootAvailable(): boolean {
		return (
			this.controlledSelectionOwnedId !== null &&
			Boolean(this.sessionSelectionLayers.treeRoot)
		);
	}

	get filesInspectionRoot(): string | null {
		return null;
	}

	get chatOwnedId(): string | null {
		return this.sessionSelectionLayers.chatOwnedId;
	}

	setEditorPanel(panel: EditorPanelLifecycle | null): void {
		this.editorSessions.setPanel(panel);
	}

	async selectSession(ownedId: string): Promise<void> {
		this.remoteConnectAbort?.abort();
		this.selectionError = null;
		this.openedWithoutMachine = null;
		this.activeWorkspaceSnapshot = null;
		this.expansionOwnedId = null;
		this.expandedPathsByRoot = {};
		this.controlledSelectionOwnedId = ownedId;
		setActiveOwned(ownedId);
		void this.saveActiveSession(ownedId);
		this.pendingSelectionOwnedId = ownedId;
		const requestedSession = rail.owned.find((candidate) => candidate.ownedId === ownedId);
		this.activeRootRemote = requestedSession?.executionEnvironment === "remote";
		this.sessionSelectionLayers.clearTreeView();
		if (this.selectionWork) {
			this.selectionAbort?.abort();
			await this.selectionWork;
			return;
		}

		const work = this.drainSelections();
		this.selectionWork = work;
		try {
			await work;
		} finally {
			if (this.selectionWork === work) this.selectionWork = null;
		}
	}

	get disconnectedRemoteProfileId(): string | null {
		const session = this.controlledSession;
		const id = session?.executionEnvironment === 'remote' ? session.remoteProfileId : null;
		return id && rail.remoteConnections[id] !== 'connected' ? id : null;
	}

	/** The selected session's machine once it connects, when the session was opened
	 *  before it did, so the session can be loaded again with its saved workspace.
	 *  A Connect started here refreshes the session itself. */
	get reconnectedProfileId(): string | null {
		const id = this.openedWithoutMachine;
		return id && !this.connectingRemote && rail.remoteConnections[id] === 'connected' ? id : null;
	}

	async connectSelectedRemote(): Promise<string | null> {
		const profileId = this.disconnectedRemoteProfileId;
		if (!profileId || this.connectingRemote) return null;
		const attempt = new AbortController();
		this.remoteConnectAbort = attempt;
		this.connectingRemote = true;
		this.selectionError = 'Connecting…';
		try {
			const environment = await readRemoteAssemblyEnvironmentFromTauri();
			if (attempt.signal.aborted) return null;
			const profile = environment.profiles.find((saved) => saved.id === profileId);
			if (!profile) throw new Error('This saved machine is unavailable. Open Settings → Connections to configure it.');
			const result = await connectRemoteAssemblyFromTauri(profile, attempt.signal, (message) => {
				if (!attempt.signal.aborted) this.selectionError = message;
			});
			if (attempt.signal.aborted) return null;
			const replaced = new Set([result.profile.id, result.replacedProfileId]);
			hydrateOwned([...rail.owned.filter((session) => !replaced.has(session.remoteProfileId ?? '')),
				...result.sessions.map(ownedSessionFromBackend)]);
			this.openedWithoutMachine = null;
			return result.profile.id;
		} catch (error) {
			if (!attempt.signal.aborted) this.selectionError = `Could not connect. ${error instanceof Error ? error.message : typeof error === 'object' && error !== null && 'message' in error ? String(error.message) : String(error)}`;
			return null;
		} finally {
			if (this.remoteConnectAbort === attempt) {
				this.remoteConnectAbort = null;
				this.connectingRemote = false;
			}
		}
	}

	async refreshRemoteConnection(profileId: string): Promise<boolean> {
		const selected = rail.owned.find((session) => session.ownedId === this.activeOwnedId);
		if (selected?.executionEnvironment !== 'remote' || selected.remoteProfileId !== profileId) return false;
		await flushConversationSessionDraft(selected.ownedId);
		this.sessionSelectionLayers.clearTreeView();
		this.sessionSelectionLayers.clearChatHistory();
		await this.selectSession(selected.ownedId);
		refreshExplorer();
		this.editorSessions.refreshActiveFiles(selected.ownedId);
		return this.activeWorkspaceSnapshot !== null;
	}

	private async drainSelections(): Promise<void> {
		while (this.pendingSelectionOwnedId !== null) {
			const ownedId = this.pendingSelectionOwnedId;
			this.pendingSelectionOwnedId = null;
			await this.selectSessionOnce(ownedId);
		}
	}

	private async selectSessionOnce(ownedId: string): Promise<void> {
		const owner = this.beginSelection();

		const session = rail.owned.find((candidate) => candidate.ownedId === ownedId);
		if (session) {
			this.activeRootRemote = session.executionEnvironment === "remote";
			const root = canonicalPath(sessionWorkspaceRoot(session, projectRegistry.projects));
			try {
				await this.materializeSelection(session, root, owner, this.chatOwnedId);
			} catch (error) {
				if (!this.isCurrent(owner)) return;
				this.sessionSelectionLayers.clearTreeView();
				this.sessionSelectionLayers.clearChatHistory();
				this.activeRootAvailable = false;
				shellPanels.sessionPicked(false);
				const detail = error instanceof Error ? error.message : typeof error === 'object' && error !== null && 'message' in error ? String(error.message) : String(error);
				this.selectionError = session.executionEnvironment === 'remote' && detail.toLowerCase().includes('not connected')
					? 'This remote machine is not connected.'
					: `Conversation could not be loaded. ${detail}`;
			}
		} else {
			this.expandedPathsByRoot = {};
			this.sessionSelectionLayers.abandonSelection(owner);
			this.sessionSelectionLayers.clearTreeView();
			this.sessionSelectionLayers.clearChatHistory();
		}
	}

	rememberExpandedPaths(ownedId: string, root: string, paths: readonly string[]): void {
		const projectRoot = canonicalPath(root);
		if (ownedId !== this.controlledSelectionOwnedId || ownedId !== this.expansionOwnedId ||
			!projectRoot) return;

		this.expandedPathsByRoot = { [projectRoot]: [...paths] };
		const previousWrite = this.workspaceWriteQueue;
		const write = this.writeExpandedPathsAfter(previousWrite, ownedId, projectRoot, paths);
		this.workspaceWriteQueue = write;
		void this.ignoreWorkspaceWriteFailure(write);
	}

	rememberWorkspaceState(patch: Partial<SessionWorkspaceSnapshot>): void {
		// A session whose selection failed has no editor owner; keep showing its
		// saved workspace rather than dropping it on the first panel change.
		this.activeWorkspaceSnapshot = this.editorSessions.rememberWorkspaceState(patch) ?? this.activeWorkspaceSnapshot;
	}

	persistWorkspaceState(ownedId: string, patch: Partial<SessionWorkspaceSnapshot>): void {
		if (this.activeOwnedId !== ownedId) return;
		this.rememberWorkspaceState(patch);
		void this.ignoreWorkspaceWriteFailure(this.editorSessions.persistWorkspaceState(ownedId));
	}

	async persistConversationAttachmentIds(ownedId: string, ids: readonly string[]): Promise<void> {
		if (this.activeOwnedId === ownedId && this.chatOwnedId === ownedId) {
			const conversation = captureConversationWorkspace(ownedId);
			if (conversation) this.rememberWorkspaceState({ conversation: { ...conversation, attachmentIds: [...ids] } });
		}
		await this.editorSessions.persistWorkspaceState(ownedId, ids);
	}

	/** One removable seam for changing every checkout-backed session surface. */
	async useSessionCheckout(requestedRoot: string): Promise<boolean> {
		const ownedId = this.activeOwnedId;
		const selected = rail.owned.find((session) => session.ownedId === ownedId);
		if (!ownedId || !selected?.canChangeCheckout || selected.origin !== 'app') {
			rail.error = 'Only the active supported Assembly session can change checkout.';
			return false;
		}

		const root = canonicalPath(requestedRoot);
		if (!root) {
			rail.error = 'A checkout folder is required.';
			return false;
		}

		const remoteRoot = parseRemoteWorkspacePath(root);
		if ((selected.executionEnvironment === 'remote' && remoteRoot?.profileId !== selected.remoteProfileId)
			|| (selected.executionEnvironment !== 'remote' && remoteRoot)) {
			rail.error = 'Choose a folder on this session’s machine.';
			return false;
		}
		const owner = this.beginSelection();
		try {
			countInvoke('validate_project_root');
			const validation = await validateProjectRootFromTauri(root, owner.signal);
			if (!this.isCurrent(owner)) return false;
			if (validation && (!validation.exists || !validation.isDirectory)) {
				rail.error = 'That checkout folder is not available.';
				return false;
			}

			await flushConversationSessionDraft(ownedId);
			if (!this.isCurrent(owner)) return false;
			const record = await changeStructuredConversationCheckout(ownedId, parseRemoteWorkspacePath(root)?.path ?? root);
			if (!record) return false;
			if (!this.isCurrent(owner)) return true;

			this.expansionOwnedId = null;
			// Unsaved files follow the root the session is shown at; a session with a project stays there.
			const moved = rail.owned.find((session) => session.ownedId === ownedId);
			await this.editorSessions.resetForCheckoutChange(owner.signal, moved ? canonicalPath(sessionWorkspaceRoot(moved, projectRegistry.projects)) : root);
			if (!this.isCurrent(owner)) return true;
			this.expandedPathsByRoot = {};
			this.sessionSelectionLayers.clearTreeView();
			const updated = rail.owned.find((session) => session.ownedId === ownedId);
			if (!updated) return true;
			this.activeRootRemote = updated.executionEnvironment === "remote";
			await this.materializeSelection(updated, canonicalPath(sessionWorkspaceRoot(updated, projectRegistry.projects)), owner, this.chatOwnedId);
			return true;
		} catch (error) {
			if (this.isCurrent(owner)) {
				rail.error = `Checkout change failed: ${error instanceof Error ? error.message : String(error)}`;
			}
			return false;
		}
	}

	async removeSession(ownedId: string): Promise<void> {
		await deleteAgentConversationSessionFromTauri(ownedId);
		removeOwnedSession(ownedId);
		if (this.controlledSelectionOwnedId === ownedId) {
			this.cancelSelection();
			this.controlledSelectionOwnedId = null;
			this.sessionSelectionLayers.clearTreeView();
			this.sessionSelectionLayers.clearChatHistory();
		}
	}

	async dispose(): Promise<void> {
		this.remoteConnectAbort?.abort();
		this.cancelSelection();
		this.newSession.dispose();
		const editorDisposal = this.editorSessions.dispose();
		this.controlledSelectionOwnedId = null;
		this.expandedPathsByRoot = {};
		this.activeWorkspaceSnapshot = null;
		this.sessionSelectionLayers.clearTreeView();
		this.sessionSelectionLayers.clearChatHistory();
		await editorDisposal;
	}

	private beginSelection(): SessionSelectionOwner {
		this.selectionAbort?.abort();
		const controller = new AbortController();
		this.selectionAbort = controller;
		const owner = {
			generation: ++this.selectionGeneration,
			signal: controller.signal,
		};
		return owner;
	}

	private cancelSelection(): void {
		this.selectionAbort?.abort();
		this.selectionAbort = null;
		this.pendingSelectionOwnedId = null;
		this.selectionGeneration += 1;
	}

	private isCurrent(owner: SessionSelectionOwner): boolean {
		return !owner.signal.aborted && owner.generation === this.selectionGeneration;
	}

	private async materializeSelection(
		session: OwnedSession,
		root: string,
		owner: SessionSelectionOwner,
		displayedChatOwnedId: string | null,
	): Promise<void> {
		if (!this.isCurrent(owner)) return;
		this.openedWithoutMachine = session.executionEnvironment === 'remote'
			&& rail.remoteConnections[session.remoteProfileId ?? ''] !== 'connected' ? session.remoteProfileId ?? null : null;

		this.newSession.abandonDraftForSessionSwitch(session.ownedId);
		if (displayedChatOwnedId && displayedChatOwnedId !== session.ownedId) {
			const conversation = captureConversationWorkspace(displayedChatOwnedId);
			if (conversation) this.editorSessions.rememberWorkspaceState({ conversation });
		}
		try {
			await this.sessionSelectionLayers.selectSession(session, displayedChatOwnedId, owner);
		} catch (error) {
			if (!this.isCurrent(owner)) return;
			const snapshot = await this.editorSessions.restoreEditorWorkspaceForSession(
				session.ownedId, root, false, owner.signal,
			);
			if (!this.isCurrent(owner)) return;
			this.editorSessions.releaseOwnership();
			this.activeWorkspaceSnapshot = snapshot;
			throw error;
		}
		if (!this.isCurrent(owner)) return;
		this.activeRootAvailable = Boolean(this.sessionSelectionLayers.treeRoot);
		shellPanels.sessionPicked(this.activeRootAvailable);
		const snapshot = await this.editorSessions.restoreEditorWorkspaceForSession(
			session.ownedId,
			this.sessionSelectionLayers.treeRoot,
			Boolean(this.sessionSelectionLayers.treeRoot),
			owner.signal,
		);
		if (!this.isCurrent(owner)) return;
		// The folder check failed, so this session's tabs were never shown; leaving
		// it must not save the empty editor over them.
		if (this.sessionSelectionLayers.treeCheckFailed) this.editorSessions.releaseOwnership();
		this.activeWorkspaceSnapshot = snapshot;
		if (snapshot?.conversation?.attachmentIds && getConversationSession(session.ownedId)) {
			restoreConversationAttachmentIds(session.ownedId, snapshot.conversation.attachmentIds);
		}
		if (!root || (session.executionEnvironment === 'remote'
			&& rail.remoteConnections[session.remoteProfileId ?? ''] !== 'connected')) return;
		await this.loadExpandedPaths(session.ownedId, root, owner);
	}

	private async loadExpandedPaths(
		ownedId: string,
		root: string,
		owner: SessionSelectionOwner,
	): Promise<void> {
		try {
			await this.ignoreWorkspaceWriteFailure(this.workspaceWriteQueue);
			if (!this.isCurrent(owner)) return;
			countInvoke("read_agent_conversation_workspace_expanded_paths");
			const paths = await readAgentConversationWorkspaceExpandedPathsFromTauri(ownedId, root, owner.signal);
			if (!this.isCurrent(owner)) return;
			this.expandedPathsByRoot = { [root]: paths };
			this.expansionOwnedId = ownedId;
		} catch {
			// The session may have changed while the uncancellable invoke was in flight.
		}
	}

	private async writeExpandedPathsAfter(
		previousWrite: Promise<void> | null,
		ownedId: string,
		projectRoot: string,
		paths: readonly string[],
	): Promise<void> {
		try {
			if (previousWrite) await previousWrite;
		} catch {
			// Keep later writes ordered even if an earlier persistence call failed.
		}
		countInvoke("write_agent_conversation_workspace_expanded_paths");
		await writeAgentConversationWorkspaceExpandedPathsFromTauri(ownedId, projectRoot, paths);
	}

	private async saveActiveSession(ownedId: string): Promise<void> {
		try {
			await writeAssemblySettingFromTauri(ACTIVE_OWNED_SESSION_SETTING_KEY, ownedId);
		} catch {
			// Best-effort: the selection itself has already changed in memory.
		}
	}

	private async ignoreWorkspaceWriteFailure(write: Promise<void> | null): Promise<void> {
		try {
			await write;
		} catch {
			// Persistence is best-effort; the current projection remains in memory.
		}
	}
}
