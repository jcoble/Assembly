import { rail } from '../stores/sessionRailStore.svelte';
import { parseRemoteWorkspacePath, mapWorkspaceSnapshotPaths } from '../../workspacePaths.ts';
/** Saves and restores only the editor portion of each session workspace. */
import {
	editorState,
	resetEditorState,
	restoreEditorFiles,
	setEditorProjectRoot,
} from '../files/editor/editorStore.svelte';
import {
	captureWorkspace,
	draftsForCheckout,
	planWorkspaceRestore,
	type SessionWorkspaceSnapshot,
} from '../sessionWorkspaces';
import {
	readAgentConversationWorkspaceFromTauri,
	writeAgentConversationWorkspaceFromTauri,
} from '../../tauriSource';
import { captureConversationWorkspace } from '../conversation/conversationStore.svelte';

/** The machine whose paths a session's saved record keeps native: null for the Mac. */
function machineOf(ownedId: string): string | null {
	const session = rail.owned.find((candidate) => candidate.ownedId === ownedId);
	return session?.executionEnvironment === 'remote' ? session.remoteProfileId ?? null : null;
}

/** A session's saved record with its paths back in app form, ready to change and save again. */
async function readWorkspace(ownedId: string): Promise<SessionWorkspaceSnapshot | null> {
	return mapWorkspaceSnapshotPaths(await readAgentConversationWorkspaceFromTauri(ownedId), machineOf(ownedId)) as SessionWorkspaceSnapshot | null;
}

export type EditorPanelLifecycle = {
	captureViewStates(paths: readonly string[]): Record<string, object>;
	workspaceOwnedPaths(): string[];
	restoreViewStates(files: readonly { path: string; viewState?: object }[]): void;
	releaseSessionResources(paths: readonly string[]): void;
	refreshOpenFiles(): void;
};

export class EditorSessionController {
	private panel: EditorPanelLifecycle | null = null;
	private activeOwnedId: string | null = null;
	private activeSnapshot: SessionWorkspaceSnapshot | null = null;
	private checkpointQueue: Promise<void> = Promise.resolve();
	/** Aborted by dispose(); stops queued saves that would otherwise outlive the controller. */
	private readonly stop = new AbortController();

	setPanel(panel: EditorPanelLifecycle | null): void {
		this.panel = panel;
	}

	refreshActiveFiles(ownedId: string): void {
		if (this.activeOwnedId === ownedId) this.panel?.refreshOpenFiles();
	}

	/** One session-selection seam: commenting out this call disables editor switching. */
	async restoreEditorWorkspaceForSession(
		ownedId: string,
		projectRoot: string,
		rootAvailable: boolean,
		stopSignal: AbortSignal,
	): Promise<SessionWorkspaceSnapshot | null> {
		if (stopSignal.aborted) return null;
		if (this.activeOwnedId === ownedId) {
			setEditorProjectRoot(rootAvailable ? projectRoot : null);
			return this.activeSnapshot;
		}

		try {
			await this.enqueue(() => this.checkpointActiveWorkspace(stopSignal));
		} catch (error) {
			const message = error instanceof Error ? error.message
				: typeof error === 'object' && error !== null && 'message' in error
					? String(error.message) : String(error);
			// A removed departing session has nowhere to save its workspace. It must
			// not prevent opening an existing session; other save failures still do.
			if (message !== 'could not save the workspace because the session does not exist') throw error;
		}
		if (stopSignal.aborted) return null;
		this.releaseActiveEditorResources();
		// The outgoing files are gone now; an abort or error below must not leave
		// this session owned, or the next switch would save its empty editor.
		this.releaseOwnership();

		if (stopSignal.aborted) return null;
		const session = rail.owned.find((candidate) => candidate.ownedId === ownedId);
		const offline = session?.executionEnvironment === 'remote'
			&& rail.remoteConnections[session.remoteProfileId ?? ''] !== 'connected';
		// Its saved workspace can't be read until its machine connects. Owning it
		// now would keep the default view after the connect and save it over the
		// stored one, so the session is read in full when it is selected again.
		if (offline) return null;
		const remote = parseRemoteWorkspacePath(projectRoot);
		let stored: SessionWorkspaceSnapshot | null;
		try {
			stored = await readAgentConversationWorkspaceFromTauri(ownedId);
		} catch (error) {
			if (stopSignal.aborted) return null;
			// A remote machine that stopped answering has no folder to show; its
			// saved chat still opens without the workspace record.
			if (rootAvailable || session?.executionEnvironment !== 'remote') throw error;
			return null;
		}
		const snapshot = mapWorkspaceSnapshotPaths(stored, remote?.profileId ?? null) as SessionWorkspaceSnapshot | null;
		if (stopSignal.aborted) return null;
		this.activeOwnedId = ownedId;
		this.activeSnapshot = snapshot;
		setEditorProjectRoot(rootAvailable ? projectRoot : null);
		if (!rootAvailable) {
			this.panel?.restoreViewStates([]);
			resetEditorState();
			return snapshot;
		}

		const plan = planWorkspaceRestore(snapshot);
		this.panel?.restoreViewStates(plan.openFiles);
		if (plan.openFiles.length > 0) restoreEditorFiles(plan.openFiles, plan.activePath);
		else resetEditorState();
		return snapshot;
	}

	/** Merge small non-editor surface state into the same per-session record. */
	rememberWorkspaceState(patch: Partial<SessionWorkspaceSnapshot>): SessionWorkspaceSnapshot | null {
		if (!this.activeOwnedId) return null;
		const fallback = captureWorkspace({
			openFiles: editorState.openFiles,
			activePath: editorState.activePath,
			selectedPath: null,
			scrollTop: 0,
			rightTab: 'files',
		});
		this.activeSnapshot = { ...(this.activeSnapshot ?? fallback), ...patch };
		return this.activeSnapshot;
	}

	async persistWorkspaceState(ownedId: string, attachmentIds?: readonly string[]): Promise<void> {
		const stopSignal = this.stop.signal;
		await this.enqueue(async () => {
			if (this.activeOwnedId === ownedId) {
				await this.checkpointActiveWorkspace(stopSignal, attachmentIds);
				if (this.activeOwnedId === ownedId || !attachmentIds) return;
			}
			if (!attachmentIds) return;
			const latest = await readWorkspace(ownedId);
			if (!latest?.conversation) throw new Error('Conversation workspace is no longer available');
			await writeAgentConversationWorkspaceFromTauri(ownedId, {
				...latest,
				conversation: { ...latest.conversation, attachmentIds: [...attachmentIds] },
			}, machineOf(ownedId));
		});
	}

	async dispose(): Promise<void> {
		const stopSignal = new AbortController().signal;
		const checkpoint = this.checkpointActiveWorkspace(stopSignal);
		this.stop.abort();
		this.releaseActiveEditorResources();
		// The checkpoint writes only while this session is still owned, so ownership ends after it.
		try {
			await checkpoint;
		} finally {
			this.releaseOwnership();
		}
	}

	/** Persist an empty editor (or only `keptFiles`) for this session before another selection can restore it. */
	async clearActiveEditors(keptFiles: Parameters<typeof captureWorkspace>[0]['openFiles'] = []): Promise<boolean> {
		const ownedId = this.activeOwnedId;
		if (!ownedId) return false;
		const previous = this.activeSnapshot ?? await readWorkspace(ownedId);
		const fallback = captureWorkspace({
			openFiles: keptFiles,
			activePath: null,
			selectedPath: previous?.selectedPath ?? null,
			scrollTop: previous?.scrollTop ?? 0,
			rightTab: previous?.rightTab ?? 'files',
		});
		const next: SessionWorkspaceSnapshot = {
			...(previous ?? fallback),
			openPaths: fallback.openPaths,
			activePath: null,
		};
		if (fallback.fileStates) next.fileStates = fallback.fileStates;
		else delete next.fileStates;
		await writeAgentConversationWorkspaceFromTauri(ownedId, next, machineOf(ownedId));
		if (this.activeOwnedId === ownedId) this.activeSnapshot = next;
		return true;
	}

	/** Stop owning a session whose files couldn't be shown, so leaving it can't
	 *  save its empty editor over the tabs it had. */
	releaseOwnership(): void {
		this.activeOwnedId = null;
		this.activeSnapshot = null;
	}

	/** Drops file-backed editor state after the session moves to another checkout,
	 *  except unsaved drafts, which move to the same files in the new checkout. */
	async resetForCheckoutChange(stopSignal: AbortSignal, checkoutRoot: string): Promise<void> {
		if (stopSignal.aborted) return;
		await this.clearActiveEditors(
			draftsForCheckout(editorState.openFiles, editorState.projectRoot, checkoutRoot)
		);
		if (stopSignal.aborted) return;
		this.releaseActiveEditorResources();
		this.activeOwnedId = null;
		this.activeSnapshot = null;
	}

	private async checkpointActiveWorkspace(stopSignal: AbortSignal, attachmentIds?: readonly string[]): Promise<void> {
		const ownedId = this.activeOwnedId;
		if (!ownedId || stopSignal.aborted) return;
		const session = rail.owned.find((candidate) => candidate.ownedId === ownedId);
		if (session?.executionEnvironment === 'remote'
			&& rail.remoteConnections[session.remoteProfileId ?? ''] !== 'connected') return;
		const panel = this.panel;
		const ownedPaths = panel?.workspaceOwnedPaths()
			?? editorState.openFiles.map((file) => file.path);
		const ownedPathSet = new Set(ownedPaths);
		const openFiles = editorState.openFiles.filter((file) => ownedPathSet.has(file.path));
		const activePath = editorState.activePath && ownedPathSet.has(editorState.activePath)
			? editorState.activePath
			: openFiles.at(-1)?.path ?? null;
		const viewStates = panel?.captureViewStates(ownedPaths);
		const capturedConversation = captureConversationWorkspace(ownedId) ?? this.activeSnapshot?.conversation;
		const conversation = attachmentIds && capturedConversation ? { ...capturedConversation, attachmentIds: [...attachmentIds] } : capturedConversation;

		if (stopSignal.aborted) return;
		const latest = await readWorkspace(ownedId);
		if (stopSignal.aborted || this.activeOwnedId !== ownedId) return;
		const previous = latest && this.activeSnapshot
			? {
					...latest,
					...this.activeSnapshot,
					conversation: latest.conversation ?? this.activeSnapshot.conversation,
				}
			: latest ?? this.activeSnapshot;
		const editorCapture = captureWorkspace({
			openFiles,
			activePath,
			viewStates,
			selectedPath: previous?.selectedPath ?? null,
			scrollTop: previous?.scrollTop ?? 0,
			rightTab: previous?.rightTab ?? 'files',
		});
		const snapshot: SessionWorkspaceSnapshot = {
			...(previous ?? editorCapture),
			conversation: conversation ?? latest?.conversation,
			openPaths: editorCapture.openPaths,
			activePath: editorCapture.activePath,
		};
		if (editorCapture.fileStates) snapshot.fileStates = editorCapture.fileStates;
		else delete snapshot.fileStates;

		if (stopSignal.aborted) return;
		const before = this.activeSnapshot;
		await writeAgentConversationWorkspaceFromTauri(ownedId, snapshot, machineOf(ownedId));
		if (stopSignal.aborted) return;
		// Keep any workspace change made while the write was pending.
		if (this.activeSnapshot === before) this.activeSnapshot = snapshot;
	}

	/** Saves run one at a time, in call order; a failed save does not stop the next. */
	private async enqueue(work: () => Promise<void>): Promise<void> {
		const run = this.runAfter(this.checkpointQueue, work);
		this.checkpointQueue = run;
		await run;
	}

	private async runAfter(previous: Promise<void>, work: () => Promise<void>): Promise<void> {
		try {
			await previous;
		} catch {
			// The earlier caller already saw its error.
		}
		await work();
	}

	private releaseActiveEditorResources(): void {
		const paths = editorState.openFiles.map((file) => file.path);
		this.panel?.releaseSessionResources(paths);
		if (!this.panel) resetEditorState();
		setEditorProjectRoot(null);
	}
}
